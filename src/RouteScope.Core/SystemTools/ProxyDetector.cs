using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RouteScope.Core.SystemTools;

public sealed record DetectedProxy(string Host, int Port, string Type, int LatencyMs, string ProcessName = "")
{
    public string Label => $"{Type} · {Host}:{Port}";
    public string Endpoint => $"{Host}:{Port}";
    public string OwnerLabel => string.IsNullOrWhiteSpace(ProcessName) ? "" : ProcessName;
}

/// <summary>
/// Finds proxies that are already running on this machine (v2rayN, Clash, Hiddify…)
/// so RouteScope can chain through them with one click instead of asking the user to type ports.
///
/// The scan is real: it enumerates every listening TCP port, then talks SOCKS5 and HTTP
/// handshakes to each one. The well-known ports are probed first so the common case is instant.
/// </summary>
public static class ProxyDetector
{
    /// <summary>Ports these clients use by default, probed first.</summary>
    public static readonly int[] CandidatePorts =
    {
        10808, 10809,          // v2rayN (socks / http)
        2080, 2081,            // v2rayN variants
        1080, 1081,            // generic socks / http
        2801,                  // common manual socks port
        7890, 7891, 7897,      // Clash / mihomo
        20170, 20171,          // Clash verge rev
        8889, 8090,            // misc
        1086, 1087,
        47890,                 // RouteScope's own mixed port
    };

    /// <summary>Ports that belong to Windows itself; never proxies worth chaining to.</summary>
    private static readonly HashSet<int> SystemPorts = new()
    {
        135, 139, 445, 5040, 5357, 7680, 47001, 49664, 49665, 49666, 49667, 49668, 49669, 49670,
    };

    public static async Task<List<DetectedProxy>> ScanAsync(int timeoutMs = 320, CancellationToken ct = default)
    {
        // Known ports first, then everything else that is listening.
        var ordered = new List<(int Port, string Address, string Owner)>();

        var listening = ProcessScanner.GetListeningPorts()
            .Where(l => l.Pid > 4)
            .GroupBy(l => l.Port)
            .Select(g => g.First())
            .ToList();

        string OwnerOf(int pid)
        {
            try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
            catch { return ""; }
        }

        var byPort = listening.ToDictionary(l => l.Port, l => l);

        foreach (var port in CandidatePorts)
        {
            var address = byPort.TryGetValue(port, out var entry) ? entry.Address : "127.0.0.1";
            ordered.Add((port, address, byPort.TryGetValue(port, out var e2) ? OwnerOf(e2.Pid) : ""));
        }

        foreach (var entry in listening.OrderBy(l => l.Port))
        {
            if (CandidatePorts.Contains(entry.Port)) continue;
            if (SystemPorts.Contains(entry.Port)) continue;
            ordered.Add((entry.Port, entry.Address, OwnerOf(entry.Pid)));
        }

        var found = new ConcurrentBag<DetectedProxy>();
        var seen = new ConcurrentDictionary<int, byte>();

        await Parallel.ForEachAsync(
            ordered,
            new ParallelOptions { MaxDegreeOfParallelism = 24, CancellationToken = ct },
            async (item, token) =>
            {
                if (!seen.TryAdd(item.Port, 0)) return;

                var host = NormaliseHost(item.Address);
                var probe = await ProbeAsync(host, item.Port, timeoutMs, token).ConfigureAwait(false);
                if (probe is not null)
                    found.Add(probe with { ProcessName = item.Owner });
            }).ConfigureAwait(false);

        return found
            .GroupBy(f => f.Port)
            .Select(g => g.First())
            .OrderBy(f => f.Port)
            .ToList();
    }

    private static string NormaliseHost(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "127.0.0.1";
        if (address is "0.0.0.0" or "::") return "127.0.0.1";
        if (address.Contains(':')) return "127.0.0.1"; // IPv6 binding: loopback is still reachable
        return address;
    }

    public static async Task<DetectedProxy?> ProbeAsync(string host, int port, int timeoutMs = 600, CancellationToken ct = default)
    {
        var socks = await TrySocks5Async(host, port, timeoutMs, ct).ConfigureAwait(false);
        if (socks is not null) return socks;

        return await TryHttpAsync(host, port, timeoutMs, ct).ConfigureAwait(false);
    }

    private static async Task<DetectedProxy?> TrySocks5Async(string host, int port, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);

            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token).ConfigureAwait(false);
            var buf = new byte[2];
            var read = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
            if (read == 2 && buf[0] == 0x05 && (buf[1] == 0x00 || buf[1] == 0x02 || buf[1] == 0xFF))
                return new DetectedProxy(host, port, "SOCKS5", (int)sw.ElapsedMilliseconds);
        }
        catch { }
        return null;
    }

    private static async Task<DetectedProxy?> TryHttpAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);

            var stream = client.GetStream();
            var req = Encoding.ASCII.GetBytes(
                "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\nUser-Agent: RouteScope\r\n\r\n");
            await stream.WriteAsync(req, cts.Token).ConfigureAwait(false);

            var buf = new byte[64];
            var read = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
            if (read >= 12)
            {
                var text = Encoding.ASCII.GetString(buf, 0, read);

                // Only a 2xx answer to CONNECT means "proxy". Plain web servers answer 400/405
                // and would otherwise be reported as proxies.
                if (text.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains(" 200", StringComparison.Ordinal))
                    return new DetectedProxy(host, port, "HTTP", (int)sw.ElapsedMilliseconds);
            }
        }
        catch { }
        return null;
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    public static int FindFreePort(int preferred, int span = 40)
    {
        for (var i = 0; i < span; i++)
        {
            var p = preferred + i;
            if (p > 65535) break;
            if (IsPortFree(p)) return p;
        }
        return preferred;
    }
}
