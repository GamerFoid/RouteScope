using System.Runtime.InteropServices;

namespace RouteScope.Core.Interop;

/// <summary>
/// Raw bindings for NetFilterSDK's user-mode API (<c>nfapi.dll</c>).
///
/// The driver terminates nothing by itself: it intercepts <c>connect()</c> calls from the
/// processes we ask it to, hands the request to this process, and lets us rewrite the
/// destination. That is what lets RouteScope proxy a native game that has no proxy support
/// of its own, while every other program keeps talking to the internet untouched.
///
/// Every struct in the SDK header is inside <c>#pragma pack(push, 1)</c>, so the
/// layouts here are Pack = 1. Getting that wrong corrupts memory silently.
/// </summary>
internal static class NetFilterApi
{
    public const int NF_STATUS_SUCCESS = 0;
    public const int NF_STATUS_FAIL = -1;
    public const int NF_STATUS_INVALID_ENDPOINT_ID = -2;
    public const int NF_STATUS_NOT_INITIALIZED = -3;
    public const int NF_STATUS_IO_ERROR = -4;

    // NF_FILTERING_FLAG
    public const uint NF_ALLOW = 0;
    public const uint NF_BLOCK = 1;
    public const uint NF_FILTER = 2;
    public const uint NF_SUSPENDED = 4;
    public const uint NF_OFFLINE = 8;
    public const uint NF_INDICATE_CONNECT_REQUESTS = 16;
    public const uint NF_DISABLE_REDIRECT_PROTECTION = 32;
    public const uint NF_PEND_CONNECT_REQUEST = 64;
    public const uint NF_FILTER_AS_IP_PACKETS = 128;
    public const uint NF_READONLY = 256;
    public const uint NF_REDIRECT = 1024;

    // NF_DIRECTION
    public const byte NF_D_IN = 1;
    public const byte NF_D_OUT = 2;
    public const byte NF_D_BOTH = 3;

    public const int IPPROTO_TCP = 6;
    public const int IPPROTO_UDP = 17;
    public const ushort AF_INET = 2;
    public const ushort AF_INET6 = 23;

    public const int NF_MAX_ADDRESS_LENGTH = 28;
    public const int NF_MAX_IP_ADDRESS_LENGTH = 16;

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_init([MarshalAs(UnmanagedType.LPStr)] string driverName, IntPtr handler);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void nf_free();

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void nf_setOptions(uint threads, uint flags);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_registerDriver([MarshalAs(UnmanagedType.LPStr)] string driverName);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_unRegisterDriver([MarshalAs(UnmanagedType.LPStr)] string driverName);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void nf_adjustProcessPriviledges();

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_addRule(ref NF_RULE rule, int toHead);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_deleteRules();

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_tcpDisableFiltering(ulong id);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_udpDisableFiltering(ulong id);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_tcpPostSend(ulong id, IntPtr buffer, int length);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_tcpPostReceive(ulong id, IntPtr buffer, int length);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_tcpClose(ulong id);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_udpPostSend(ulong id, IntPtr remoteAddress, IntPtr buffer, int length, IntPtr options);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int nf_udpPostReceive(ulong id, IntPtr remoteAddress, IntPtr buffer, int length, IntPtr options);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern bool nf_getProcessNameFromKernel(uint processId,
        [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder buffer, uint length);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern bool nf_getProcessNameW(uint processId,
        [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder buffer, uint length);

    [DllImport("nfapi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint nf_getDriverType();

    // ------------------------------------------------------------------ structs

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NF_RULE
    {
        public int protocol;
        public uint processId;
        public byte direction;
        public ushort localPort;
        public ushort remotePort;
        public ushort ip_family;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NF_MAX_IP_ADDRESS_LENGTH)] public byte[] localIpAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NF_MAX_IP_ADDRESS_LENGTH)] public byte[] localIpAddressMask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NF_MAX_IP_ADDRESS_LENGTH)] public byte[] remoteIpAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NF_MAX_IP_ADDRESS_LENGTH)] public byte[] remoteIpAddressMask;
        public uint filteringFlag;

        public static NF_RULE Create(int protocol, uint filteringFlag, byte direction = NF_D_OUT) => new()
        {
            protocol = protocol,
            processId = 0,
            direction = direction,
            localPort = 0,
            remotePort = 0,
            ip_family = AF_INET,
            localIpAddress = new byte[NF_MAX_IP_ADDRESS_LENGTH],
            localIpAddressMask = new byte[NF_MAX_IP_ADDRESS_LENGTH],
            remoteIpAddress = new byte[NF_MAX_IP_ADDRESS_LENGTH],
            remoteIpAddressMask = new byte[NF_MAX_IP_ADDRESS_LENGTH],
            filteringFlag = filteringFlag,
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NF_TCP_CONN_INFO
    {
        public uint filteringFlag;
        public uint processId;
        public byte direction;
        public ushort ip_family;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NF_MAX_ADDRESS_LENGTH)] public byte[] localAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NF_MAX_ADDRESS_LENGTH)] public byte[] remoteAddress;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NF_UDP_CONN_INFO
    {
        public uint processId;
        public ushort ip_family;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NF_MAX_ADDRESS_LENGTH)] public byte[] localAddress;
    }

    /// <summary>
    /// The header is <c>unsigned long flags; long optionsLength; unsigned char options[1];</c>
    /// — the options follow inline, they are not a pointer. Only the 8-byte header is ever
    /// built here (optionsLength = 0), so no inline storage is declared.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NF_UDP_OPTIONS
    {
        public uint flags;
        public int optionsLength;
    }

    /// <summary>16 function pointers, in header order. Do not reorder.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NF_EVENT_HANDLER
    {
        public IntPtr threadStart;
        public IntPtr threadEnd;
        public IntPtr tcpConnectRequest;
        public IntPtr tcpConnected;
        public IntPtr tcpClosed;
        public IntPtr tcpReceive;
        public IntPtr tcpSend;
        public IntPtr tcpCanReceive;
        public IntPtr tcpCanSend;
        public IntPtr udpCreated;
        public IntPtr udpConnectRequest;
        public IntPtr udpClosed;
        public IntPtr udpReceive;
        public IntPtr udpSend;
        public IntPtr udpCanReceive;
        public IntPtr udpCanSend;
    }

    // --------------------------------------------------------------- addresses

    /// <summary>Reads a sockaddr (v4 or v6) out of the fixed 28-byte field.</summary>
    public static (ushort family, byte[] raw, string text, ushort port) ReadAddress(byte[] field)
    {
        var family = BitConverter.ToUInt16(field, 0);
        var port = (ushort)System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt16(field, 2));

        string text;
        if (family == AF_INET6)
        {
            var bytes = new byte[16];
            Array.Copy(field, 8, bytes, 0, 16);
            text = new System.Net.IPAddress(bytes).ToString();
        }
        else
        {
            var bytes = new byte[4];
            Array.Copy(field, 4, bytes, 0, 4);
            text = new System.Net.IPAddress(bytes).ToString();
        }
        return (family, field, text, port);
    }

    /// <summary>Writes a loopback destination of the same family into a 28-byte field.</summary>
    public static void WriteLoopback(byte[] field, ushort family, ushort port)
    {
        Array.Clear(field, 0, field.Length);
        BitConverter.TryWriteBytes(field.AsSpan(0, 2), family);
        BitConverter.TryWriteBytes(field.AsSpan(2, 2), System.Net.IPAddress.HostToNetworkOrder((short)port));

        if (family == AF_INET6)
        {
            // ::1
            field[23] = 1;
        }
        else
        {
            field[4] = 127;
            field[7] = 1;
        }
    }
}
