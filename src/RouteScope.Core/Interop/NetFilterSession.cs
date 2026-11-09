using System.Runtime.InteropServices;
using System.Text;

namespace RouteScope.Core.Interop;

public enum NetFilterStatus
{
    Success = 0,
    Failed = -1,
    InvalidEndpoint = -2,
    NotInitialized = -3,
    IoError = -4,
}

/// <summary>
/// Owns the NetFilterSDK session: the managed callbacks, the unmanaged handler table and
/// the driver handle. One instance per running engine.
/// </summary>
public sealed class NetFilterSession : IDisposable
{
    // Delegate instances must stay rooted for as long as the driver holds the pointers.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ThreadCb();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ConnCb(ulong id, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DataCb(ulong id, IntPtr buffer, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void IdCb(ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UdpDataCb(ulong id, IntPtr remoteAddress, IntPtr buffer, int length, IntPtr options);

    private readonly List<Delegate> _keepAlive = new();
    private IntPtr _handlerBlock;
    private bool _initialised;

    public bool IsRunning => _initialised;

    /// <summary>1 = TDI, 2 = WFP. WFP is the modern one and what Windows 10/11 uses.</summary>
    public static uint DriverType => NetFilterApi.nf_getDriverType();

    /// <summary>The network filter driver process mode runs on.</summary>
    public static string SystemDriverPath =>
        Path.Combine(Environment.SystemDirectory, "drivers", "netfilter2.sys");

    public static bool IsDriverInstalled()
    {
        try { return File.Exists(SystemDriverPath); } catch { return false; }
    }

    /// <summary>
    /// Copies and registers the bundled driver when it is not present yet. Needs admin,
    /// the same way the driver's own installer does.
    /// </summary>
    public static (bool ok, string? error) EnsureDriverInstalled()
    {
        if (IsDriverInstalled()) return (true, null);

        var bundled = Path.Combine(AppContext.BaseDirectory, "nfdriver.sys");
        if (!File.Exists(bundled))
            return (false, "nfdriver.sys is missing next to RouteScope.exe.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SystemDriverPath)!);
            File.Copy(bundled, SystemDriverPath, overwrite: true);

            var status = (NetFilterStatus)NetFilterApi.nf_registerDriver("netfilter2");
            if (status == NetFilterStatus.Success)
            {
                LogBus.Info("nf", "Installed the netfilter2 driver.");
                return (true, null);
            }

            return (false, $"The driver was copied but could not be registered (code {(int)status}).");
        }
        catch (Exception ex)
        {
            return (false, "Could not install the driver: " + ex.Message);
        }
    }

