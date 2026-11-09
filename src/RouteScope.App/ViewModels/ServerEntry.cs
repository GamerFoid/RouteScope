using RouteScope.Core.Models;

namespace RouteScope.App.ViewModels;

/// <summary>
/// One row of the "Proxy servers" list. Servers you added and configs imported from a
/// subscription are the same thing to the user, so they share a row and a set of actions.
/// </summary>
public sealed class ServerEntry
{
    public ProxyNode? Node { get; init; }

    /// <summary>Set when this row is a whole config imported from a subscription.</summary>
    public ExternalProxy? Config { get; init; }

    public bool IsConfig => Config is not null;
    public bool IsWireGuard => Config?.IsWireGuard == true;
    public bool IsOpenVpn => Config?.IsOpenVpn == true;

    public string Name => Node?.Name ?? Config!.Name;

    public string Endpoint => IsWireGuard
        ? EndpointOf(Config!.ImportedConfigText) + " · WireGuard"
        : IsOpenVpn
            ? Core.Nodes.OpenVpnConfig.Parse(Config!.ImportedConfigText ?? "").Endpoint + " · OpenVPN"
            : IsConfig ? "Xray config · started on demand" : Node!.Endpoint;

    private static string EndpointOf(string? conf) =>
        string.IsNullOrWhiteSpace(conf) ? "tunnel" : Core.Nodes.WireGuardConfig.Parse(conf).Endpoint;

    public string Badge => IsWireGuard ? "WIREGUARD" : IsOpenVpn ? "OPENVPN" : IsConfig ? "CONFIG" : Node!.ProtocolLabel;

    public string LatencyLabel => IsConfig ? "tunnel" : Node!.LatencyLabel;

    public string LatencyBrushKey => IsConfig ? "B.Text.Tertiary" : Node!.LatencyBrushKey;

    public string RealPingLabel => IsConfig ? "on demand" : Node!.RealPingLabel;

    public string RealPingBrushKey => IsConfig ? "B.Text.Tertiary" : Node!.RealPingBrushKey;

    public bool IsActive => IsConfig ? Config!.IsActive : Node!.IsActive;

    public string Id => Node?.Id ?? Config!.Id;
}
