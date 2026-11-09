namespace RouteScope.Core.Models;

/// <summary>A single proxy server the user imported or typed in.</summary>
public sealed class ProxyNode : NotifyBase
{
    private string _name = "New server";
    private string _server = "";
    private int _port = 443;
    private int? _lastLatencyMs;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public NodeProtocol Protocol { get; set; } = NodeProtocol.Vless;

    public string Server
    {
        get => _server;
        set
        {
            if (Set(ref _server, value)) Raise(nameof(Endpoint));
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (Set(ref _port, value)) Raise(nameof(Endpoint));
        }
    }

    // Credentials ---------------------------------------------------------
    public string? Uuid { get; set; }
    public string? Password { get; set; }
    public string? Username { get; set; }
    public string? Method { get; set; }          // shadowsocks cipher
    public string? Flow { get; set; }            // xtls-rprx-vision

    // TLS -----------------------------------------------------------------
    public SecurityKind Security { get; set; } = SecurityKind.None;
    public string? Sni { get; set; }
    public List<string> Alpn { get; set; } = new();
    public bool AllowInsecure { get; set; }
    public string? Fingerprint { get; set; }
    public string? PublicKey { get; set; }       // reality
    public string? ShortId { get; set; }         // reality
    public string? SpiderX { get; set; }

    // Transport -----------------------------------------------------------
    public TransportKind Transport { get; set; } = TransportKind.Tcp;
    public string? Path { get; set; }
    public string? Host { get; set; }
    public string? ServiceName { get; set; }     // grpc
    public string? QuicSecurity { get; set; }
    public string? QuicKey { get; set; }
    public string? HeaderType { get; set; }

    // Extras --------------------------------------------------------------
    public int? Mux { get; set; }
    public string? SubscriptionId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Query parameters this app does not model (ech, echOutbound, …). Kept verbatim so
    /// copying a link back out does not silently drop settings the server relies on.
    /// </summary>
    public Dictionary<string, string> Extra { get; set; } = new();

    public int? LastLatencyMs
    {
        get => _lastLatencyMs;
        set
        {
            if (Set(ref _lastLatencyMs, value)) RaiseAll(nameof(LatencyLabel), nameof(LatencyBrushKey));
        }
    }

    public string? RawLink { get; set; }

    private bool _isActive;

    /// <summary>True when this is the currently selected destination.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public string LatencyLabel => _lastLatencyMs switch
    {
        null => "—",
        < 0 => "timeout",
        var ms => $"{ms} ms",
    };

    private int? _realPingMs;

    /// <summary>Time to open a real tunnel through this server to the internet, in ms.</summary>
    public int? RealPingMs
    {
        get => _realPingMs;
        set
        {
            if (Set(ref _realPingMs, value)) RaiseAll(nameof(RealPingLabel), nameof(RealPingBrushKey));
        }
    }

    public string RealPingLabel => _realPingMs switch
    {
        null => "ping",
        < 0 => "no route",
        var ms => $"{ms} ms",
    };

    public string LatencyBrushKey => _lastLatencyMs switch
    {
        null => "B.Text.Tertiary",
        < 0 => "B.Danger",
        > 300 => "B.Warning",
        _ => "B.Success",
    };

    public string RealPingBrushKey => _realPingMs switch
    {
        null => "B.Text.Tertiary",
        < 0 => "B.Danger",
        > 700 => "B.Warning",
        _ => "B.Success",
    };

    public string Endpoint => Port is > 0 ? $"{Server}:{Port}" : Server;

    public string ProtocolLabel => Protocol switch
    {
        NodeProtocol.Vless => "VLESS",
        NodeProtocol.Vmess => "VMess",
        NodeProtocol.Trojan => "Trojan",
        NodeProtocol.Shadowsocks => "Shadowsocks",
        NodeProtocol.Hysteria2 => "Hysteria2",
        NodeProtocol.Tuic => "TUIC",
        NodeProtocol.Socks => "SOCKS",
        NodeProtocol.Http => "HTTP",
        NodeProtocol.WireGuard => "WireGuard",
        NodeProtocol.AnyTls => "AnyTLS",
        _ => Protocol.ToString(),
    };

    public string SecurityLabel => Security switch
    {
        SecurityKind.Reality => "REALITY",
        SecurityKind.Tls => "TLS",
        _ => "None",
    };

    public ProxyNode Clone() => (ProxyNode)MemberwiseClone();
}

