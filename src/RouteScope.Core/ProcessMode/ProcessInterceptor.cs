using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RouteScope.Core.Interop;
using RouteScope.Core.Models;

namespace RouteScope.Core.ProcessMode;

/// <summary>
/// Selective proxying at the socket layer.
///
/// The NetFilter driver is told to report outgoing connect() calls. For every one we look at
/// the owning process: if it is one of the user's targets, the destination is rewritten to a
/// local relay that speaks SOCKS5 to our engine; if not, filtering is switched off for that
/// connection and the app talks to the internet exactly as it would without us.
///
/// Nothing outside the selected processes is touched — no adapter, no firewall rules, no
/// change to any other program's traffic.
/// </summary>
public sealed class ProcessInterceptor : IAsyncDisposable
{
    private readonly NetFilterSession _session = new();
    private readonly ConcurrentDictionary<ushort, PendingConnect> _pending = new();
    private readonly ConcurrentDictionary<uint, string?> _processPaths = new();
    private readonly ConcurrentDictionary<ulong, UdpChannel> _udpChannels = new();
    private readonly ConcurrentDictionary<string, byte> _notified = new(StringComparer.OrdinalIgnoreCase);

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    private Socket? _listener;
    private int _relayPort;
    private int _socksPort;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    private ProxyTarget[] _targets = Array.Empty<ProxyTarget>();
    private HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<uint> _excludedPids = new();
    private bool _udpEnabled;

    private const int MaxConcurrentRelays = 512;
    private int _activeRelays;
    private int _relayFailures;

    private long _bytesUp;
    private long _bytesDown;
    private long _connectionCount;

    public bool IsRunning => _running;
    public long BytesUploaded => Interlocked.Read(ref _bytesUp);
    public long BytesDownloaded => Interlocked.Read(ref _bytesDown);
    public long ConnectionsHandled => Interlocked.Read(ref _connectionCount);
    public int RelayPort => _relayPort;

    /// <summary>Processes that matched a target, for the "proxied" badge in the UI.</summary>
    public event Action<string>? ProcessMatched;

    private readonly record struct PendingConnect(string Host, int Port, ushort Family);

    // ------------------------------------------------------------------ lifecycle

    public (bool ok, string? error) Start(
        IReadOnlyList<ProxyTarget> targets,
        int socksPort,
        bool enableUdp,
        IEnumerable<string> excludedPaths,
        IEnumerable<uint>? excludedPids = null)
    {
        if (_running) return (true, null);

        _targets = targets.Where(t => t.Enabled).ToArray();
        _socksPort = socksPort;
        _udpEnabled = enableUdp;
        _excludedPaths = new HashSet<string>(excludedPaths, StringComparer.OrdinalIgnoreCase);
        _excludedPids = new HashSet<uint>(excludedPids ?? Array.Empty<uint>());

        if (_targets.Length == 0)
            return (false, "Add at least one app or folder before using process mode.");

        if (!OpenRelay(out var relayError))
            return (false, relayError);

        var (driverOk, driverError) = NetFilterSession.EnsureDriverInstalled();
        if (!driverOk)
        {
            CloseRelay();
            return (false, driverError);
        }

        _session.TcpConnectRequest += OnTcpConnectRequest;
        _session.UdpCreated += OnUdpCreated;
        _session.UdpSend += OnUdpSend;
        _session.UdpClosed += OnUdpClosed;

        // One worker thread (the SDK default). Every callback must therefore be
        // non-blocking: a stall here blocks connect() inside the kernel.
        var status = _session.Start(threads: 1);
        if (status != NetFilterStatus.Success)
        {
            _session.Dispose();
            CloseRelay();
            return (false, status switch
            {
                NetFilterStatus.NotInitialized => "The NetFilter driver is not running.",
                _ => "Could not open the network filter driver. Process mode needs administrator, " +
                     "and the driver has to be installed (RouteScope can do that for you).",
            });
        }

        var ruleResult = _session.AddConnectRequestRules(enableUdp, _excludedPids);
        if (ruleResult != NetFilterApi.NF_STATUS_SUCCESS)
        {
            _session.Dispose();
            CloseRelay();
            return (false, "The driver rejected the filtering rules.");
        }

        LogBus.Info("process", $"Process mode active on relay port {_relayPort} → SOCKS {socksPort}" +
                               (enableUdp ? " (TCP + UDP)" : " (TCP only)"));
        return (true, null);
    }

