using System.Text;
using System.Text.Json;
using RouteScope.Core.Models;

namespace RouteScope.Core.Nodes;

/// <summary>
/// Parses the share links people actually paste (vless, vmess, trojan, ss, hysteria2,
/// tuic, socks, http, anytls) plus base64 subscription blobs.
/// </summary>
public static class NodeLinkParser
{
    /// <summary>Parses many links / a whole subscription body. Never throws on a single bad line.</summary>
    public static List<ProxyNode> ParseMany(string input)
    {
        var result = new List<ProxyNode>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var text = input.Trim();

        // A subscription body is usually base64 of newline separated links.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            var decoded = TryBase64Decode(text);
            if (decoded is not null && decoded.Contains("://", StringComparison.Ordinal))
                text = decoded;
        }

        foreach (var raw in text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var node = Parse(line);
            if (node is not null) result.Add(node);
        }

        return result;
    }

    public static ProxyNode? Parse(string link)
    {
        try
        {
            link = link.Trim();
            if (link.Length == 0) return null;

            var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0) return null;
            var scheme = link[..schemeEnd].ToLowerInvariant();

            return scheme switch
            {
                "vless" => ParseUriLike(link, NodeProtocol.Vless),
                "trojan" => ParseUriLike(link, NodeProtocol.Trojan),
                "hysteria2" or "hy2" => ParseUriLike(link, NodeProtocol.Hysteria2),
                "hysteria" => ParseUriLike(link, NodeProtocol.Hysteria2),
                "anytls" => ParseUriLike(link, NodeProtocol.AnyTls),
                "tuic" => ParseUriLike(link, NodeProtocol.Tuic),
                "vmess" => ParseVmess(link),
                "ss" => ParseShadowsocks(link),
                "socks" or "socks5" => ParsePlainProxy(link, NodeProtocol.Socks),
                "http" or "https" => ParsePlainProxy(link, NodeProtocol.Http),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            LogBus.Debug("nodes", $"Skipped unparsable link ({ex.Message})");
            return null;
        }
    }

    // ------------------------------------------------------------ URI style

    private static ProxyNode? ParseUriLike(string link, NodeProtocol protocol)
    {
        var fragment = GetFragment(link);
        var uri = new Uri(StripFragment(link));
        var query = ParseQuery(uri.Query);

        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        var node = new ProxyNode
        {
            Protocol = protocol,
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : DefaultPort(protocol),
            Name = string.IsNullOrWhiteSpace(fragment) ? $"{protocol} {uri.Host}" : fragment!,
            RawLink = link,
        };

        switch (protocol)
        {
            case NodeProtocol.Vless:
                node.Uuid = userInfo;
                node.Flow = Get(query, "flow");
                break;
            case NodeProtocol.Trojan:
            case NodeProtocol.Hysteria2:
            case NodeProtocol.AnyTls:
                node.Password = userInfo;
                break;
            case NodeProtocol.Tuic:
            {
                var parts = userInfo.Split(':', 2);
                node.Uuid = parts[0];
                if (parts.Length > 1) node.Password = parts[1];
                break;
            }
        }

        ApplyCommonQuery(node, query);
        CaptureExtras(node, query);
        return node;
    }

    /// <summary>
    /// Stashes query parameters we do not understand so a copied link keeps them.
    /// </summary>
    private static void CaptureExtras(ProxyNode node, Dictionary<string, string> query)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "type", "security", "sni", "peer", "servername", "fp", "fingerprint",
            "pbk", "publicKey", "sid", "shortId", "spx", "alpn",
            "allowInsecure", "insecure", "allow_insecure",
            "path", "serviceName", "service_name", "host", "authority",
            "flow", "obfs-password", "obfsPassword", "encryption",
        };

        foreach (var pair in query)
        {
            if (!known.Contains(pair.Key))
                node.Extra[pair.Key] = pair.Value;
        }
    }

    private static void ApplyCommonQuery(ProxyNode node, Dictionary<string, string> q)
    {
        var type = Get(q, "type")?.ToLowerInvariant();
        node.Transport = type switch
        {
            "ws" or "websocket" => TransportKind.WebSocket,
            "grpc" or "gun" => TransportKind.Grpc,
            "httpupgrade" => TransportKind.HttpUpgrade,
            "http" or "h2" => TransportKind.Http,
            "quic" => TransportKind.Quic,
            _ => TransportKind.Tcp,
        };

        node.Path = Get(q, "path") ?? Get(q, "serviceName") ?? Get(q, "service_name");
        node.Host = Get(q, "host") ?? Get(q, "authority");
        if (node.Transport == TransportKind.Grpc)
            node.ServiceName = Get(q, "serviceName") ?? Get(q, "service_name") ?? node.Path;

        var security = Get(q, "security")?.ToLowerInvariant();
        node.Security = security switch
        {
            "tls" or "xtls" => SecurityKind.Tls,
            "reality" => SecurityKind.Reality,
            _ => node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.Tuic or NodeProtocol.AnyTls
                ? SecurityKind.Tls
                : SecurityKind.None,
        };

        node.Sni = Get(q, "sni") ?? Get(q, "peer") ?? Get(q, "servername");
        node.Fingerprint = Get(q, "fp") ?? Get(q, "fingerprint");
        node.PublicKey = Get(q, "pbk") ?? Get(q, "publicKey");
        node.ShortId = Get(q, "sid") ?? Get(q, "shortId");
        node.SpiderX = Get(q, "spx");

        var alpn = Get(q, "alpn");
        if (!string.IsNullOrWhiteSpace(alpn))
            node.Alpn = alpn.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

        var insecure = Get(q, "allowInsecure") ?? Get(q, "insecure") ?? Get(q, "allow_insecure");
        node.AllowInsecure = insecure is "1" or "true";

        if (node.Protocol == NodeProtocol.Hysteria2)
        {
            var obfs = Get(q, "obfs-password") ?? Get(q, "obfsPassword");
            if (!string.IsNullOrWhiteSpace(obfs)) node.Path = obfs;
        }
    }

    // ---------------------------------------------------------------- vmess

    private static ProxyNode? ParseVmess(string link)
    {
        var payload = link["vmess://".Length..];
        var json = TryBase64Decode(payload) ?? payload;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        string? S(string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? I(string name)
        {
            if (!r.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
            return null;
        }

        var port = I("port") ?? 443;
        var node = new ProxyNode
        {
            Protocol = NodeProtocol.Vmess,
            Server = S("add") ?? "",
            Port = port,
            Uuid = S("id"),
            Name = S("ps") is { Length: > 0 } ps ? ps : "VMess " + S("add"),
            RawLink = link,
        };

        var net = (S("net") ?? "tcp").ToLowerInvariant();
        node.Transport = net switch
        {
            "ws" => TransportKind.WebSocket,
            "grpc" => TransportKind.Grpc,
            "h2" or "http" => TransportKind.Http,
            "httpupgrade" => TransportKind.HttpUpgrade,
            "quic" => TransportKind.Quic,
            _ => TransportKind.Tcp,
        };

        node.Path = S("path");
        node.Host = S("host");
        if (node.Transport == TransportKind.Grpc) node.ServiceName = node.Path;

        var tls = (S("tls") ?? "").ToLowerInvariant();
        node.Security = tls is "tls" or "reality" ? SecurityKind.Tls : SecurityKind.None;
        node.Sni = S("sni") ?? S("host");
        node.Fingerprint = S("fp");

        var alpn = S("alpn");
        if (!string.IsNullOrWhiteSpace(alpn))
            node.Alpn = alpn.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

        return node;
    }

    // --------------------------------------------------------- shadowsocks

    private static ProxyNode? ParseShadowsocks(string link)
    {
        var body = link["ss://".Length..];
        var fragment = GetFragment(link);
        if (fragment is not null) body = StripFragment(body);

        // Two shapes: ss://base64(method:password)@host:port  and  ss://base64(method:password@host:port)
        string method, password, host;
        int port;

        var at = body.LastIndexOf('@');
        if (at > 0)
        {
            var userPart = body[..at];
            var decoded = TryBase64Decode(userPart) ?? Uri.UnescapeDataString(userPart);
            var colon = decoded.IndexOf(':');
            if (colon < 0) return null;
            method = decoded[..colon];
            password = decoded[(colon + 1)..];

            var hostPart = body[(at + 1)..];
            var qi = hostPart.IndexOf('?');
            if (qi >= 0) hostPart = hostPart[..qi];
            var hp = SplitHostPort(hostPart);
            host = hp.host;
            port = hp.port;
        }
        else
        {
            var decoded = TryBase64Decode(body);
            if (decoded is null) return null;
            var qi = decoded.IndexOf('?');
            if (qi >= 0) decoded = decoded[..qi];
            var at2 = decoded.LastIndexOf('@');
            if (at2 < 0) return null;
            var cred = decoded[..at2];
            var colon = cred.IndexOf(':');
            if (colon < 0) return null;
            method = cred[..colon];
            password = cred[(colon + 1)..];
            var hp = SplitHostPort(decoded[(at2 + 1)..]);
            host = hp.host;
            port = hp.port;
        }

        if (string.IsNullOrWhiteSpace(host)) return null;

        return new ProxyNode
        {
            Protocol = NodeProtocol.Shadowsocks,
            Server = host,
            Port = port,
            Method = method.Trim(),
            Password = password,
            Name = string.IsNullOrWhiteSpace(fragment) ? $"SS {host}" : fragment!,
            RawLink = link,
        };
    }

    // -------------------------------------------------------- socks / http

    private static ProxyNode? ParsePlainProxy(string link, NodeProtocol protocol)
    {
        var fragment = GetFragment(link);
        var body = StripFragment(link);
        var uri = new Uri(body);
        var node = new ProxyNode
        {
            Protocol = protocol,
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : protocol == NodeProtocol.Socks ? 1080 : 8080,
            Name = string.IsNullOrWhiteSpace(fragment) ? $"{protocol} {uri.Host}:{uri.Port}" : fragment!,
            RawLink = link,
        };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2);
            node.Username = parts[0];
            if (parts.Length > 1) node.Password = parts[1];
        }
        return node;
    }

    // -------------------------------------------------------------- helpers

    private static int DefaultPort(NodeProtocol p) => p switch
    {
        NodeProtocol.Hysteria2 => 443,
        NodeProtocol.Tuic => 443,
        NodeProtocol.AnyTls => 443,
        NodeProtocol.Socks => 1080,
        NodeProtocol.Http => 8080,
        _ => 443,
    };

    private static string StripFragment(string link)
    {
        var i = link.IndexOf('#');
        return i < 0 ? link : link[..i];
    }

    private static string? GetFragment(string link)
    {
        var i = link.IndexOf('#');
        if (i < 0 || i == link.Length - 1) return null;
        try { return Uri.UnescapeDataString(link[(i + 1)..]); } catch { return link[(i + 1)..]; }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return d;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) d[Uri.UnescapeDataString(pair)] = "";
            else d[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return d;
    }

    private static string? Get(Dictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static (string host, int port) SplitHostPort(string s)
    {
        s = s.Trim();
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close > 0)
            {
                var h = s[1..close];
                var rest = s[(close + 1)..];
                var p = rest.StartsWith(':') && int.TryParse(rest[1..], out var pp) ? pp : 443;
                return (h, p);
            }
        }
        var idx = s.LastIndexOf(':');
        if (idx > 0 && int.TryParse(s[(idx + 1)..], out var port)) return (s[..idx], port);
        return (s, 443);
    }

    public static string? TryBase64Decode(string input)
    {
        try
        {
            var s = input.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            var bytes = Convert.FromBase64String(s);
            var text = Encoding.UTF8.GetString(bytes);
            // Reject binary junk that happens to decode.
            return text.All(c => c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c)) ? text : null;
        }
        catch { return null; }
    }
}
