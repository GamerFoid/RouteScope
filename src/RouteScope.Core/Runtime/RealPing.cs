using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using RouteScope.Core.Engine;
using RouteScope.Core.Updates;
using RouteScope.Core.Models;

namespace RouteScope.Core.Runtime;

/// <summary>
/// "Real ping": instead of measuring a TCP handshake to the proxy's own port (which only tells
/// you the proxy is listening), this opens an actual tunnel through the proxy to a real
/// destination and measures how long the tunnel takes to come up.
/// </summary>
public static class RealPing
{
    /// <summary>Destinations used to prove a route works. Tried in order.</summary>
    public static readonly (string Host, int Port)[] Targets =
    {
        ("www.gstatic.com", 443),
        ("1.1.1.1", 443),
        ("www.cloudflare.com", 443),
    };

    /// <summary>Measures a local SOCKS5 or HTTP proxy directly, no engine involved.</summary>
    public static async Task<int> ThroughLocalProxyAsync(
        string host, int port, string type, int timeoutMs, CancellationToken ct)
    {
        foreach (var (targetHost, targetPort) in Targets)
        {
            var ms = string.Equals(type, "HTTP", StringComparison.OrdinalIgnoreCase)
                ? await ThroughHttpProxyAsync(host, port, targetHost, targetPort, timeoutMs, ct).ConfigureAwait(false)
                : await ThroughSocks5Async(host, port, targetHost, targetPort, timeoutMs, ct).ConfigureAwait(false);

            if (ms > 0) return ms;
        }
        return -1;
    }

    /// <summary>
    /// Measures a server by starting a throwaway engine with only that outbound, tunnelling
    /// through it, then shutting the engine down again.
    /// </summary>
    public static async Task<int> ThroughNodeAsync(ProxyNode node, int timeoutMs, CancellationToken ct)
        => await ThroughOutboundsAsync(new List<RouteHop> { RouteHop.From(node) }, timeoutMs, ct).ConfigureAwait(false);

    /// <summary>
    /// Measures a chain by starting a throwaway engine with those hops wired together, so the
    /// ping really travels through every proxy above the one being tested.
    /// </summary>
    public static async Task<int> ThroughChainAsync(
        List<RouteHop> hops, int timeoutMs, CancellationToken ct)
    {
        if (hops.Count == 0) return -1;
        return await ThroughOutboundsAsync(hops, timeoutMs, ct).ConfigureAwait(false);
    }

    private static async Task<int> ThroughOutboundsAsync(
        List<RouteHop> hops, int timeoutMs, CancellationToken ct)
    {
        var installer = new EngineInstaller();
        var probeState = new AppState { Mode = CaptureMode.AppProxy, Route = RouteKind.Direct };
        var choice = EngineSelector.Choose(probeState, hops, installer);

        var definition = choice.Engine == EngineBackend.Xray ? EngineCatalog.Get("xray")! : EngineCatalog.SingBox;
        var engine = installer.GetActive(definition);
        if (engine is null) return -1;

        var port = SystemTools.ProxyDetector.FindFreePort(49_000, 200);
        var configPath = Path.Combine(AppPaths.CacheDir, $"ping-{Guid.NewGuid():N}.json");

        var context = new BuildContext
        {
            State = probeState,
            ProxyHops = hops,
            RouteEverything = true,
            MixedPort = port,
            ClashApiPort = SystemTools.ProxyDetector.FindFreePort(port + 1, 20),
            ClashSecret = "ping",
        };

        var json = choice.Engine == EngineBackend.Xray
            ? XrayConfigBuilder.Build(context)
            : SingBoxConfigBuilder.Build(context);

        Directory.CreateDirectory(AppPaths.CacheDir);
        await File.WriteAllTextAsync(configPath, json, ct).ConfigureAwait(false);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = engine.ExePath,
                Arguments = $"-c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(engine.ExePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!await WaitForPortAsync(port, 4000, ct).ConfigureAwait(false)) return -1;

            foreach (var (targetHost, targetPort) in Targets)
            {
                var ms = await ThroughSocks5Async("127.0.0.1", port, targetHost, targetPort, timeoutMs, ct)
                    .ConfigureAwait(false);
                if (ms > 0) return ms;
            }

            return -1;
        }
        catch
        {
            return -1;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { File.Delete(configPath); } catch { }
        }
    }

    // ------------------------------------------------------------------ socks5

    public static async Task<int> ThroughSocks5Async(
        string proxyHost, int proxyPort, string targetHost, int targetPort, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await client.ConnectAsync(proxyHost, proxyPort, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token).ConfigureAwait(false);
            var handshake = new byte[2];
            if (await stream.ReadAsync(handshake, cts.Token).ConfigureAwait(false) != 2) return -1;
            if (handshake[0] != 0x05) return -1;

            if (handshake[1] == 0x02)
            {
                // Username/password auth with empty credentials.
                await stream.WriteAsync(new byte[] { 0x01, 0x00, 0x00 }, cts.Token).ConfigureAwait(false);
                var auth = new byte[2];
                if (await stream.ReadAsync(auth, cts.Token).ConfigureAwait(false) != 2 || auth[1] != 0x00) return -1;
            }
            else if (handshake[1] != 0x00)
            {
                return -1;
            }

            var request = BuildConnectRequest(targetHost, targetPort);
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

            var header = new byte[4];
            if (await stream.ReadAsync(header, cts.Token).ConfigureAwait(false) != 4) return -1;
            if (header[1] != 0x00) return -1;

            var skip = header[3] switch
            {
                0x01 => 4,
                0x03 => (await ReadOneAsync(stream, cts.Token).ConfigureAwait(false)) + 1,
                0x04 => 16,
                _ => -1,
            };
            if (skip < 0) return -1;

            var tail = new byte[skip + 2];
            if (await stream.ReadAsync(tail, cts.Token).ConfigureAwait(false) < skip + 2) return -1;

            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<int> ReadOneAsync(NetworkStream stream, CancellationToken ct)
    {
        var one = new byte[1];
        await stream.ReadAsync(one, ct).ConfigureAwait(false);
        return one[0];
    }

    private static byte[] BuildConnectRequest(string host, int port)
    {
        var payload = new List<byte> { 0x05, 0x01, 0x00 };

        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            payload.Add((byte)(bytes.Length == 4 ? 0x01 : 0x04));
            payload.AddRange(bytes);
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            payload.Add(0x03);
            payload.Add((byte)name.Length);
            payload.AddRange(name);
        }

        payload.Add((byte)(port >> 8));
        payload.Add((byte)(port & 0xFF));
        return payload.ToArray();
    }

    // -------------------------------------------------------------------- http

    public static async Task<int> ThroughHttpProxyAsync(
        string proxyHost, int proxyPort, string targetHost, int targetPort, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await client.ConnectAsync(proxyHost, proxyPort, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            var request = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token).ConfigureAwait(false);

            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (read < 12) return -1;

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            return text.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) &&
                   text.Contains(" 200", StringComparison.Ordinal)
                ? (int)sw.ElapsedMilliseconds
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<bool> WaitForPortAsync(int port, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(250);
                await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                await Task.Delay(120, ct).ConfigureAwait(false);
            }
        }
        return false;
    }
}
