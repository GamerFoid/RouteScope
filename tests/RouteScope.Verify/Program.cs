using System.Diagnostics;
using System.Text;
using RouteScope.Core;
using RouteScope.Core.Engine;
using RouteScope.Core.Interop;
using RouteScope.Core.Models;
using RouteScope.Core.Nodes;
using RouteScope.Core.Psiphon;
using RouteScope.Core.Runtime;
using RouteScope.Core.SystemTools;
using RouteScope.Core.Updates;

if (args.Contains("relay", StringComparer.OrdinalIgnoreCase))
    return await RouteScope.Verify.RelayTest.RunAsync();

if (args.Contains("probe", StringComparer.OrdinalIgnoreCase))
    return RouteScope.Verify.Probe.Run();

if (args.Contains("e2e", StringComparer.OrdinalIgnoreCase))
    return await RouteScope.Verify.E2E.RunAsync();

// Dev helper: write the normalized .ovpn so it can be fed to a real openvpn.exe.
var normIndex = Array.FindIndex(args, a => a == "--normalize");
if (normIndex >= 0 && args.Length >= normIndex + 3)
{
    var source = await File.ReadAllTextAsync(args[normIndex + 1]);
    var changes = new List<string>();
    var normalized = OpenVpnConfig.Normalize(source, changes.Add);
    await File.WriteAllTextAsync(args[normIndex + 2], normalized, new UTF8Encoding(false));
    foreach (var change in changes) Console.WriteLine("change: " + change);
    Console.WriteLine("wrote " + args[normIndex + 2]);
    return 0;
}

var failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  — " + detail)}");
    Console.ResetColor();
    if (!ok) failures++;
}

// ---------------------------------------------------------------- link parsing
Console.WriteLine("\n== share link parsing ==");
var samples = new[]
{
    "vless://3f9a1c1e-1111-2222-3333-444455556666@example.com:443?type=ws&security=tls&sni=example.com&path=%2Fws&host=example.com&fp=chrome#My%20Node",
    "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
        """{"v":"2","ps":"VM Test","add":"1.2.3.4","port":"8080","id":"3f9a1c1e-1111-2222-3333-444455556666","aid":"0","net":"ws","path":"/p","host":"h.com","tls":"tls","sni":"h.com"}""")),
    "trojan://secretpass@trojan.example.com:443?sni=trojan.example.com#Trojan%20X",
    "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:password123")) + "@ss.example.com:8388#SS%20Node",
    "hysteria2://pw123@hy.example.com:8443?sni=hy.example.com&obfs-password=obfspw#HY2",
    "tuic://3f9a1c1e-1111-2222-3333-444455556666:pw@tuic.example.com:443?congestion_control=bbr#TUIC",
    "socks://user:pass@127.0.0.1:1080#Local",
};

foreach (var link in samples)
{
    var node = NodeLinkParser.Parse(link);
    Check($"parse {link[..Math.Min(link.IndexOf(':'), 20)]}",
        node is not null && !string.IsNullOrWhiteSpace(node.Server) && node.Port > 0,
        node is null ? "returned null" : $"{node.ProtocolLabel} {node.Server}:{node.Port} name='{node.Name}'");
}

var many = NodeLinkParser.ParseMany(string.Join('\n', samples));
Check("parse subscription batch", many.Count == samples.Length, $"{many.Count}/{samples.Length}");

// ------------------------------------------------------------ config building
Console.WriteLine("\n== config generation ==");
var state = new AppState();
state.Targets.Add(new ProxyTarget { Kind = TargetKind.Folder, Value = @"C:\Games\MyGame", DisplayName = "My Game" });
state.Targets.Add(new ProxyTarget { Kind = TargetKind.Application, Value = @"C:\Program Files\Telegram\Telegram.exe" });
state.Targets.Add(new ProxyTarget { Kind = TargetKind.ProcessName, Value = "discord.exe" });
state.Externals.Add(new ExternalProxy { Name = "local", Host = "127.0.0.1", Port = 10808 });
state.ActiveExternalId = state.Externals[0].Id;
state.Route = RouteKind.External;
state.Mode = CaptureMode.Transparent;

var externalHop = RouteHop.From(state.Externals[0]);
var excluded = new List<string> { @"(?i)^C:\\Program Files\\Proxy\\Proxy.exe$" };

foreach (var mode in new[] { CaptureMode.AppProxy, CaptureMode.Transparent, CaptureMode.SystemProxy })
{
    state.Mode = mode;
    // Use the production mapping, not a copy of it: a test that re-implements the rule cannot
    // catch the rule being wrong.
    var routeEverything = RouteEngine.RoutesEverything(mode, state.Settings.Advanced);
    var json = SingBoxConfigBuilder.Build(new BuildContext
    {
        State = state,
        ProxyHops = new List<RouteHop> { externalHop },
        IncludeTun = mode == CaptureMode.Transparent,
        RouteEverything = routeEverything,
        ExcludedProcessRegexes = excluded,
        MixedPort = 47890,
        ClashApiPort = 47891,
        ClashSecret = "test",
    });

    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"cfg-{mode}.json"), json, new UTF8Encoding(false));

    Check($"build {mode}", json.Contains("\"server_port\": 10808"), $"{json.Length} chars");
    Check($"{mode}: proxy client kept out of the tunnel", json.Contains("Proxy.exe"));

    if (mode == CaptureMode.AppProxy)
    {
        // The port IS the opt-in: everything that reaches it uses the route.
        Check("app proxy: everything reaching the port is proxied",
            json.Contains("\"final\": \"proxy\""));
        Check("app proxy: no tun adapter", !json.Contains("\"type\": \"tun\""));
    }

    if (mode == CaptureMode.Transparent)
    {
        Check("whole computer: tun adapter present", json.Contains("\"type\": \"tun\""));
        Check("whole computer: strict_route is off", json.Contains("\"strict_route\": false"));
        Check("whole computer: everything routed to the proxy", json.Contains("\"final\": \"proxy\""));
        Check("whole computer: target list ignored", !json.Contains("MyGame"));
    }

    if (mode == CaptureMode.SystemProxy)
    {
        Check("system proxy: no tun adapter", !json.Contains("\"type\": \"tun\""));
        Check("system proxy: everything routed to the proxy", json.Contains("\"final\": \"proxy\""));
    }
}

// ------------------------------------------------------------ chained route
Console.WriteLine("\n== proxy chaining ==");
var chainState = new AppState();
chainState.Externals.Add(new ExternalProxy { Name = "local", Host = "127.0.0.1", Port = 10808, Type = "SOCKS5" });
chainState.ActiveExternalId = chainState.Externals[0].Id;
chainState.Route = RouteKind.External;
chainState.Mode = CaptureMode.Process;
chainState.Chain.Add(new ChainHop
{
    Kind = RouteKind.External,
    RefId = "second-hop",
    DisplayName = "Second hop",
});
chainState.Externals.Add(new ExternalProxy { Id = "second-hop", Name = "Second hop", Host = "127.0.0.1", Port = 2801, Type = "SOCKS5" });

var chainHops = new List<RouteHop>
{
    RouteHop.From(chainState.Externals[0]),
    RouteHop.From(chainState.Externals[1]),
};

var chainJson = SingBoxConfigBuilder.Build(new BuildContext
{
    State = chainState,
    ProxyHops = chainHops,
    RouteEverything = true,
    MixedPort = 47890,
    ClashApiPort = 47891,
    ClashSecret = "test",
});
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cfg-Chain.json"), chainJson, new UTF8Encoding(false));

