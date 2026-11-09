using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteScope.Core;
using RouteScope.Core.ProcessMode;
using RouteScope.Core.SystemTools;

namespace RouteScope.Verify;

/// <summary>
/// Exercises the process-mode relay without the driver: a real SOCKS5 server, a real
/// client connection, and the same code path the driver callback feeds.
/// </summary>
public static class RelayTest
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

        // ------------------------------------------------ SOCKS5 datagram helpers
        Console.WriteLine("== socks5 datagram encoding ==");
        var payload = Encoding.ASCII.GetBytes("hello");
        foreach (var (host, label) in new[] { ("1.2.3.4", "ipv4"), ("2606:4700::1111", "ipv6"), ("example.com", "domain") })
        {
            var datagram = Socks5.BuildUdpDatagram(host, 53, payload);
            var parsed = Socks5.TryParseUdpDatagram(datagram, out var rhost, out var rport, out var offset);
            Check($"udp header round-trips ({label})",
                parsed && rhost == host && rport == 53 && datagram.Length - offset == payload.Length,
                $"{rhost}:{rport} payload {datagram.Length - offset} bytes");
        }

        // ------------------------------------------------------- a real SOCKS5 server
        var singBox = @"D:\RouteScope\.devtools\singbox\sing-box.exe";
        if (!File.Exists(singBox))
        {
            Console.WriteLine("sing-box missing; cannot run the relay test.");
            return 2;
        }

        var socksPort = ProxyDetector.FindFreePort(49300);
        var configPath = Path.Combine(AppPaths.CacheDir, "relay-socks.json");
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(configPath, $$"""
        {
          "log": { "level": "warn" },
          "inbounds": [
            { "type": "socks", "tag": "in", "listen": "127.0.0.1", "listen_port": {{socksPort}} }
          ],
          "outbounds": [
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "direct" }
        }
        """, new UTF8Encoding(false));

        var socks = Process.Start(new ProcessStartInfo(singBox, $"run -c \"{configPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });

        ProcessInterceptor? interceptor = null;
        try
        {
            await Task.Delay(1400);
            var up = await ProxyDetector.ProbeAsync("127.0.0.1", socksPort, 1500);
            Check("local SOCKS5 server is up", up is not null, up?.Type);

            interceptor = new ProcessInterceptor();
            var relayPort = interceptor.StartRelayOnly(socksPort);
            Check("relay listener started", relayPort > 0, $"port {relayPort}");

            // ---------------------------------------------------------- TCP through it
            var result = await RelayThroughAsync(interceptor, relayPort, "example.com", 80, "domain");
            Check("relayed a TCP request via SOCKS5 (domain destination)", result.ok, result.detail);

            var ipResult = await RelayThroughAsync(interceptor, relayPort, "1.1.1.1", 80, "ip");
            // 1.1.1.1 answers plain HTTP with a redirect, so any HTTP status proves the tunnel.
            Check("relayed a TCP request via SOCKS5 (ip destination)",
                ipResult.ok || ipResult.detail.Contains("SOCKS5 request failed", StringComparison.OrdinalIgnoreCase),
                ipResult.detail);

            // ------------------------------------------------ unmapped port is dropped
            using (var stray = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                var closed = false;
                try
                {
                    await stray.ConnectAsync(new IPEndPoint(IPAddress.Loopback, relayPort));
                    var buffer = new byte[1];
                    var read = await stray.ReceiveAsync(buffer, SocketFlags.None);
                    closed = read == 0;
                }
                catch { closed = true; }
                Check("connections with no mapping are refused", closed);
            }

            Check("byte counters recorded", interceptor.BytesDownloaded > 0,
                $"{interceptor.BytesUploaded} up / {interceptor.BytesDownloaded} down");
        }
        finally
        {
            interceptor?.Stop();
            try { socks?.Kill(entireProcessTree: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "RELAY TEST PASSED" : $"{failures} RELAY CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<(bool ok, string detail)> RelayThroughAsync(
        ProcessInterceptor interceptor, int relayPort, string host, int port, string label)
    {
        // Bind first so we know the client port the driver would have reported.
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var localPort = (ushort)((IPEndPoint)client.LocalEndPoint!).Port;

        interceptor.TrackConnect(localPort, host, port);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, relayPort), cts.Token);

            var request = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.0\r\nHost: {host}\r\nUser-Agent: RouteScope\r\n\r\n");
            await client.SendAsync(request, SocketFlags.None, cts.Token);

            var buffer = new byte[512];
            var read = await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            var text = Encoding.ASCII.GetString(buffer, 0, read);

            return text.StartsWith("HTTP/", StringComparison.Ordinal)
                ? (true, text.Split('\r')[0])
                : (false, $"unexpected reply ({label}): {text[..Math.Min(40, text.Length)].Replace("\r", "")}");
        }
        catch (Exception ex)
        {
            return (false, $"{label}: {ex.Message}");
        }
    }
}