/// <summary>
/// An already-running proxy on this machine — most importantly v2rayN on
/// 127.0.0.1:10808. RouteScope chains through it instead of re-implementing it.
/// </summary>
public sealed class ExternalProxy : NotifyBase
{
    private string _name = "v2rayN";
    private string _host = "127.0.0.1";
    private int _port = 10808;
    private string _type = "Auto";
    private bool _isReachable;
    private string? _detectedType;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set
        {
            if (Set(ref _host, value)) Raise(nameof(Endpoint));
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (Set(ref _port, value)) Raise(nameof(Endpoint));
        }
    }

    /// <summary>SOCKS5, HTTP or Auto. Auto probes both.</summary>
    public string Type
    {
        get => _type;
        set => Set(ref _type, value);
    }

    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool AutoDetected { get; set; }
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Set by the detector: is something answering on this port right now?</summary>
    public bool IsReachable
    {
        get => _isReachable;
        set
        {
            if (Set(ref _isReachable, value)) RaiseAll(nameof(StatusText), nameof(StatusBrushKey));
        }
    }

    public string? DetectedType
    {
        get => _detectedType;
        set
        {
            if (Set(ref _detectedType, value)) Raise(nameof(StatusText));
        }
    }

    private bool _isActive;

    /// <summary>True when this is the currently selected destination.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>
    /// A complete Xray config imported from a subscription, run by RouteScope as-is.
    /// </summary>
    public string? CustomConfigJson { get; set; }

    /// <summary>Set when this entry came from a subscription, so refreshes can replace it.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>The raw text of an imported .conf / .ovpn file, kept so it can be edited.</summary>
    public string? ImportedConfigText { get; set; }

    /// <summary>True when RouteScope starts this client itself instead of expecting it to be running.</summary>
    public bool IsManaged =>
        IsPsiphon || IsXrayConfig || IsOpenVpn || IsWireGuard;

    /// <summary>True only for entries RouteScope must launch as a separate client process
    /// (Psiphon, or a whole Xray config). Tunnels are started elsewhere — OpenVPN by
    /// OpenVpnSession, WireGuard by the engine itself — and must never be handed to the
    /// Psiphon startup loop.</summary>
    public bool NeedsClientSession => IsPsiphon || IsXrayConfig;

    public bool IsPsiphon => string.Equals(Type, "Psiphon", StringComparison.OrdinalIgnoreCase);

    public bool IsXrayConfig => string.Equals(Type, "Xray config", StringComparison.OrdinalIgnoreCase);
    /// <summary>A WireGuard tunnel the engine dials itself — no process, no admin.</summary>
    public bool IsWireGuard => string.Equals(Type, "WireGuard", StringComparison.OrdinalIgnoreCase);

    /// <summary>An OpenVPN tunnel RouteScope runs and points the engine at.</summary>
    public bool IsOpenVpn => string.Equals(Type, "OpenVPN", StringComparison.OrdinalIgnoreCase);

    public string Endpoint => IsManaged && !IsOpenVpn && !IsWireGuard
        ? "started by RouteScope"
        : $"{Host}:{Port}";

    public string TypeLabel => Type;

    public string StatusText => IsPsiphon
        ? "Psiphon · tunnel on demand"
        : IsXrayConfig
            ? "Xray config · starts with routing"
            : IsOpenVpn
                ? "OpenVPN · starts with routing"
                : IsWireGuard
                    ? "WireGuard · runs in the engine"
                    : IsReachable
                        ? $"Running · {DetectedType ?? Type}"
                        : "Not detected";

    public string StatusBrushKey => IsReachable ? "B.Success" : "B.Text.Tertiary";

    private int? _realPingMs;

    /// <summary>Time to open a real tunnel through this proxy to the internet, in ms.</summary>
    public int? RealPingMs
    {
        get => _realPingMs;
        set
        {
            if (Set(ref _realPingMs, value)) RaiseAll(nameof(RealPingLabel), nameof(RealPingBrushKey));
        }
    }

    public string RealPingLabel => _realPingMs switch
    {
        null => "ping",
        < 0 => "no route",
        var ms => $"{ms} ms",
    };

    public string LatencyBrushKey => _realPingMs switch
    {
        null => "B.Text.Tertiary",
        < 0 => "B.Danger",
        > 700 => "B.Warning",
        _ => "B.Success",
    };

    public string RealPingBrushKey => LatencyBrushKey;

    private int? _pingMs;

    /// <summary>Plain TCP reachability of the proxy's own port.</summary>
    public int? PingMs
    {
        get => _pingMs;
        set
        {
            if (Set(ref _pingMs, value)) RaiseAll(nameof(PingLabel), nameof(PingBrushKey));
        }
    }

    public string PingLabel => IsManaged
        ? "on demand"
        : _pingMs switch
        {
            null => "ping",
            < 0 => "offline",
            var ms => $"{ms} ms",
        };

    public string PingBrushKey => IsManaged
        ? "B.Text.Tertiary"
        : _pingMs switch
        {
            null => "B.Text.Tertiary",
            < 0 => "B.Danger",
            > 300 => "B.Warning",
            _ => "B.Success",
        };

    public ExternalProxy Clone() => (ExternalProxy)MemberwiseClone();
}

