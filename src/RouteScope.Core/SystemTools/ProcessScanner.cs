using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RouteScope.Core.SystemTools;

public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string Name,
    string? ExePath,
    DateTime? StartedUtc)
{
    public string? Folder
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExePath)) return null;
            try { return Path.GetDirectoryName(ExePath); } catch { return null; }
        }
    }

    public bool IsSystem => Pid is 0 or 4 || string.IsNullOrWhiteSpace(ExePath);
}

/// <summary>Enumerates running processes with their image path, which is what per-folder routing needs.</summary>
public static class ProcessScanner
{
    public static List<ProcessInfo> Snapshot()
    {
        var list = new List<ProcessInfo>();
        var handles = new Dictionary<int, string?>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var pid = p.Id;
                if (pid <= 4) { p.Dispose(); continue; }

                string? exe = null;
                try { exe = p.MainModule?.FileName; } catch { exe = QueryImagePath(pid); }

                DateTime? started = null;
                try { started = p.StartTime.ToUniversalTime(); } catch { }

                list.Add(new ProcessInfo(pid, GetParentPid(pid), p.ProcessName, exe, started));
            }
            catch { /* protected process */ }
            finally { p.Dispose(); }
        }

        return list;
    }

    private static string? QueryImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, sb, ref size)) return sb.ToString();
            return null;
        }
        finally { CloseHandle(handle); }
    }

    private static int GetParentPid(int pid)
    {
        try
        {
            var snapshot = CreateToolhelp32Snapshot(0x00000002 /*TH32CS_SNAPPROCESS*/, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return 0;
            try
            {
                var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snapshot, ref pe))
                {
                    do
                    {
                        if ((int)pe.th32ProcessID == pid) return (int)pe.th32ParentProcessID;
                    } while (Process32NextW(snapshot, ref pe));
                }
            }
            finally { CloseHandle(snapshot); }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Given the enabled targets, returns extra process-path regexes for processes that
    /// were spawned by a matched process. This is what makes launchers and game clients
    /// work without the user having to hunt down every child executable.
    /// </summary>
    public static (List<string> PathRegexes, List<string> Names) ExpandChildProcesses(
        IEnumerable<Models.ProxyTarget> targets)
    {
        var pathRegexes = new List<string>();
        var names = new List<string>();
        var targetList = targets.Where(t => t.Enabled).ToList();
        if (targetList.Count == 0) return (pathRegexes, names);

        var snapshot = Snapshot();
        if (snapshot.Count == 0) return (pathRegexes, names);

        var byPid = snapshot.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var children = snapshot
            .Where(p => p.ParentPid > 0 && p.ParentPid != p.Pid)
            .GroupBy(p => p.ParentPid)
            .ToDictionary(g => g.Key, g => g.ToList());

        var matched = new HashSet<int>();
        foreach (var t in targetList.Where(t => t.IncludeChildProcesses))
        {
            foreach (var p in snapshot)
            {
                if (p.IsSystem) continue;
                if (t.Matches(p.ExePath, p.Name + ".exe"))
                    matched.Add(p.Pid);
            }
        }

        // Walk down the tree from every match.
        var frontier = new Queue<int>(matched);
        var seen = new HashSet<int>(matched);
        while (frontier.Count > 0)
        {
            var pid = frontier.Dequeue();
            if (!children.TryGetValue(pid, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!seen.Add(kid.Pid)) continue;
                frontier.Enqueue(kid.Pid);
                if (string.IsNullOrWhiteSpace(kid.ExePath)) continue;

                var re = "(?i)^" + System.Text.RegularExpressions.Regex.Escape(kid.ExePath) + "$";
                if (!pathRegexes.Contains(re)) pathRegexes.Add(re);
            }
        }

        return (pathRegexes, names);
    }

    /// <summary>All distinct executable paths that live inside a folder (recursive, depth limited).</summary>
    public static List<string> FindExecutables(string folder, int maxDepth = 4)
    {
        var results = new List<string>();
        if (!Directory.Exists(folder)) return results;

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
                    results.Add(file);
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Walk(sub, depth + 1);
            }
            catch { }
        }

        Walk(folder, 0);
        return results;
    }

    /// <summary>
    /// Finds the process that owns a listening TCP port on this machine. Used to work out
    /// which client is providing a local proxy (v2rayN, Clash…) so its own upstream
    /// traffic can be kept out of the tunnel — otherwise it gets captured, sent back to
    /// itself, and loops.
    /// </summary>
    public static ProcessInfo? FindListenerProcess(int port)
    {
        try
        {
            var buffer = IntPtr.Zero;
            var size = 0;

            // TCP_TABLE_OWNER_PID_LISTENER = 3, AF_INET = 2
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
            if (size <= 0) return null;

            buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
                if (result != 0) return null;

                var count = Marshal.ReadInt32(buffer);
                var rowPtr = IntPtr.Add(buffer, 4);
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(IntPtr.Add(rowPtr, i * rowSize));
                    var localPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row.localPort & 0xFFFF));
                    if (localPort != port) continue;

                    var pid = (int)row.owningPid;
                    if (pid <= 4) continue;

                    var name = "unknown";
                    try { name = Process.GetProcessById(pid).ProcessName; } catch { }

                    return new ProcessInfo(pid, 0, name, QueryImagePath(pid), null);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Every TCP port currently listening on this machine, with the process that owns it.
    /// Used to find local proxies instead of guessing a handful of well-known ports.
    /// </summary>
    public static List<(int Port, int Pid, string Address)> GetListeningPorts()
    {
        var result = new List<(int, int, string)>();
        var buffer = IntPtr.Zero;
        try
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0); // AF_INET, LISTENER
            if (size <= 0) return result;

            buffer = Marshal.AllocHGlobal(size);
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) return result;

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(IntPtr.Add(rowPtr, i * rowSize));
                var port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)(row.localPort & 0xFFFF));
                if (port == 0) continue;

                var address = new System.Net.IPAddress(row.localAddr).ToString();
                result.Add((port, (int)row.owningPid, address));
            }
        }
        catch { }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }

        return result;
    }

    // ---------------------------------------------------------------- native

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order,
        int addressFamily, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
