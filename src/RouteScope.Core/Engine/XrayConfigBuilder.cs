using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouteScope.Core.Models;
using RouteScope.Core.Nodes;

namespace RouteScope.Core.Engine;

/// <summary>
/// Turns a RouteScope profile into an Xray-core configuration.
///
/// Xray covers VLESS, VMess, Trojan, Shadowsocks, SOCKS and HTTP — but not Hysteria2, TUIC or
/// AnyTLS, and it has no TUN. Those cases are refused here and routed to sing-box instead, so
/// the user never gets a config that silently drops their transport.
/// </summary>
public static class XrayConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    public const string InboundTag = "mixed-in";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Protocols Xray-core cannot dial at all.</summary>
    public static readonly HashSet<string> UnsupportedProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "hysteria2", "tuic", "anytls",
    };

    public static bool CanDial(RouteHop hop)
    {
        if (hop.External?.IsOpenVpn == true) return false;
        if (UnsupportedProtocols.Contains(hop.ProtocolName)) return false;
        if (hop.Node is { } node && node.Transport == TransportKind.Quic) return false;
        return true;
    }

    /// <summary>Explains, in the user's terms, why a hop rules Xray out.</summary>
    public static string? RefusalReason(RouteHop hop)
    {
        if (hop.External is { IsOpenVpn: true } openVpn)
            return $"{openVpn.Name} is an OpenVPN tunnel, which sing-box reaches through its adapter";

        if (hop.Node is { } node)
        {
            if (UnsupportedProtocols.Contains(hop.ProtocolName))
                return $"{node.Name} uses {node.ProtocolLabel}, which Xray-core does not support";
            if (node.Transport == TransportKind.Quic)
                return $"{node.Name} uses the QUIC transport, which Xray-core does not support";
        }
        return null;
    }

    public static string Build(BuildContext ctx)
    {
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = MapLogLevel(ctx.State.Settings.Advanced.LogLevel) },
        };

        root["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = InboundTag,
                ["listen"] = "127.0.0.1",
                ["port"] = ctx.MixedPort,
                // Xray's mixed inbound accepts HTTP and SOCKS5 on one port, like sing-box.
                ["protocol"] = "mixed",
                ["settings"] = new JsonObject
                {
                    ["auth"] = "noauth",
                    ["udp"] = true,
                },
                ["sniffing"] = new JsonObject { ["enabled"] = false },
            },
        };

        var outbounds = new JsonArray();
        var hopCount = ctx.ProxyHops.Count;

        for (var i = 0; i < hopCount; i++)
        {
            var hop = ctx.ProxyHops[i];
            var outbound = BuildHop(hop);
            if (outbound is null) continue;

            // The last hop is the exit and carries the tag everything else refers to.
            outbound["tag"] = i == hopCount - 1 ? ProxyTag : $"chain-{i}";

            // Reach this hop through the one before it.
            if (i > 0)
            {
                var stream = outbound["streamSettings"] as JsonObject ?? new JsonObject();
                var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
                sockopt["dialerProxy"] = $"chain-{i - 1}";
                stream["sockopt"] = sockopt;
                outbound["streamSettings"] = stream;
            }

            outbounds.Add(outbound);
        }

        outbounds.Add(new JsonObject { ["tag"] = DirectTag, ["protocol"] = "freedom" });
        outbounds.Add(new JsonObject { ["tag"] = BlockTag, ["protocol"] = "blackhole" });
        root["outbounds"] = outbounds;

        var rules = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                // Written out rather than "geoip:private" so the config never depends on
                // geoip.dat being present beside the binary.
                ["ip"] = new JsonArray(
                    "127.0.0.0/8",
                    "10.0.0.0/8",
                    "172.16.0.0/12",
                    "192.168.0.0/16",
                    "169.254.0.0/16",
                    "100.64.0.0/10",
                    "::1/128",
                    "fc00::/7",
                    "fe80::/10"),
                ["outboundTag"] = DirectTag,
            },
            new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray("localhost"),
                ["outboundTag"] = DirectTag,
            },
        };

        // Keep the proxy client itself out of the tunnel, same rule the sing-box path uses.
        if (ctx.ExcludedProcessRegexes.Count > 0)
            LogBus.Debug("engine", "Xray cannot express process rules; exclusions are applied at capture time.");

        root["routing"] = new JsonObject
        {
            ["domainStrategy"] = "AsIs",
            ["rules"] = rules,
        };

        return root.ToJsonString(JsonOptions);
    }

    // ------------------------------------------------------------------ hops

    private static JsonObject? BuildHop(RouteHop hop)
    {
        if (hop.Node is { } node) return BuildNode(node);

        // WireGuard is an ordinary outbound in Xray. sing-box was tried first and its
        // userspace endpoint stack is what locked the machine up.
        if (hop.External is { IsWireGuard: true } wireguard)
        {
            if (string.IsNullOrWhiteSpace(wireguard.ImportedConfigText)) return null;
            return WireGuardConfig.Parse(wireguard.ImportedConfigText).ToXrayOutbound(ProxyTag);
        }

        if (hop.External is { } external) return BuildExternal(external);
        return null;
    }

    private static JsonObject? BuildNode(ProxyNode node)
    {
        var outbound = new JsonObject();

        switch (node.Protocol)
        {
            case NodeProtocol.Vless:
                outbound["protocol"] = "vless";
                outbound["settings"] = Vnext(node, new JsonObject
                {
                    ["id"] = node.Uuid ?? "",
                    ["encryption"] = "none",
                    ["flow"] = node.Flow ?? "",
                    ["level"] = 0,
                });
                break;

            case NodeProtocol.Vmess:
                outbound["protocol"] = "vmess";
                outbound["settings"] = Vnext(node, new JsonObject
                {
                    ["id"] = node.Uuid ?? "",
                    ["alterId"] = 0,
                    ["security"] = "auto",
                    ["level"] = 0,
                });
                break;

            case NodeProtocol.Trojan:
                outbound["protocol"] = "trojan";
                outbound["settings"] = Servers(node, new JsonObject
                {
                    ["password"] = node.Password ?? "",
                    ["flow"] = node.Flow ?? "",
                });
                break;

            case NodeProtocol.Shadowsocks:
                outbound["protocol"] = "shadowsocks";
                outbound["settings"] = Servers(node, new JsonObject
                {
                    ["method"] = string.IsNullOrWhiteSpace(node.Method) ? "aes-256-gcm" : node.Method,
                    ["password"] = node.Password ?? "",
                    ["uot"] = false,
                });
                break;

            case NodeProtocol.Socks:
                outbound["protocol"] = "socks";
                outbound["settings"] = Servers(node, new JsonObject
                {
                    ["users"] = Users(node.Username, node.Password),
                });
                break;

            case NodeProtocol.Http:
                outbound["protocol"] = "http";
                outbound["settings"] = Servers(node, new JsonObject
                {
                    ["users"] = Users(node.Username, node.Password),
                });
                break;

            default:
                // Hysteria2 / TUIC / AnyTLS: Xray cannot dial these.
                return null;
        }

        outbound["streamSettings"] = StreamSettings(node);
        return outbound;
    }

    private static JsonObject BuildExternal(ExternalProxy external)
    {
        var isHttp = string.Equals(external.Type, "HTTP", StringComparison.OrdinalIgnoreCase);

        return new JsonObject
        {
            ["protocol"] = isHttp ? "http" : "socks",
            ["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = external.Host,
                        ["port"] = external.Port,
                        ["users"] = Users(external.Username, external.Password),
                    },
                },
            },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "tcp",
                ["security"] = "none",
            },
        };
    }

    private static JsonObject Vnext(ProxyNode node, JsonObject user) => new()
    {
        ["vnext"] = new JsonArray
        {
            new JsonObject
            {
                ["address"] = node.Server,
                ["port"] = node.Port,
                ["users"] = new JsonArray(user),
            },
        },
    };

    private static JsonObject Servers(ProxyNode node, JsonObject server)
    {
        server["address"] = node.Server;
        server["port"] = node.Port;
        return new JsonObject { ["servers"] = new JsonArray(server) };
    }

    private static JsonArray Users(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
            return new JsonArray();

        return new JsonArray(new JsonObject
        {
            ["user"] = username ?? "",
            ["pass"] = password ?? "",
        });
    }

    private static JsonObject StreamSettings(ProxyNode node)
    {
        var network = node.Transport switch
        {
            TransportKind.WebSocket => "ws",
            TransportKind.Grpc => "grpc",
            TransportKind.Http => "h2",
            TransportKind.HttpUpgrade => "httpupgrade",
            TransportKind.Quic => "quic",
            _ => "tcp",
        };

        var settings = new JsonObject { ["network"] = network };

        switch (node.Security)
        {
            case SecurityKind.Tls:
                settings["security"] = "tls";
                settings["tlsSettings"] = TlsSettings(node, reality: false);
                break;
            case SecurityKind.Reality:
                settings["security"] = "reality";
                settings["realitySettings"] = TlsSettings(node, reality: true);
                break;
            default:
                settings["security"] = "none";
                break;
        }

        switch (network)
        {
            case "ws":
                var ws = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : node.Path,
                };
                // Modern Xray reads a top-level "host"; the old headers.Host form is deprecated.
                if (!string.IsNullOrWhiteSpace(node.Host)) ws["host"] = node.Host;
                settings["wsSettings"] = ws;
                break;

            case "grpc":
                settings["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = node.ServiceName ?? node.Path ?? "",
                    ["multiMode"] = false,
                };
                break;

            case "h2":
                settings["httpSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : node.Path,
                    ["host"] = new JsonArray(node.Host ?? ""),
                };
                break;

            case "httpupgrade":
                settings["httpupgradeSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(node.Path) ? "/" : node.Path,
                    ["host"] = node.Host ?? "",
                };
                break;
        }

        return settings;
    }

    private static JsonObject TlsSettings(ProxyNode node, bool reality)
    {
        var tls = new JsonObject
        {
            ["serverName"] = node.Sni ?? "",
            ["allowInsecure"] = node.AllowInsecure,
        };

        if (!string.IsNullOrWhiteSpace(node.Fingerprint))
            tls["fingerprint"] = node.Fingerprint;

        if (node.Alpn.Count > 0)
            tls["alpn"] = new JsonArray(node.Alpn.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());

        if (reality)
        {
            tls["publicKey"] = node.PublicKey ?? "";
            tls["shortId"] = node.ShortId ?? "";
            tls["spiderX"] = node.SpiderX ?? "";
            tls["show"] = false;
        }

        return tls;
    }

    private static string MapLogLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "trace" => "debug",
        "debug" => "debug",
        "warn" or "warning" => "warning",
        "error" => "error",
        _ => "warning",
    };
}
