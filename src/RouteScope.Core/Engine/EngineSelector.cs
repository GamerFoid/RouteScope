using RouteScope.Core.Models;
using RouteScope.Core.Updates;

namespace RouteScope.Core.Engine;

public enum EngineBackend
{
    SingBox,
    Xray,
}

public sealed record EngineChoice(EngineBackend Engine, string? Note)
{
    public string DisplayName => Engine == EngineBackend.Xray ? "Xray-core" : "sing-box";
}

/// <summary>
/// Picks which core runs the route. Xray is used when the user asks for it and the route is
/// something Xray can actually dial; anything else falls back to sing-box with a reason, so a
/// silent downgrade never happens.
/// </summary>
public static class EngineSelector
{
    public static EngineChoice Choose(AppState state, IReadOnlyList<RouteHop> hops, EngineInstaller installer)
    {
        var singBox = installer.GetActive(EngineCatalog.SingBox);
        var xray = installer.GetActive(EngineCatalog.Get("xray")!);

        var preference = (state.Settings.PreferredEngine ?? "auto").ToLowerInvariant();
        var needsTun = state.Mode == CaptureMode.Transparent;

        // WireGuard is an endpoint in sing-box, and only sing-box exposes the Clash API that
        // powers live connections and traffic totals. Xray can dial it too, but its stats API
        // has no connection list. The earlier sing-box hang was the NetFilter driver, not the
        // endpoint — that is fixed, so sing-box is the right default again.
        var wireGuard = hops.Any(h => h.External?.IsWireGuard == true);

        if (wireGuard)
        {
            if (singBox is null)
                return new(EngineBackend.SingBox,
                    "WireGuard needs sing-box for live stats. Install it under Cores & updates.");

            return new(EngineBackend.SingBox,
                "Routing the WireGuard tunnel with sing-box (live stats enabled).");
        }

        string? refusal = null;
        foreach (var hop in hops)
        {
            refusal = XrayConfigBuilder.RefusalReason(hop);
            if (refusal is not null) break;
        }

        if (preference == "xray")
        {
            if (needsTun)
                return new(EngineBackend.SingBox, "Whole-computer mode needs sing-box — Xray has no TUN. Using sing-box.");

            if (refusal is not null)
                return new(EngineBackend.SingBox, refusal + " — using sing-box instead.");

            if (xray is null)
                return new(EngineBackend.SingBox, "Xray-core is not installed yet — using sing-box.");

            return new(EngineBackend.Xray, "Routing with Xray-core.");
        }

        if (preference == "singbox")
        {
            if (singBox is not null) return new(EngineBackend.SingBox, "Routing with sing-box.");
            if (xray is not null && !needsTun && refusal is null)
                return new(EngineBackend.Xray, "sing-box is not installed — using Xray-core.");
            return new(EngineBackend.SingBox, null);
        }

        // auto
        if (singBox is not null) return new(EngineBackend.SingBox, null);
        if (xray is not null && !needsTun && refusal is null)
            return new(EngineBackend.Xray, "sing-box is not installed — using Xray-core.");

        return new(EngineBackend.SingBox, null);
    }
}
