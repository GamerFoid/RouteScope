using RouteScope.Core.Models;

namespace RouteScope.Core.Models;

/// <summary>Everything RouteScope persists. One file, human-readable, easy to back up.</summary>
public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    public List<ProxyTarget> Targets { get; set; } = new();
    public List<ProxyNode> Nodes { get; set; } = new();
    public List<ExternalProxy> Externals { get; set; } = new();
    public List<Preset> Presets { get; set; } = new();

    public RouteKind Route { get; set; } = RouteKind.External;
    public string? ActiveNodeId { get; set; }
    public string? ActiveExternalId { get; set; }
    public CaptureMode Mode { get; set; } = CaptureMode.Process;

    /// <summary>
    /// Extra proxies stacked on top of the base route. Empty means "just the base route".
    /// Traffic is tunnelled through these in order and exits from the last one.
    /// </summary>
    public List<ChainHop> Chain { get; set; } = new();

    /// <summary>Saved subscription URLs; their servers live in <see cref="Nodes"/>.</summary>
    public List<Subscription> Subscriptions { get; set; } = new();

    public Settings Settings { get; set; } = new();

    public ProxyNode? ActiveNode => Nodes.FirstOrDefault(n => n.Id == ActiveNodeId);
    public ExternalProxy? ActiveExternal => Externals.FirstOrDefault(e => e.Id == ActiveExternalId);
    public IReadOnlyList<ProxyTarget> EnabledTargets => Targets.Where(t => t.Enabled).ToList();

    public string RouteSummary => Route switch
    {
        RouteKind.Node when ActiveNode is not null => ActiveNode.Name,
        RouteKind.Node => "No server selected",
        RouteKind.External when ActiveExternal is not null => $"{ActiveExternal.Name} · {ActiveExternal.Endpoint}",
        RouteKind.External => "No local proxy selected",
        _ => "Direct (no proxy)",
    };

    /// <summary>Full path description, including stacked hops.</summary>
    public string RoutePath
    {
        get
        {
            if (Route == RouteKind.Direct) return "Direct (no proxy)";

            var parts = new List<string> { RouteSummary };
            foreach (var hop in Chain)
                if (!string.IsNullOrWhiteSpace(hop.DisplayName))
                    parts.Add(hop.DisplayName);

            return string.Join("  →  ", parts);
        }
    }

    /// <summary>Seeds a friendly first-run experience with a local proxy.</summary>
    public void SeedDefaults()
    {
        // Only for a genuinely fresh profile: never re-add something the user deleted.
        if (Externals.Count == 0 && Nodes.Count == 0 && Targets.Count == 0)
        {
            Externals.Add(new ExternalProxy
            {
                Name = "v2rayN",
                Host = "127.0.0.1",
                Port = 10808,
                Type = "Auto",
            });
        }

        ActiveExternalId ??= Externals.FirstOrDefault()?.Id;

        if (Presets.Count == 0)
        {
            Presets.Add(new Preset
            {
                Name = "My apps",
                Glyph = "\uE7B8",
                Route = RouteKind.External,
                ExternalId = ActiveExternalId,
                Mode = CaptureMode.Transparent,
                BuiltIn = true,
            });
        }
    }
}
