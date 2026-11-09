using System.Text;
using System.Text.Json.Nodes;
using System.Net;
using RouteScope.Core.Engine;
using RouteScope.Core.Models;
using RouteScope.Core.Psiphon;
using RouteScope.Core.SystemTools;
using RouteScope.Core.Updates;

namespace RouteScope.Core.Runtime;

public sealed record StartResult(
    bool Ok,
    string? Error = null,
    string? ErrorCode = null,
    bool DirectChain = false);

/// <summary>
/// The conductor: resolves the route, generates a sing-box config, starts the engine,
/// hooks up the Clash API for live stats and applies/reverts the system proxy.
/// </summary>
public sealed class RouteEngine : IAsyncDisposable
{
    private readonly EngineRunner _runner = new();

    /// <summary>Endpoints assigned at run time by managed clients such as Psiphon.</summary>
    private readonly Dictionary<string, (string Host, int Port)> _managed = new();

    private readonly List<PsiphonSession> _psiphonSessions = new();
    private readonly List<XrayConfigSession> _configSessions = new();
    private readonly List<OpenVpnSession> _openVpnSessions = new();
    private string? _bindInterface;
    private readonly SystemProxyController _systemProxy = new();
    private readonly object _gate = new();

    private int _mixedPort;
    private int _clashPort;
    private string _clashSecret = "";

    public RunState State => _runner.State;
    public event Action<RunState, string?>? StateChanged;

    public ClashClient? Clash { get; private set; }
    public ProcessMode.ProcessInterceptor? Interceptor { get; private set; }
    public string? ProxyDescription { get; private set; }
    public string? ConfigPath { get; private set; }
    public bool IsDirectChain { get; private set; }

    /// <summary>Where apps should point when the engine is not running (direct chaining to a local proxy).</summary>
    public string? FallbackProxyEndpoint { get; private set; }
    public string FallbackProxyType { get; private set; } = "SOCKS5";

    public int MixedPort => _mixedPort;

    public RouteEngine()
    {
        _runner.StateChanged += (s, d) => StateChanged?.Invoke(s, d);
    }

    /// <summary>
    /// Resolves the route into an ordered list of outbounds. Each entry detours through the
    /// previous one, so a chain of [local, Frankfurt] means: reach Frankfurt's server through
    /// the local proxy, then send everything out from Frankfurt.
    /// </summary>
    public static async Task<(List<RouteHop> hops, string description, string? error)> ResolveRouteAsync(
        AppState state, CancellationToken ct,
        IReadOnlyDictionary<string, (string Host, int Port)>? managed = null)
    {
        var hops = new List<RouteHop>();
        var names = new List<string>();

        switch (state.Route)
        {
            case RouteKind.Direct:
                return (hops, "Direct", null);

            case RouteKind.Node:
            {
                var node = state.ActiveNode;
                if (node is null) return (hops, "", "Pick a server first, or switch to a local proxy.");
                hops.Add(RouteHop.From(node));
                names.Add(node.Name);
                break;
            }

            case RouteKind.External:
            {
                var ext = state.ActiveExternal;
                if (ext is null) return (hops, "", "No local proxy configured.");

                // Tunnels are not local proxies: there is no port to probe. The engine dials
                // WireGuard itself, and OpenVPN is reached through the adapter it creates.
                if (ext.IsWireGuard || ext.IsOpenVpn)
                {
                    hops.Add(RouteHop.From(ext));
                    names.Add(ext.Name);
                    break;
                }

                var (baseHost, basePort) = managed is not null && managed.TryGetValue(ext.Id, out var ep)
                    ? ep
                    : (ext.Host, ext.Port);

                var detected = await ProxyDetector.ProbeAsync(baseHost, basePort, 900, ct).ConfigureAwait(false);
                if (detected is null)
                {
                    return (hops, "",
                        $"Nothing is listening on {baseHost}:{basePort}. Start {ext.Name} first, or pick a server.");
                }

                var resolved = ext.Clone();
                resolved.Host = baseHost;
                resolved.Port = basePort;
                if (string.Equals(ext.Type, "Auto", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext.Type, "Psiphon", StringComparison.OrdinalIgnoreCase))
                    resolved.Type = detected.Type;

                hops.Add(RouteHop.From(resolved));
                names.Add($"{ext.Name} · {baseHost}:{basePort}");
                break;
            }

            default:
                return (hops, "Direct", null);
        }

        // Stacked hops, applied in order.
        foreach (var hop in state.Chain)
        {
            RouteHop? resolvedHop = null;
            string name;

            if (hop.Kind == RouteKind.External)
            {
                var ext = state.Externals.FirstOrDefault(e => e.Id == hop.RefId);
                if (ext is null) continue;

                var (hopHost, hopPort) = managed is not null && managed.TryGetValue(ext.Id, out var hopEp)
                    ? hopEp
                    : (ext.Host, ext.Port);

                var detected = await ProxyDetector.ProbeAsync(hopHost, hopPort, 900, ct).ConfigureAwait(false);
                if (detected is null)
                    return (new List<RouteHop>(), "",
                        $"The chained proxy \"{ext.Name}\" on {hopHost}:{hopPort} is not answering.");

                var resolved = ext.Clone();
                resolved.Host = hopHost;
                resolved.Port = hopPort;
                if (string.Equals(ext.Type, "Auto", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext.Type, "Psiphon", StringComparison.OrdinalIgnoreCase))
                    resolved.Type = detected.Type;

                resolvedHop = RouteHop.From(resolved);
                name = ext.Name;
            }
            else
            {
                var node = state.Nodes.FirstOrDefault(n => n.Id == hop.RefId);
                if (node is null) continue;
                resolvedHop = RouteHop.From(node);
                name = node.Name;
            }

            hops.Add(resolvedHop);
            names.Add(name);
        }

        return (hops, string.Join("  →  ", names), null);
    }

