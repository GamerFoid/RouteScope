using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace RouteScope.Core.Runtime;

/// <summary>
/// Runs an OpenVPN tunnel and reports the adapter it created.
///
/// OpenVPN is not a proxy — it creates a TUN/TAP adapter and expects the system to route
/// into it. So the engine is pointed at the adapter with <c>bind_interface</c> and nothing
/// else on the machine is touched: OpenVPN is started with <c>--route-nopull</c> so it never
/// installs its own default route, and RouteScope adds exactly one high-metric route that
/// only the engine uses. That route is removed again on stop, and a route left behind by a
/// crash is cleaned up on the next launch.
/// </summary>
public sealed class OpenVpnSession : IAsyncDisposable
{
    private Process? _process;
    private string? _credentialFile;
    private readonly List<string> _recent = new();
    private readonly StringBuilder _log = new();

    /// <summary>The adapter OpenVPN created, e.g. "Ethernet 3".</summary>
    public string? AdapterName { get; private set; }

    public int AdapterIndex { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public static string? FindBinary(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var candidate in new[]
                 {
                     // Shipped with the bundled edition, right next to the app.
                     Path.Combine(AppPaths.Root, "engines", "openvpn", "openvpn.exe"),
                     @"C:\Program Files\OpenVPN\bin\openvpn.exe",
                     @"C:\Program Files (x86)\OpenVPN\bin\openvpn.exe",
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public async Task<(bool ok, string? error)> StartAsync(
        string binaryPath, string configText, int timeoutSeconds, CancellationToken ct,
        string? username = null, string? password = null)
    {
        var parsed = Nodes.OpenVpnConfig.Parse(configText);

        // Bare "auth-user-pass" makes openvpn prompt on the console. In a GUI app there is no
        // console, so it waits forever — the exact "did not bring up the adapter" symptom.
        if (parsed.NeedsCredentials && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)))
            return (false,
                "This .ovpn needs a username and password. Edit the OpenVPN entry and add them " +
                "(the login your VPN provider gave you).");

        var before = SnapshotAdapters();

        Directory.CreateDirectory(AppPaths.CacheDir);
        var configPath = Path.Combine(AppPaths.CacheDir, $"openvpn-{Guid.NewGuid():N}.ovpn");

        // Rewrite the few directives that cannot work unattended (dev tunN, and the
        // machine-wide DNS/route options). Log each change so it is visible in Activity log.
        var normalized = Nodes.OpenVpnConfig.Normalize(configText,
            change => LogBus.Info("engine", $"OpenVPN config adjusted: {change}"));

        await File.WriteAllTextAsync(configPath, normalized,
            new UTF8Encoding(false), ct).ConfigureAwait(false);

        // --route-nopull keeps OpenVPN from replacing the machine's default route. The pull
        // filters are belt and braces for pushes that still try.
        var arguments =
            $"--config \"{configPath}\" --verb 3 --route-nopull " +
            "--pull-filter ignore \"redirect-gateway\" " +
            "--pull-filter ignore \"route \" " +
            "--pull-filter ignore \"dhcp-option\" " +
            "--pull-filter ignore \"block-outside-dns\" " +
            // OpenVPN resolves vpn_gateway from the pushed route-gateway, adds the route
            // itself and removes it on exit. The high metric keeps the physical adapter
            // preferred for everything except traffic pinned to the tunnel.
            "--route 0.0.0.0 0.0.0.0 vpn_gateway 9999";

        if (parsed.NeedsCredentials)
        {
            _credentialFile = Path.Combine(AppPaths.CacheDir, $"openvpn-auth-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(_credentialFile, $"{username}\n{password}\n",
                new UTF8Encoding(false), ct).ConfigureAwait(false);
            arguments += $" --auth-user-pass \"{_credentialFile}\"";
        }

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binaryPath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(binaryPath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // Close stdin so any unexpected console prompt fails fast instead of
                    // hanging the whole tunnel startup for a minute.
                    RedirectStandardInput = true,
                },
            };

            _process.OutputDataReceived += (_, e) => OnLine(e.Data);
            _process.ErrorDataReceived += (_, e) => OnLine(e.Data);

            _process.Start();
            _process.StandardInput.Close();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return (false, "Could not start OpenVPN: " + ex.Message);
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 15, 40));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
                return (false, ExplainExit());

            // Fail fast on a fatal auth error instead of waiting out the whole timeout.
            if (HasFatalAuthError())
                return (false, "OpenVPN could not log in: " + LastProblem() +
                               " Check the username and password on this entry.");

            var adapter = FindNewAdapter(before);
            if (adapter is not null)
            {
                AdapterName = adapter.Value.Name;
                AdapterIndex = adapter.Value.Index;

                if (!await EnsureTunnelRouteAsync(adapter.Value).ConfigureAwait(false))
                {
                    await StopAsync().ConfigureAwait(false);
                    return (false,
                        "OpenVPN connected, but the route through its adapter could not be added, " +
                        "so no traffic could use the tunnel. " + (LastRouteError ?? "") +
                        " RouteScope needs administrator rights for this.");
                }

                LogBus.Info("engine", $"OpenVPN adapter {AdapterName} (index {AdapterIndex}) is ready");
                return (true, null);
            }

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        await StopAsync().ConfigureAwait(false);
        return (false, "OpenVPN did not bring up an adapter in time. " + LastProblem());
    }

    private static List<string> SnapshotAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.OperationalStatus == OperationalStatus.Up)
            .Select(a => a.Name)
            .ToList();

    /// <summary>
    /// OpenVPN usually does NOT create a new adapter — it brings an existing TAP-Windows6 /
    /// Wintun adapter Up. So looking only for a new NAME never finds it. Accept a new adapter
    /// OR a TAP/Wintun adapter that just came Up.
    /// </summary>
    private static (string Name, int Index, IPAddress? Gateway, IPAddress? Address)? FindNewAdapter(List<string> before)
    {
        // "before" holds only adapters that were ALREADY Up. An existing TAP adapter that
        // OpenVPN brings up, or a brand-new Wintun adapter, is therefore absent from it.
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (before.Contains(adapter.Name)) continue;

            var description = adapter.Description ?? "";
            var isVpnAdapter = description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                               description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                               description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                               adapter.Name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                               adapter.Name.Contains("Wintun", StringComparison.OrdinalIgnoreCase);

            var properties = adapter.GetIPProperties();
            var address = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            if (address is null) continue;

            var gateway = properties.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address;

            var index = properties.GetIPv4Properties()?.Index ?? 0;
            return (adapter.Name, index, gateway, address.Address);
        }

        return null;
    }

    /// <summary>
    /// Turns openvpn's last words into something actionable. "Exiting due to fatal error" on
    /// its own tells the user nothing.
    /// </summary>
    private string ExplainExit()
    {
        lock (_recent)
        {
            if (_recent.Any(l => l.Contains("All TAP-Windows adapters on this system are currently in use",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return "The TAP adapter is still held by another OpenVPN instance. " +
                       "Close it (or wait a few seconds) and start again.";
            }

            if (HasFatalAuthErrorUnlocked())
            {
                return "The server rejected the login (AUTH_FAILED). Check the username and password " +
                       "on this entry. Some servers also refuse a second session while the previous " +
                       "one is still closing, so wait a few seconds and retry.";
            }

            if (_recent.Any(l => l.Contains("Cannot open TUN/TAP", StringComparison.OrdinalIgnoreCase)))
                return "OpenVPN could not open its TAP adapter. Reinstall the TAP driver, or run as administrator.";

            var problem = LastProblemUnlocked();
            return "OpenVPN stopped: " + problem;
        }
    }

    private bool HasFatalAuthError()
    {
        lock (_recent) return HasFatalAuthErrorUnlocked();
    }

    private bool HasFatalAuthErrorUnlocked() =>
        _recent.Any(l =>
            l.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Failed retrieving username or password", StringComparison.OrdinalIgnoreCase));

    private string LastProblemUnlocked()
    {
        var line = _recent.LastOrDefault(l =>
            l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("fatal", StringComparison.OrdinalIgnoreCase));

        return line is null ? "no detail was reported." : line.Length > 240 ? line[..240] + "…" : line;
    }


    /// <summary>
    /// Waits for the route OpenVPN was told to add, and only falls back to adding it here.
    /// OpenVPN removes its own routes on exit, so the preferred path needs no cleanup.
    /// </summary>
    private async Task<bool> EnsureTunnelRouteAsync(
        (string Name, int Index, IPAddress? Gateway, IPAddress? Address) adapter)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await HasDefaultRouteAsync(adapter.Index, adapter.Address).ConfigureAwait(false))
            {
                LogBus.Info("engine", $"Tunnel route is in place on interface {adapter.Index}");
                return true;
            }

            await Task.Delay(300).ConfigureAwait(false);
        }

        LogBus.Warn("engine", "OpenVPN did not install the tunnel route; adding it directly.");
        return await AddTunnelRouteAsync(adapter).ConfigureAwait(false);
    }

    /// <summary>Is there a 0.0.0.0/0 route whose interface is this adapter?</summary>
    private static async Task<bool> HasDefaultRouteAsync(int index, IPAddress? adapterAddress)
    {
        var (_, output) = await RunAsync("print -4").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return false;

        var address = adapterAddress?.ToString();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("0.0.0.0", StringComparison.Ordinal)) continue;

            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5) continue;

            // Destination, Mask, Gateway, Interface, Metric. Metric 9999 is our signature;
            // OpenVPN adds the same route with that metric when it succeeds.
            if (address is not null && columns[3] == address) return true;
            if (columns.Length > 4 && columns[4] == "9999") return true;
        }

        return false;
    }

    /// <summary>
    /// Adds one default route through the tunnel with a very high metric. Windows keeps
    /// preferring the physical adapter for everything else; only traffic pinned to the tunnel
    /// interface (the engine) uses it.
    ///
    /// This is not optional: with <c>--route-nopull</c> OpenVPN installs no routes at all, so
    /// a socket bound to the adapter has nowhere to send anything and the engine reports
    /// "unreachable network". Failures here must be reported, never swallowed.
    /// </summary>
    /// <summary>
    /// Arguments for route.exe. RunAsync already sets FileName = "route.exe", so the verb
    /// must be "add", NOT "route add" — passing the program name as its own argument makes
    /// route.exe print its usage text and fail.
    /// </summary>
    public static string RouteAddCommand(string gateway, int index) =>
        $"add 0.0.0.0 mask 0.0.0.0 {gateway} metric 9999 if {index}";

    private async Task<bool> AddTunnelRouteAsync((string Name, int Index, IPAddress? Gateway, IPAddress? Address) adapter)
    {
        // The pushed route-gateway is the correct next hop for a TAP tunnel. It is not
        // exposed by NetworkInterface, so read it from OpenVPN's own log first.
        var candidates = new List<string>();

        var pushed = PushedGateway();
        if (!string.IsNullOrWhiteSpace(pushed)) candidates.Add(pushed);

        if (adapter.Gateway is not null) candidates.Add(adapter.Gateway.ToString());

        // On-link default: works for point-to-point adapters with no gateway.
        candidates.Add("0.0.0.0");

        var lastOutput = "";
        foreach (var gateway in candidates.Distinct())
        {
            var (code, output) = await RunAsync(RouteAddCommand(gateway, adapter.Index))
                .ConfigureAwait(false);

            lastOutput = output.Trim();

            if (code == 0 || lastOutput.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                RouteRecord.Save(new RouteRecord.Record(adapter.Index, gateway));
                LogBus.Info("engine", $"Tunnel route added via {gateway} on interface {adapter.Index}");
                return true;
            }

            LogBus.Warn("engine", $"route add via {gateway} failed ({code}): {lastOutput}");
        }

        LastRouteError = string.IsNullOrWhiteSpace(lastOutput)
            ? "route.exe returned no detail."
            : lastOutput;

        return false;
    }

    /// <summary>Reads "route-gateway x.x.x.x" out of the PUSH_REPLY that OpenVPN logged.</summary>
    private string? PushedGateway()
    {
        lock (_recent)
        {
            foreach (var line in _recent)
            {
                var index = line.IndexOf("route-gateway ", StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;

                var rest = line[(index + "route-gateway ".Length)..].Trim();
                var end = rest.IndexOfAny(new[] { ',', ' ', '\r' });
                var value = end < 0 ? rest : rest[..end];

                if (IPAddress.TryParse(value, out var parsed)) return parsed.ToString();
            }
        }

        return null;
    }

    /// <summary>Why the tunnel route could not be added, when it could not.</summary>
    public string? LastRouteError { get; private set; }

    /// <summary>Removes the route a previous run left behind, if any.</summary>
    public static async Task CleanStaleAsync()
    {
        var record = RouteRecord.Load();
        if (record is null) return;

        await RunAsync($"delete 0.0.0.0 mask 0.0.0.0 {record.Gateway} if {record.Index}")
            .ConfigureAwait(false);
        RouteRecord.Clear();

        LogBus.Info("engine", "Removed a tunnel route left behind by a previous session");
    }

    private static async Task<(int code, string output)> RunAsync(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "route.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            process.Start();
            // route.exe writes its errors to stdout, but capture both so a failure is never
            // reported as "no detail".
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            var output = (await outputTask.ConfigureAwait(false)) + (await errorTask.ConfigureAwait(false));
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        lock (_recent)
        {
            _recent.Add(line);
            if (_recent.Count > 60) _recent.RemoveAt(0);
            _log.AppendLine(line);
        }
    }

    private string LastProblem()
    {
        lock (_recent) return LastProblemUnlocked();
    }

    public async Task StopAsync()
    {
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }

        var record = RouteRecord.Load();
        if (record is not null)
        {
            await RunAsync($"delete 0.0.0.0 mask 0.0.0.0 {record.Gateway} if {record.Index}")
                .ConfigureAwait(false);
            RouteRecord.Clear();
        }

        // Wipe the temp credential file rather than leaving a password on disk.
        if (_credentialFile is not null)
        {
            try
            {
                if (File.Exists(_credentialFile))
                {
                    var bytes = new byte[new FileInfo(_credentialFile).Length];
                    File.WriteAllBytes(_credentialFile, bytes);
                    File.Delete(_credentialFile);
                }
            }
            catch { }
            _credentialFile = null;
        }

        AdapterName = null;
        AdapterIndex = 0;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Remembers the route so a crash cannot leave it behind forever.</summary>
    private static class RouteRecord
    {
        public sealed record Record(int Index, string Gateway);

        private static string Path => System.IO.Path.Combine(AppPaths.DataDir, "openvpn-route.json");

        public static void Save(Record record)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(Path,
                    System.Text.Json.JsonSerializer.Serialize(record));
            }
            catch { }
        }

        public static Record? Load()
        {
            try
            {
                if (!File.Exists(Path)) return null;
                return System.Text.Json.JsonSerializer.Deserialize<Record>(File.ReadAllText(Path));
            }
            catch { return null; }
        }

        public static void Clear()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        }
    }
}
