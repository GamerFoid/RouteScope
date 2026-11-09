using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace RouteScope.Core.Updates;

/// <summary>
/// Downloads release assets. Tries GitHub directly, then walks a list of mirrors,
/// because release downloads are frequently blocked on filtered networks.
/// </summary>
public static class DownloadService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient(string? proxyUrl = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };

        // .NET understands socks5:// here, so the same path works for SOCKS and HTTP proxies.
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RouteScope", "1.0"));
        return client;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>. Returns the source that worked.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        string url,
        string destination,
        string? preferredMirror,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? proxyUrl = null)
    {
        using var owned = proxyUrl is null ? null : CreateClient(proxyUrl);
        var client = owned ?? Http;

        var candidates = new List<string> { url };

        if (!string.IsNullOrWhiteSpace(preferredMirror))
            candidates.Add(preferredMirror.TrimEnd('/') + "/" + url);

        foreach (var mirror in GitHubReleases.DefaultMirrors)
        {
            var candidate = mirror.TrimEnd('/') + "/" + url;
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                candidates.Add(candidate);
        }

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var host = new Uri(candidate).Host;
                LogBus.Info("download", $"Trying {host} …");
                await DownloadOneAsync(client, candidate, destination, progress, ct).ConfigureAwait(false);
                if (new FileInfo(destination).Length > 0)
                {
                    LogBus.Info("download", $"Downloaded {new FileInfo(destination).Length / 1024 / 1024} MB from {host}");
                    return candidate;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                LogBus.Warn("download", $"{new Uri(candidate).Host} failed: {ex.Message}");
                TryDelete(destination);
            }
        }

        LogBus.Error("download", "Every download source failed. " +
                                 (last is null ? "" : "Last error: " + last.Message));
        return null;
    }

    private static async Task DownloadOneAsync(
        HttpClient client, string url, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long read = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;

                if (progress is not null && (sw.Elapsed - lastReport).TotalMilliseconds > 120)
                {
                    lastReport = sw.Elapsed;
                    var speed = sw.Elapsed.TotalSeconds > 0 ? read / sw.Elapsed.TotalSeconds : 0;
                    progress.Report(new DownloadProgress(read, total, speed));
                }
            }
            progress?.Report(new DownloadProgress(read, total, 0));
        }
    }

    /// <summary>Extracts a zip and returns the directory that holds the files.</summary>
    public static string ExtractZip(string zipPath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        ZipFile.ExtractToDirectory(zipPath, destinationDir, overwriteFiles: true);

        // Some archives wrap everything in a single folder; find the shallowest exe.
        var files = Directory.GetFiles(destinationDir, "*", SearchOption.AllDirectories);
        return files.Length > 0 ? Path.GetDirectoryName(files[0])! : destinationDir;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}

public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
    public bool IsIndeterminate => Total <= 0;
    public string Human =>
        Total > 0
            ? $"{Received / 1048576.0:0.0} / {Total / 1048576.0:0.0} MB"
            : $"{Received / 1048576.0:0.0} MB";
    public string SpeedHuman =>
        BytesPerSecond <= 0 ? "" : $"{BytesPerSecond / 1048576.0:0.0} MB/s";
}
