using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RouteScope.Core.Models;
using RouteScope.Core.Nodes;

namespace RouteScope.Core.Engine;

/// <summary>Everything the config generator needs that is not part of the saved profile.</summary>
public sealed class BuildContext
{
    public required AppState State { get; init; }

    /// <summary>The adapter an OpenVPN tunnel created, when one is in use.</summary>
    public string? BindInterface { get; init; }

    /// <summary>
    /// The resolved route as an ordered list of hops. Each one detours through the previous,
    /// so traffic exits from the last. Empty means "no proxy".
    /// </summary>
    public List<RouteHop> ProxyHops { get; init; } = new();
    public string ProxyTag { get; init; } = "proxy";
    /// <summary>Concrete proxy type actually reachable (used for labels + diagnostics).</summary>
    public string ProxyDescription { get; init; } = "";
    /// <summary>Extra process-path regexes discovered at start time (child process expansion).</summary>
    public List<string> ExtraProcessPathRegexes { get; init; } = new();
    public List<string> ExtraProcessNames { get; init; } = new();
    /// <summary>
    /// Processes that must never enter the tunnel. The upstream proxy client lives here:
    /// capturing it would send its traffic back to itself.
    /// </summary>
    public List<string> ExcludedProcessRegexes { get; init; } = new();
    public bool IncludeTun { get; init; }
    /// <summary>Route everything instead of only the selected targets (whole-computer mode).</summary>
    public bool RouteEverything { get; init; }
    public int MixedPort { get; init; } = 47890;
    public int ClashApiPort { get; init; } = 47891;
    public string ClashSecret { get; init; } = "";
}