    /// <summary>
    /// Starts every Psiphon entry the route uses. The hop directly above each one becomes its
    /// upstream, which is what lets the tunnel reach Psiphon's servers when a direct
    /// connection to them is blocked.
    /// </summary>
    /// <summary>
    /// Brings up an OpenVPN tunnel when it is the active route, and returns the adapter the
    /// engine should be pinned to.
    /// </summary>
    private async Task<(string? adapter, string? error)> StartOpenVpnAsync(
        AppState state, Settings settings, CancellationToken ct)
    {
        // Use the ACTIVE entry, not "the first OpenVPN in the list". With two OpenVPN
        // profiles saved, FirstOrDefault picked the wrong one and we returned a null
        // adapter without an error — which surfaced later as "the tunnel is not running".
        var tunnel = state.Route == RouteKind.External && state.ActiveExternal is { IsOpenVpn: true } active
            ? active
            : null;
        if (tunnel is null) return (null, null);

        var binary = OpenVpnSession.FindBinary(settings.OpenVpnBinaryPath);
        if (binary is null)
            return (null, "OpenVPN is not installed. Install it, or point RouteScope at openvpn.exe in Settings.");

        var session = new OpenVpnSession();
        var (ok, error) = await session
            .StartAsync(binary, tunnel.ImportedConfigText ?? "", 30, ct,
                tunnel.Username, tunnel.Password)
            .ConfigureAwait(false);

        if (!ok)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return (null, error ?? "OpenVPN did not start.");
        }

