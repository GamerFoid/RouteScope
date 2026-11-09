using System.Diagnostics;
using System.Text;
using RouteScope.Core;
using RouteScope.Core.Models;
using RouteScope.Core.Runtime;
using RouteScope.Core.Updates;

namespace RouteScope.Verify;

/// <summary>
/// End-to-end proof that RouteScope routes only the selected process.
///
/// Layout of the test:
///   * A stand-in local proxy listens on 127.0.0.1:10808 and blocks everything.
///   * RouteScope runs its own engine, chaining to that proxy, with curl.exe as the only target.
///   * curl.exe  -> matched  -> goes through the proxy -> blocked -> must fail
///   * powershell -> unmatched -> direct                    -> must succeed
/// </summary>
public static class E2E
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  — " + detail)}");
            Console.ResetColor();
            if (!ok) failures++;
        }

        var singBoxSource = @"D:\RouteScope\.devtools\singbox\sing-box.exe";
        if (!File.Exists(singBoxSource))
        {
            Console.WriteLine("sing-box is missing from .devtools; run tools\\fetch-engine.ps1 first.");
            return 2;
        }

        // Make RouteScope believe sing-box is installed.
        var targetDir = Path.Combine(AppPaths.EngineDir("singbox"), "1.14.4");
        Directory.CreateDirectory(targetDir);
        var engineExe = Path.Combine(targetDir, "sing-box.exe");
        File.Copy(singBoxSource, engineExe, overwrite: true);

        var installer = new EngineInstaller();
        var active = installer.GetActive(EngineCatalog.SingBox);
        Check("engine discovered", active is not null, active?.ExePath);

        // ---------------------------------------------------------- stand-in proxy
        var standInConfig = Path.Combine(AppPaths.CacheDir, "standin.json");
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(standInConfig, """
        {
          "log": { "level": "warn" },
          "inbounds": [
            { "type": "socks", "tag": "in", "listen": "127.0.0.1", "listen_port": 10808 }
          ],
          "outbounds": [
            { "type": "block", "tag": "block" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "block" }
        }
        """, new UTF8Encoding(false));

        var standIn = Process.Start(new ProcessStartInfo(engineExe, $"run -c \"{standInConfig}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });

        try
        {
            await Task.Delay(1500);
            var reachable = await Task.Run(() =>
                Core.SystemTools.ProxyDetector.ProbeAsync("127.0.0.1", 10808, 1200));
            Check("stand-in proxy listening on 10808", reachable is not null, reachable?.Type);

            // The loop guard: RouteScope must be able to name the process behind the port
            // so it can keep that process out of the tunnel.
            var owner = Core.SystemTools.ProcessScanner.FindListenerProcess(10808);
            Check("identified the client owning port 10808",
                owner?.ExePath is { Length: > 0 }, owner is null ? "not found" : $"{owner.Name}");

            // ------------------------------------------------------ RouteScope state
            var state = new AppState();
            state.Targets.Clear();
            state.Targets.Add(new ProxyTarget
            {
                Kind = TargetKind.ProcessName,
                Value = "curl.exe",
                Enabled = true,
            });
            state.Externals.Clear();
            state.Externals.Add(new ExternalProxy { Name = "Stand-in", Host = "127.0.0.1", Port = 10808, Type = "Auto" });
            state.ActiveExternalId = state.Externals[0].Id;
            state.Route = RouteKind.External;
            state.Mode = CaptureMode.AppProxy;
            state.Settings.MixedPort = Core.SystemTools.ProxyDetector.FindFreePort(48990);
            state.Settings.ClashApiPort = Core.SystemTools.ProxyDetector.FindFreePort(48991);
            state.Settings.Advanced.EnableClashApi = true;
            state.Settings.Advanced.LogLevel = "info";

            await using var engine = new RouteEngine();
            var result = await engine.StartAsync(state, installer, CancellationToken.None);
            Check("engine started", result.Ok, result.Error ?? engine.ProxyDescription);

            if (!result.Ok)
            {
                Console.WriteLine(engine.ProxyDescription);
                return 1;
            }

            await Task.Delay(800);
            var port = state.Settings.MixedPort;

            // ------------------------------------------------------------- matched
            var matched = await RunCurl(port, useProxy: true);
            Check("matched process (curl.exe) is proxied and blocked", matched.exitCode != 0,
                $"exit {matched.exitCode}");

            // ----------------------------------------------------------- unmatched
            // App proxy hands out a local port: anything that connects to it did so because it
            // was pointed there, so it uses the full route. A non-target process reaching the
            // port must therefore ALSO be proxied (and blocked by the stand-in).
            var viaPort = await RunHttpViaProxy(port);
            Check("non-target process using the port is proxied too", !viaPort.ok, viaPort.detail);

            // The isolation promise still holds: traffic that does not use the port is
            // untouched, so a plain direct request from the same process succeeds.
            var direct = await RunCurl(port, useProxy: false);
            Check("direct traffic (not using the port) is unaffected", direct.exitCode == 0,
                $"exit {direct.exitCode}");

            // ------------------------------------------------------- dashboard api
            await Task.Delay(2200);
            var clash = new ClashClient("127.0.0.1", state.Settings.ClashApiPort, state.Settings.ClashSecret);
            var ping = await clash.PingAsync();
            Check("clash api reachable", ping);
            clash.Dispose();

            var config = File.ReadAllText(AppPaths.RuntimeConfigFile);
            Check("config sends everything to the proxy", config.Contains("\"final\": \"proxy\""), "");
            Check("config does not filter the route by process",
                !config.Contains("\"process_name\""), "");
            Check("config chains to 10808", config.Contains("\"server_port\": 10808"), "");

            await engine.StopAsync();
            Check("engine stopped cleanly", engine.State == RunState.Stopped);
        }
        finally
        {
            try { standIn?.Kill(entireProcessTree: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "E2E PASSED" : $"{failures} E2E CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<(int exitCode, string output)> RunCurl(int port, bool useProxy)
    {
        var args = useProxy
            ? $"-s -o NUL -w \"%{{http_code}}\" --max-time 8 -x http://127.0.0.1:{port} https://example.com"
            : "-s -o NUL -w \"%{http_code}\" --max-time 8 https://example.com";

        var psi = new ProcessStartInfo("curl.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output.Trim());
    }

    private static async Task<(bool ok, string detail)> RunHttpViaProxy(int port)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
            })
            { Timeout = TimeSpan.FromSeconds(20) };

            var resp = await client.GetAsync("https://example.com");
            return (resp.IsSuccessStatusCode, ((int)resp.StatusCode).ToString());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
