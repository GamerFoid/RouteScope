using System.Net;
using System.Net.Sockets;

namespace RouteScope.Core.ProcessMode;

/// <summary>Minimal SOCKS5 client: CONNECT for TCP, UDP ASSOCIATE for datagrams.</summary>
public static class Socks5
{
    public const byte Version = 0x05;
    private const byte CmdConnect = 0x01;
    private const byte CmdUdpAssociate = 0x03;
    private const byte AtypIpv4 = 0x01;
    private const byte AtypDomain = 0x03;
    private const byte AtypIpv6 = 0x04;

    /// <summary>
    /// Opens a tunnel to <paramref name="host"/>:<paramref name="port"/> through a SOCKS5
    /// server on the loopback. Returns the connected socket positioned at the first payload byte.
    /// </summary>
    public static async Task<Socket> ConnectAsync(
        int socksPort, string host, int port, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, socksPort), ct).ConfigureAwait(false);

            // Greeting: version 5, one method, "no authentication".
            await socket.SendAsync(new byte[] { Version, 0x01, 0x00 }, SocketFlags.None, ct).ConfigureAwait(false);

            var reply = new byte[2];
            await ReadExactAsync(socket, reply, ct).ConfigureAwait(false);
            if (reply[0] != Version) throw new IOException("Not a SOCKS5 proxy on the local port.");
            if (reply[1] != 0x00) throw new IOException("The local SOCKS5 proxy refused authentication.");

            var request = BuildRequest(CmdConnect, host, port);
            await socket.SendAsync(request, SocketFlags.None, ct).ConfigureAwait(false);

            _ = await ReadReplyAsync(socket, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a UDP association. Returns the relay endpoint to send datagrams to and the
    /// control connection that must stay open for the association to live.
    /// </summary>
    public static async Task<(Socket control, IPEndPoint relay)> UdpAssociateAsync(
        int socksPort, CancellationToken ct)
    {
        var control = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await control.ConnectAsync(new IPEndPoint(IPAddress.Loopback, socksPort), ct).ConfigureAwait(false);
            await control.SendAsync(new byte[] { Version, 0x01, 0x00 }, SocketFlags.None, ct).ConfigureAwait(false);

            var greeting = new byte[2];
            await ReadExactAsync(control, greeting, ct).ConfigureAwait(false);
            if (greeting[0] != Version || greeting[1] != 0x00)
                throw new IOException("The local SOCKS5 proxy refused the UDP association.");

            // 0.0.0.0:0 means "use whatever source address you see".
            await control.SendAsync(BuildRequest(CmdUdpAssociate, "0.0.0.0", 0), SocketFlags.None, ct)
                .ConfigureAwait(false);

            var relay = await ReadReplyAsync(control, ct).ConfigureAwait(false);
            if (relay.Port == 0) relay = new IPEndPoint(IPAddress.Loopback, socksPort);
            if (relay.Address.Equals(IPAddress.Any)) relay = new IPEndPoint(IPAddress.Loopback, relay.Port);

            return (control, relay);
        }
        catch
        {
            control.Dispose();
            throw;
        }
    }

    /// <summary>Wraps a payload in a SOCKS5 UDP request header.</summary>
    public static byte[] BuildUdpDatagram(string host, int port, ReadOnlySpan<byte> payload)
    {
        var addressBytes = EncodeAddress(host, out var atyp);
        var buffer = new byte[4 + addressBytes.Length + 2 + payload.Length];
        buffer[0] = 0; // RSV
        buffer[1] = 0; // RSV
        buffer[2] = 0; // FRAG
        buffer[3] = atyp;
        addressBytes.CopyTo(buffer.AsSpan(4));
        var offset = 4 + addressBytes.Length;
        buffer[offset] = (byte)(port >> 8);
        buffer[offset + 1] = (byte)(port & 0xFF);
        payload.CopyTo(buffer.AsSpan(offset + 2));
        return buffer;
    }

    /// <summary>Reads the destination out of a SOCKS5 UDP response header.</summary>
    public static bool TryParseUdpDatagram(ReadOnlySpan<byte> datagram,
        out string host, out int port, out int payloadOffset)
    {
        host = "";
        port = 0;
        payloadOffset = 0;
        if (datagram.Length < 5 || datagram[2] != 0) return false;

        var atyp = datagram[3];
        int addressLength;
        switch (atyp)
        {
            case AtypIpv4:
                if (datagram.Length < 10) return false;
                host = new IPAddress(datagram.Slice(4, 4)).ToString();
                addressLength = 4;
                break;
            case AtypIpv6:
                if (datagram.Length < 22) return false;
                host = new IPAddress(datagram.Slice(4, 16)).ToString();
                addressLength = 16;
                break;
            case AtypDomain:
                addressLength = datagram[4];
                if (datagram.Length < 5 + addressLength + 2) return false;
                host = System.Text.Encoding.ASCII.GetString(datagram.Slice(5, addressLength));
                addressLength += 1;
                break;
            default:
                return false;
        }

        var portOffset = 4 + addressLength;
        port = (datagram[portOffset] << 8) | datagram[portOffset + 1];
        payloadOffset = portOffset + 2;
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] BuildRequest(byte command, string host, int port)
    {
        var addressBytes = EncodeAddress(host, out var atyp);
        var request = new byte[4 + addressBytes.Length + 2];
        request[0] = Version;
        request[1] = command;
        request[2] = 0;
        request[3] = atyp;
        addressBytes.CopyTo(request.AsSpan(4));
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);
        return request;
    }

    private static byte[] EncodeAddress(string host, out byte atyp)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                atyp = AtypIpv6;
                return ip.GetAddressBytes();
            }
            atyp = AtypIpv4;
            return ip.GetAddressBytes();
        }

        atyp = AtypDomain;
        var name = System.Text.Encoding.ASCII.GetBytes(host);
        var buffer = new byte[1 + name.Length];
        buffer[0] = (byte)name.Length;
        name.CopyTo(buffer, 1);
        return buffer;
    }

    private static async Task<IPEndPoint> ReadReplyAsync(Socket socket, CancellationToken ct)
    {
        var head = new byte[4];
        await ReadExactAsync(socket, head, ct).ConfigureAwait(false);
        if (head[0] != Version) throw new IOException("Bad SOCKS5 reply version.");
        if (head[1] != 0x00) throw new IOException($"SOCKS5 request failed with code {head[1]}.");

        IPAddress address;
        switch (head[3])
        {
            case AtypIpv4:
            {
                var raw = new byte[4];
                await ReadExactAsync(socket, raw, ct).ConfigureAwait(false);
                address = new IPAddress(raw);
                break;
            }
            case AtypIpv6:
            {
                var raw = new byte[16];
                await ReadExactAsync(socket, raw, ct).ConfigureAwait(false);
                address = new IPAddress(raw);
                break;
            }
            case AtypDomain:
            {
                var length = await ReadOneAsync(socket, ct).ConfigureAwait(false);
                await ReadExactAsync(socket, new byte[length], ct).ConfigureAwait(false);
                address = IPAddress.Loopback;
                break;
            }
            default:
                throw new IOException("Bad SOCKS5 reply address type.");
        }

        var portBytes = new byte[2];
        await ReadExactAsync(socket, portBytes, ct).ConfigureAwait(false);
        return new IPEndPoint(address, (portBytes[0] << 8) | portBytes[1]);
    }

    private static async Task<int> ReadOneAsync(Socket socket, CancellationToken ct)
    {
        var one = new byte[1];
        await ReadExactAsync(socket, one, ct).ConfigureAwait(false);
        return one[0];
    }

    public static async Task ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct)
                .ConfigureAwait(false);
            if (read == 0) throw new IOException("The proxy closed the connection.");
            offset += read;
        }
    }
}
