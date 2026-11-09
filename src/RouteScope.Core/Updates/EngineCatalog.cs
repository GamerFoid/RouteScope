using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RouteScope.Core.Updates;

/// <summary>Describes one downloadable, updatable core binary.</summary>
public sealed class EngineDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Repo { get; init; }
    /// <summary>Executable name inside the archive.</summary>
    public required string ExeName { get; init; }
    /// <summary>Regex matched against release asset names for this platform.</summary>
    public required string AssetPattern { get; init; }
    public string Description { get; init; } = "";
    public string License { get; init; } = "";
    public string Website { get; init; } = "";
    /// <summary>Argument that makes the binary print its version.</summary>
    public string VersionArg { get; init; } = "version";
    /// <summary>True when RouteScope can generate configs for it.</summary>
    public bool IsRunnable { get; init; }
    /// <summary>Companion files that must be extracted next to the exe.</summary>
    public string[] ExtraFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set when the binary is a single file in the repository rather than a release asset.
    /// Psiphon publishes its client this way, with no releases at all.
    /// </summary>
    public string? RepoFilePath { get; init; }

    /// <summary>True when RouteScope can start this client for you.</summary>
    public bool IsManagedClient { get; init; }
}

public static class EngineCatalog
{
    public static string Arch =>
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "amd64",
        };

    public static IReadOnlyList<EngineDefinition> All { get; } = new List<EngineDefinition>
    {
        new()
        {
            Id = "singbox",
            DisplayName = "sing-box",
            Repo = "SagerNet/sing-box",
            ExeName = "sing-box.exe",
            AssetPattern = $@"sing-box-.*-windows-{Arch}\.zip$",
            Description = "The engine RouteScope runs by default: TUN, per-process routing, every modern protocol.",
            License = "GPL-3.0",
            Website = "https://sing-box.sagernet.org",
            VersionArg = "version",
            IsRunnable = true,
            ExtraFiles = new[] { "libcronet.dll", "wintun.dll" },
        },
        new()
        {
            Id = "xray",
            DisplayName = "Xray-core",
            Repo = "XTLS/Xray-core",
            ExeName = "xray.exe",
            AssetPattern = @"^Xray-windows-(64|arm64-v8a)\.zip$",
            Description = "The classic v2rayN engine. Routes VLESS, VMess, Trojan, Shadowsocks, SOCKS and HTTP.",
            License = "MPL-2.0",
            Website = "https://github.com/XTLS/Xray-core",
            VersionArg = "version",
            IsRunnable = true,
            ExtraFiles = new[] { "geoip.dat", "geosite.dat" },
        },
        new()
        {
            Id = "psiphon",
            DisplayName = "Psiphon",
            Repo = "Psiphon-Labs/psiphon-tunnel-core-binaries",
            ExeName = "psiphon-tunnel-core.exe",
            AssetPattern = "",
            RepoFilePath = "windows/psiphon-tunnel-core-i686.exe",
            Description = "Psiphon's tunnel client. RouteScope starts it and dials it through the hop above it.",
            License = "GPL-3.0",
            Website = "https://psiphon.ca",
            IsRunnable = false,
            IsManagedClient = true,
        },
    };

    public static IReadOnlyList<EngineDefinition> Routing =>
        All.Where(d => d.IsRunnable).ToList();

    public static EngineDefinition? Get(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static EngineDefinition SingBox => Get("singbox")!;

    /// <summary>Normalises "v1.14.4" / "1.14.4" / "1.14.4-beta.1" into a comparable version.</summary>
    public static Version ParseVersion(string tag)
    {
        var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return new Version(0, 0);
        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value);
        var patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        return new Version(major, minor, patch);
    }
}
