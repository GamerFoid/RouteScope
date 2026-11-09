using System.Diagnostics;

namespace RouteScope.Core.Updates;

public sealed record InstalledEngine(string EngineId, string Version, string ExePath, DateTime InstalledUtc)
{
    public Version Parsed => EngineCatalog.ParseVersion(Version);
}

/// <summary>
/// Installs engines side by side under %LOCALAPPDATA%\RouteScope\engines\{id}\{version}
/// so an update can never break a working install: the new one is unpacked next to the
/// old one and only becomes "current" once it is complete.
/// </summary>
public sealed class EngineInstaller
{
    private readonly string _enginesRoot;

    public EngineInstaller(string? enginesRoot = null) => _enginesRoot = enginesRoot ?? AppPaths.EnginesDir;

    public IReadOnlyList<InstalledEngine> ListInstalled(EngineDefinition def)
    {
        var dir = Path.Combine(_enginesRoot, def.Id);
        var list = new List<InstalledEngine>();
        if (!Directory.Exists(dir)) return list;

        foreach (var versionDir in Directory.GetDirectories(dir))
        {
            var exe = Path.Combine(versionDir, def.ExeName);
            if (!File.Exists(exe)) continue;

            var name = Path.GetFileName(versionDir);
            if (!IsPlausibleVersion(name))
            {
                // Left behind when a version probe returned an error blob instead of a version.
                LogBus.Warn("engine", $"Ignoring {def.Id} folder with an unusable version name: {Trim(name)}");
                try { Directory.Delete(versionDir, recursive: true); } catch { }
                continue;
            }

            list.Add(new InstalledEngine(def.Id, name, exe, Directory.GetLastWriteTimeUtc(versionDir)));
        }
        return list.OrderByDescending(e => e.Parsed).ToList();
    }