    /// <summary>Installs the broad "tell me about connect requests" rules used by process mode.</summary>
    /// <param name="excludedPids">
    /// Processes that must bypass the filter entirely at the DRIVER level — above all the
    /// engine. If the engine's sockets are redirected back into its own inbound, every packet
    /// loops forever and the kernel livelocks. A managed "disable filtering" call is racy;
    /// an NF_ALLOW rule is not.
    /// </param>
    public int AddConnectRequestRules(bool includeUdp, IEnumerable<uint>? excludedPids = null)
    {
        int result = NetFilterApi.NF_STATUS_SUCCESS;

        // Loopback must bypass the filter entirely. Our relay and the engine's mixed inbound
        // are both 127.0.0.1, so without this the driver can indicate the very connections we
        // use to move proxied traffic - a re-entrant path that feeds packets back into the proxy.
        foreach (var (family, address, mask) in LoopbackRules())
        {
            var rule = NetFilterApi.NF_RULE.Create(
                NetFilterApi.IPPROTO_TCP, NetFilterApi.NF_ALLOW, NetFilterApi.NF_D_BOTH);
            rule.ip_family = family;
            rule.remoteIpAddress = address;
            rule.remoteIpAddressMask = mask;
            var status = NetFilterApi.nf_addRule(ref rule, 0);
            if (status != NetFilterApi.NF_STATUS_SUCCESS) result = status;

            var udpRule = NetFilterApi.NF_RULE.Create(
                NetFilterApi.IPPROTO_UDP, NetFilterApi.NF_ALLOW, NetFilterApi.NF_D_BOTH);
            udpRule.ip_family = family;
            udpRule.remoteIpAddress = address;
            udpRule.remoteIpAddressMask = mask;
            var udpStatus = NetFilterApi.nf_addRule(ref udpRule, 0);
            if (udpStatus != NetFilterApi.NF_STATUS_SUCCESS) result = udpStatus;
        }

        // These go in FIRST, before the catch-all rules below. nfapi applies the FIRST
        // matching rule, so allow rules must precede the catch-all ones.
        if (excludedPids is not null)
        {
            foreach (var pid in excludedPids.Distinct())
            {
                if (pid == 0) continue;

                foreach (var family in new[] { NetFilterApi.AF_INET, NetFilterApi.AF_INET6 })
                {
                    var allowTcp = NetFilterApi.NF_RULE.Create(
                        NetFilterApi.IPPROTO_TCP, NetFilterApi.NF_ALLOW, NetFilterApi.NF_D_BOTH);
                    allowTcp.processId = pid;
                    allowTcp.ip_family = family;
                    var tcpStatus = NetFilterApi.nf_addRule(ref allowTcp, 0);
                    if (tcpStatus != NetFilterApi.NF_STATUS_SUCCESS) result = tcpStatus;

                    var allowUdp = NetFilterApi.NF_RULE.Create(
                        NetFilterApi.IPPROTO_UDP, NetFilterApi.NF_ALLOW, NetFilterApi.NF_D_BOTH);
                    allowUdp.processId = pid;
                    allowUdp.ip_family = family;
                    var udpStatus = NetFilterApi.nf_addRule(ref allowUdp, 0);
                    if (udpStatus != NetFilterApi.NF_STATUS_SUCCESS) result = udpStatus;
                }

                // Without the IPv6 pair the engine's IPv6 traffic (the shipped WG config has
                // ::/0 in AllowedIPs) would fall through to the catch-all rules.
                LogBus.Info("nf", $"Excluding pid {pid} from all filtering (IPv4 + IPv6)");
            }
        }

        var tcpV4 = NetFilterApi.NF_RULE.Create(NetFilterApi.IPPROTO_TCP, NetFilterApi.NF_INDICATE_CONNECT_REQUESTS);
        var statusV4 = NetFilterApi.nf_addRule(ref tcpV4, 0);
        if (statusV4 != NetFilterApi.NF_STATUS_SUCCESS) result = statusV4;

        var tcpV6 = NetFilterApi.NF_RULE.Create(NetFilterApi.IPPROTO_TCP, NetFilterApi.NF_INDICATE_CONNECT_REQUESTS);
        tcpV6.ip_family = NetFilterApi.AF_INET6;
        NetFilterApi.nf_addRule(ref tcpV6, 0);

        if (includeUdp)
        {
            var udpV4 = NetFilterApi.NF_RULE.Create(NetFilterApi.IPPROTO_UDP, NetFilterApi.NF_FILTER, NetFilterApi.NF_D_BOTH);
            NetFilterApi.nf_addRule(ref udpV4, 0);

            var udpV6 = NetFilterApi.NF_RULE.Create(NetFilterApi.IPPROTO_UDP, NetFilterApi.NF_FILTER, NetFilterApi.NF_D_BOTH);
            udpV6.ip_family = NetFilterApi.AF_INET6;
            NetFilterApi.nf_addRule(ref udpV6, 0);
        }

        return result;
    }

    /// <summary>127.0.0.0/8 and ::1/128 — loopback never enters the filter.</summary>
    private static IEnumerable<(ushort Family, byte[] Address, byte[] Mask)> LoopbackRules()
    {
        const int size = NetFilterApi.NF_MAX_IP_ADDRESS_LENGTH;

        var v4Address = new byte[size];
        v4Address[0] = 127; v4Address[3] = 1;
        var v4Mask = new byte[size];
        v4Mask[0] = 255;
        yield return (NetFilterApi.AF_INET, v4Address, v4Mask);

        var v6Address = new byte[size];
        v6Address[15] = 1;
        var v6Mask = new byte[size];
        for (var i = 0; i < size; i++) v6Mask[i] = 255;
        yield return (NetFilterApi.AF_INET6, v6Address, v6Mask);
    }

    public int DeleteRules() => NetFilterApi.nf_deleteRules();

    /// <summary>Raised when a filtered process opens an outgoing TCP connection.</summary>
    public event Action<ulong, IntPtr, NF_TCP_CONN_INFO_VIEW>? TcpConnectRequest;
    public event Action<ulong, uint, ushort>? UdpCreated;
    public event Action<ulong, IntPtr, int, IntPtr>? UdpSend;

    /// <summary>
    /// Raised when the driver drops a UDP endpoint. Anything still holding its id must stop
    /// posting to it — a stale id is a use-after-free inside the driver.
    /// </summary>
    public event Action<ulong>? UdpClosed;