    /// <summary>
    /// Opens the relay listener without touching the driver. Used by tests, and by the
    /// diagnostic page to prove the SOCKS path works on this machine.
    /// </summary>
    public int StartRelayOnly(int socksPort)
    {
        _socksPort = socksPort;
        return OpenRelay(out _) ? _relayPort : 0;
    }

    /// <summary>Records a destination for a client port, exactly as the driver callback does.</summary>
    public void TrackConnect(ushort localPort, string host, int port) =>
        _pending[localPort] = new PendingConnect(host, port, 2);

    private bool OpenRelay(out string? error)
    {
        error = null;
        try
        {
            // Dual-stack listener: the driver rewrites IPv4 and IPv6 destinations here.
            _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            _listener.DualMode = true;
            _listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            _listener.Listen(512);
            _relayPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;

            _cts = new CancellationTokenSource();
            _running = true;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not open the relay listener: " + ex.Message;
            return false;
        }
    }

    private void CloseRelay()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        try { _session.DeleteRules(); } catch { }
        try { _session.Dispose(); } catch { }

        CloseRelay();

        foreach (var channel in _udpChannels.Values) channel.Dispose();
        _udpChannels.Clear();
        _pending.Clear();
        _processPaths.Clear();
        _notified.Clear();

        LogBus.Info("process", "Process mode stopped.");
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- TCP path

