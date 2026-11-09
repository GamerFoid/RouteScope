using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteScope.Core.Runtime;

/// <summary>
/// Runs a complete Xray configuration that came from a subscription.
///
/// Some subscriptions do not hand out server links at all — they hand out whole configs, with
/// their own inbounds, DNS and routing. Those cannot be turned into a node, so RouteScope runs
/// the document as-is with Xray and exposes its local listener as a managed proxy.
/// </summary>
public sealed class XrayConfigSession : IAsyncDisposable
{
    private Process? _process;
    private readonly List<string> _recent = new();

    public int Port { get; private set; }

    /// <summary>The core version the config asks for, when it declares one.</summary>
    public string? RequiredVersion { get; private set; }

    /// <summary>The proxy this config dials through, when it is chained.</summary>
    public string? Upstream { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Starts the config. When <paramref name="upstream"/> is given, every proxy outbound is
    /// told to dial through it. That is what makes these configs work where their own server
    /// hostname cannot be resolved: the upstream resolves it, so the DNS loop disappears.
    /// </summary>
    public async Task<(bool ok, string? error)> StartAsync(
        string binaryPath, string configJson, int timeoutSeconds, CancellationToken ct,
        string? upstream = null)
    {
        RequiredVersion = ReadRequiredVersion(configJson);
        Upstream = upstream;

        var rewritten = RewriteInbounds(configJson, out var port, out var error, upstream);
        if (rewritten is null) return (false, error);

        Port = port;

        Directory.CreateDirectory(AppPaths.CacheDir);
        var configPath = Path.Combine(AppPaths.CacheDir, $"xray-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, rewritten, new UTF8Encoding(false), ct).ConfigureAwait(false);

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binaryPath,
                    Arguments = $"run -c \"{configPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(binaryPath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            _process.OutputDataReceived += (_, e) => OnLine(e.Data);
            _process.ErrorDataReceived += (_, e) => OnLine(e.Data);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return (false, "Could not start Xray: " + ex.Message);
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(8, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                var hint = RequiredVersion is null ? "" : $" This config asks for Xray {RequiredVersion} or newer.";
                return (false, "Xray rejected the config: " + LastProblem() + hint);
            }

            if (await PortOpenAsync(Port, ct).ConfigureAwait(false))
            {
                LogBus.Info("engine", $"Custom config is listening on 127.0.0.1:{Port}");
                return (true, null);
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        await StopAsync().ConfigureAwait(false);
        return (false, "The config did not open a local listener. " + LastProblem());
    }

    private static string? ReadRequiredVersion(string configJson)
    {
        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root) return null;
            return root["version"]?["min"]?.GetValue<string>();
        }
        catch { return null; }
    }

    /// <summary>
    /// Rewrites only the inbound RouteScope dials and leaves every other inbound exactly as
    /// written. These configs reference their own inbound ports elsewhere — a DNS listener on
    /// 10853 is pointed at by the dns section — so moving them all silently breaks DNS.
    /// </summary>
    private static string? RewriteInbounds(string configJson, out int port, out string? error, string? upstream = null)
    {
        port = 0;
        error = null;

        JsonObject? root;
        try { root = JsonNode.Parse(configJson) as JsonObject; }
        catch (Exception ex)
        {
            error = "That config is not valid JSON: " + ex.Message;
            return null;
        }

        if (root is null)
        {
            error = "That config is not a JSON object.";
            return null;
        }

        if (root["inbounds"] is not JsonArray inbounds || inbounds.Count == 0)
        {
            error = "The config has no inbound to connect to.";
            return null;
        }

        JsonObject? chosen = null;
        foreach (var candidate in inbounds)
        {
            if (candidate is not JsonObject inbound) continue;

            var protocol = inbound["protocol"]?.GetValue<string>() ?? "";
            if (protocol is "mixed" or "socks" or "http")
            {
                chosen = inbound;
                break;
            }
        }

        if (chosen is null)
        {
            error = "The config has no SOCKS, HTTP or mixed inbound.";
            return null;
        }

        var assigned = SystemTools.ProxyDetector.FindFreePort(43_000, 300);
        chosen["listen"] = "127.0.0.1";
        chosen["port"] = assigned;
        port = assigned;

        if (!string.IsNullOrWhiteSpace(upstream)) InjectUpstream(root, upstream);

        // Quiet the log; these configs are chatty.
        if (root["log"] is JsonObject log) log["loglevel"] = "warning";
        else root["log"] = new JsonObject { ["loglevel"] = "warning" };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Adds an upstream outbound and points every proxy outbound at it. The hostname of the
    /// config's own server is then resolved by the upstream, not by the config's DNS — which
    /// in these configs is routed back through the proxy and can never resolve.
    /// </summary>
    private static void InjectUpstream(JsonObject root, string upstreamUrl)
    {
        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri)) return;

        var isHttp = uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        const string tag = "__routescope_upstream";

        if (root["outbounds"] is not JsonArray outbounds) return;

        var relay = new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = isHttp ? "http" : "socks",
            ["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = uri.Host,
                        ["port"] = uri.Port,
                    },
                },
            },
            ["streamSettings"] = new JsonObject { ["network"] = "tcp", ["security"] = "none" },
        };

        foreach (var node in outbounds)
        {
            if (node is not JsonObject outbound) continue;

            var protocol = outbound["protocol"]?.GetValue<string>() ?? "";
            if (protocol is "freedom" or "blackhole" or "dns" or "direct" or "block") continue;

            var stream = outbound["streamSettings"] as JsonObject ?? new JsonObject();
            var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
            sockopt["dialerProxy"] = tag;
            stream["sockopt"] = sockopt;
            outbound["streamSettings"] = stream;
        }

        outbounds.Add(relay);
        LogBus.Info("engine", $"Imported config will dial through {upstreamUrl}");
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        lock (_recent)
        {
            _recent.Add(line);
            if (_recent.Count > 30) _recent.RemoveAt(0);
        }
    }

    private string LastProblem()
    {
        lock (_recent)
        {
            var line = _recent.LastOrDefault(l => l.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                                                  || l.Contains("error", StringComparison.OrdinalIgnoreCase));
            if (line is null) return "no detail was reported.";
            return line.Length > 240 ? line[..240] + "…" : line;
        }
    }

    private static async Task<bool> PortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(500);
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    public Task StopAsync()
    {
        if (_process is null) return Task.CompletedTask;

        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
        _process = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
