using System.Text;
using System.Text.Json.Nodes;
using RouteScope.Core.Models;

namespace RouteScope.Core.Nodes;

/// <summary>
/// Turns a saved server back into a share link, so a server you imported can be copied out
/// again — including any query parameters this app does not model.
/// </summary>
public static class NodeLinkWriter
{
    public static string ToLink(ProxyNode node)
    {
        var name = Uri.EscapeDataString(node.Name ?? "");
        return node.Protocol switch
        {
            NodeProtocol.Vmess => BuildVmess(node),
            NodeProtocol.Shadowsocks => BuildShadowsocks(node, name),
            _ => BuildUri(node, name),
        };
    }

    private static string SchemeOf(NodeProtocol protocol) => protocol switch
    {
        NodeProtocol.Vless => "vless",
        NodeProtocol.Trojan => "trojan",
        NodeProtocol.Hysteria2 => "hysteria2",
        NodeProtocol.Tuic => "tuic",
        NodeProtocol.AnyTls => "anytls",
        NodeProtocol.Socks => "socks",
        NodeProtocol.Http => "http",
        _ => "vless",
    };

    private static string TransportToken(TransportKind transport) => transport switch
    {
        TransportKind.WebSocket => "ws",
        TransportKind.Grpc => "grpc",
        TransportKind.HttpUpgrade => "httpupgrade",
        TransportKind.Http => "h2",
        TransportKind.Quic => "quic",
        _ => "tcp",
    };

    private static string? SecurityToken(SecurityKind security) => security switch
    {
        SecurityKind.Tls => "tls",
        SecurityKind.Reality => "reality",
        _ => null,
    };

    private static string BuildUri(ProxyNode node, string name)
    {
        var credentials = node.Protocol switch
        {
            NodeProtocol.Vless => node.Uuid ?? "",
            NodeProtocol.Trojan or NodeProtocol.Hysteria2 or NodeProtocol.AnyTls => node.Password ?? "",
            NodeProtocol.Tuic => string.IsNullOrEmpty(node.Password) ? node.Uuid ?? "" : $"{node.Uuid}:{node.Password}",
            NodeProtocol.Socks or NodeProtocol.Http =>
                string.IsNullOrEmpty(node.Password) ? node.Username ?? "" : $"{node.Username}:{node.Password}",
            _ => "",
        };

        var query = new List<string>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                query.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        if (node.Protocol == NodeProtocol.Vless) Add("encryption", "none");

        Add("type", TransportToken(node.Transport));
        Add("security", SecurityToken(node.Security));
        Add("sni", node.Sni);
        Add("fp", node.Fingerprint);
        if (node.Alpn.Count > 0) Add("alpn", string.Join(",", node.Alpn));
        if (node.AllowInsecure) Add("allowInsecure", "1");
        Add("pbk", node.PublicKey);
        Add("sid", node.ShortId);
        Add("spx", node.SpiderX);
        Add("flow", node.Flow);

        if (node.Transport == TransportKind.Grpc)
            Add("serviceName", string.IsNullOrWhiteSpace(node.ServiceName) ? node.Path : node.ServiceName);
        else
            Add("path", node.Path);

        Add("host", node.Host);

        // Parameters we never modelled, replayed exactly as they arrived.
        foreach (var pair in node.Extra)
            Add(pair.Key, pair.Value);

        var userPart = "";
        if (!string.IsNullOrEmpty(credentials))
        {
            userPart = credentials.Contains(':')
                ? string.Join(":", credentials.Split(':', 2).Select(Uri.EscapeDataString))
                : Uri.EscapeDataString(credentials);
            userPart += "@";
        }

        var suffix = query.Count > 0 ? "?" + string.Join("&", query) : "";
        return $"{SchemeOf(node.Protocol)}://{userPart}{node.Server}:{node.Port}{suffix}#{name}";
    }

    private static string BuildShadowsocks(ProxyNode node, string name)
    {
        var userInfo = Base64Url(Encoding.UTF8.GetBytes($"{node.Method ?? "aes-256-gcm"}:{node.Password ?? ""}"));
        return $"ss://{userInfo}@{node.Server}:{node.Port}#{name}";
    }

    private static string BuildVmess(ProxyNode node)
    {
        var payload = new JsonObject
        {
            ["v"] = "2",
            ["ps"] = node.Name ?? "",
            ["add"] = node.Server,
            ["port"] = node.Port.ToString(),
            ["id"] = node.Uuid ?? "",
            ["aid"] = "0",
            ["scy"] = "auto",
            ["net"] = TransportToken(node.Transport),
            ["type"] = "none",
            ["host"] = node.Host ?? "",
            ["path"] = node.Path ?? "",
            ["tls"] = node.Security == SecurityKind.Tls ? "tls" : "",
            ["sni"] = node.Sni ?? "",
            ["alpn"] = string.Join(",", node.Alpn),
            ["fp"] = node.Fingerprint ?? "",
        };

        return "vmess://" + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