    public readonly record struct NF_TCP_CONN_INFO_VIEW(
        uint ProcessId,
        ushort Family,
        string LocalIp,
        ushort LocalPort,
        string RemoteIp,
        ushort RemotePort);

    /// <summary>
    /// Rewrites the destination of a pending connect request to loopback. The driver then
    /// completes the app's connect() against our relay listener, keeping the app's socket
    /// pointed at the original address.
    /// </summary>
    public static void RedirectRemoteToLoopback(IntPtr infoPtr, ushort port)
    {
        var info = Marshal.PtrToStructure<NetFilterApi.NF_TCP_CONN_INFO>(infoPtr);
        NetFilterApi.WriteLoopback(info.remoteAddress, info.ip_family, port);
        Marshal.StructureToPtr(info, infoPtr, false);
    }

    /// <summary>Reads a sockaddr out of the 28-byte address field the driver hands us.</summary>
    public static (string host, ushort port, ushort family) ReadSockaddr(IntPtr addressPtr)
    {
        var field = new byte[NetFilterApi.NF_MAX_ADDRESS_LENGTH];
        Marshal.Copy(addressPtr, field, 0, field.Length);
        var address = NetFilterApi.ReadAddress(field);
        return (address.text, address.port, address.family);
    }

    /// <summary>
    /// Starts filtering. <paramref name="threads"/> controls how many SDK threads invoke the
    /// callbacks; callbacks must therefore be quick and thread-safe.
    /// </summary>
    public NetFilterStatus Start(int threads = 2)
    {
        if (_initialised) return NetFilterStatus.Success;

        try
        {
            NetFilterApi.nf_adjustProcessPriviledges();
            NetFilterApi.nf_setOptions((uint)Math.Clamp(threads, 1, 16), 0);

            _handlerBlock = Marshal.AllocHGlobal(Marshal.SizeOf<NetFilterApi.NF_EVENT_HANDLER>());
            var table = BuildHandlerTable();
            Marshal.StructureToPtr(table, _handlerBlock, false);

            var status = (NetFilterStatus)NetFilterApi.nf_init("netfilter2", _handlerBlock);
            _initialised = status == NetFilterStatus.Success;
            return status;
        }
        catch (DllNotFoundException ex)
        {
            LogBus.Error("nf", "nfapi.dll is missing: " + ex.Message);
            return NetFilterStatus.Failed;
        }
        catch (BadImageFormatException ex)
        {
            LogBus.Error("nf", "nfapi.dll architecture mismatch: " + ex.Message);
            return NetFilterStatus.Failed;
        }
        catch (Exception ex)
        {
            LogBus.Error("nf", "Could not start NetFilter: " + ex.Message);
            return NetFilterStatus.Failed;
        }
    }

    /// <summary>
    /// Verifies that every function pointer handed to the driver has a rooted delegate.
    /// Marshal.GetFunctionPointerForDelegate does not root it: a collected delegate leaves the
    /// driver calling freed memory from kernel mode, which hard-hangs the whole machine.
    /// </summary>
    /// <summary>
    /// udpReceive must be a pure nf_udpPostReceive pass-through. A no-op silently pends or
    /// drops every inbound UDP datagram of a filtered endpoint.
    /// </summary>
    private void OnUdpReceive(ulong id, IntPtr remoteAddress, IntPtr buffer, int length, IntPtr options)
    {
        try
        {
            NetFilterApi.nf_udpPostReceive(id, remoteAddress, buffer, length, options);
        }
        catch (Exception ex)
        {
            LogBus.Warn("nf", "udpReceive failed: " + ex.Message);
        }
    }

    public static IReadOnlyList<string> ValidateHandlerRoots()
    {
        var session = new NetFilterSession();
        var table = session.BuildHandlerTable();

        var rooted = new HashSet<IntPtr>(
            session._keepAlive.Select(Marshal.GetFunctionPointerForDelegate));

        var missing = new List<string>();
        foreach (var field in typeof(NetFilterApi.NF_EVENT_HANDLER).GetFields())
        {
            var pointer = (IntPtr)field.GetValue(table)!;
            if (pointer != IntPtr.Zero && !rooted.Contains(pointer))
                missing.Add(field.Name);
        }

        return missing;
    }

