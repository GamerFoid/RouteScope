using RouteScope.App.ViewModels;
using RouteScope.Core;
using RouteScope.Core.Models;
using RouteScope.Core.Runtime;
using RouteScope.Core.Storage;

namespace RouteScope.App;

/// <summary>
/// Sample content used by <c>--demo</c> and by the screenshot harness. It only lives in
/// memory and is never written to the user's profile.
/// </summary>
public static class DemoData
{
    public static void Seed(StateStore store)
    {
        var state = store.State;
        state.Targets.Clear();
        state.Nodes.Clear();

        state.Targets.Add(new ProxyTarget
        {
            Kind = TargetKind.Folder,
            Value = @"C:\Program Files\qBittorrent",
            DisplayName = "qBittorrent",
            Enabled = true,
            LiveConnections = 4,
            IsVerified = true,
        });
        state.Targets.Add(new ProxyTarget
        {
            Kind = TargetKind.Application,
            Value = @"C:\Users\Alex\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            DisplayName = "Visual Studio Code",
            Enabled = true,
            LiveConnections = 2,
            IsVerified = true,
        });
        state.Targets.Add(new ProxyTarget
        {
            Kind = TargetKind.ProcessName,
            Value = "telegram.exe",
            DisplayName = "Telegram",
            Enabled = true,
        });
        state.Targets.Add(new ProxyTarget
        {
            Kind = TargetKind.Application,
            Value = @"C:\Users\Alex\AppData\Local\Discord\Update.exe",
            DisplayName = "Discord",
            Enabled = true,
        });
        state.Targets.Add(new ProxyTarget
        {
            Kind = TargetKind.Folder,
            Value = @"C:\Program Files\Google\Chrome",
            DisplayName = "Chrome",
            Enabled = false,
        });

        state.Nodes.Add(new ProxyNode
        {
            Name = "Frankfurt · vless-reality",
            Protocol = NodeProtocol.Vless,
            Server = "de1.example.net",
            Port = 443,
            Security = SecurityKind.Reality,
            Transport = TransportKind.Tcp,
            Flow = "xtls-rprx-vision",
            LastLatencyMs = 84,
        });
        state.Nodes.Add(new ProxyNode
        {
            Name = "Amsterdam · hysteria2",
            Protocol = NodeProtocol.Hysteria2,
            Server = "nl2.example.net",
            Port = 8443,
            Security = SecurityKind.Tls,
            LastLatencyMs = 112,
        });
        state.Nodes.Add(new ProxyNode
        {
            Name = "Istanbul · trojan-ws",
            Protocol = NodeProtocol.Trojan,
            Server = "tr1.example.net",
            Port = 443,
            Security = SecurityKind.Tls,
            Transport = TransportKind.WebSocket,
            Path = "/tunnel",
            LastLatencyMs = -1,
        });

        // One imported config, so the merged servers list is exercised in screenshots.
        state.Externals.Add(new ExternalProxy
        {
            Name = "💦 1. VLESS - Domain : 443",
            Type = "Xray config",
            Host = "127.0.0.1",
            Port = 0,
            CustomConfigJson = """
            {
              "remarks": "demo",
              "log": { "loglevel": "warning" },
              "inbounds": [ { "tag": "mixed-in", "port": 10808, "protocol": "mixed", "settings": { "auth": "noauth", "udp": true } } ],
              "outbounds": [
                { "tag": "proxy", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "demo.example.net", "port": 443,
                    "users": [ { "id": "00000000-0000-0000-0000-000000000000", "encryption": "none" } ] } ] },
                  "streamSettings": { "network": "ws", "security": "tls",
                    "wsSettings": { "host": "demo.example.net", "path": "/demo" } } },
                { "tag": "direct", "protocol": "freedom" }
              ],
              "routing": { "rules": [ { "type": "field", "network": "tcp", "outboundTag": "proxy" } ] }
            }
            """,
        });

        state.Externals.Add(new ExternalProxy
        {
            Name = "Cloudflare WARP",
            Type = "WireGuard",
            Host = "162.159.192.1",
            Port = 2408,
            ImportedConfigText = """
            [Interface]
            PrivateKey = YSJ+C4GGBx46A9c0BbOHLxf0JoicFLBlW0EkYAHR+aU=
            Address = 172.16.0.2/32, fd01:5ca1:ab1e:8d3f::2/128
            MTU = 1280

            [Peer]
            PublicKey = YBnMFtnB7yrcsACPMnZfgHEj5FhrlBzzcerET/q/jr4=
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = 162.159.192.1:2408
            """,
        });

        state.Externals.Add(new ExternalProxy
        {
            Name = "Work VPN",
            Type = "OpenVPN",
            Host = "vpn.example.net",
            Port = 1194,
            ImportedConfigText = """
            client
            dev tun
            proto udp
            remote vpn.example.net 1194
            <ca>
            -----BEGIN CERTIFICATE-----
            MIIB
            -----END CERTIFICATE-----
            </ca>
            """,
        });

        state.Externals[0].IsReachable = true;
        state.Externals[0].DetectedType = "SOCKS5";
        state.Route = RouteKind.External;
        state.Mode = CaptureMode.Process;
        state.Settings.OnboardingCompleted = true;
        state.Settings.PreferredEngine = Environment.GetEnvironmentVariable("ROUTESCOPE_DEMO_ENGINE") ?? "auto";

        // Show a proxy-over-proxy chain: exit via Frankfurt, reached through the local proxy.
        state.Chain.Add(new ChainHop
        {
            Kind = RouteKind.Node,
            RefId = state.Nodes[0].Id,
        });

        var demoSub = new Subscription
        {
            Name = "example.net",
            Url = "https://example.net/api/v1/client/subscribe?token=demo",
            NodeCount = state.Nodes.Count,
            LastUpdatedUtc = DateTime.UtcNow.AddHours(-6),
        };
        state.Subscriptions.Add(demoSub);
        foreach (var node in state.Nodes) node.SubscriptionId = demoSub.Id;

        state.Externals[0].RealPingMs = 96;
        state.Nodes[0].RealPingMs = 148;
        state.Nodes[1].RealPingMs = 173;
        state.Nodes[2].RealPingMs = -1;

        LogBus.Info("engine", "Started sing-box.exe (pid 21884)");
        LogBus.Info("engine", "start inbound/tun[tun-in]: started at 172.19.0.1/30");
        LogBus.Info("engine", "start inbound/mixed[mixed-in]: started at 127.0.0.1:47890");
        LogBus.Info("engine", "Routing through: v2rayN · 127.0.0.1:10808 (SOCKS5)");
        LogBus.Info("engine", "Matched 6 child process(es) of your targets.");
        LogBus.Info("engine", "router: found process path: C:\\Program Files (x86)\\Steam\\steam.exe");
        LogBus.Info("engine", "outbound/socks[proxy]: outbound connection to store.steampowered.com:443");
        LogBus.Info("engine", "router: found process path: C:\\Users\\Alex\\AppData\\Local\\Discord\\app-1.0.9180\\Discord.exe");
        LogBus.Info("engine", "outbound/socks[proxy]: outbound connection to gateway.discord.gg:443");
        LogBus.Warn("engine", "dns: lookup failed for cdn.discordapp.com: temporary failure, retrying");
        LogBus.Info("engine", "router: found process path: C:\\Users\\Alex\\AppData\\Roaming\\Telegram Desktop\\Telegram.exe");
        LogBus.Info("engine", "outbound/socks[proxy]: outbound connection to 149.154.167.51:443");
        LogBus.Info("ui", "Targets changed: 5 configured, 4 enabled");
    }