    /// <summary>A version folder name is short and free of JSON punctuation.</summary>
    private static bool IsPlausibleVersion(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 40) return false;
        if (name.Contains('{') || name.Contains('}')) return false;
        if (name.Contains('"')) return false;
        return true;
    }

    private static string Trim(string value) =>
        value.Length <= 60 ? value : value[..60] + "…";

    public InstalledEngine? GetActive(EngineDefinition def, string? pinnedVersion = null)
    {
        var all = ListInstalled(def);
        if (all.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(pinnedVersion))
        {
            var pinned = all.FirstOrDefault(e => string.Equals(e.Version, pinnedVersion, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null) return pinned;
        }
        return all[0];
    }

    /// <summary>
    /// Downloads, unpacks and activates a release. Reports granular progress to the UI.
    /// </summary>
    /// <summary>
    /// Installs a single-file client (Psiphon) from a repository blob. There is no archive to
    /// unpack: the download is the executable.
    /// </summary>
    /// <summary>
    /// Registers a binary the user already has, as an alternative to downloading it. Companion
    /// files sitting next to it (wintun.dll, geoip.dat) are copied along when present.
    /// </summary>
    public InstalledEngine? InstallLocalFile(EngineDefinition def, string sourcePath, string? version)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;

            var label = string.IsNullOrWhiteSpace(version) ? "local" : version;
            var safeVersion = SanitizeVersion(label);
            var targetDir = Path.Combine(_enginesRoot, def.Id, safeVersion);
            Directory.CreateDirectory(targetDir);

            var target = Path.Combine(targetDir, def.ExeName);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, target, overwrite: true);

            var sourceDir = Path.GetDirectoryName(sourcePath)!;
            foreach (var extra in def.ExtraFiles)
            {
                var from = Path.Combine(sourceDir, extra);
                if (File.Exists(from))
                {
                    try { File.Copy(from, Path.Combine(targetDir, extra), overwrite: true); } catch { }
                }
            }

            LogBus.Info("engine", $"{def.DisplayName} registered from {sourcePath} ({label})");
            return new InstalledEngine(def.Id, label, target, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            LogBus.Error("engine", "Could not register the file: " + ex.Message);
            return null;
        }
    }
    public async Task<InstalledEngine?> InstallFromRepoFileAsync(
        EngineDefinition def,
        GitHubReleases.RepoFileInfo file,
        string? preferredMirror,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? proxyUrl = null)
    {
        AppPaths.EnsureCreated();

        var version = file.Sha.Length > 8 ? file.Sha[..8] : file.Sha;
        var safeVersion = SanitizeVersion(version);
        var tempFile = AppPaths.TempDownloadPath(def.ExeName);

        try
        {
            LogBus.Info("engine", $"Downloading {def.DisplayName} {safeVersion}");
            var source = await DownloadService.DownloadAsync(file.DownloadUrl, tempFile, preferredMirror, progress, ct, proxyUrl)
                .ConfigureAwait(false);
            if (source is null) return null;

            ct.ThrowIfCancellationRequested();

            var targetDir = Path.Combine(_enginesRoot, def.Id, safeVersion);
            Directory.CreateDirectory(targetDir);

            var target = Path.Combine(targetDir, def.ExeName);
            File.Copy(tempFile, target, overwrite: true);

            LogBus.Info("engine", $"{def.DisplayName} {safeVersion} installed");
            return new InstalledEngine(def.Id, version, target, DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            LogBus.Info("engine", "Download cancelled");
            return null;
        }
        catch (Exception ex)
        {
            LogBus.Error("engine", "Install failed: " + ex.Message);
            return null;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }
    public async Task<InstalledEngine?> InstallAsync(
        EngineDefinition def,
        ReleaseAsset asset,
        string version,
        string? preferredMirror,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? proxyUrl = null)
    {
        AppPaths.EnsureCreated();
        var safeVersion = SanitizeVersion(version);
        var tempZip = AppPaths.TempDownloadPath(asset.Name);
        var extractDir = Path.Combine(AppPaths.CacheDir, "extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            LogBus.Info("engine", $"Downloading {def.DisplayName} {safeVersion} ({asset.Name})");
            var source = await DownloadService.DownloadAsync(asset.DownloadUrl, tempZip, preferredMirror, progress, ct, proxyUrl)
                .ConfigureAwait(false);
            if (source is null) return null;

            ct.ThrowIfCancellationRequested();
            LogBus.Info("engine", "Unpacking…");
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

            var exe = FindFile(extractDir, def.ExeName);
            if (exe is null)
            {
                LogBus.Error("engine", $"{def.ExeName} was not found inside {asset.Name}");
                return null;
            }

            var targetDir = Path.Combine(_enginesRoot, def.Id, safeVersion);
            Directory.CreateDirectory(targetDir);

            var srcDir = Path.GetDirectoryName(exe)!;
            foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, file);
                var dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }

            // Pull in companion files that live elsewhere in the archive.
            foreach (var extra in def.ExtraFiles)
            {
                if (File.Exists(Path.Combine(targetDir, extra))) continue;
                var found = FindFile(extractDir, extra);
                if (found is not null) File.Copy(found, Path.Combine(targetDir, extra), overwrite: true);
            }

            var installed = new InstalledEngine(def.Id, safeVersion, Path.Combine(targetDir, def.ExeName), DateTime.UtcNow);
            LogBus.Info("engine", $"{def.DisplayName} {safeVersion} installed at {targetDir}");

            PruneOldVersions(def, keep: 2, keepVersion: safeVersion);
            return installed;
        }
        catch (OperationCanceledException)
        {
            LogBus.Warn("engine", "Install cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            LogBus.Error("engine", $"Install failed: {ex.Message}");
            return null;
        }
        finally
        {
            DownloadService.TryDelete(tempZip);
            DownloadService.TryDeleteDir(extractDir);
        }
    }

    private void PruneOldVersions(EngineDefinition def, int keep, string keepVersion)
    {
        try
        {
            var dir = Path.Combine(_enginesRoot, def.Id);
            if (!Directory.Exists(dir)) return;

            var versions = Directory.GetDirectories(dir)
                .OrderByDescending(d => EngineCatalog.ParseVersion(Path.GetFileName(d)))
                .ToList();

            foreach (var v in versions.Skip(keep))
            {
                if (string.Equals(Path.GetFileName(v), keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
                DownloadService.TryDeleteDir(v);
            }
        }
        catch { }
    }

    private static string? FindFile(string root, string fileName)
    {
        var direct = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
        return direct.Length > 0 ? direct[0] : null;
    }

    private static string SanitizeVersion(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        foreach (var c in Path.GetInvalidFileNameChars()) v = v.Replace(c, '_');
        return string.IsNullOrWhiteSpace(v) ? "unknown" : v;
    }

    /// <summary>Runs the engine with its version argument to double check what is installed.</summary>
    public static async Task<string?> ProbeVersionAsync(EngineDefinition def, string exePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath, def.VersionArg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            try { p.Kill(entireProcessTree: true); } catch { }
            var line = (text + "\n" + err).Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
            return line?.Trim();
        }
        catch { return null; }
    }
}
