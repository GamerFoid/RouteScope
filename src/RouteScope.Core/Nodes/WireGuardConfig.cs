using System.Text.Json.Nodes;

namespace RouteScope.Core.Nodes;

/// <summary>
/// Parses a WireGuard <c>.conf</c> file. sing-box dials WireGuard itself, so no extra core and
/// no administrator prompt are needed — the tunnel runs inside the engine.
/// </summary>
public sealed class WireGuardConfig
{
    public string PrivateKey { get; private set; } = "";
    public string PublicKey { get; private set; } = "";
    public string? PreSharedKey { get; private set; }
    public string EndpointHost { get; private set; } = "";
    public int EndpointPort { get; private set; } = 51820;
    public List<string> Addresses { get; } = new();
    public List<string> AllowedIps { get; } = new();
    public int? Mtu { get; private set; }
    public int? Keepalive { get; private set; }
    public string? Dns { get; private set; }
    public List<int> Reserved { get; } = new();

    public bool IsUsable => PrivateKey.Length > 0 && PublicKey.Length > 0 &&
                            EndpointHost.Length > 0 && Addresses.Count > 0;

    public string Endpoint => $"{EndpointHost}:{EndpointPort}";

    public static WireGuardConfig Parse(string text)
    {
        var config = new WireGuardConfig();
        var section = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var key = line[..split].Trim().ToLowerInvariant();
            var value = line[(split + 1)..].Trim();

            switch (section)
            {
                case "interface":
                    switch (key)
                    {
                        case "privatekey": config.PrivateKey = value; break;
                        case "address":
                            config.Addresses.Clear();
                            config.Addresses.AddRange(Split(value));
                            break;
                        case "dns": config.Dns = value; break;
                        case "mtu":
                            if (int.TryParse(value, out var mtu)) config.Mtu = mtu;
                            break;
                    }
                    break;

                case "peer":
                    switch (key)
                    {
                        case "publickey": config.PublicKey = value; break;
                        case "presharedkey": config.PreSharedKey = value; break;
                        case "endpoint":
                        {
                            var lastColon = value.LastIndexOf(':');
                            if (lastColon > 0 && int.TryParse(value[(lastColon + 1)..], out var port))
                            {
                                config.EndpointHost = value[..lastColon].Trim('[', ']');
                                config.EndpointPort = port;
                            }
                            else
                            {
                                config.EndpointHost = value;
                            }
                            break;
                        }
                        case "allowedips":
                            config.AllowedIps.Clear();
                            config.AllowedIps.AddRange(Split(value));
                            break;
                        case "persistentkeepalive":
                            if (int.TryParse(value, out var keepalive)) config.Keepalive = keepalive;
                            break;
                        case "reserved":
                            config.Reserved.Clear();
                            foreach (var part in Split(value))
                                if (int.TryParse(part, out var number)) config.Reserved.Add(number);
                            break;
                    }
                    break;
            }
        }

        return config;
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Builds the sing-box endpoint. WireGuard moved from "outbound" to "endpoint" in 1.11;
    /// sing-box exposes its traffic through the Clash API, so live stats keep working.
    /// </summary>
    public JsonObject? ToSingBoxEndpoint(string tag = "proxy")
    {
        if (!IsUsable) return null;

        var peer = new JsonObject
        {
            ["address"] = EndpointHost,
            ["port"] = EndpointPort,
            ["public_key"] = PublicKey,
        };

        if (!string.IsNullOrWhiteSpace(PreSharedKey)) peer["pre_shared_key"] = PreSharedKey;
        if (AllowedIps.Count > 0)
            peer["allowed_ips"] = new JsonArray(AllowedIps.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        if (Keepalive is > 0) peer["persistent_keepalive_interval"] = Keepalive!.Value;

        var endpoint = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = tag,
            ["address"] = new JsonArray(Addresses.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["private_key"] = PrivateKey,
            ["peers"] = new JsonArray(peer),
        };

        if (Mtu is > 0) endpoint["mtu"] = Mtu!.Value;
        if (Reserved.Count == 3)
            endpoint["reserved"] = new JsonArray(Reserved.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());

        return endpoint;
    }

    /// <summary>
    /// Builds the Xray outbound for this tunnel. Kept as an alternative, but the default is
    /// sing-box because Xray has no Clash API and therefore no live connection list.
    /// </summary>
    public JsonObject? ToXrayOutbound(string tag)
    {
        if (!IsUsable) return null;

        var peer = new JsonObject
        {
            ["publicKey"] = PublicKey,
            ["endpoint"] = Endpoint,
        };

        if (!string.IsNullOrWhiteSpace(PreSharedKey)) peer["preSharedKey"] = PreSharedKey;
        if (Keepalive is > 0) peer["keepAlive"] = Keepalive!.Value;
        if (AllowedIps.Count > 0)
            peer["allowedIPs"] = new JsonArray(AllowedIps.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());

        var settings = new JsonObject
        {
            ["address"] = new JsonArray(Addresses.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["secretKey"] = PrivateKey,
            ["peers"] = new JsonArray(peer),
        };

        if (Mtu is > 0) settings["mtu"] = Mtu!.Value;
        if (Reserved.Count == 3)
            settings["reserved"] = new JsonArray(Reserved.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());

        return new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = "wireguard",
            ["settings"] = settings,
        };
    }
}