using System.Text.Json.Serialization;

namespace RouteScope.Core.Models;

/// <summary>
/// One extra proxy stacked on top of the base route. Hops are applied in order: the first hop
/// is reached directly, the next one is reached through it, and so on. Traffic exits through
/// the last hop.
/// </summary>
public sealed class ChainHop : NotifyBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>External (a local proxy) or Node (one of your servers).</summary>
    public RouteKind Kind { get; set; } = RouteKind.Node;

    /// <summary>Id of the external proxy or node this hop points at.</summary>
    public string RefId { get; set; } = "";

    // Display-only, filled in by the view model.
    [JsonIgnore] public string DisplayName { get; set; } = "";
    [JsonIgnore] public string Endpoint { get; set; } = "";
    [JsonIgnore] public string Badge { get; set; } = "";

    [JsonIgnore] public string Glyph => Kind == RouteKind.External ? "\uE774" : "\uE968";

    private int? _pingMs;

    /// <summary>Time to reach the internet through this hop and everything above it.</summary>
    [JsonIgnore]
    public int? PingMs
    {
        get => _pingMs;
        set
        {
            if (Set(ref _pingMs, value)) RaiseAll(nameof(PingLabel), nameof(PingBrushKey));
        }
    }

    /// <summary>True when the hop is a client RouteScope starts itself (Psiphon, a custom config).</summary>
    [JsonIgnore] public bool IsManaged { get; set; }

    [JsonIgnore]
    public string PingLabel => IsManaged
        ? "on demand"
        : _pingMs switch
        {
            null => "ping",
            < 0 => "no route",
            var ms => $"{ms} ms",
        };

    [JsonIgnore]
    public string PingBrushKey => IsManaged
        ? "B.Text.Tertiary"
        : _pingMs switch
        {
            null => "B.Text.Tertiary",
            < 0 => "B.Danger",
            > 700 => "B.Warning",
            _ => "B.Success",
        };

    public ChainHop Clone() => (ChainHop)MemberwiseClone();
}