    private void OnTcpConnectRequest(ulong id, IntPtr infoPtr, NetFilterSession.NF_TCP_CONN_INFO_VIEW view)
    {
        try
        {
            if (!_running) return;

            if (view.ProcessId == _ownProcessId || !IsTarget(view.ProcessId, out var path))
            {
                // Not ours: let the connection continue untouched.
                NetFilterApi.nf_tcpDisableFiltering(id);
                return;
            }

            // Never redirect a loopback destination. The relay's own connection to the
            // engine's mixed port is loopback; indicating it again would feed proxied traffic
            // back into the proxy, which is the classic self-feeding loop.
            if (view.RemoteIp is "127.0.0.1" or "::1" ||
                view.RemoteIp.StartsWith("127.", StringComparison.Ordinal))
            {
                NetFilterApi.nf_tcpDisableFiltering(id);
                return;
            }

            _pending[view.LocalPort] = new PendingConnect(view.RemoteIp, view.RemotePort, view.Family);
            NetFilterSession.RedirectRemoteToLoopback(infoPtr, (ushort)_relayPort);

            // Report each process once, not once per connection.
            if (path is not null && _notified.TryAdd(path, 0)) ProcessMatched?.Invoke(path);
        }
        catch (Exception ex)
        {
            LogBus.Warn("process", "connect request: " + ex.Message);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                LogBus.Warn("process", "accept: " + ex.Message);
                return;
            }

            // Refuse rather than queue once we are saturated: an unbounded pile of stuck
            // relays is what makes a bad route take the whole machine down with it.
            if (Interlocked.Increment(ref _activeRelays) > MaxConcurrentRelays)
            {
                Interlocked.Decrement(ref _activeRelays);
                try { client.Dispose(); } catch { }
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await RelayAsync(client, ct).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _activeRelays); }
            }, ct);
        }
    }

    private async Task RelayAsync(Socket client, CancellationToken ct)
    {
        PendingConnect? target = null;
        Socket? upstream = null;

        try
        {
            var clientPort = (ushort)((IPEndPoint)client.RemoteEndPoint!).Port;
            if (!_pending.TryRemove(clientPort, out var pending))
            {
                // No mapping: a stale or unrelated connection to our relay port.
                client.Dispose();
                return;
            }

            target = pending;
            // Bounded: if the engine is not answering, the app gets a reset quickly instead
            // of a connection that hangs forever and piles up.
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));

            upstream = await Socks5.ConnectAsync(_socksPort, pending.Host, pending.Port, connectCts.Token)
                .ConfigureAwait(false);

            Interlocked.Increment(ref _connectionCount);

            var up = PumpAsync(client, upstream, isUpload: true, ct);
            var down = PumpAsync(upstream, client, isUpload: false, ct);
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Throttled: a dead route produces one failure per connection, and a flood of
            // log lines is its own problem.
            if (Interlocked.Increment(ref _relayFailures) <= 20)
                LogBus.Debug("process", $"relay {target?.Host}:{target?.Port}: {ex.Message}");
        }
        finally
        {
            try { upstream?.Shutdown(SocketShutdown.Both); } catch { }
            try { upstream?.Dispose(); } catch { }
            try { client.Shutdown(SocketShutdown.Both); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private async Task PumpAsync(Socket from, Socket to, bool isUpload, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                if (read <= 0) break;

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, ct).ConfigureAwait(false);

                if (isUpload) Interlocked.Add(ref _bytesUp, read);
                else Interlocked.Add(ref _bytesDown, read);
            }
        }
        catch { }
        finally
        {
            try { to.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    // ---------------------------------------------------------------- UDP path

    /// <summary>
    /// The driver dropped this endpoint. Its id is no longer valid, so the channel must stop
    /// before it posts again — posting to a closed id is what crashes inside the driver.
    /// </summary>
    private void OnUdpClosed(ulong id)
    {
        if (!_udpChannels.TryRemove(id, out var channel)) return;
        channel.Stop();
    }

    private void OnUdpCreated(ulong id, uint processId, ushort family)
    {
        try
        {
            if (!_running || !_udpEnabled) return;

            if (processId == _ownProcessId || !IsTarget(processId, out _))
            {
                NetFilterApi.nf_udpDisableFiltering(id);
                return;
            }

            _udpChannels.TryAdd(id, new UdpChannel(_socksPort, id));
        }
        catch (Exception ex)
        {
            LogBus.Warn("process", "udp create: " + ex.Message);
        }
    }

    private void OnUdpSend(ulong id, IntPtr buffer, int length, IntPtr remoteAddress)
    {
        try
        {
            if (!_running || !_udpEnabled) return;
            if (!_udpChannels.TryGetValue(id, out var channel)) return;

            var (host, port, _) = NetFilterSession.ReadSockaddr(remoteAddress);
            var payload = new byte[length];
            Marshal.Copy(buffer, payload, 0, length);

            channel.Send(host, port, payload);
            Interlocked.Add(ref _bytesUp, length);
        }
        catch (Exception ex)
        {
            LogBus.Debug("process", "udp send: " + ex.Message);
        }
    }

    /// <summary>
    /// One SOCKS5 UDP association per intercepted endpoint: a control connection to keep the
    /// association alive, plus a datagram socket to the relay.
    /// </summary>
    private sealed class UdpChannel : IDisposable
    {
        private readonly int _socksPort;
        private readonly ulong _endpointId;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Socket? _control;
        private Socket? _udp;
        private IPEndPoint? _relay;
        private volatile bool _ready;
        private volatile bool _failed;
        private volatile bool _closed;

        public UdpChannel(int socksPort, ulong endpointId)
        {
            _socksPort = socksPort;
            _endpointId = endpointId;
        }

        private async Task EnsureAsync()
        {
            if (_ready || _failed || _closed) return;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_ready || _failed || _closed) return;

                try
                {
                    // Bounded: a hung handshake must not pin this task forever.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var (control, relay) = await Socks5.UdpAssociateAsync(_socksPort, timeout.Token)
                        .ConfigureAwait(false);

                    _control = control;
                    _relay = relay;
                    _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    _ready = true;
                    _ = Task.Run(ReceiveLoopAsync);
                }
                catch (Exception ex)
                {
                    _failed = true;
                    LogBus.Debug("process", "udp associate: " + ex.Message);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private int _starting;

        /// <summary>
        /// Runs on a driver callback thread. It must never block: a stalled callback thread
        /// stalls the whole filter pipeline, which takes the machine's networking with it.
        /// </summary>
        public void Send(string host, int port, byte[] payload)
        {
            if (!_ready)
            {
                if (Interlocked.Exchange(ref _starting, 1) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await EnsureAsync().ConfigureAwait(false); }
                        finally { Interlocked.Exchange(ref _starting, 0); }
                    });
                }

                return;   // this one datagram is dropped; the next one will flow
            }

            if (_closed || _failed || _udp is null || _relay is null) return;

            var datagram = Socks5.BuildUdpDatagram(host, port, payload);
            try { _udp.SendTo(datagram, _relay); } catch { }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65535];
            var remote = new IPEndPoint(IPAddress.Any, 0);

            while (_ready && _udp is not null)
            {
                int read;
                try
                {
                    EndPoint from = remote;
                    read = _udp.ReceiveFrom(buffer, ref from);
                }
                catch { return; }

                if (!Socks5.TryParseUdpDatagram(buffer.AsSpan(0, read), out var host, out var port, out var offset))
                    continue;

                var payloadLength = read - offset;
                if (payloadLength <= 0) continue;

                try
                {
                    var payload = Marshal.AllocHGlobal(payloadLength);
                    var address = Marshal.AllocHGlobal(NetFilterApi.NF_MAX_ADDRESS_LENGTH);
                    // flags + optionsLength only; no inline options are sent.
                    var options = Marshal.AllocHGlobal(8);
                    try
                    {
                        Marshal.Copy(buffer, offset, payload, payloadLength);

                        var addressBytes = new byte[NetFilterApi.NF_MAX_ADDRESS_LENGTH];
                        var ip = IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Loopback;
                        var family = ip.AddressFamily == AddressFamily.InterNetworkV6
                            ? NetFilterApi.AF_INET6
                            : NetFilterApi.AF_INET;
                        BitConverter.TryWriteBytes(addressBytes.AsSpan(0, 2), family);
                        BitConverter.TryWriteBytes(addressBytes.AsSpan(2, 2),
                            IPAddress.HostToNetworkOrder((short)port));
                        if (family == NetFilterApi.AF_INET6)
                        {
                            ip.GetAddressBytes().CopyTo(addressBytes, 8);
                        }
                        else
                        {
                            ip.GetAddressBytes().CopyTo(addressBytes, 4);
                        }
                        Marshal.Copy(addressBytes, 0, address, addressBytes.Length);

                        // flags = TDI_RECEIVE_NORMAL, no extra options
                        Marshal.WriteInt32(options, 0, 0x20);
                        Marshal.WriteInt32(options, 4, 0);

                        NetFilterApi.nf_udpPostReceive(_endpointId, address, payload, payloadLength, options);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(options);
                        Marshal.FreeHGlobal(address);
                        Marshal.FreeHGlobal(payload);
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Debug("process", "udp receive: " + ex.Message);
                }
            }
        }

        /// <summary>Stops the channel before its endpoint id can be used again.</summary>
        public void Stop() => Dispose();

        public void Dispose()
        {
            _closed = true;
            _ready = false;
            try { _udp?.Dispose(); } catch { }
            try { _control?.Dispose(); } catch { }
            _udp = null;
            _control = null;
        }
    }

    // ---------------------------------------------------------------- matching

    private bool IsTarget(uint processId, out string? path)
    {
        // Hard stop: the engine, our relay, and RouteScope itself must never be redirected
        // into the relay. A path lookup can fail (kernel returns device paths), so the PID
        // check comes first and is authoritative.
        if (processId == _ownProcessId || _excludedPids.Contains(processId))
        {
            path = null;
            return false;
        }

        path = ResolvePath(processId);
        if (string.IsNullOrEmpty(path)) return false;
        if (_excludedPaths.Contains(path)) return false;

        var name = Path.GetFileName(path);
        foreach (var target in _targets)
            if (target.Matches(path, name))
                return true;

        return false;
    }

    /// <summary>
    /// Runs inside a driver callback, so it must never block. Only the kernel query is used —
    /// <c>Process.MainModule</c> can stall for seconds on a protected or suspended process,
    /// and stalling here blocks a connect() inside the kernel. An unknown process simply is
    /// not intercepted, which is the safe way to be wrong.
    /// </summary>
    private string? ResolvePath(uint processId)
    {
        if (_processPaths.TryGetValue(processId, out var cached)) return cached;

        var path = NetFilterSession.GetProcessPath(processId);

        // Do not cache failures: the process may not have finished starting.
        if (!string.IsNullOrEmpty(path)) _processPaths[processId] = path;
        return path;
    }
}