/// <summary>
/// Turns a RouteScope profile into a sing-box configuration document.
///
/// The design goal is that only the selected targets are proxied: route rules match
/// process paths/names and send everything else to <c>direct</c>. That is what makes
/// "proxy just this folder" work for programs that have no proxy setting at all.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    public const string ProxyTag = "proxy";
    public const string DnsDirectTag = "dns-direct";
    public const string DnsProxyTag = "dns-proxy";
    public const string TunInTag = "tun-in";
    public const string MixedInTag = "mixed-in";

    public static string Build(BuildContext ctx)
    {
        var state = ctx.State;
        var adv = state.Settings.Advanced;
        var dns = state.Settings.Dns;

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = string.IsNullOrWhiteSpace(adv.LogLevel) ? "info" : adv.LogLevel,
                ["timestamp"] = true,
            },
        };

        root["dns"] = BuildDns(ctx);
        root["inbounds"] = BuildInbounds(ctx);

        // WireGuard is a whole tunnel, not a proxy. sing-box 1.11+ emits it as an "endpoint",
        // and the Clash API reports its traffic just like any other outbound, so live
        // connections and upload/download totals keep working. The earlier hang was the
        // NetFilter driver, not this endpoint — that is fixed, so we can use sing-box again.
        var wireGuard = ctx.ProxyHops.FirstOrDefault(h => h.External?.IsWireGuard == true)?.External;

        if (wireGuard is not null)
        {
            if (ctx.ProxyHops.Count > 1)
                throw new InvalidOperationException(
                    "A WireGuard tunnel has to be the only step in the route. It is a network " +
                    "interface, not a proxy, so nothing can be chained through it.");

            var endpoint = BuildWireGuardEndpoint(wireGuard, ProxyTag);
            if (endpoint is null)
                throw new InvalidOperationException(
                    "That WireGuard file is missing a key, an address or an endpoint.");

            root["endpoints"] = new JsonArray(endpoint);
        }

        // An OpenVPN tunnel is not a proxy: the engine reaches the internet through the
        // adapter OpenVPN created, and only the engine is pinned to it.
        var openVpn = ctx.ProxyHops.FirstOrDefault(h => h.External?.IsOpenVpn == true)?.External;

        if (openVpn is not null)
        {
            if (ctx.ProxyHops.Count > 1)
                throw new InvalidOperationException(
                    "An OpenVPN tunnel has to be the only step in the route. It is a network " +
                    "interface, not a proxy, so nothing can be chained through it.");

            if (string.IsNullOrWhiteSpace(ctx.BindInterface))
                throw new InvalidOperationException(
                    "The OpenVPN tunnel is not running, so there is no adapter to route through.");
        }

        root["outbounds"] = BuildOutbounds(ctx);
        root["route"] = BuildRoute(ctx);

        var experimental = new JsonObject();
        if (adv.EnableClashApi && ctx.ClashApiPort > 0)
        {
            experimental["clash_api"] = new JsonObject
            {
                ["external_controller"] = $"127.0.0.1:{ctx.ClashApiPort}",
                ["secret"] = ctx.ClashSecret,
                ["default_mode"] = "rule",
            };
        }
        experimental["cache_file"] = new JsonObject { ["enabled"] = true };
        root["experimental"] = experimental;

        // Free-form power-user override, merged last so it wins.
        if (!string.IsNullOrWhiteSpace(adv.ConfigOverrideJson))
        {
            try
            {
                if (JsonNode.Parse(adv.ConfigOverrideJson) is JsonObject ov)
                    MergeInto(root, ov);
                else
                    LogBus.Warn("config", "Advanced override must be a JSON object; ignored.");
            }
            catch (Exception ex)
            {
                LogBus.Warn("config", "Advanced override is not valid JSON and was ignored: " + ex.Message);
            }
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    // ------------------------------------------------------------------ DNS

    private static JsonObject BuildDns(BuildContext ctx)
    {
        var dns = ctx.State.Settings.Dns;
        var servers = new JsonArray();
        var rules = new JsonArray();

        var directServer = FirstDirectServer(dns);
        servers.Add(BuildDnsServer(DnsDirectTag, directServer, detour: null));

        var hasProxy = ctx.ProxyHops.Count > 0;
        string finalTag = DnsDirectTag;

        if (hasProxy && !string.Equals(dns.Mode, "system", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(dns.Mode, "direct", StringComparison.OrdinalIgnoreCase))
        {
            var remote = dns.RemoteServers.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (!string.IsNullOrWhiteSpace(remote))
            {
                servers.Add(BuildDnsServer(DnsProxyTag, remote, detour: ProxyTag));
                finalTag = DnsProxyTag;
            }
        }

        var node = new JsonObject
        {
            ["servers"] = servers,
            ["final"] = finalTag,
            ["strategy"] = dns.PreferIpv4 ? "prefer_ipv4" : "prefer_ipv6",
        };

        if (dns.EnableFakeIp)
        {
            // Fake-IP needs the DNS rules to know which domains should be materialised.
            node["independent_cache"] = true;
            rules.Add(new JsonObject
            {
                ["query_type"] = new JsonArray("A", "AAAA"),
                ["server"] = finalTag == DnsProxyTag ? DnsProxyTag : DnsDirectTag,
            });
        }

        if (rules.Count > 0) node["rules"] = rules;
        return node;
    }

    private static string FirstDirectServer(DnsSettings dns)
    {
        var s = dns.DirectServers.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return string.IsNullOrWhiteSpace(s) ? "1.1.1.1" : s.Trim();
    }

    private static JsonObject BuildDnsServer(string tag, string address, string? detour)
    {
        var obj = new JsonObject { ["tag"] = tag };

        if (address.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            address.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            obj["type"] = "local";
            return obj;
        }

        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("h3://", StringComparison.OrdinalIgnoreCase))
        {
            obj["type"] = address.StartsWith("h3", StringComparison.OrdinalIgnoreCase) ? "h3" : "https";
            var host = Regex.Replace(address, @"^(https|h3)://", "", RegexOptions.IgnoreCase);
            host = host.Split('/')[0];
            var path = "/dns-query";
            var slash = address.IndexOf('/', address.IndexOf("://", StringComparison.Ordinal) + 3);
            if (slash > 0) path = address[slash..];
            obj["server"] = StripPort(host, out var port);
            if (port is > 0) obj["server_port"] = port;
            if (path != "/dns-query") obj["path"] = path;
        }
        else if (address.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
        {
            obj["type"] = "tls";
            var host = Regex.Replace(address, "^tls://", "", RegexOptions.IgnoreCase);
            obj["server"] = StripPort(host, out var port);
            if (port is > 0) obj["server_port"] = port;
        }
        else if (address.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            obj["type"] = "tcp";
            var host = Regex.Replace(address, "^tcp://", "", RegexOptions.IgnoreCase);
            obj["server"] = StripPort(host, out var port);
            if (port is > 0) obj["server_port"] = port;
        }
        else if (address.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            obj["type"] = "udp";
            var host = Regex.Replace(address, "^udp://", "", RegexOptions.IgnoreCase);
            obj["server"] = StripPort(host, out var port);
            if (port is > 0) obj["server_port"] = port;
        }
        else
        {
            obj["type"] = "udp";
            obj["server"] = StripPort(address, out var port);
            if (port is > 0) obj["server_port"] = port;
        }

        if (!string.IsNullOrEmpty(detour)) obj["detour"] = detour;
        return obj;
    }

    private static string StripPort(string hostPort, out int port)
    {
        port = 0;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close > 0) return hostPort[..(close + 1)];
            return hostPort;
        }
        var idx = hostPort.LastIndexOf(':');
        if (idx > 0 && int.TryParse(hostPort[(idx + 1)..], out var p))
        {
            port = p;
            return hostPort[..idx];
        }
        return hostPort;
    }

    // ------------------------------------------------------------- inbounds

    private static JsonArray BuildInbounds(BuildContext ctx)
    {
        var adv = ctx.State.Settings.Advanced;
        var inbounds = new JsonArray();

        if (ctx.IncludeTun)
        {
            var octet = Math.Clamp(adv.TunAddressPrefix, 1, 254);
            var tun = new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = TunInTag,
                ["address"] = new JsonArray($"172.{octet}.0.1/30", $"fdfe:dcba:{octet:x}::1/126"),
                ["mtu"] = Math.Clamp(adv.Mtu, 1280, 65535),
                ["auto_route"] = adv.AutoRoute,
                ["strict_route"] = adv.StrictRoute,
                ["stack"] = string.IsNullOrWhiteSpace(adv.TunStack) ? "mixed" : adv.TunStack,
            };
            inbounds.Add(tun);
        }

        inbounds.Add(new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = MixedInTag,
            ["listen"] = "127.0.0.1",
            ["listen_port"] = ctx.MixedPort,
        });

        return inbounds;
    }

    // ------------------------------------------------------------ outbounds

    private static JsonArray BuildOutbounds(BuildContext ctx)
    {
        var outbounds = new JsonArray();
        for (var i = 0; i < ctx.ProxyHops.Count; i++)
        {
            var outbound = BuildHop(ctx.ProxyHops[i], ctx.BindInterface);
            if (outbound is null) continue;

            // The last hop is the exit, and carries the tag everything else refers to.
            outbound["tag"] = i == ctx.ProxyHops.Count - 1 ? ProxyTag : $"chain-{i}";

            // Reach this hop through the one before it.
            if (i > 0) outbound["detour"] = $"chain-{i - 1}";

            outbounds.Add(outbound);
        }
        // With an OpenVPN tunnel, even "direct" traffic (LAN-bypass, DNS) must leave through
        // the tunnel adapter. Otherwise a poisoned private IP such as the ISP's DNS sinkhole
        // matches the LAN-bypass rule, goes out the physical adapter and times out.
        var direct = new JsonObject { ["type"] = "direct", ["tag"] = DirectTag };
        if (!string.IsNullOrWhiteSpace(ctx.BindInterface)) direct["bind_interface"] = ctx.BindInterface;
        outbounds.Add(direct);
        outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = BlockTag });

        if (!string.IsNullOrWhiteSpace(ctx.State.Settings.Advanced.ExtraOutboundsJson))
        {
            try
            {
                if (JsonNode.Parse(ctx.State.Settings.Advanced.ExtraOutboundsJson) is JsonArray extra)
                    foreach (var item in extra.ToList())
                    {
                        extra.Remove(item);
                        outbounds.Add(item);
                    }
            }
            catch (Exception ex)
            {
                LogBus.Warn("config", "Extra outbounds are not valid JSON and were ignored: " + ex.Message);
            }
        }

        return outbounds;
    }

    // ---------------------------------------------------------------- route

    private static JsonObject BuildRoute(BuildContext ctx)
    {
        var state = ctx.State;
        var adv = state.Settings.Advanced;
        var rules = new JsonArray();

        rules.Add(new JsonObject { ["action"] = "sniff" });
        rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });

        // Keep local network reachable even for proxied apps (games, printers, NAS).
        if (adv.BypassLan)
        {
            rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag });
        }

        var hasProxy = ctx.ProxyHops.Count > 0;

        // Never tunnel the upstream proxy client itself.
        if (hasProxy && ctx.ExcludedProcessRegexes.Count > 0)
        {
            var excluded = new JsonArray();
            foreach (var re in ctx.ExcludedProcessRegexes)
                if (!excluded.Any(x => (string?)x == re))
                    excluded.Add(re);
            rules.Add(new JsonObject { ["process_path_regex"] = excluded, ["outbound"] = DirectTag });
        }

        if (hasProxy && !ctx.RouteEverything)
        {
            var pathRegexes = new JsonArray();
            var processNames = new JsonArray();

            foreach (var t in state.EnabledTargets)
            {
                var re = t.ToProcessPathRegex();
                if (!string.IsNullOrWhiteSpace(re) && !pathRegexes.Any(x => (string?)x == re))
                    pathRegexes.Add(re);

                var name = t.ToProcessName();
                if (!string.IsNullOrWhiteSpace(name) && !processNames.Any(x => (string?)x == name))
                    processNames.Add(name);
            }

            foreach (var extra in ctx.ExtraProcessPathRegexes)
                if (!pathRegexes.Any(x => (string?)x == extra))
                    pathRegexes.Add(extra);

            foreach (var extra in ctx.ExtraProcessNames)
                if (!processNames.Any(x => (string?)x == extra))
                    processNames.Add(extra);

            // One rule per target keeps the log readable ("matched rule N").
            if (pathRegexes.Count > 0 || processNames.Count > 0)
            {
                var rule = new JsonObject();
                if (pathRegexes.Count > 0) rule["process_path_regex"] = pathRegexes;
                if (processNames.Count > 0) rule["process_name"] = processNames;
                rule["outbound"] = ProxyTag;
                rules.Add(rule);
            }
        }

        if (!string.IsNullOrWhiteSpace(adv.ExtraRouteRulesJson))
        {
            try
            {
                if (JsonNode.Parse(adv.ExtraRouteRulesJson) is JsonArray extra)
                    foreach (var item in extra.ToList())
                    {
                        extra.Remove(item);
                        rules.Add(item);
                    }
            }
            catch (Exception ex)
            {
                LogBus.Warn("config", "Extra route rules are not valid JSON and were ignored: " + ex.Message);
            }
        }

        var route = new JsonObject
        {
            ["rules"] = rules,
            ["final"] = hasProxy && ctx.RouteEverything ? ProxyTag : DirectTag,
            // With an OpenVPN tunnel the engine is pinned to a specific adapter. Auto-detecting
            // the default interface would fight that binding, so it is switched off.
            ["auto_detect_interface"] = string.IsNullOrWhiteSpace(ctx.BindInterface),
            ["default_domain_resolver"] = DnsDirectTag,
        };

        return route;
    }

    // --------------------------------------------------------------- helpers

    private static void MergeInto(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            overlay.Remove(key);
            if (value is JsonObject vo && target[key] is JsonObject existing)
                MergeInto(existing, (JsonObject)vo.DeepClone());
            else
                target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Builds the outbound object for a node. Exposed so the UI can preview what will run.
    /// </summary>
    /// <summary>Materialises one route hop as a sing-box outbound.</summary>
    public static JsonObject? BuildHop(RouteHop hop, string? bindInterface = null) => hop switch
    {
        // An OpenVPN tunnel is a network interface, so the "outbound" is direct but pinned
        // to the adapter OpenVPN created.
        { External.IsOpenVpn: true } => bindInterface is null
            ? null
            : new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = "proxy",
                ["bind_interface"] = bindInterface,
            },
        { Node: { } node } => BuildNodeOutbound(node),
        { External.IsWireGuard: true } => null,   // Xray dials WireGuard; sing-box does not
        { External: { } external } => BuildExternalOutbound(external),
        _ => null,
    };


    /// <summary>
    /// Builds the sing-box WireGuard endpoint (1.11+ schema). Only the dialled mixed inbound
    /// details come from here; the tunnel's own DNS/routing stay exactly as the file says.
    /// </summary>
    public static JsonObject? BuildWireGuardEndpoint(ExternalProxy external, string tag = "proxy")
    {
        if (string.IsNullOrWhiteSpace(external.ImportedConfigText)) return null;
        return WireGuardConfig.Parse(external.ImportedConfigText).ToSingBoxEndpoint(tag);
    }

    public static JsonObject? BuildNodeOutbound(ProxyNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Server)) return null;

        var o = new JsonObject
        {
            ["server"] = node.Server.Trim(),
            ["server_port"] = node.Port,
        };

        switch (node.Protocol)
        {
            case NodeProtocol.Vless:
                o["type"] = "vless";
                o["uuid"] = node.Uuid ?? "";
                if (!string.IsNullOrWhiteSpace(node.Flow)) o["flow"] = node.Flow;
                break;

            case NodeProtocol.Vmess:
                o["type"] = "vmess";
                o["uuid"] = node.Uuid ?? "";
                o["security"] = "auto";
                o["alter_id"] = 0;
                break;

            case NodeProtocol.Trojan:
                o["type"] = "trojan";
                o["password"] = node.Password ?? "";
                break;

            case NodeProtocol.Shadowsocks:
                o["type"] = "shadowsocks";
                o["method"] = string.IsNullOrWhiteSpace(node.Method) ? "aes-256-gcm" : node.Method;
                o["password"] = node.Password ?? "";
                break;

            case NodeProtocol.Hysteria2:
                o["type"] = "hysteria2";
                o["password"] = node.Password ?? "";
                if (!string.IsNullOrWhiteSpace(node.Path)) o["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = node.Path };
                break;

            case NodeProtocol.Tuic:
                o["type"] = "tuic";
                o["uuid"] = node.Uuid ?? "";
                o["password"] = node.Password ?? "";
                break;

            case NodeProtocol.AnyTls:
                o["type"] = "anytls";
                o["password"] = node.Password ?? "";
                break;

            case NodeProtocol.Socks:
                o["type"] = "socks";
                o["version"] = "5";
                if (!string.IsNullOrWhiteSpace(node.Username)) o["username"] = node.Username;
                if (!string.IsNullOrWhiteSpace(node.Password)) o["password"] = node.Password;
                break;

            case NodeProtocol.Http:
                o["type"] = "http";
                if (!string.IsNullOrWhiteSpace(node.Username)) o["username"] = node.Username;
                if (!string.IsNullOrWhiteSpace(node.Password)) o["password"] = node.Password;
                break;

            case NodeProtocol.WireGuard:
                o["type"] = "wireguard";
                o["private_key"] = node.Password ?? "";
                if (!string.IsNullOrWhiteSpace(node.Uuid))
                    o["peer_public_key"] = node.Uuid;
                o["local_address"] = new JsonArray("10.0.0.2/32");
                break;

            default:
                return null;
        }

        var tls = BuildTls(node);
        if (tls is not null) o["tls"] = tls;

        var transport = BuildTransport(node);
        if (transport is not null) o["transport"] = transport;

        return o;
    }

    private static JsonObject? BuildTls(ProxyNode node)
    {
        var needsTls = node.Security != SecurityKind.None
                       || node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.Tuic or NodeProtocol.AnyTls;
        if (!needsTls) return null;

        var tls = new JsonObject { ["enabled"] = true };

        var sni = node.Sni;
        if (string.IsNullOrWhiteSpace(sni) && !IsIpAddress(node.Server)) sni = node.Server;
        if (!string.IsNullOrWhiteSpace(sni)) tls["server_name"] = sni;

        if (node.AllowInsecure) tls["insecure"] = true;

        if (node.Alpn.Count > 0)
        {
            var alpn = new JsonArray();
            foreach (var a in node.Alpn.Where(a => !string.IsNullOrWhiteSpace(a))) alpn.Add(a.Trim());
            if (alpn.Count > 0) tls["alpn"] = alpn;
        }

        if (node.Security == SecurityKind.Reality)
        {
            var reality = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = node.PublicKey ?? "",
                ["short_id"] = node.ShortId ?? "",
            };
            tls["reality"] = reality;
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fingerprint) ? "chrome" : node.Fingerprint,
            };
        }
        else if (!string.IsNullOrWhiteSpace(node.Fingerprint))
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = node.Fingerprint };
        }

        return tls;
    }

    private static JsonObject? BuildTransport(ProxyNode node)
    {
        switch (node.Transport)
        {
            case TransportKind.WebSocket:
            {
                var t = new JsonObject { ["type"] = "ws" };
                if (!string.IsNullOrWhiteSpace(node.Path)) t["path"] = node.Path;
                if (!string.IsNullOrWhiteSpace(node.Host))
                    t["headers"] = new JsonObject { ["Host"] = node.Host };
                return t;
            }
            case TransportKind.Grpc:
            {
                var t = new JsonObject { ["type"] = "grpc" };
                if (!string.IsNullOrWhiteSpace(node.ServiceName)) t["service_name"] = node.ServiceName;
                return t;
            }
            case TransportKind.HttpUpgrade:
            {
                var t = new JsonObject { ["type"] = "httpupgrade" };
                if (!string.IsNullOrWhiteSpace(node.Path)) t["path"] = node.Path;
                if (!string.IsNullOrWhiteSpace(node.Host)) t["host"] = node.Host;
                return t;
            }
            case TransportKind.Http:
            {
                var t = new JsonObject { ["type"] = "http" };
                if (!string.IsNullOrWhiteSpace(node.Path)) t["path"] = node.Path;
                if (!string.IsNullOrWhiteSpace(node.Host)) t["host"] = new JsonArray(node.Host);
                return t;
            }
            case TransportKind.Quic:
            {
                return new JsonObject { ["type"] = "quic" };
            }
            default:
                return null;
        }
    }

    public static bool IsIpAddress(string? s) =>
        !string.IsNullOrWhiteSpace(s) && System.Net.IPAddress.TryParse(s.Trim(), out _);

    /// <summary>Outbound that chains to an already running local proxy (v2rayN, Clash…).</summary>
    public static JsonObject BuildExternalOutbound(ExternalProxy ext)
    {
        var type = ext.Type?.ToLowerInvariant();
        var o = new JsonObject
        {
            ["type"] = type == "http" ? "http" : "socks",
            ["server"] = string.IsNullOrWhiteSpace(ext.Host) ? "127.0.0.1" : ext.Host.Trim(),
            ["server_port"] = ext.Port,
        };
        if (type != "http") o["version"] = "5";
        if (!string.IsNullOrWhiteSpace(ext.Username)) o["username"] = ext.Username;
        if (!string.IsNullOrWhiteSpace(ext.Password)) o["password"] = ext.Password;
        return o;
    }
}
