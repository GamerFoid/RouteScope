using RouteScope.Core.Models;

namespace RouteScope.Core.Engine;

/// <summary>
/// One resolved step of the route, in the engine-neutral form the config builders consume.
/// The base route is hop 0; each additional chain entry follows. Traffic exits from the last.
/// </summary>
public sealed record RouteHop
{
    /// <summary>Set when this hop is one of the user's own servers.</summary>
    public ProxyNode? Node { get; init; }

    /// <summary>Set when this hop is a local proxy already running on the machine.</summary>
    public ExternalProxy? External { get; init; }

    public string Label { get; init; } = "";

    /// <summary>Protocol name used for engine-compatibility checks (vless, hysteria2, socks…).</summary>
    public string ProtocolName => Node is not null
        ? Node.Protocol.ToString().ToLowerInvariant()
        : (External?.DetectedType ?? External?.Type ?? "socks").ToLowerInvariant();

    public static RouteHop From(ProxyNode node) => new()
    {
        Node = node,
        Label = node.Name,
    };

    public static RouteHop From(ExternalProxy external) => new()
    {
        External = external,
        Label = $"{external.Name} · {external.Endpoint}",
    };
}
