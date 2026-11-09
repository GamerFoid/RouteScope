using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteScope.Core.Updates;

public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl);

public sealed record ReleaseInfo(
    string Tag,
    string Version,
    DateTime? PublishedUtc,
    string Notes,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Talks to the GitHub releases API and, when that is blocked or rate limited,
/// falls back to a list of community mirrors. Both are common in filtered networks.
/// </summary>
public sealed class GitHubReleases
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Mirror prefixes tried in order when a direct download fails.</summary>
    public static readonly string[] DefaultMirrors =
    {
        "https://ghproxy.net/",
        "https://gh-proxy.com/",
        "https://github.moeyy.xyz/",
        "https://ghfast.top/",
    };

    /// <summary>
    /// Builds a client. Passing a proxy makes the API lookups travel the same way the download
    /// does — without this the version check fails even when the proxy itself is fine.
    /// </summary>
    private static HttpClient CreateClient(string? proxyUrl = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
        };

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RouteScope", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static void SetToken(string? token)
    {
        Http.DefaultRequestHeaders.Authorization =
            string.IsNullOrWhiteSpace(token) ? null : new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>Returns the latest release, or null when the network refuses us.</summary>
    public sealed record RepoFileInfo(string Sha, string DownloadUrl, long Size);

    /// <summary>
    /// Reads a single file from a repository through the contents API. Used for clients that
    /// publish a binary in the tree instead of attaching it to a release, as Psiphon does.
    /// </summary>
    public static async Task<RepoFileInfo?> GetRepoFileAsync(
        string repo, string path, CancellationToken ct, string? proxyUrl = null)
    {
        try
        {
            using var client = CreateClient(proxyUrl);
            var url = $"https://api.github.com/repos/{repo}/contents/{path}";
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception) when (proxyUrl is not null)
            {
                LogBus.Warn("update", "The download proxy failed for the version check; trying direct.");
                response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            }

            using var _response = response;
            if (!response.IsSuccessStatusCode) return null;

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (json is null) return null;

            var sha = json["sha"]?.GetValue<string>();
            var download = json["download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(download)) return null;

            long size = 0;
            try { size = json["size"]?.GetValue<long>() ?? 0; } catch { }

            return new RepoFileInfo(sha, download, size);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogBus.Warn("update", "Could not read the repository file: " + ex.Message);
            return null;
        }
    }
    public static async Task<ReleaseInfo?> GetLatestAsync(
        string repo, bool includePrerelease, CancellationToken ct, string? proxyUrl = null)
    {
        var url = includePrerelease
            ? $"https://api.github.com/repos/{repo}/releases?per_page=5"
            : $"https://api.github.com/repos/{repo}/releases/latest";

        try
        {
            using var owned = proxyUrl is null ? null : CreateClient(proxyUrl);
            HttpResponseMessage resp;
            try
            {
                resp = await (owned ?? Http).GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception) when (proxyUrl is not null)
            {
                // A proxy that is down should not block updates: try once more, directly.
                LogBus.Warn("update", "The download proxy failed for the version check; trying direct.");
                resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            }

            using var _resp = resp;
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                LogBus.Warn("update", $"GitHub API {resp.StatusCode} for {repo}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement rel = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().FirstOrDefault(e => !e.GetProperty("draft").GetBoolean())
                : root;

            if (rel.ValueKind != JsonValueKind.Object) return null;

            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var notes = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            DateTime? published = null;
            if (rel.TryGetProperty("published_at", out var p) && DateTime.TryParse(p.GetString(), out var pd))
                published = pd.ToUniversalTime();

            var assets = new List<ReleaseAsset>();
            if (rel.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    var dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (name.Length > 0 && dl.Length > 0) assets.Add(new ReleaseAsset(name, size, dl));
                }
            }

            return new ReleaseInfo(tag, tag.TrimStart('v', 'V'), published, notes, assets);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogBus.Warn("update", $"Could not reach GitHub for {repo}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Picks the archive that matches this machine from a release.</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, EngineDefinition def)
    {
        var regex = new System.Text.RegularExpressions.Regex(def.AssetPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = release.Assets.FirstOrDefault(a => regex.IsMatch(a.Name));
        if (match is not null) return match;

        // Fallback: anything that mentions windows and the architecture.
        return release.Assets.FirstOrDefault(a =>
                   a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.Contains(EngineCatalog.Arch, StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a =>
                   a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}
