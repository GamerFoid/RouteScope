using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RouteScope.Core.Models;

namespace RouteScope.Core.Nodes;

/// <summary>
/// Downloads a subscription URL and turns whatever comes back (base64 link list, plain
/// link list, or a Clash/sing-box JSON document) into nodes.
/// </summary>
public static class SubscriptionFetcher
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RouteScope", "1.0"));
        // Some providers gate the user-agent.
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "v2rayN/7.0 RouteScope/1.0");
        return client;
    }

    public static async Task<List<ProxyNode>> FetchAsync(string url, CancellationToken ct)
        => await FetchAsync(url, null, ct).ConfigureAwait(false);

    private static HttpClient CreateProxiedClient(string proxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
            Proxy = new WebProxy(proxyUrl),
            UseProxy = true,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "v2rayN/7.0 RouteScope/1.0");
        return client;
    }

    /// <summary>
    /// Downloads through a local proxy when one is given. Useful when the subscription host
    /// itself is blocked and only reachable through the tunnel.
    /// </summary>
    public static async Task<List<ProxyNode>> FetchAsync(string url, string? proxyUrl, CancellationToken ct)
    {
        try
        {
            LogBus.Info("sub", proxyUrl is null
                ? "Fetching subscription…"
                : $"Fetching subscription through {proxyUrl}…");

            var client = proxyUrl is null ? Http : CreateProxiedClient(proxyUrl);
            var body = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            var nodes = Parse(body);
            LogBus.Info("sub", $"Subscription returned {nodes.Count} server(s).");
            return nodes;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogBus.Warn("sub", "Subscription download failed: " + ex.Message);
            return new List<ProxyNode>();
        }
    }

    /// <summary>One entry of a "whole config" subscription.</summary>
    public sealed record CustomConfigItem(string Name, string Json);

    /// <summary>
    /// Some subscriptions hand out complete configs instead of server links. Returns them when
    /// the body is an array of objects that carry their own outbounds, otherwise null.
    /// </summary>
    public static List<CustomConfigItem>? TryParseCustomConfigs(string body)
    {
        var text = body.Trim();
        if (!text.StartsWith('[')) return null;

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonArray;
            if (node is null || node.Count == 0) return null;

            var items = new List<CustomConfigItem>();
            for (var i = 0; i < node.Count; i++)
            {
                if (node[i] is not System.Text.Json.Nodes.JsonObject entry) continue;
                if (entry["outbounds"] is not System.Text.Json.Nodes.JsonArray outbounds || outbounds.Count == 0)
                    continue;

                var name = entry["remarks"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) name = $"Config {i + 1}";

                items.Add(new CustomConfigItem(name!, entry.ToJsonString()));
            }

            return items.Count > 0 ? items : null;
        }
        catch
        {
            return null;
        }
    }
    /// <summary>Downloads the raw body, so callers can decide which format it is.</summary>
    public static async Task<string?> FetchRawAsync(string url, string? proxyUrl, CancellationToken ct)
    {
        try
        {
            var client = proxyUrl is null ? Http : CreateProxiedClient(proxyUrl);
            LogBus.Info("sub", proxyUrl is null
                ? "Fetching subscription…"
                : $"Fetching subscription through {proxyUrl}…");
            return await client.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogBus.Warn("sub", "Subscription download failed: " + ex.Message);
            return null;
        }
    }
    public static List<ProxyNode> Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new List<ProxyNode>();

        var trimmed = body.TrimStart();

        // sing-box style document with outbounds
        if (trimmed.StartsWith('{'))
        {
            var fromJson = TryParseJsonDocument(body);
            if (fromJson.Count > 0) return fromJson;
        }

        var nodes = NodeLinkParser.ParseMany(body);
        if (nodes.Count > 0) return nodes;

        // Clash YAML with a proxies: block — handled by the crude scanner below.
        if (trimmed.Contains("proxies:", StringComparison.OrdinalIgnoreCase))
            return ParseClashProxies(body);

        return nodes;
    }

    private static List<ProxyNode> TryParseJsonDocument(string json)
    {
        var result = new List<ProxyNode>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("outbounds", out var outbounds)) return result;
            if (outbounds.ValueKind != System.Text.Json.JsonValueKind.Array) return result;

            foreach (var o in outbounds.EnumerateArray())
            {
                var type = o.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var protocol = type switch
                {
                    "vless" => NodeProtocol.Vless,
                    "vmess" => NodeProtocol.Vmess,
                    "trojan" => NodeProtocol.Trojan,
                    "shadowsocks" => NodeProtocol.Shadowsocks,
                    "hysteria2" => NodeProtocol.Hysteria2,
                    "tuic" => NodeProtocol.Tuic,
                    "socks" => NodeProtocol.Socks,
                    "http" => NodeProtocol.Http,
                    "anytls" => NodeProtocol.AnyTls,
                    _ => (NodeProtocol?)null,
                };
                if (protocol is null) continue;

                string? S(string name) =>
                    o.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString() : null;

                var node = new ProxyNode
                {
                    Protocol = protocol.Value,
                    Name = S("tag") ?? S("server") ?? type,
                    Server = S("server") ?? "",
                    Port = o.TryGetProperty("server_port", out var sp) && sp.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? sp.GetInt32() : 443,
                    Uuid = S("uuid"),
                    Password = S("password"),
                    Method = S("method"),
                };
                if (!string.IsNullOrWhiteSpace(node.Server)) result.Add(node);
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Very small YAML scanner for Clash subscriptions. It only reads the handful of keys
    /// RouteScope needs, which avoids pulling in a YAML dependency.
    /// </summary>
    private static List<ProxyNode> ParseClashProxies(string yaml)
    {
        var result = new List<ProxyNode>();
        var lines = yaml.Replace("\r", "").Split('\n');

        ProxyNode? current = null;
        var inProxies = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase))
            {
                inProxies = true;
                continue;
            }
            if (!inProxies) continue;
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!line.StartsWith(' ') && !line.StartsWith('-')) break;

            var content = line.TrimStart(' ', '-').Trim();
            var colon = content.IndexOf(':');
            if (colon <= 0) continue;

            var key = content[..colon].Trim();
            var value = content[(colon + 1)..].Trim().Trim('"', '\'');

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && !string.IsNullOrWhiteSpace(current.Server)) result.Add(current);
                current = new ProxyNode { Name = value };
                continue;
            }

            if (current is null) continue;

            switch (key.ToLowerInvariant())
            {
                case "type":
                    current.Protocol = value.ToLowerInvariant() switch
                    {
                        "vless" => NodeProtocol.Vless,
                        "vmess" => NodeProtocol.Vmess,
                        "trojan" => NodeProtocol.Trojan,
                        "ss" or "shadowsocks" => NodeProtocol.Shadowsocks,
                        "hysteria2" or "hy2" => NodeProtocol.Hysteria2,
                        "tuic" => NodeProtocol.Tuic,
                        "socks5" => NodeProtocol.Socks,
                        "http" => NodeProtocol.Http,
                        _ => current.Protocol,
                    };
                    break;
                case "server": current.Server = value; break;
                case "port": if (int.TryParse(value, out var p)) current.Port = p; break;
                case "uuid": current.Uuid = value; break;
                case "password": current.Password = value; break;
                case "cipher": current.Method = value; break;
                case "sni": case "servername": current.Sni = value; break;
                case "network":
                    current.Transport = value.ToLowerInvariant() switch
                    {
                        "ws" => TransportKind.WebSocket,
                        "grpc" => TransportKind.Grpc,
                        "h2" or "http" => TransportKind.Http,
                        _ => TransportKind.Tcp,
                    };
                    break;
                case "tls":
                    if (value is "true" or "1") current.Security = SecurityKind.Tls;
                    break;
                case "skip-cert-verify":
                    current.AllowInsecure = value is "true";
                    break;
                case "ws-opts": case "grpc-opts":
                    break;
            }
        }

        if (current is not null && !string.IsNullOrWhiteSpace(current.Server)) result.Add(current);
        return result.Where(n => !string.IsNullOrWhiteSpace(n.Server)).ToList();
    }

    public static string ToBase64List(IEnumerable<ProxyNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes)
            if (!string.IsNullOrWhiteSpace(n.RawLink)) sb.AppendLine(n.RawLink);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