        _openVpnSessions.Add(session);
        return (session.AdapterName, null);
    }

    /// <summary>
    /// Should everything reaching the engine use the route, or only matching processes?
    ///
    /// App proxy and Process mode both answer "everything": in Process mode the driver already
    /// decided, and in App proxy the port itself is the opt-in — an app only reaches it because
    /// the user pointed it there. Filtering by process in App proxy silently sent such a
    /// connection out direct, so the chain looked healthy while the traffic never entered it.
    /// </summary>
    public static bool RoutesEverything(CaptureMode mode, AdvancedSettings advanced) => mode switch
    {
        CaptureMode.SystemProxy => true,
        CaptureMode.Process => true,
        CaptureMode.AppProxy => true,
        CaptureMode.Transparent => !advanced.TunOnlyTargets,
        _ => false,
    };

    /// <summary>
    /// Makes one real request through the engine. If this fails the route is not usable, and
    /// starting interception anyway is what turns a bad route into a hung machine.
    /// </summary>
    public static async Task<(bool ok, string detail)> VerifyTunnelAsync(int port, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
                AllowAutoRedirect = true,
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var response = await client
                .GetAsync("http://www.gstatic.com/generate_204", ct)
                .ConfigureAwait(false);

            var code = (int)response.StatusCode;
            return (code is >= 200 and < 400, "HTTP " + code);
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private async Task<(bool ok, string? error)> EnsurePsiphonAsync(
        AppState state, Settings settings, EngineInstaller installer, CancellationToken ct)
    {
        _managed.Clear();

        // The route as an ordered list: base first, then each chain hop.
        var entries = new List<ExternalProxy?>();
        entries.Add(state.Route == RouteKind.External ? state.ActiveExternal : null);

        foreach (var hop in state.Chain)
        {
            entries.Add(hop.Kind == RouteKind.External
                ? state.Externals.FirstOrDefault(e => e.Id == hop.RefId)
                : null);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var ext = entries[i];
            if (ext is null || !ext.NeedsClientSession) continue;

            if (string.Equals(ext.Type, "Xray config", StringComparison.OrdinalIgnoreCase))
            {
                var xray = installer.GetActive(EngineCatalog.Get("xray")!);
                if (xray is null)
                    return (false, $"\"{ext.Name}\" is an Xray config, so Xray-core has to be installed first.");

                if (string.IsNullOrWhiteSpace(ext.CustomConfigJson))
                    return (false, $"\"{ext.Name}\" has no config saved.");

                // The hop above becomes the config's upstream. These configs usually cannot
                // resolve their own server hostname, and the upstream can.
                string? configUpstream = null;
                for (var j = i - 1; j >= 0; j--)
                {
                    var previous = entries[j];
                    if (previous is null) continue;

                    var (upHost, upPort) = _managed.TryGetValue(previous.Id, out var upAssigned)
                        ? upAssigned
                        : (previous.Host, previous.Port);

                    configUpstream = PsiphonConfig.UpstreamUrl(upHost, upPort, previous.Type);
                    break;
                }

                var configSession = new XrayConfigSession();
                var (configOk, configError) = await configSession
                    .StartAsync(xray.ExePath, ext.CustomConfigJson, 20, ct, configUpstream)
                    .ConfigureAwait(false);

                if (!configOk)
                {
                    await configSession.DisposeAsync().ConfigureAwait(false);
                    await StopPsiphonAsync().ConfigureAwait(false);
                    return (false, $"\"{ext.Name}\": {configError}");
                }

                _configSessions.Add(configSession);
                _managed[ext.Id] = ("127.0.0.1", configSession.Port);
                LogBus.Info("engine", $"{ext.Name} is running from its own config on 127.0.0.1:{configSession.Port}");
                continue;
            }

            var binary = PsiphonSession.FindBinary(settings.PsiphonBinaryPath);
            if (binary is null)
            {
                return (false,
                    "psiphon-tunnel-core.exe was not found. Point RouteScope at it in Settings → Engine, " +
                    "or copy it next to RouteScope.exe.");
            }

            // Nearest non-Psiphon hop above this one is the upstream.
            string? upstream = null;
            for (var j = i - 1; j >= 0; j--)
            {
                var previous = entries[j];
                if (previous is null) continue;

                var (host, port) = _managed.TryGetValue(previous.Id, out var assigned)
                    ? assigned
                    : (previous.Host, previous.Port);

                upstream = PsiphonConfig.UpstreamUrl(host, port, previous.Type);
                break;
            }

            var session = new PsiphonSession();
            var (ok, error) = await session.StartAsync(binary, upstream, 60, ct, settings.PsiphonRegion)
                .ConfigureAwait(false);

            if (!ok)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                await StopPsiphonAsync().ConfigureAwait(false);
                return (false, error);
            }

            _psiphonSessions.Add(session);
            _managed[ext.Id] = ("127.0.0.1", session.SocksPort);

            LogBus.Info("psiphon", upstream is null
                ? $"Psiphon tunnel up on 127.0.0.1:{session.SocksPort} (direct)"
                : $"Psiphon tunnel up on 127.0.0.1:{session.SocksPort} through {upstream}");
        }

        return (true, null);
    }

    private async Task StopPsiphonAsync()
    {
        foreach (var session in _psiphonSessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _psiphonSessions.Clear();

        foreach (var session in _configSessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _configSessions.Clear();

        // Stopping OpenVPN removes the route it needed. This runs on every stop path.
        foreach (var session in _openVpnSessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _openVpnSessions.Clear();
        _bindInterface = null;

        _managed.Clear();
    }
    public async Task<StartResult> StartAsync(AppState state, EngineInstaller installer, CancellationToken ct)
    {
        if (State is RunState.Running or RunState.Starting)
            return new StartResult(true);

        var settings = state.Settings;
        _mixedPort = settings.MixedPort;
        _clashPort = settings.ClashApiPort;
        _clashSecret = settings.ClashSecret;
        IsDirectChain = false;
        FallbackProxyEndpoint = null;

        if ((state.Mode == CaptureMode.Transparent || state.Mode == CaptureMode.Process)
            && !Elevation.IsElevated)
            return new StartResult(false,
                state.Mode == CaptureMode.Process
                    ? "Process mode needs administrator rights to reach the network filter driver. " +
                      "RouteScope can restart itself elevated."
                    : "Transparent mode needs administrator rights. RouteScope can restart itself elevated.",
                "needs-admin");

        if (!ProxyDetector.IsPortFree(_mixedPort))
            return new StartResult(false,
                $"Port {_mixedPort} is already in use. Change the proxy port in Settings → Network.",
                "port-busy");

        if (settings.Advanced.EnableClashApi && _clashPort > 0 && !ProxyDetector.IsPortFree(_clashPort))
            return new StartResult(false,
                $"Dashboard port {_clashPort} is already in use. Change it in Settings → Network.",
                "port-busy");

        // Start any managed clients (Psiphon) before resolving the route, because their local
        // port only exists once they are running.
        await OpenVpnSession.CleanStaleAsync().ConfigureAwait(false);

        var (adapter, openVpnError) = await StartOpenVpnAsync(state, settings, ct).ConfigureAwait(false);
        if (openVpnError is not null) return new StartResult(false, openVpnError, "openvpn");
        _bindInterface = adapter;

        var (psiphonOk, psiphonError) = await EnsurePsiphonAsync(state, settings, installer, ct).ConfigureAwait(false);
        if (!psiphonOk) return new StartResult(false, psiphonError, "psiphon");

        var (hops, description, error) = await ResolveRouteAsync(state, ct, _managed).ConfigureAwait(false);
        if (error is not null) return new StartResult(false, error, "route");
        ProxyDescription = description;

        var choice = EngineSelector.Choose(state, hops, installer);
        if (choice.Note is not null) LogBus.Info("engine", choice.Note);

        var def = choice.Engine == EngineBackend.Xray
            ? EngineCatalog.Get("xray")!
            : EngineCatalog.SingBox;
        var engine = installer.GetActive(def);

        // Zero-download path: chaining to a local proxy needs no engine at all in App Proxy mode.
        if (engine is null)
        {
            if (state.Route == RouteKind.External && state.ActiveExternal is not null &&
                state.Mode == CaptureMode.AppProxy)
            {
                var ext = state.ActiveExternal;
                var detected = await ProxyDetector.ProbeAsync(ext.Host, ext.Port, 900, ct).ConfigureAwait(false);
                if (detected is null)
                    return new StartResult(false, $"Nothing is listening on {ext.Endpoint}.", "route");

                IsDirectChain = true;
                FallbackProxyEndpoint = $"{ext.Host}:{ext.Port}";
                FallbackProxyType = detected.Type;
                ProxyDescription = $"{ext.Name} · {ext.Endpoint} (direct)";
                LogBus.Info("engine", "Running in direct-chain mode (no local engine needed).");
                return new StartResult(true, DirectChain: true);
            }

            return new StartResult(false,
                "The sing-box engine is not installed yet. RouteScope can download it for you.",
                "engine-missing");
        }

        // Expand launcher/game child processes into extra path rules.
        var routeEverything = RoutesEverything(state.Mode, state.Settings.Advanced);

        var (childPaths, childNames) = routeEverything
            ? (new List<string>(), new List<string>())
            : ProcessScanner.ExpandChildProcesses(state.EnabledTargets);

        if (childPaths.Count > 0)
            LogBus.Info("engine", $"Matched {childPaths.Count} child process(es) of your targets.");

        // A local proxy client must never be tunnelled into itself.
        var excluded = new List<string>();
        if (state.Route == RouteKind.External && state.ActiveExternal is not null)
        {
            var owner = ProcessScanner.FindListenerProcess(state.ActiveExternal.Port);
            if (owner?.ExePath is { Length: > 0 } exePath)
            {
                excluded.Add("(?i)^" + System.Text.RegularExpressions.Regex.Escape(exePath) + "$");
                LogBus.Info("engine",
                    $"Keeping {owner.Name} (the proxy on port {state.ActiveExternal.Port}) out of the tunnel.");
            }
        }

        var ctx = new BuildContext
        {
            BindInterface = _bindInterface,
            State = state,
            ProxyHops = hops,
            ProxyDescription = description,
            ExtraProcessPathRegexes = childPaths,
            ExtraProcessNames = childNames,
            ExcludedProcessRegexes = excluded,
            IncludeTun = state.Mode == CaptureMode.Transparent,
            RouteEverything = routeEverything,
            MixedPort = _mixedPort,
            ClashApiPort = _clashPort,
            ClashSecret = _clashSecret,
        };

        string config;
        try
        {
            config = choice.Engine == EngineBackend.Xray
                ? XrayConfigBuilder.Build(ctx)
                : SingBoxConfigBuilder.Build(ctx);
        }
        catch (Exception ex)
        {
            return new StartResult(false, "Could not build the configuration: " + ex.Message, "config");
        }

        AppPaths.EnsureCreated();
        ConfigPath = AppPaths.RuntimeConfigFile;
        // sing-box refuses a config that starts with a UTF-8 BOM.
        File.WriteAllText(ConfigPath, config, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (settings.Advanced.LogLevel is "debug" or "trace")
            LogBus.Debug("config", config);

        var workDir = Path.GetDirectoryName(engine.ExePath) ?? AppPaths.EngineDir(def.Id);
        _runner.VerboseLogging = settings.Advanced.VerboseEngineLog;
        var (ok, startError) = await _runner.StartAsync(
            engine.ExePath,
            $"run -c \"{ConfigPath}\"",
            workDir,
            _mixedPort,
            TimeSpan.FromSeconds(Math.Max(5, settings.Advanced.EngineStartTimeoutSeconds)),
            ct).ConfigureAwait(false);

        if (!ok) return new StartResult(false, startError, "engine-start");

        if (choice.Engine == EngineBackend.Xray)
        {
            // Xray has no Clash API, so live traffic stats are unavailable in this mode.
            LogBus.Info("engine", "Xray-core is running: traffic totals come from the relay; the live connection list needs sing-box.");
        }
        else if (settings.Advanced.EnableClashApi)
        {
            Clash = new ClashClient("127.0.0.1", _clashPort, _clashSecret);
            Clash.Start();
        }

        if (state.Mode == CaptureMode.SystemProxy)
        {
            if (!_systemProxy.Apply(_mixedPort))
                LogBus.Warn("system-proxy", "Could not change the Windows proxy settings.");
        }

        if (state.Mode == CaptureMode.Process)
        {
            var excludedPaths = new List<string>();

            // The engine's own process is the one thing that must never be intercepted: if its
            // outbound sockets are redirected back into its own inbound, traffic loops forever.
            // For a local proxy we can find it by its listening port. For a tunnel (WireGuard /
            // OpenVPN) the external "port" is a REMOTE server port, so that lookup finds nothing
            // — which is exactly why tunnel routes looped. Use the process RouteScope launched.
            if (_runner.ExePath is { Length: > 0 } enginePath)
                excludedPaths.Add(enginePath);

            if (state.ActiveExternal is not null)
            {
                var owner = ProcessScanner.FindListenerProcess(state.ActiveExternal.Port);
                if (owner?.ExePath is { Length: > 0 } ownerPath) excludedPaths.Add(ownerPath);
            }
            if (Environment.ProcessPath is { Length: > 0 } selfPath) excludedPaths.Add(selfPath);

            // A route that opens a port but carries no traffic would send every intercepted
        // connection into a black hole, and a machine full of hung connections can wedge.
        // Prove the route works before anything is redirected into it.
        var (routeWorks, routeDetail) = await VerifyTunnelAsync(_mixedPort, ct).ConfigureAwait(false);
        if (!routeWorks)
        {
            await _runner.StopAsync().ConfigureAwait(false);
            return new StartResult(false,
                "The route started, but nothing can pass through it (" + routeDetail + "). " +
                "Stopped before redirecting any app.", "dead-route");
        }

        var interceptor = new ProcessMode.ProcessInterceptor();
            var excludedPids = new List<uint>();
            if (_runner.ProcessId is { } enginePid) excludedPids.Add(enginePid);

            // A tunnel's own traffic is UDP, and the all-process NF_FILTER UDP rule is the
            // highest-rate callback path there is. Forcing it off for tunnels removes the
            // self-amplifying loop entirely; the tunnel still carries the selected apps' TCP.
            var isTunnel = state.ActiveExternal is { IsWireGuard: true } or { IsOpenVpn: true };
            var enableUdp = state.Settings.Advanced.ProcessModeUdp && !isTunnel;
            if (isTunnel)
                LogBus.Warn("engine",
                    "A tunnel in Process mode uses the network filter driver. If anything stalls, " +
                    "switch to System proxy mode, which does not use the driver.");
            if (isTunnel && state.Settings.Advanced.ProcessModeUdp)
                LogBus.Info("engine", "Tunnel route: UDP filtering is left off to protect the driver callback path.");

            var (interceptorOk, interceptError) = interceptor.Start(
                state.EnabledTargets, _mixedPort, enableUdp,
                excludedPaths, excludedPids);

            if (!interceptorOk)
            {
                await _runner.StopAsync().ConfigureAwait(false);
                return new StartResult(false, interceptError, "process-mode");
            }

            Interceptor = interceptor;

            // Verify AGAIN with the rules live. The first check proves the route works; this
            // one proves the filter did not break it. If the driver wedges loopback, tear the
            // rules down immediately rather than leaving the machine in that state.
            try
            {
                using var afterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                afterCts.CancelAfter(TimeSpan.FromSeconds(6));

                var (afterOk, afterDetail) = await VerifyTunnelAsync(_mixedPort, afterCts.Token)
                    .ConfigureAwait(false);

                if (!afterOk)
                {
                    LogBus.Warn("engine", "Route broke once the filter was active: " + afterDetail);
                    await interceptor.DisposeAsync().ConfigureAwait(false);
                    Interceptor = null;
                    await _runner.StopAsync().ConfigureAwait(false);
                    return new StartResult(false,
                        "The network filter broke the route (" + afterDetail + "). " +
                        "Stopped and removed the filter rules. Try System proxy or Whole computer mode instead.",
                        "filter-broke-route");
                }
            }
            catch (OperationCanceledException)
            {
                await interceptor.DisposeAsync().ConfigureAwait(false);
                Interceptor = null;
                await _runner.StopAsync().ConfigureAwait(false);
                return new StartResult(false,
                    "The network filter stalled the route. Stopped and removed the filter rules. " +
                    "Try System proxy or Whole computer mode instead.",
                    "filter-stalled");
            }
        }

        LogBus.Info("engine", $"Routing through: {description}");
        return new StartResult(true);
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_systemProxy.IsApplied) _systemProxy.Revert();
        }

        await StopPsiphonAsync().ConfigureAwait(false);

        if (Interceptor is not null)
        {
            try { Interceptor.Stop(); } catch { }
            try { await Interceptor.DisposeAsync().ConfigureAwait(false); } catch { }
            Interceptor = null;
        }

        if (Clash is not null)
        {
            try { Clash.Dispose(); } catch { }
            Clash = null;
        }

        IsDirectChain = false;
        await _runner.StopAsync().ConfigureAwait(false);
        ProxyDescription = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _runner.DisposeAsync().ConfigureAwait(false);
    }
}
