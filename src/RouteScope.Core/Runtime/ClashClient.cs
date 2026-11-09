using System.Net.Http.Headers;
using System.Text.Json;

namespace RouteScope.Core.Runtime;

public sealed record TrafficSample(long Upload, long Download);

public sealed record ConnectionInfo(
    string Id,
    string Host,
    string DestinationIp,
    int DestinationPort,
    string Network,
    string Type,
    string ProcessPath,
    string ProcessName,
    string Rule,
    string Chain,
    long Upload,
    long Download,
    DateTimeOffset Start)
{
    public string Destination =>
        !string.IsNullOrWhiteSpace(Host) ? Host : $"{DestinationIp}:{DestinationPort}";

    public string DisplayProcess =>
        !string.IsNullOrWhiteSpace(ProcessName) ? ProcessName
        : !string.IsNullOrWhiteSpace(ProcessPath) ? Path.GetFileName(ProcessPath)
        : "—";

    public long Total => Upload + Download;
}

/// <summary>
/// Small client for the Clash API that sing-box exposes. It powers the live connection list
/// and the up/down graph without RouteScope having to intercept any traffic itself.
/// </summary>
public sealed class ClashClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private Task? _trafficLoop;
    private Task? _connectionsLoop;

    public string BaseUrl { get; }

    public event Action<TrafficSample>? Traffic;
    public event Action<IReadOnlyList<ConnectionInfo>>? ConnectionsUpdated;
    public event Action<bool>? AvailabilityChanged;

    private bool _available;

    public ClashClient(string host, int port, string secret)
    {
        BaseUrl = $"http://{host}:{port}";
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(secret))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(BaseUrl + "/version", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Start()
    {
        _trafficLoop ??= Task.Run(() => TrafficLoopAsync(_cts.Token));
        _connectionsLoop ??= Task.Run(() => ConnectionsLoopAsync(_cts.Token));
    }

    private async Task TrafficLoopAsync(CancellationToken ct)
    {
        var delay = 500;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/traffic");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    SetAvailable(false);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 8000);
                    continue;
                }

                SetAvailable(true);
                delay = 500;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    if (line.Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var up = doc.RootElement.TryGetProperty("up", out var u) ? u.GetInt64() : 0;
                        var down = doc.RootElement.TryGetProperty("down", out var d) ? d.GetInt64() : 0;
                        Traffic?.Invoke(new TrafficSample(up, down));
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                SetAvailable(false);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch { return; }
                delay = Math.Min(delay * 2, 8000);
            }
        }
    }

    private async Task ConnectionsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(BaseUrl + "/connections", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var list = ParseConnections(json);
                    ConnectionsUpdated?.Invoke(list);
                }
            }
            catch (OperationCanceledException) { return; }
            catch { }

            try { await Task.Delay(1500, ct).ConfigureAwait(false); }
            catch { return; }
        }
    }

    public static List<ConnectionInfo> ParseConnections(string json)
    {
        var list = new List<ConnectionInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("connections", out var arr)) return list;
            if (arr.ValueKind != JsonValueKind.Array) return list;

            foreach (var c in arr.EnumerateArray())
            {
                var meta = c.TryGetProperty("metadata", out var m) ? m : default;
                string S(JsonElement el, string name) =>
                    el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
                    v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

                var chains = "";
                if (c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array)
                    chains = string.Join(" → ", ch.EnumerateArray().Select(x => x.GetString()));

                DateTimeOffset start = default;
                if (c.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(st.GetString(), out var parsed))
                    start = parsed;

                list.Add(new ConnectionInfo(
                    Id: S(c, "id"),
                    Host: S(meta, "host"),
                    DestinationIp: S(meta, "destinationIP"),
                    DestinationPort: meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("destinationPort", out var dp)
                        ? (dp.ValueKind == JsonValueKind.Number ? dp.GetInt32() : 0) : 0,
                    Network: S(meta, "network"),
                    Type: S(meta, "type"),
                    ProcessPath: S(meta, "processPath"),
                    ProcessName: S(meta, "process"),
                    Rule: S(c, "rule"),
                    Chain: chains,
                    Upload: c.TryGetProperty("upload", out var up) && up.ValueKind == JsonValueKind.Number ? up.GetInt64() : 0,
                    Download: c.TryGetProperty("download", out var dn) && dn.ValueKind == JsonValueKind.Number ? dn.GetInt64() : 0,
                    Start: start));
            }
        }
        catch { }
        // The API can list the same connection twice while it is being set up or torn down.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<ConnectionInfo>(list.Count);
        foreach (var connection in list)
        {
            if (string.IsNullOrEmpty(connection.Id)) { unique.Add(connection); continue; }
            if (seen.Add(connection.Id)) unique.Add(connection);
        }

        return unique;
    }

    public async Task CloseAllConnectionsAsync(CancellationToken ct = default)
    {
        try { await _http.DeleteAsync(BaseUrl + "/connections", ct).ConfigureAwait(false); } catch { }
    }

    private void SetAvailable(bool value)
    {
        if (_available == value) return;
        _available = value;
        try { AvailabilityChanged?.Invoke(value); } catch { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _http.Dispose(); } catch { }
        _cts.Dispose();
    }
}
