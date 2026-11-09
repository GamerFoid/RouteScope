using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RouteScope.Core.Models;

namespace RouteScope.Core.Runtime;

/// <summary>
/// Owns the engine child process: starts it, streams its log into the app, waits until it
/// is actually serving traffic, and shuts it down cleanly.
/// </summary>
public sealed class EngineRunner : IAsyncDisposable
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex LevelRegex = new(
        @"\b(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b", RegexOptions.Compiled);

    /// <summary>
    /// Lines the engine emits for every single connection that say nothing the user needs:
    /// the inbound half of our own relay, and the process lookup for it. They stay in the
    /// diagnostic buffer but never reach the activity log unless verbose logging is on.
    /// </summary>
    private static readonly string[] RoutineNoise =
    {
        "router: found process path:",
        "inbound/mixed[mixed-in]: inbound connection",
        "inbound/tun[tun-in]: inbound connection",
        "inbound/tun[tun-in]: connection to",
        "outbound/direct[direct]: outbound connection to 127.0.0.1:",
        "outbound/direct[direct]: outbound connection to [::1]:",
    };

    /// <summary>When false, routine per-connection chatter is hidden from the activity log.</summary>
    public bool VerboseLogging { get; set; }

    public static bool IsRoutineNoise(string message)
    {
        foreach (var pattern in RoutineNoise)
            if (message.Contains(pattern, StringComparison.Ordinal))
                return true;
        return false;
    }

    private Process? _process;
    private SystemTools.ProcessJob? _job;
    private readonly StringBuilder _recent = new();
    private readonly object _gate = new();

    public RunState State { get; private set; } = RunState.Stopped;
    public event Action<RunState, string?>? StateChanged;

    public string? ExePath { get; private set; }

    /// <summary>The engine process id. Interception must never touch this process.</summary>
    public uint? ProcessId { get; private set; }
    public DateTime StartedUtc { get; private set; }

    /// <summary>Last few hundred log lines, useful when the engine refuses to start.</summary>
    public string RecentOutput
    {
        get { lock (_gate) return _recent.ToString(); }
    }

    private void SetState(RunState state, string? detail = null)
    {
        State = state;
        try { StateChanged?.Invoke(state, detail); } catch { }
    }

    public async Task<(bool ok, string? error)> StartAsync(
        string exePath,
        string arguments,
        string workingDirectory,
        int readyPort,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (_process is { HasExited: false })
            return (true, null);

        SetState(RunState.Starting);
        lock (_gate) _recent.Clear();

        try
        {
            var psi = new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory,
            };

            // Keep the engine's own traffic away from its own TUN adapter.
            psi.Environment["ENABLE_DEPRECATED_LEGACY_DNS_SERVERS"] = "false";

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.Exited += (_, _) => OnExited();

            ExePath = exePath;
            _process = process;

            if (!process.Start())
                return Fail("The engine process could not be started.");

            ProcessId = (uint)process.Id;

            // Guarantee the engine dies with us, even on a hard kill.
            _job = new SystemTools.ProcessJob();
            _job.Attach(process);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            StartedUtc = DateTime.UtcNow;

            LogBus.Info("engine", $"Started {Path.GetFileName(exePath)} (pid {process.Id})");

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (process.HasExited)
                    return Fail(BuildStartupError(process.ExitCode));

                if (readyPort <= 0 || await IsPortOpenAsync(readyPort, ct).ConfigureAwait(false))
                {
                    // Give the engine a beat to finish wiring routes.
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    if (process.HasExited) return Fail(BuildStartupError(process.ExitCode));
                    SetState(RunState.Running);
                    return (true, null);
                }

                await Task.Delay(200, ct).ConfigureAwait(false);
            }

            return Fail("The engine did not become ready in time. Check the log for details.");
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private (bool, string?) Fail(string error)
    {
        LogBus.Error("engine", error);
        SetState(RunState.Faulted, error);
        return (false, error);
    }

    private string BuildStartupError(int exitCode)
    {
        var tail = RecentOutput;
        var fatal = tail.Split('\n')
            .LastOrDefault(l => l.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            ?.Trim();
        return !string.IsNullOrWhiteSpace(fatal)
            ? fatal
            : $"The engine exited with code {exitCode}.";
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(300);
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var clean = AnsiRegex.Replace(line, "").TrimEnd();
        if (clean.Length == 0) return;

        lock (_gate)
        {
            _recent.AppendLine(clean);
            if (_recent.Length > 24000) _recent.Remove(0, _recent.Length - 16000);
        }

        var level = LogLevel.Info;
        var m = LevelRegex.Match(clean);
        if (m.Success)
        {
            level = m.Groups[1].Value switch
            {
                "TRACE" or "DEBUG" => LogLevel.Debug,
                "WARN" or "WARNING" => LogLevel.Warn,
                "ERROR" or "FATAL" => LogLevel.Error,
                _ => LogLevel.Info,
            };
        }

        var message = StripPrefix(clean);

        // Keep the diagnostic buffer complete, but spare the activity log the routine lines.
        if (!VerboseLogging && IsRoutineNoise(message)) return;

        LogBus.Write(level, "engine", message);
    }

    /// <summary>Turns "+0330 2026-11-08 04:42:16 INFO [123 1ms] router: …" into "router: …".</summary>
    private static string StripPrefix(string line)
    {
        var idx = line.IndexOf("] ", StringComparison.Ordinal);
        if (idx > 0 && line.IndexOf('[') is > 0 and var open && open < idx)
            return line[(idx + 2)..];
        return line;
    }

    private void OnExited()
    {
        if (State is RunState.Stopping or RunState.Stopped) return;
        LogBus.Warn("engine", "The engine process exited unexpectedly.");
        SetState(RunState.Faulted, "The engine stopped unexpectedly.");
    }

    public async Task StopAsync()
    {
        var process = _process;
        if (process is null) return;

        SetState(RunState.Stopping);
        try
        {
            if (!process.HasExited)
            {
                // Ask nicely first so sing-box can tear its TUN routes down.
                try { process.CloseMainWindow(); } catch { }
                try { process.StandardInput.Close(); } catch { }

                using var cts = new CancellationTokenSource(3000);
                try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    LogBus.Warn("engine", "Engine did not exit in time; terminating.");
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { await process.WaitForExitAsync(new CancellationTokenSource(3000).Token).ConfigureAwait(false); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            LogBus.Warn("engine", "Stop: " + ex.Message);
        }
        finally
        {
            try { process.Dispose(); } catch { }
            try { _job?.Dispose(); } catch { }
            _job = null;
            _process = null;
        ProcessId = null;
            SetState(RunState.Stopped);
            LogBus.Info("engine", "Engine stopped.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
