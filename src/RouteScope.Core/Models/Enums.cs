namespace RouteScope.Core.Models;

/// <summary>What a target points at.</summary>
public enum TargetKind
{
    /// <summary>A directory. Every executable that runs from inside it (recursively) is proxied.</summary>
    Folder = 0,
    /// <summary>A single executable file.</summary>
    Application = 1,
    /// <summary>An image name such as <c>telegram.exe</c>, regardless of where it lives.</summary>
    ProcessName = 2,
}

/// <summary>How RouteScope captures traffic.</summary>
public enum CaptureMode
{
    /// <summary>No admin. RouteScope hands apps a proxy via environment/launch flags and a local port.</summary>
    AppProxy = 0,
    /// <summary>System wide TUN adapter. Captures everything; needs administrator.</summary>
    Transparent = 1,
    /// <summary>Windows system proxy (WinINet). Whole user session.</summary>
    SystemProxy = 2,
    /// <summary>
    /// Selective: the network filter driver reports connect() calls and only the selected
    /// processes are redirected. Works for native apps with no proxy support; needs admin
    /// once for the driver.
    /// </summary>
    Process = 3,
}

/// <summary>Where proxied traffic is sent.</summary>
public enum RouteKind
{
    /// <summary>An imported/created node, run by a local engine.</summary>
    Node = 0,
    /// <summary>An already-running local proxy, e.g. v2rayN on 127.0.0.1:10808.</summary>
    External = 1,
    /// <summary>No proxy (everything direct).</summary>
    Direct = 2,
}

public enum NodeProtocol
{
    Vless = 0,
    Vmess = 1,
    Trojan = 2,
    Shadowsocks = 3,
    Hysteria2 = 4,
    Tuic = 5,
    Socks = 6,
    Http = 7,
    WireGuard = 8,
    AnyTls = 9,
}

/// <summary>Which engine binary executes the configuration.</summary>
public enum EngineKind
{
    SingBox = 0,
    Xray = 1,
    Mihomo = 2,
}

public enum TransportKind
{
    Tcp = 0,
    WebSocket = 1,
    Grpc = 2,
    HttpUpgrade = 3,
    Http = 4,
    Quic = 5,
}

public enum SecurityKind
{
    None = 0,
    Tls = 1,
    Reality = 2,
}

/// <summary>What the user is told about a running state.</summary>
public enum RunState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Faulted = 4,
}