    public static void PopulateLive(MainViewModel vm)
    {
        var now = DateTimeOffset.Now;
        var rows = new (string proc, string path, string host, int port, string chain, long up, long down)[]
        {
            ("Discord.exe", @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9180\Discord.exe", "gateway.discord.gg", 443, "proxy", 12_400, 486_200),
            ("Steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", "steamcommunity-a.akamaihd.net", 443, "proxy", 4_120, 1_820_400),
            ("Telegram.exe", @"C:\Users\Alex\AppData\Roaming\Telegram Desktop\Telegram.exe", "149.154.167.51", 443, "proxy", 18_600, 92_300),
            ("Code.exe", @"C:\Users\Alex\AppData\Local\Programs\Microsoft VS Code\Code.exe", "marketplace.visualstudio.com", 443, "proxy", 2_050, 64_100),
            ("Discord.exe", @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9180\Discord.exe", "cdn.discordapp.com", 443, "proxy", 900, 3_240_000),
            ("steamwebhelper.exe", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", "store.steampowered.com", 443, "proxy", 1_300, 210_500),
        };

        var connections = rows.Select((r, i) => new ConnectionInfo(
            Id: "demo-" + i,
            Host: r.host,
            DestinationIp: "203.0.113." + (10 + i),
            DestinationPort: r.port,
            Network: "tcp",
            Type: "",
            ProcessPath: r.path,
            ProcessName: r.proc,
            Rule: "rule",
            Chain: r.chain,
            Upload: r.up,
            Download: r.down,
            Start: now.AddMinutes(-i * 3))).ToList();

        vm.LoadDemoConnections(connections);
    }
}