Check("chain: second hop detours through the first", chainJson.Contains("\"detour\": \"chain-0\""));
Check("chain: exit hop is tagged proxy", chainJson.Contains("\"tag\": \"proxy\""));
Check("chain: both hops present", chainJson.Contains("\"server_port\": 10808") && chainJson.Contains("\"server_port\": 2801"));
Check("chain: traffic goes to the exit hop", chainJson.Contains("\"final\": \"proxy\""));

// ------------------------------------------------------------ local proxy scan
Console.WriteLine("\n== local proxy scan ==");
{
    var probeListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    probeListener.Start();
    var probePort = ((System.Net.IPEndPoint)probeListener.LocalEndpoint).Port;
    var stop = false;

    _ = Task.Run(async () =>
    {
        while (!stop)
        {
            try
            {
                var client = await probeListener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buf = new byte[3];
                        await stream.ReadAsync(buf).ConfigureAwait(false);
                        await stream.WriteAsync(new byte[] { 0x05, 0x00 }).ConfigureAwait(false);
                    }
                    catch { }
                    finally { client.Close(); }
                });
            }
            catch { return; }
        }
    });

    var scanned = await ProxyDetector.ScanAsync(400);
    var hit = scanned.FirstOrDefault(p => p.Port == probePort);
    Check($"scan finds a SOCKS5 on port {probePort} (not a known port)", hit is not null && hit.Type == "SOCKS5");
    Check("scan reports what it found", scanned.Count > 0);

    stop = true;
    probeListener.Stop();
}

// --------------------------------------------------------- link round-trips
Console.WriteLine("\n== share link round-trip ==");
{
    // Shadowsocks, SIP002 form
    const string ssLink =
        "ss://eGNoYWNoYTIwLWlldGYtcG9seTEzMDU6UXRycUZpU05INVI4SGM0ZnZOL2R6LzRGQm1yc2lwcTVpRGMxalloODlHdz0@sb47.refreshping.com:443?#ersvsquisite27";

    var ssNode = NodeLinkParser.Parse(ssLink);
    Check("ss: parsed", ssNode is not null);
    if (ssNode is not null)
    {
        Check("ss: cipher", ssNode.Method == "xchacha20-ietf-poly1305", ssNode.Method);
        Check("ss: host", ssNode.Server == "sb47.refreshping.com", ssNode.Server);
        Check("ss: port", ssNode.Port == 443);

        var rewritten = NodeLinkWriter.ToLink(ssNode);
        var again = NodeLinkParser.Parse(rewritten);
        Check("ss: round-trips through a new link", again is not null);
        Check("ss: password survives", again?.Password == ssNode.Password);
        Check("ss: cipher survives", again?.Method == ssNode.Method);
        Check("ss: host survives", again?.Server == ssNode.Server);
    }

    // VLESS with TLS, WS, ALPN and two parameters we do not model
    const string vlessLink =
        "vless://5fee0a88-2abb-4bba-b72a-2445aa11c179@www.speedtest.net:443?encryption=none&security=tls" +
        "&sni=tlBN75UN37u-7dOkpzSZ84n3t4DAowyt.LuckYPED10.woRKERs.dev&fp=chrome&alpn=http%2F1.1" +
        "&ech=cloudflare-ech.com&echOutbound=repack&type=ws" +
        "&host=tlbn75un37u.luckyped10.workers.dev&path=%2FeyJqdW5rIjoiR2JQblFieVhPbyJ9#Server%202";

    var vlessNode = NodeLinkParser.Parse(vlessLink);
    Check("vless: parsed", vlessNode is not null);
    if (vlessNode is not null)
    {
        Check("vless: uuid", vlessNode.Uuid == "5fee0a88-2abb-4bba-b72a-2445aa11c179");
        Check("vless: transport ws", vlessNode.Transport == TransportKind.WebSocket, vlessNode.Transport.ToString());
        Check("vless: security tls", vlessNode.Security == SecurityKind.Tls);
        Check("vless: sni kept", vlessNode.Sni?.StartsWith("tlBN75UN37u", StringComparison.Ordinal) == true);
        Check("vless: fingerprint", vlessNode.Fingerprint == "chrome");
        Check("vless: alpn", vlessNode.Alpn.Contains("http/1.1"), string.Join(",", vlessNode.Alpn));
        Check("vless: ws host", vlessNode.Host == "tlbn75un37u.luckyped10.workers.dev");
        Check("vless: path", vlessNode.Path?.StartsWith("/eyJqdW5rIjoi", StringComparison.Ordinal) == true);
        Check("vless: unknown params preserved", vlessNode.Extra.ContainsKey("ech") && vlessNode.Extra.ContainsKey("echOutbound"),
            string.Join(",", vlessNode.Extra.Keys));

        var rewritten = NodeLinkWriter.ToLink(vlessNode);
        Check("vless: unknown params appear in the new link",
            rewritten.Contains("ech=cloudflare-ech.com") && rewritten.Contains("echOutbound=repack"));

        var again = NodeLinkParser.Parse(rewritten);
        Check("vless: round-trips", again is not null);
        Check("vless: uuid survives", again?.Uuid == vlessNode.Uuid);
        Check("vless: sni survives", again?.Sni == vlessNode.Sni);
        Check("vless: path survives", again?.Path == vlessNode.Path);
        Check("vless: alpn survives", again?.Alpn.SequenceEqual(vlessNode.Alpn) == true);
        Check("vless: extras survive", again?.Extra.ContainsKey("ech") == true);
    }
}