    private NetFilterApi.NF_EVENT_HANDLER BuildHandlerTable()
    {
        ThreadCb noopThread = () => { };
        ConnCb onTcpConnect = OnTcpConnectRequest;
        ConnCb noopConn = (_, _) => { };
        DataCb noopData = (_, _, _) => { };
        IdCb noopId = _ => { };
        ConnCb onUdpCreated = OnUdpCreated;
        ConnCb onUdpClosed = OnUdpClosed;
        UdpDataCb onUdpSend = OnUdpSend;
        UdpDataCb onUdpReceive = OnUdpReceive;

        // EVERY delegate whose pointer is handed to the driver must be rooted here.
        // Marshal.GetFunctionPointerForDelegate does NOT keep it alive: a collected delegate
        // leaves the driver calling a freed thunk from kernel mode, which hangs the machine.
        _keepAlive.AddRange(new Delegate[]
        {
            noopThread, onTcpConnect, noopConn, noopData, noopId,
            onUdpCreated, onUdpClosed, onUdpSend, onUdpReceive,
        });

        IntPtr P(Delegate d) => Marshal.GetFunctionPointerForDelegate(d);

        return new NetFilterApi.NF_EVENT_HANDLER
        {
            threadStart = P(noopThread),
            threadEnd = P(noopThread),
            tcpConnectRequest = P(onTcpConnect),
            tcpConnected = P(noopConn),
            tcpClosed = P(noopConn),
            tcpReceive = P(noopData),
            tcpSend = P(noopData),
            tcpCanReceive = P(noopId),
            tcpCanSend = P(noopId),
            udpCreated = P(onUdpCreated),
            udpConnectRequest = P(noopConn),
            udpClosed = P(onUdpClosed),
            udpReceive = P(onUdpReceive),
            udpSend = P(onUdpSend),
            udpCanReceive = P(noopId),
            udpCanSend = P(noopId),
        };
    }

    private void OnTcpConnectRequest(ulong id, IntPtr infoPtr)
    {
        try
        {
            var info = Marshal.PtrToStructure<NetFilterApi.NF_TCP_CONN_INFO>(infoPtr);
            var local = NetFilterApi.ReadAddress(info.localAddress);
            var remote = NetFilterApi.ReadAddress(info.remoteAddress);

            var view = new NF_TCP_CONN_INFO_VIEW(
                info.processId, info.ip_family, local.text, local.port, remote.text, remote.port);

            TcpConnectRequest?.Invoke(id, infoPtr, view);
        }
        catch (Exception ex)
        {
            LogBus.Warn("nf", "tcpConnectRequest failed: " + ex.Message);
        }
    }

    private void OnUdpClosed(ulong id, IntPtr infoPtr)
    {
        try { UdpClosed?.Invoke(id); }
        catch (Exception ex) { LogBus.Warn("nf", "udpClosed failed: " + ex.Message); }
    }

    private void OnUdpCreated(ulong id, IntPtr infoPtr)
    {
        try
        {
            var info = Marshal.PtrToStructure<NetFilterApi.NF_UDP_CONN_INFO>(infoPtr);
            UdpCreated?.Invoke(id, info.processId, info.ip_family);
        }
        catch (Exception ex)
        {
            LogBus.Warn("nf", "udpCreated failed: " + ex.Message);
        }
    }

    private void OnUdpSend(ulong id, IntPtr remoteAddress, IntPtr buffer, int length, IntPtr options)
    {
        try
        {
            UdpSend?.Invoke(id, buffer, length, remoteAddress);
        }
        catch (Exception ex)
        {
            LogBus.Warn("nf", "udpSend failed: " + ex.Message);
        }
    }

    /// <summary>Resolves a process id to its full image path, using the driver when possible.</summary>
    public static string? GetProcessPath(uint pid)
    {
        if (pid == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            if (NetFilterApi.nf_getProcessNameFromKernel(pid, buffer, (uint)buffer.Capacity) &&
                buffer.Length > 0)
                return buffer.ToString();

            buffer.Clear();
            if (NetFilterApi.nf_getProcessNameW(pid, buffer, (uint)buffer.Capacity) && buffer.Length > 0)
                return buffer.ToString();
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (_initialised)
        {
            try { NetFilterApi.nf_deleteRules(); } catch { }
            try { NetFilterApi.nf_free(); } catch { }
            _initialised = false;
        }

        if (_handlerBlock != IntPtr.Zero)
        {
            try { Marshal.FreeHGlobal(_handlerBlock); } catch { }
            _handlerBlock = IntPtr.Zero;
        }

        _keepAlive.Clear();
    }
}