/// <summary>One-click bundle of targets + route + capture mode.</summary>
public sealed class Preset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New preset";
    public string? Glyph { get; set; }
    public List<string> TargetIds { get; set; } = new();
    public RouteKind Route { get; set; } = RouteKind.External;
    public string? NodeId { get; set; }
    public string? ExternalId { get; set; }
    public CaptureMode Mode { get; set; } = CaptureMode.Transparent;
    public bool BuiltIn { get; set; }
}

public sealed class DnsSettings
{
    /// <summary>system | proxy | direct | custom</summary>
    public string Mode { get; set; } = "proxy";
    public List<string> RemoteServers { get; set; } = new() { "https://1.1.1.1/dns-query", "https://dns.google/dns-query" };
    public List<string> DirectServers { get; set; } = new() { "223.5.5.5", "1.1.1.1" };
    public bool PreferIpv4 { get; set; } = true;
    public bool EnableFakeIp { get; set; }
}

public sealed class AdvancedSettings
{
    public string LogLevel { get; set; } = "info";
    public int Mtu { get; set; } = 9000;
    /// <summary>gvisor | system | mixed</summary>
    public string TunStack { get; set; } = "mixed";
    /// <summary>
    /// Windows firewall-style "strict route". Off by default: it blocks traffic that does
    /// not leave through the tunnel, which also blocks the upstream proxy client's own
    /// connection and collapses the chain.
    /// </summary>
    public bool StrictRoute { get; set; }
    public bool AutoRoute { get; set; } = true;
    public bool BypassLan { get; set; } = true;
    /// <summary>Extra route rules appended verbatim (JSON array of sing-box rules).</summary>
    public string ExtraRouteRulesJson { get; set; } = "";
    /// <summary>Extra outbounds appended verbatim (JSON array).</summary>
    public string ExtraOutboundsJson { get; set; } = "";
    /// <summary>Merged into the generated document last (JSON object).</summary>
    public string ConfigOverrideJson { get; set; } = "";
    public int TunAddressPrefix { get; set; } = 19; // 172.19.0.1/30
    public bool EnableClashApi { get; set; } = true;
    public int EngineStartTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Process mode: also intercept UDP. Off by default — the driver's UDP endpoints are
    /// short-lived, and posting to one that has just closed is a use-after-free inside the
    /// kernel driver. Turn it on only if a game needs it.
    /// </summary>
    public bool ProcessModeUdp { get; set; }

    /// <summary>Show every engine line, including the routine per-connection ones.</summary>
    public bool VerboseEngineLog { get; set; }


    /// <summary>
    /// Advanced: make whole-computer mode filter by the target list instead of routing
    /// everything. Off by default, because a globally captured network is not selective
    /// no matter what the rules say afterwards.
    /// </summary>
    public bool TunOnlyTargets { get; set; }
}

public sealed class Settings
{
    /// <summary>system | dark | light</summary>
    public string Theme { get; set; } = "dark";
    public string Accent { get; set; } = "violet";

    public int MixedPort { get; set; } = 47890;
    public int ClashApiPort { get; set; } = 47891;
    public string ClashSecret { get; set; } = Guid.NewGuid().ToString("N")[..16];

    public bool StartEngineOnLaunch { get; set; }
    public bool LaunchAtLogin { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    /// <summary>What the close button does: ask, tray, or exit.</summary>
    public string CloseAction { get; set; } = "ask";
    public bool NotifyOnStateChange { get; set; } = true;

    public bool AutoUpdateEngines { get; set; } = true;
    public bool AutoUpdateApp { get; set; } = true;
    public bool CheckUpdatesOnLaunch { get; set; } = true;
    /// <summary>GitHub mirror prefix used when direct download fails. Empty disables.</summary>
    public string Mirror { get; set; } = "https://ghproxy.net/";

    /// <summary>Which core to run: "auto", "singbox" or "xray".</summary>
    public string PreferredEngine { get; set; } = "auto";

    /// <summary>Where psiphon-tunnel-core.exe lives. Empty means "find it automatically".</summary>
    public string PsiphonBinaryPath { get; set; } = "";

    /// <summary>Where openvpn.exe lives. Empty means "find it in Program Files".</summary>
    public string OpenVpnBinaryPath { get; set; } = "";

    /// <summary>Two-letter country Psiphon should exit from. Empty means "let Psiphon choose".</summary>
    public string PsiphonRegion { get; set; } = "";

    /// <summary>Fetch core downloads through the route instead of going direct.</summary>
    public bool DownloadViaProxy { get; set; }

    public bool OnboardingCompleted { get; set; }
    public bool AdvancedUnlocked { get; set; }
    public string? LastEngineVersion { get; set; }

    public DnsSettings Dns { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
}

