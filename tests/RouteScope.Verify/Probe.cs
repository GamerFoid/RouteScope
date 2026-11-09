using System.Diagnostics;
using RouteScope.Core.Interop;

namespace RouteScope.Verify;

/// <summary>
/// Answers one question: can this machine open the NetFilterSDK driver from a normal,
/// unelevated process, and does it hand back connect requests?
/// </summary>
public static class Probe
{
    public static int Run()
    {
        Console.WriteLine($"nfapi.dll present: {File.Exists(Path.Combine(AppContext.BaseDirectory, "nfapi.dll"))}");
        Console.WriteLine($"elevated: {new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}");

        using var session = new NetFilterSession();

        var started = Stopwatch.StartNew();
        var status = session.Start(threads: 2);
        started.Stop();

        Console.WriteLine($"nf_init(\"netfilter2\") -> {status}  ({started.ElapsedMilliseconds} ms)");
        if (status != NetFilterStatus.Success)
        {
            Console.WriteLine("RESULT: driver could not be opened");
            return 1;
        }

        Console.WriteLine($"driver type: {NetFilterSession.DriverType} (2 = WFP)");

        var ruleResult = session.AddConnectRequestRules(includeUdp: false);
        Console.WriteLine($"nf_addRule -> {ruleResult}");

        var hitCount = 0;
        var matched = "";
        using var gate = new ManualResetEventSlim(false);

        session.TcpConnectRequest += (id, infoPtr, info) =>
        {
            Interlocked.Increment(ref hitCount);
            if (matched.Length == 0)
            {
                var path = NetFilterSession.GetProcessPath(info.ProcessId);
                matched = $"{path} -> {info.RemoteIp}:{info.RemotePort}";
            }
        };

        Console.WriteLine("opening a test connection (curl)…");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.GetAsync("http://example.com").GetAwaiter().GetResult();
        }
        catch { /* the request itself does not matter */ }

        Thread.Sleep(700);

        Console.WriteLine($"connect requests observed: {hitCount}");
        if (matched.Length > 0) Console.WriteLine($"first: {matched}");

        session.DeleteRules();
        session.Dispose();

        var ok = hitCount > 0;
        Console.WriteLine(ok ? "RESULT: driver works unelevated and delivers connect requests"
                             : "RESULT: driver opened but delivered no connect requests");
        return ok ? 0 : 1;
    }
}