// ------------------------------------------------------------ xray configs
Console.WriteLine("\n== xray config generation ==");
{
    var xrayState = new AppState();
    xrayState.Externals.Add(new ExternalProxy { Name = "local", Host = "127.0.0.1", Port = 10808, Type = "SOCKS5" });
    xrayState.ActiveExternalId = xrayState.Externals[0].Id;
    xrayState.Route = RouteKind.External;
    xrayState.Mode = CaptureMode.Process;

    var vless = new ProxyNode
    {
        Name = "Frankfurt",
        Protocol = NodeProtocol.Vless,
        Server = "de1.example.net",
        Port = 443,
        Uuid = "5fee0a88-2abb-4bba-b72a-2445aa11c179",
        Security = SecurityKind.Tls,
        Sni = "de1.example.net",
        Fingerprint = "chrome",
        Transport = TransportKind.WebSocket,
        Path = "/ws",
        Host = "de1.example.net",
    };
    vless.Alpn.Add("h2");

    var reality = new ProxyNode
    {
        Name = "Reality",
        Protocol = NodeProtocol.Vless,
        Server = "re2.example.net",
        Port = 443,
        Uuid = "11111111-2222-3333-4444-555555555555",
        Security = SecurityKind.Reality,
        Sni = "www.microsoft.com",
        Fingerprint = "chrome",
        PublicKey = "PUBKEYPUBKEYPUBKEYPUBKEYPUBKEYPUBKEYPUBKEY0",
        ShortId = "abcd1234",
        SpiderX = "/",
        Transport = TransportKind.Grpc,
        ServiceName = "grpcsvc",
    };

    var ss = new ProxyNode
    {
        Name = "SS",
        Protocol = NodeProtocol.Shadowsocks,
        Server = "ss.example.net",
        Port = 8388,
        Method = "xchacha20-ietf-poly1305",
        Password = "secret",
    };

    var trojan = new ProxyNode
    {
        Name = "Trojan",
        Protocol = NodeProtocol.Trojan,
        Server = "tj.example.net",
        Port = 443,
        Password = "pw",
        Security = SecurityKind.Tls,
        Sni = "tj.example.net",
        Transport = TransportKind.Tcp,
    };

    var hysteria = new ProxyNode
    {
        Name = "Hysteria",
        Protocol = NodeProtocol.Hysteria2,
        Server = "hy.example.net",
        Port = 443,
        Password = "pw",
    };

    var xrayContext = new BuildContext
    {
        State = xrayState,
        ProxyHops = new List<RouteHop> { RouteHop.From(vless) },
        RouteEverything = true,
        MixedPort = 47890,
    };

    var xrayJson = XrayConfigBuilder.Build(xrayContext);
    Check("xray: mixed inbound", xrayJson.Contains("\"protocol\": \"mixed\""));
    Check("xray: vless outbound", xrayJson.Contains("\"protocol\": \"vless\""));
    Check("xray: ws host at top level", xrayJson.Contains("\"host\": \"de1.example.net\""));
    Check("xray: no deprecated headers block", !xrayJson.Contains("\"headers\""));
    Check("xray: direct + block present", xrayJson.Contains("freedom") && xrayJson.Contains("blackhole"));

    // chain: second hop dials through the first
    var chainContext = new BuildContext
    {
        State = xrayState,
        ProxyHops = new List<RouteHop>
        {
            RouteHop.From(xrayState.Externals[0]),
            RouteHop.From(vless),
        },
        RouteEverything = true,
        MixedPort = 47890,
    };
    var xrayChainJson = XrayConfigBuilder.Build(chainContext);
    Check("xray: chain uses dialerProxy", xrayChainJson.Contains("\"dialerProxy\": \"chain-0\""));

    // protocols Xray cannot dial
    Check("xray: refuses hysteria2", !XrayConfigBuilder.CanDial(RouteHop.From(hysteria)));
    Check("xray: refusal names the protocol",
        XrayConfigBuilder.RefusalReason(RouteHop.From(hysteria))?.Contains("Hysteria2") == true,
        XrayConfigBuilder.RefusalReason(RouteHop.From(hysteria)) ?? "(null)");
    Check("xray: accepts vless", XrayConfigBuilder.CanDial(RouteHop.From(vless)));
    Check("xray: accepts shadowsocks", XrayConfigBuilder.CanDial(RouteHop.From(ss)));

    // engine selection
    var selectorState = new AppState { Mode = CaptureMode.Process };
    selectorState.Settings.PreferredEngine = "xray";

    var installer = new EngineInstaller();
    var installedXray = installer.GetActive(EngineCatalog.Get("xray")!);
    var installedSingbox = installer.GetActive(EngineCatalog.SingBox);
    Console.WriteLine($"   installed: sing-box={(installedSingbox?.Version ?? "none")} xray={(installedXray?.Version ?? "none")}");

    var choiceOk = EngineSelector.Choose(selectorState, new List<RouteHop> { RouteHop.From(vless) }, installer);
    Check("selector: honours the xray preference when possible",
        installedXray is null || choiceOk.Engine == EngineBackend.Xray,
        $"{choiceOk.Engine} / {choiceOk.Note}");

    var choiceBad = EngineSelector.Choose(selectorState, new List<RouteHop> { RouteHop.From(hysteria) }, installer);
    Check("selector: falls back to sing-box for hysteria2", choiceBad.Engine == EngineBackend.SingBox,
        choiceBad.Note ?? "(no note)");

    selectorState.Mode = CaptureMode.Transparent;
    var choiceTun = EngineSelector.Choose(selectorState, new List<RouteHop> { RouteHop.From(vless) }, installer);
    Check("selector: TUN forces sing-box", choiceTun.Engine == EngineBackend.SingBox, choiceTun.Note ?? "(no note)");

    // validate every shape against a real xray binary when one is available
    var xrayExe = FindXray();
    if (xrayExe is null)
    {
        Console.WriteLine("   SKIP xray validation (no xray.exe found)");
    }
    else
    {
        foreach (var (name, node) in new (string, ProxyNode)[]
                 { ("vless-ws", vless), ("reality-grpc", reality), ("ss", ss), ("trojan", trojan) })
        {
            var cfg = XrayConfigBuilder.Build(new BuildContext
            {
                State = xrayState,
                ProxyHops = new List<RouteHop> { RouteHop.From(node) },
                RouteEverything = true,
                MixedPort = 47890,
            });

            var file = Path.Combine(AppContext.BaseDirectory, $"xray-{name}.json");
            File.WriteAllText(file, cfg, new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = xrayExe,
                Arguments = $"run -test -c \"{file}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(20000);
            Check($"xray check {name}", proc.ExitCode == 0 && output.Contains("Configuration OK"),
                proc.ExitCode == 0 ? "ok" : output.Split('\n').LastOrDefault(l => l.Contains("Failed") || l.Contains("error"))?.Trim());
        }

        var chainFile = Path.Combine(AppContext.BaseDirectory, "xray-chain.json");
        File.WriteAllText(chainFile, xrayChainJson, new UTF8Encoding(false));
        var chainPsi = new ProcessStartInfo
        {
            FileName = xrayExe,
            Arguments = $"run -test -c \"{chainFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var chainProc = Process.Start(chainPsi)!;
        var chainOutput = chainProc.StandardOutput.ReadToEnd() + chainProc.StandardError.ReadToEnd();
        chainProc.WaitForExit(20000);
        Check("xray check chain", chainProc.ExitCode == 0 && chainOutput.Contains("Configuration OK"));
    }
}

/// <summary>Looks for an xray binary next to the repo or on PATH; validation is skipped if absent.</summary>
static string? FindXray()
{
    foreach (var candidate in new[]
             {
                 Path.Combine(AppContext.BaseDirectory, "xray.exe"),
                 @"D:\RouteScope\.xray\xray.exe",
             })
    {
        if (File.Exists(candidate)) return candidate;
    }

    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        try
        {
            var p = Path.Combine(dir.Trim(), "xray.exe");
            if (File.Exists(p)) return p;
        }
        catch { }
    }
    return null;
}

// -------------------------------------------------- whole-config subscriptions
Console.WriteLine("\n== custom configs ==");
{
    // The shape some subscriptions use: an array of complete configs, not server links.
    const string body = """
    [
      { "remarks": "Serverless-v59-fragA",
        "inbounds": [ { "tag": "mixed-in", "port": 10808, "protocol": "mixed", "settings": { "auth": "noauth" } } ],
        "outbounds": [ { "tag": "direct", "protocol": "freedom" } ] },
      { "remarks": "Serverless-v59-fragB",
        "inbounds": [ { "tag": "mixed-in", "port": 10808, "protocol": "mixed" } ],
        "outbounds": [ { "tag": "direct", "protocol": "freedom" } ] }
    ]
    """;

    var configs = SubscriptionFetcher.TryParseCustomConfigs(body);
    Check("custom configs: detected", configs is not null && configs.Count == 2, $"{configs?.Count ?? 0} found");
    Check("custom configs: names come from remarks", configs?[0].Name == "Serverless-v59-fragA", configs?[0].Name);

    Check("custom configs: a link list is not mistaken for configs",
        SubscriptionFetcher.TryParseCustomConfigs("vless://abc@host:443#x") is null);

    var xrayExe = FindXray();
    if (xrayExe is null)
    {
        Console.WriteLine("   SKIP custom config start (no xray.exe)");
    }
    else
    {
        await using var session = new XrayConfigSession();
        var (ok, error) = await session.StartAsync(xrayExe, configs![0].Json, 15, CancellationToken.None);
        Check("custom config: starts and opens a listener", ok, error ?? "ok");
        Check("custom config: port was rewritten away from 10808", session.Port is > 0 and not 10808, session.Port.ToString());
    }
}
// ------------------------------------------------------- live connections
Console.WriteLine("\n== live connections ==");
{
    // Same connection listed twice: keep one.
    const string duplicate = """
    { "connections": [
      { "id": "a1", "metadata": { "host": "ingest.example.com", "network": "tcp", "process": "RouteScope.exe" },
        "upload": 1500, "download": 5700, "rule": "proxy", "chains": ["proxy"] },
      { "id": "a1", "metadata": { "host": "ingest.example.com", "network": "tcp", "process": "RouteScope.exe" },
        "upload": 1500, "download": 5700, "rule": "proxy", "chains": ["proxy"] }
    ] }
    """;

    var deduped = ClashClient.ParseConnections(duplicate);
    Check("connections: duplicate id collapses to one", deduped.Count == 1, $"{deduped.Count} row(s)");

    // Two genuinely separate connections to the same host: both must survive.
    const string parallel = """
    { "connections": [
      { "id": "b1", "metadata": { "host": "ingest.example.com", "network": "tcp", "process": "RouteScope.exe" },
        "upload": 1500, "download": 5700, "rule": "proxy", "chains": ["proxy"] },
      { "id": "b2", "metadata": { "host": "ingest.example.com", "network": "tcp", "process": "RouteScope.exe" },
        "upload": 1500, "download": 5700, "rule": "proxy", "chains": ["proxy"] }
    ] }
    """;

    var both = ClashClient.ParseConnections(parallel);
    Check("connections: separate connections to one host are kept", both.Count == 2, $"{both.Count} row(s)");

    // Entries without an id must not be merged together.
    const string noId = """
    { "connections": [
      { "metadata": { "host": "x.example.com", "network": "tcp" }, "upload": 1, "download": 2 },
      { "metadata": { "host": "x.example.com", "network": "tcp" }, "upload": 3, "download": 4 }
    ] }
    """;

    var anonymous = ClashClient.ParseConnections(noId);
    Check("connections: rows without an id are not merged", anonymous.Count == 2, $"{anonymous.Count} row(s)");
}
// ------------------------------------------------------------- wireguard
Console.WriteLine("\n== wireguard ==");
{
    const string conf = """
    [Interface]
    PrivateKey = YSJ+C4GGBx46A9c0BbOHLxf0JoicFLBlW0EkYAHR+aU=
    Address = 172.16.0.2/32, fd01:5ca1:ab1e:8d3f::2/128
    DNS = 1.1.1.1
    MTU = 1420

    [Peer]
    PublicKey = YBnMFtnB7yrcsACPMnZfgHEj5FhrlBzzcerET/q/jr4=
    PresharedKey = 2w1vd6gGB6kqz6dXELBtP/sxEJvpCOtfBKsSMtArqhA=
    AllowedIPs = 0.0.0.0/0, ::/0
    Endpoint = 162.159.192.1:2408
    PersistentKeepalive = 25
    """;

    var wg = WireGuardConfig.Parse(conf);
    Check("wireguard: private key", wg.PrivateKey.Length > 0);
    Check("wireguard: endpoint", wg.Endpoint == "162.159.192.1:2408", wg.Endpoint);
    Check("wireguard: both addresses", wg.Addresses.Count == 2, string.Join(", ", wg.Addresses));
    Check("wireguard: mtu", wg.Mtu == 1420, wg.Mtu?.ToString());
    Check("wireguard: keepalive parsed", wg.Keepalive == 25, wg.Keepalive?.ToString());
    Check("wireguard: preshared key", wg.PreSharedKey?.Length > 0);

    var wgOutbound = wg.ToXrayOutbound("proxy");
    Check("wireguard: outbound built", wgOutbound is not null);
    Check("wireguard: outbound protocol", wgOutbound?["protocol"]?.GetValue<string>() == "wireguard");
    Check("wireguard: endpoint in peer", wgOutbound?["settings"]?["peers"]?[0]?["endpoint"]?.GetValue<string>() == "162.159.192.1:2408");
    Check("wireguard: peer keepalive", wgOutbound?["settings"]?["peers"]?[0]?["keepAlive"]?.GetValue<int>() == 25);
    Check("wireguard: secret key", wgOutbound?["settings"]?["secretKey"]?.GetValue<string>() == wg.PrivateKey);

    var wgHop = new ExternalProxy
    {
        Name = "WG",
        Type = "WireGuard",
        ImportedConfigText = conf,
    };

    Check("wireguard: Xray can dial it", XrayConfigBuilder.CanDial(RouteHop.From(wgHop)));

    var wgState = new AppState { Mode = CaptureMode.Process, Route = RouteKind.External };
    wgState.Externals.Add(wgHop);
    wgState.ActiveExternalId = wgHop.Id;

    // sing-box is the default again: its Clash API gives live connections + traffic totals.
    var wgJson = SingBoxConfigBuilder.Build(new BuildContext
    {
        State = wgState,
        ProxyHops = new List<RouteHop> { RouteHop.From(wgHop) },
        RouteEverything = true,
        MixedPort = 47890,
        ClashApiPort = 47891,
        ClashSecret = "test",
    });

    var wgFile = Path.Combine(AppContext.BaseDirectory, "cfg-WireGuard.json");
    File.WriteAllText(wgFile, wgJson, new UTF8Encoding(false));
    Check("wireguard: sing-box config has an endpoint", wgJson.Contains("\"endpoints\""));
    Check("wireguard: endpoint is the exit", wgJson.Contains("\"final\": \"proxy\""));

    var wgInstaller = new EngineInstaller();
    var wgChoice = EngineSelector.Choose(wgState, new List<RouteHop> { RouteHop.From(wgHop) }, wgInstaller);
    Check("wireguard: routed by sing-box so stats work", wgChoice.Engine == EngineBackend.SingBox,
        wgChoice.DisplayName);
}
// ------------------------------------------- managed-client classification
Console.WriteLine("\n== managed-client classification ==");
{
    var kinds = new (string Type, bool Session, bool Managed)[]
    {
        ("Psiphon",     true,  true),
        ("Xray config", true,  true),
        ("OpenVPN",     false, true),
        ("WireGuard",   false, true),
        ("Auto",        false, false),
    };

    foreach (var (type, expectedSession, expectedManaged) in kinds)
    {
        var e = new ExternalProxy { Type = type };
        Check($"{type}: needs a client session = {expectedSession}",
            e.NeedsClientSession == expectedSession,
            e.NeedsClientSession.ToString());
        Check($"{type}: managed = {expectedManaged}", e.IsManaged == expectedManaged,
            e.IsManaged.ToString());
    }

    // The regression: an OpenVPN entry must never be handed to the Psiphon startup loop.
    Check("openvpn is not mistaken for psiphon", !new ExternalProxy { Type = "OpenVPN" }.IsPsiphon);
    Check("wireguard is not mistaken for psiphon", !new ExternalProxy { Type = "WireGuard" }.IsPsiphon);
}
// ------------------------------------------- app proxy routes everything
Console.WriteLine("\n== app proxy routing ==");
{
    // App proxy hands out a local port. Whatever connects to it was pointed there by the
    // user, so the FULL chain must be used. Regression: process filtering made a browser
    // using 127.0.0.1:<port> go direct while the chain looked healthy.
    var apState = new AppState { Mode = CaptureMode.AppProxy, Route = RouteKind.External };

    var hop1 = new ExternalProxy { Name = "first", Host = "127.0.0.1", Port = 10808, Type = "SOCKS5" };
    var hop2 = new ExternalProxy { Name = "second", Host = "127.0.0.1", Port = 10820, Type = "SOCKS5" };
    apState.Externals.Add(hop1);
    apState.Externals.Add(hop2);
    apState.ActiveExternalId = hop1.Id;
    apState.Chain.Add(new ChainHop { Kind = RouteKind.External, RefId = hop2.Id });
    apState.Targets.Add(new ProxyTarget { Kind = TargetKind.Folder, Value = @"C:\Games\Thing" });

    var apJson = SingBoxConfigBuilder.Build(new BuildContext
    {
        State = apState,
        ProxyHops = new List<RouteHop> { RouteHop.From(hop1), RouteHop.From(hop2) },
        RouteEverything = true,          // App proxy must set this
        MixedPort = 47890,
        ClashApiPort = 47891,
        ClashSecret = "test",
        ExcludedProcessRegexes = new List<string> { @"(?i)^C:\\Apps\\client.exe$" },
    });

    Check("app proxy: final outbound is the proxy", apJson.Contains("\"final\": \"proxy\""));
    Check("app proxy: no process-path filtering of the route",
        !apJson.Contains("process_path_regex\": [\n"));
    Check("app proxy: chain hop 2 detours through hop 1", apJson.Contains("\"detour\": \"chain-0\""));
    Check("app proxy: the upstream client is still kept out", apJson.Contains("client.exe"));

    // Same route in process mode must produce the same final.
    var pmState = new AppState { Mode = CaptureMode.Process, Route = RouteKind.External };
    pmState.Externals.Add(hop1);
    pmState.Externals.Add(hop2);
    pmState.ActiveExternalId = hop1.Id;
    pmState.Chain.Add(new ChainHop { Kind = RouteKind.External, RefId = hop2.Id });

    var pmJson = SingBoxConfigBuilder.Build(new BuildContext
    {
        State = pmState,
        ProxyHops = new List<RouteHop> { RouteHop.From(hop1), RouteHop.From(hop2) },
        RouteEverything = true,
        MixedPort = 47890,
        ClashApiPort = 47891,
        ClashSecret = "test",
    });

    Check("process mode: final outbound is the proxy", pmJson.Contains("\"final\": \"proxy\""));

    // The mapping itself is the thing that was wrong, so pin it directly.
    var adv = new AdvancedSettings();
    Check("mode: app proxy routes everything", RouteEngine.RoutesEverything(CaptureMode.AppProxy, adv));
    Check("mode: process routes everything", RouteEngine.RoutesEverything(CaptureMode.Process, adv));
    Check("mode: system proxy routes everything", RouteEngine.RoutesEverything(CaptureMode.SystemProxy, adv));
    Check("mode: TUN routes everything by default", RouteEngine.RoutesEverything(CaptureMode.Transparent, adv));

    var tunOnly = new AdvancedSettings { TunOnlyTargets = true };
    Check("mode: TUN with TunOnlyTargets does not", !RouteEngine.RoutesEverything(CaptureMode.Transparent, tunOnly));
}
// ------------------------------------------------------- route.exe command
Console.WriteLine("\n== route.exe command ==");
{
    var cmd = OpenVpnSession.RouteAddCommand("192.168.102.1", 23);
    Check("route: verb is add, not the program name", cmd.StartsWith("add "), cmd);
    Check("route: METRIC comes before IF", cmd.Contains("metric 9999 if 23"), cmd);
    Check("route: gateway is the pushed next hop", cmd.Contains("192.168.102.1"), cmd);

    // The regression: passing "route add ..." as arguments prints the usage text instead of
    // adding the route, which is exactly the failure the user saw.
    Check("route: no doubled program name", !cmd.StartsWith("route ", StringComparison.OrdinalIgnoreCase));
}
// ---------------------------------------------------- openvpn normalisation
Console.WriteLine("\n== openvpn normalisation ==");
{
    // The original file: dev tun2 selects an adapter that does not exist.
    const string original = """
    client
    dev tun2
    proto udp
    remote ovp3.multipbg.com 443
    auth-user-pass
    setenv opt block-outside-dns
    key-direction 1
    """;

    var normalized = OpenVpnConfig.Normalize(original);
    Check("normalise: dev tun2 becomes dev tun", normalized.Contains("dev tun\n"), normalized.Split('\n')[1]);
    Check("normalise: no tun2 left", !normalized.Contains("tun2"));
    Check("normalise: everything else preserved", normalized.Contains("remote ovp3.multipbg.com 443")
        && normalized.Contains("key-direction 1") && normalized.Contains("auth-user-pass")
        && normalized.Contains("proto udp"));
    Check("normalise: setenv stripped from the sample",
        !normalized.Contains("setenv opt block-outside-dns"));

    // A tap device keeps tap.
    const string tap = "client\ndev tap0\nremote x 1194\n";
    Check("normalise: tap stays tap", OpenVpnConfig.Normalize(tap).Contains("dev tap\n"));

    // Already-correct files are untouched.
    const string good = "client\ndev tun\nremote x 1194\n";
    Check("normalise: correct dev is unchanged", OpenVpnConfig.Normalize(good).Contains("dev tun\n"));

    // The REAL difference between the user's two configs: setenv opt block-outside-dns makes
    // OpenVPN install WFP filters that block DNS on every other adapter, and it bypasses
    // pull-filter because it is a local setenv. It must be stripped.
    const string withBlockDns = """
    client
    dev tun2
    remote ovp3.multipbg.com 443
    setenv opt block-outside-dns
    key-direction 1
    verb 3
    pull-filter ignore "block-outside-dns"
    dhcp-option DNS 1.1.1.1
    """;

    var changes = new List<string>();
    var converted = OpenVpnConfig.Normalize(withBlockDns, changes.Add);
    Check("normalise: setenv opt block-outside-dns removed",
        !converted.Contains("setenv opt block-outside-dns"));
    Check("normalise: dev tun2 also fixed", converted.Contains("dev tun\n"));
    Check("normalise: protective pull-filter is kept",
        converted.Contains("pull-filter ignore \"block-outside-dns\""));
    Check("normalise: harmless local dhcp-option is kept",
        converted.Contains("dhcp-option DNS 1.1.1.1"));
    Check("normalise: certificates/keys region untouched",
        converted.Contains("key-direction 1") && converted.Contains("verb 3"));
    Check("normalise: changes are reported", changes.Count >= 2, string.Join(", ", changes));

    // The known-good tail must be synthesised: without local dhcp-option DNS the TAP adapter
    // has no DNS and hostnames fall through to the ISP sinkhole (10.10.34.36), which then
    // matches the private-IP LAN-bypass rule and times out on the unbound direct outbound.
    Check("normalise: protective pull-filter added",
        converted.Contains("pull-filter ignore \"redirect-gateway\"") &&
        converted.Contains("pull-filter ignore \"block-outside-dns\""));
    Check("normalise: public DNS added",
        converted.Contains("dhcp-option DNS 1.1.1.1") &&
        converted.Contains("dhcp-option DNS 8.8.8.8"));

    // A config that already has the tail must not get duplicates.
    const string alreadyGood = """
    client
    dev tun
    pull-filter ignore "redirect-gateway"
    pull-filter ignore "block-outside-dns"
    dhcp-option DNS 1.1.1.1
    dhcp-option DNS 8.8.8.8
    <ca>
    cert
    </ca>
    """;
    var untouched = OpenVpnConfig.Normalize(alreadyGood);
    Check("normalise: no duplicate pull-filter",
        (untouched.Split('\n').Count(l => l.Contains("pull-filter ignore \"redirect-gateway\"")) == 1));
    Check("normalise: no duplicate dns",
        (untouched.Split('\n').Count(l => l.Contains("dhcp-option DNS 1.1.1.1")) == 1));
    Check("normalise: tail inserted before inline blocks",
        untouched.IndexOf("dhcp-option DNS", StringComparison.Ordinal) <
        untouched.IndexOf("<ca>", StringComparison.Ordinal));

    // A bare block-outside-dns / redirect-gateway is equally damaging. The protective
    // pull-filter lines also contain those words, so check for the BARE directive.
    Check("normalise: bare block-outside-dns removed",
        !OpenVpnConfig.Normalize("client\nblock-outside-dns\n")
            .Split('\n').Any(l => l.Trim() == "block-outside-dns"));
    Check("normalise: bare redirect-gateway removed",
        !OpenVpnConfig.Normalize("client\nredirect-gateway def1\n")
            .Split('\n').Any(l => l.Trim().StartsWith("redirect-gateway ", StringComparison.Ordinal)));
}
// ------------------------------------------------- openvpn credentials
Console.WriteLine("\n== openvpn credentials ==");
{
    const string needsLogin = """
    client
    dev tun
    proto udp
    remote ovp3.multipbg.com 443
    auth-user-pass
    <ca>
    -----BEGIN CERTIFICATE-----
    MIIB
    -----END CERTIFICATE-----
    </ca>
    """;

    var needs = OpenVpnConfig.Parse(needsLogin);
    Check("openvpn: bare auth-user-pass detected", needs.NeedsCredentials);

    const string hasFile = """
    client
    dev tun
    remote vpn.example.net 1194
    auth-user-pass C:\creds.txt
    """;

    var fileBased = OpenVpnConfig.Parse(hasFile);
    Check("openvpn: auth-user-pass with a file is left alone", !fileBased.NeedsCredentials);

    const string noAuth = """
    client
    dev tun
    remote vpn.example.net 1194
    """;

    Check("openvpn: no auth directive means no prompt", !OpenVpnConfig.Parse(noAuth).NeedsCredentials);
}
// --------------------------------------------------------------- openvpn
Console.WriteLine("\n== openvpn ==");
{
    const string ovpn = """
    client
    dev tun
    proto udp
    remote vpn.example.net 1194
    resolv-retry infinite
    nobind
    persist-key
    persist-tun
    <ca>
    -----BEGIN CERTIFICATE-----
    MIIB
    -----END CERTIFICATE-----
    </ca>
    """;

    var ovpnParsed = OpenVpnConfig.Parse(ovpn);
    Check("openvpn: remote host", ovpnParsed.RemoteHost == "vpn.example.net", ovpnParsed.RemoteHost);
    Check("openvpn: remote port", ovpnParsed.RemotePort == 1194, ovpnParsed.RemotePort.ToString());
    Check("openvpn: protocol", ovpnParsed.Protocol == "udp", ovpnParsed.Protocol);
    Check("openvpn: endpoint label", ovpnParsed.Endpoint == "vpn.example.net:1194", ovpnParsed.Endpoint);

    var ovpnEntry = new ExternalProxy
    {
        Name = "Work VPN",
        Type = "OpenVPN",
        ImportedConfigText = ovpn,
    };

    Check("openvpn: counted as managed", ovpnEntry.IsManaged);
    Check("openvpn: engine cannot dial it as a proxy", !XrayConfigBuilder.CanDial(RouteHop.From(ovpnEntry)));

    var ovpnState = new AppState { Mode = CaptureMode.Process, Route = RouteKind.External };
    ovpnState.Externals.Add(ovpnEntry);
    ovpnState.ActiveExternalId = ovpnEntry.Id;

    var withoutAdapter = false;
    try
    {
        SingBoxConfigBuilder.Build(new BuildContext
        {
            State = ovpnState,
            ProxyHops = new List<RouteHop> { RouteHop.From(ovpnEntry) },
            RouteEverything = true,
            MixedPort = 47890,
            ClashApiPort = 47891,
            ClashSecret = "test",
        });
    }
    catch (InvalidOperationException) { withoutAdapter = true; }

    Check("openvpn: refuses to build without an adapter", withoutAdapter);

    var ovpnJson = SingBoxConfigBuilder.Build(new BuildContext
    {
        State = ovpnState,
        ProxyHops = new List<RouteHop> { RouteHop.From(ovpnEntry) },
        RouteEverything = true,
        MixedPort = 47890,
        ClashApiPort = 47891,
        ClashSecret = "test",
        BindInterface = "RouteScopeVPN",
    });

    Check("openvpn: outbound is pinned to the adapter", ovpnJson.Contains("\"bind_interface\": \"RouteScopeVPN\""));
    Check("openvpn: auto-detect interface is off when pinned",
        ovpnJson.Contains("\"auto_detect_interface\": false"));
    Check("openvpn: direct outbound is bound to the tunnel too",
        ovpnJson.Contains("\"tag\": \"direct\"") && ovpnJson.Contains("\"bind_interface\": \"RouteScopeVPN\""));

    var ovpnFile = Path.Combine(AppContext.BaseDirectory, "cfg-OpenVPN.json");
    File.WriteAllText(ovpnFile, ovpnJson, new UTF8Encoding(false));
}
// --------------------------------------------------- netfilter safety
Console.WriteLine("\n== netfilter safety ==");
{
    var unrooted = NetFilterSession.ValidateHandlerRoots();
    Check("nf: every callback pointer has a rooted delegate", unrooted.Count == 0,
        unrooted.Count == 0 ? "all 16 rooted" : "dangling: " + string.Join(", ", unrooted));

    // The engine must be excluded by PID, not by a listening-port lookup that fails for
    // tunnels (where the port is a REMOTE server port).
    var wgTunnel = new ExternalProxy
    {
        Name = "WG", Type = "WireGuard", Host = "vs17.vrt-server.org", Port = 11040,
        ImportedConfigText = "x",
    };
    var ownerOfRemotePort = ProcessScanner.FindListenerProcess(wgTunnel.Port);
    Check("nf: a tunnel's port has no local listener to exclude",
        ownerOfRemotePort is null,
        ownerOfRemotePort?.Name ?? "none (so the engine PID must be used instead)");
}
// ------------------------------------------------------- dead route guard
Console.WriteLine("\n== dead route guard ==");
{
    // A port that accepts and then says nothing: the engine looks alive but carries nothing.
    var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    silent.Start();
    var silentPort = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;

    var (silentOk, silentDetail) = await RouteEngine.VerifyTunnelAsync(silentPort, CancellationToken.None);
    Check("dead route: silent port is rejected", !silentOk, silentDetail);

    // Nothing listening at all.
    var (closedOk, closedDetail) = await RouteEngine.VerifyTunnelAsync(1, CancellationToken.None);
    Check("dead route: closed port is rejected", !closedOk, closedDetail);

    silent.Stop();
}
// ------------------------------------------------------- engine validation
Console.WriteLine("\n== sing-box validation ==");
var sb = Path.Combine(@"D:\RouteScope\.devtools\singbox\sing-box.exe");
if (File.Exists(sb))
{
    foreach (var name in new[] { "Transparent", "AppProxy", "SystemProxy", "Chain", "WireGuard", "OpenVPN" })
    {
        var cfg = Path.Combine(AppContext.BaseDirectory, $"cfg-{name}.json");
        var psi = new ProcessStartInfo(sb, $"check -c \"{cfg}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Check($"sing-box check {name}", p.ExitCode == 0,
            p.ExitCode == 0 ? "valid" : (err + output).Trim().Split('\n').LastOrDefault());
    }
}
else
{
    Console.WriteLine("  (sing-box not present, skipping)");
}

// ------------------------------------------------------------ target matching
Console.WriteLine("\n== target matching ==");
var folderTarget = new ProxyTarget { Kind = TargetKind.Folder, Value = @"C:\Games\MyGame" };
Check("folder matches child exe", folderTarget.Matches(@"C:\Games\MyGame\bin\game.exe", "game.exe"));
Check("folder rejects sibling", !folderTarget.Matches(@"C:\Games\OtherGame\game.exe", "game.exe"));
Check("folder is case insensitive", folderTarget.Matches(@"c:\games\mygame\bin\game.exe", "game.exe"));
Check("folder rejects prefix trap", !folderTarget.Matches(@"C:\Games\MyGame2\game.exe", "game.exe"));

var regex = folderTarget.ToProcessPathRegex();
Check("folder regex escapes backslashes", regex == @"(?i)^C:\\Games\\MyGame\\", regex);
Check("folder regex matches", System.Text.RegularExpressions.Regex.IsMatch(
    @"C:\Games\MyGame\bin\game.exe", regex!));

var appTarget = new ProxyTarget { Kind = TargetKind.Application, Value = @"C:\Program Files\Telegram\Telegram.exe" };
Check("app exact match", appTarget.Matches(@"c:\program files\telegram\telegram.exe", "Telegram"));
Check("app rejects other", !appTarget.Matches(@"C:\Other\Telegram.exe", "Telegram"));

// -------------------------------------------------------------- process scan
Console.WriteLine("\n== process scanning ==");
var snapshot = ProcessScanner.Snapshot();
Check("enumerated processes", snapshot.Count > 10, $"{snapshot.Count} processes");
Check("resolved image paths", snapshot.Count(p => p.ExePath is not null) > 5,
    $"{snapshot.Count(p => p.ExePath is not null)} with paths");

// ------------------------------------------------------------ port detection
Console.WriteLine("\n== port helpers ==");
var free = ProxyDetector.FindFreePort(47000);
Check("finds a free port", ProxyDetector.IsPortFree(free), $"port {free}");

// ------------------------------------------------------------- migrations
Console.WriteLine("\n== profile migration ==");
var tempProfile = Path.Combine(Path.GetTempPath(), "rs-migrate-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    // A v1 profile: strict_route was on by default and got saved that way.
    File.WriteAllText(tempProfile, """
    {
      "schemaVersion": 1,
      "targets": [],
      "settings": { "advanced": { "strictRoute": true } }
    }
    """, new UTF8Encoding(false));

    var migrated = new RouteScope.Core.Storage.StateStore(tempProfile).Load();
    Check("migration turns strict_route off", !migrated.Settings.Advanced.StrictRoute);
    Check("migration bumps the schema version", migrated.SchemaVersion == 2, migrated.SchemaVersion.ToString());

    var reloaded = new RouteScope.Core.Storage.StateStore(tempProfile).Load();
    Check("migration is persisted to disk", !reloaded.Settings.Advanced.StrictRoute);
}
finally { try { File.Delete(tempProfile); } catch { } }

// ------------------------------------------- listener -> process (loop guard)
Console.WriteLine("\n== upstream proxy detection ==");
var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
listener.Start();
try
{
    var listenPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var owner = ProcessScanner.FindListenerProcess(listenPort);
    Check("finds the process owning a listening port",
        owner is not null && owner.Pid == Environment.ProcessId,
        owner is null ? "not found" : $"{owner.Name} pid {owner.Pid}");
    Check("returns the image path used for the exclusion rule",
        owner?.ExePath is { Length: > 0 }, owner?.ExePath);
}
finally { listener.Stop(); }

// ------------------------------------------------------------- log filtering
Console.WriteLine("\n== activity log filtering ==");
var noisy = new[]
{
    "router: found process path: C:\\Users\\Alex\\Desktop\\RouteScope\\RouteScope.exe",
    "inbound/mixed[mixed-in]: inbound connection from 127.0.0.1:8218",
    "inbound/mixed[mixed-in]: inbound connection to oauth.battle.net:443",
    "outbound/direct[direct]: outbound connection to 127.0.0.1:47890",
};
var useful = new[]
{
    "outbound/socks[proxy]: outbound connection to oauth.battle.net:443",
    "outbound/direct[direct]: outbound connection to example.com:443",
    "dns: lookup failed for cdn.discordapp.com: temporary failure, retrying",
};

foreach (var line in noisy)
    Check("hidden: " + line[..Math.Min(46, line.Length)], RouteScope.Core.Runtime.EngineRunner.IsRoutineNoise(line));
foreach (var line in useful)
    Check("kept:   " + line[..Math.Min(46, line.Length)], !RouteScope.Core.Runtime.EngineRunner.IsRoutineNoise(line));

// ------------------------------------------------------- psiphon config
Console.WriteLine("\n== psiphon ==");
{
    var direct = PsiphonConfig.Build(@"C:\tmp\psiphon", 41100, null);
    Check("psiphon: no upstream when first hop", !direct.Contains("UpstreamProxyURL"));
    Check("psiphon: listens on the port we chose", direct.Contains("\"LocalSocksProxyPort\": 41100"));
    Check("psiphon: signed server list key present", direct.Contains("RemoteServerListSignaturePublicKey"));

    var chained = PsiphonConfig.Build(@"C:\tmp\psiphon", 41101, "socks5://127.0.0.1:10808");
    Check("psiphon: upstream is set when chained", chained.Contains("\"UpstreamProxyURL\": \"socks5://127.0.0.1:10808\""));
    Check("psiphon: upstream url for http proxies", PsiphonConfig.UpstreamUrl("127.0.0.1", 7890, "HTTP") == "http://127.0.0.1:7890");
    Check("psiphon: upstream url for socks", PsiphonConfig.UpstreamUrl("127.0.0.1", 10808, "SOCKS5") == "socks5://127.0.0.1:10808");

    var regioned = PsiphonConfig.Build(@"C:\tmp\psiphon", 41102, null, "DE");
    Check("psiphon: egress region is written", regioned.Contains("\"EgressRegion\": \"DE\""));
    Check("psiphon: no region when unset", !direct.Contains("EgressRegion"));

    // the client is published as a repo file, not a release asset
    var psiphonDef = EngineCatalog.Get("psiphon");
    Check("catalog: psiphon is a managed client", psiphonDef is { IsManagedClient: true, RepoFilePath: not null });
    Check("catalog: psiphon points at the binaries repo",
        psiphonDef?.Repo == "Psiphon-Labs/psiphon-tunnel-core-binaries", psiphonDef?.Repo);
    Check("catalog: only sing-box and xray route",
        EngineCatalog.Routing.Select(d => d.Id).SequenceEqual(new[] { "singbox", "xray" }),
        string.Join(",", EngineCatalog.Routing.Select(d => d.Id)));

    // Downloads can be routed through a local proxy for people who cannot reach GitHub directly.
    var proxyUp = await ProxyDetector.ProbeAsync("127.0.0.1", 10808, 700) is not null;
    if (proxyUp)
    {
        var dest = Path.Combine(AppContext.BaseDirectory, "download-via-proxy.json");
        try { File.Delete(dest); } catch { }

        var source = await DownloadService.DownloadAsync(
            "https://example.com",
            dest, null, null, CancellationToken.None, "socks5://127.0.0.1:10808");

        Check("downloads work through a socks proxy", source is not null && File.Exists(dest) && new FileInfo(dest).Length > 200,
            source is null ? "no source worked" : $"{new FileInfo(dest).Length} bytes via {new Uri(source).Host}");
        try { File.Delete(dest); } catch { }

        // the version lookup must travel the same way, or "GitHub is unreachable" is reported
        // even though the proxy is fine
        // GitHub's API is not always up. Only assert when it answers directly, so a GitHub
        // outage is not reported as a RouteScope bug.
        var directLookup = await GitHubReleases.GetLatestAsync("XTLS/Xray-core", false, CancellationToken.None);
        if (directLookup is null)
        {
            Console.WriteLine("   SKIP release lookup (GitHub is not answering right now)");
        }
        else
        {
            var viaProxy = await GitHubReleases.GetLatestAsync("XTLS/Xray-core", false, CancellationToken.None,
                "socks5://127.0.0.1:10808");
            Check("release lookup works through the proxy", viaProxy is not null,
                viaProxy?.Version ?? "returned null");
        }
    }
    else
    {
        Console.WriteLine("   SKIP proxy download (nothing on 127.0.0.1:10808)");
    }

    var binary = PsiphonSession.FindBinary(null) ?? PsiphonSession.FindBinary(@"C:\Users\Alex\Desktop\reference-client\bin\psiphon-tunnel-core.exe");
    Console.WriteLine($"   binary: {binary ?? "not found"}");

    if (args.Contains("--psiphon") && binary is not null)
    {
        // A local proxy must be reachable for the tunnel to work where Psiphon is blocked.
        var upstreamOk = await ProxyDetector.ProbeAsync("127.0.0.1", 10808, 800) is not null;
        var upstream = upstreamOk ? "socks5://127.0.0.1:10808" : null;
        Console.WriteLine($"   upstream: {upstream ?? "none (direct)"}");

        await using var session = new PsiphonSession();
        var (ok, error) = await session.StartAsync(binary, upstream, 70, CancellationToken.None);
        Check("psiphon: tunnel established", ok, error ?? "connected");

        if (ok)
        {
            var viaPsiphon = await FetchSocks(session.SocksPort);
            Check("psiphon: traffic exits through the tunnel", viaPsiphon.ok, viaPsiphon.detail);
        }
    }
}

static async Task<(bool ok, string detail)> FetchSocks(int port)
{
    try
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{port}"),
            UseProxy = true,
        })
        { Timeout = TimeSpan.FromSeconds(25) };

        var response = await client.GetAsync("https://example.com");
        return (response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
    }
    catch (Exception ex)
    {
        return (false, ex.Message);
    }
}
// --------------------------------------------------- run a real imported config
var configArg = args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault();
if (configArg is not null && File.Exists(configArg))
{
    Console.WriteLine($"\n== imported config: {Path.GetFileName(configArg)} ==");
    var exe = FindXray();
    if (exe is null) Console.WriteLine("   SKIP: no xray.exe");
    else
    {
        await using var session = new XrayConfigSession();
        // Chained through a local proxy when one is up: these configs cannot resolve their
        // own server hostname on their own.
        var up = await ProxyDetector.ProbeAsync("127.0.0.1", 10808, 700) is not null
            ? "socks5://127.0.0.1:10808"
            : null;
        Console.WriteLine($"   upstream: {up ?? "none"}");

        var (ok, error) = await session.StartAsync(exe, File.ReadAllText(configArg), 20, CancellationToken.None, up);
        Check("config starts", ok, error ?? "ok");

        if (ok)
        {
            var fetched = await FetchSocks(session.Port);
            Check("traffic exits through the config", fetched.ok, fetched.detail);
        }
    }
}
// ------------------------------------------------------------ xray live test
if (args.Contains("--xray"))
{
    Console.WriteLine("\n== xray live routing ==");
    var liveXray = FindXray();
    if (liveXray is null)
    {
        Console.WriteLine("   SKIP: no xray.exe found");
    }
    else
    {
        const int upstreamPort = 41080;
        const int mixedPort = 41081;

        var upstreamCfg = """
        {
          "log": {"loglevel": "warning"},
          "inbounds": [{"tag":"socks-in","listen":"127.0.0.1","port":41080,"protocol":"socks","settings":{"auth":"noauth","udp":false}}],
          "outbounds": [{"tag":"direct","protocol":"freedom"}]
        }
        """;

        var routedState = new AppState { Mode = CaptureMode.AppProxy };
        routedState.Externals.Add(new ExternalProxy
        {
            Name = "upstream",
            Host = "127.0.0.1",
            Port = upstreamPort,
            Type = "SOCKS5",
        });
        routedState.ActiveExternalId = routedState.Externals[0].Id;
        routedState.Route = RouteKind.External;

        var routedCfg = XrayConfigBuilder.Build(new BuildContext
        {
            State = routedState,
            ProxyHops = new List<RouteHop> { RouteHop.From(routedState.Externals[0]) },
            RouteEverything = true,
            MixedPort = mixedPort,
        });

        var upFile = Path.Combine(AppContext.BaseDirectory, "xray-live-upstream.json");
        var mixFile = Path.Combine(AppContext.BaseDirectory, "xray-live-mixed.json");
        File.WriteAllText(upFile, upstreamCfg, new UTF8Encoding(false));
        File.WriteAllText(mixFile, routedCfg, new UTF8Encoding(false));

        using var upstream = StartProcess(liveXray, $"run -c \"{upFile}\"");
        await Task.Delay(1500);
        using var mixed = StartProcess(liveXray, $"run -c \"{mixFile}\"");
        await Task.Delay(2000);

        try
        {
            var viaUpstream = await FetchThrough(mixedPort);
            Check("xray: generated config routes through the upstream proxy", viaUpstream.ok, viaUpstream.detail);
        }
        finally
        {
            try { if (!mixed.HasExited) mixed.Kill(true); } catch { }
            try { if (!upstream.HasExited) upstream.Kill(true); } catch { }
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static Process StartProcess(string file, string arguments) => Process.Start(new ProcessStartInfo
{
    FileName = file,
    Arguments = arguments,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
})!;

static async Task<(bool ok, string detail)> FetchThrough(int port)
{
    try
    {
        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new System.Net.WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
        })
        { Timeout = TimeSpan.FromSeconds(15) };

        var response = await client.GetAsync("https://example.com");
        return (response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
    }
    catch (Exception ex)
    {
        return (false, ex.Message);
    }
}
