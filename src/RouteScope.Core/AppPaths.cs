using System.Diagnostics;
using System.Text;

namespace RouteScope.Core;

/// <summary>
/// Well-known filesystem locations. Everything RouteScope writes lives under
/// %LOCALAPPDATA%\RouteScope so an uninstall is a single folder delete.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "RouteScope";
    public const string AppId = "RouteScope";

    public static string Root { get; } = ResolveRoot();
    public static string EnginesDir { get; } = Path.Combine(Root, "engines");
    public static string DataDir { get; } = Path.Combine(Root, "data");
    public static string LogsDir { get; } = Path.Combine(Root, "logs");
    public static string CacheDir { get; } = Path.Combine(Root, "cache");
    public static string PsiphonDir { get; } = Path.Combine(Root, "psiphon");
    public static string StateFile { get; } = Path.Combine(DataDir, "routescope.json");
    public static string RuntimeConfigFile { get; } = Path.Combine(DataDir, "runtime.singbox.json");
    public static string LogFile { get; } = Path.Combine(LogsDir, "routescope.log");

    /// <summary>True when everything lives next to the executable (the normal, portable layout).</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>Where older builds kept their data, kept around so we can migrate it once.</summary>
    public static string LegacyRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    private static string ResolveRoot()
    {
        // 1. Explicit override wins, useful for tests and for a shared install.
        var overridden = Environment.GetEnvironmentVariable("ROUTESCOPE_HOME");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        // 2. Portable: data\, engines\, logs\ and cache\ sit beside the exe.
        var exeDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(exeDir) && IsWritable(exeDir))
        {
            IsPortable = true;
            return exeDir.TrimEnd(Path.DirectorySeparatorChar);
        }

        // 3. Read-only location (e.g. Program Files): fall back to the user profile.
        IsPortable = false;
        return LegacyRoot;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".routescope-write-test");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                fs.WriteByte(0);
            return true;
        }
        catch { return false; }
    }

    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Root, EnginesDir, DataDir, LogsDir, CacheDir })
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Copies a profile and downloaded engines from the old %LOCALAPPDATA% location the first
    /// time the portable layout is used, so an update does not look like a reset.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            if (!IsPortable) return;
            if (string.Equals(Root, LegacyRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(LegacyRoot)) return;

            var legacyState = Path.Combine(LegacyRoot, "data", "routescope.json");
            if (!File.Exists(legacyState)) return;

            EnsureCreated();
            if (File.Exists(StateFile)) return; // already have a profile here

            File.Copy(legacyState, StateFile, overwrite: false);
            LogBus.Info("storage", "Imported your previous profile from " + LegacyRoot);

            var legacyEngines = Path.Combine(LegacyRoot, "engines");
            if (Directory.Exists(legacyEngines) && !Directory.EnumerateFileSystemEntries(EnginesDir).Any())
            {
                foreach (var dir in Directory.GetDirectories(legacyEngines))
                {
                    var target = Path.Combine(EnginesDir, Path.GetFileName(dir));
                    CopyDirectory(dir, target);
                }
                LogBus.Info("storage", "Copied previously downloaded engines next to the app.");
            }
        }
        catch (Exception ex)
        {
            LogBus.Warn("storage", "Could not import the old profile: " + ex.Message);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
        catch { }
    }

    public static string EngineDir(string engineId) => Path.Combine(EnginesDir, engineId);

    public static string EngineExe(string engineId, string exeName) => Path.Combine(EngineDir(engineId), exeName);

    /// <summary>Directory that holds a downloaded-and-unpacked engine version.</summary>
    public static string EngineVersionDir(string engineId, string version) =>
        Path.Combine(EngineDir(engineId), version);

    public static string TempDownloadPath(string fileName) =>
        Path.Combine(CacheDir, $"{Guid.NewGuid():N}-{fileName}");
}

/// <summary>Tiny append-only rolling logger that also feeds the in-app log view.</summary>
public static class LogBus
{
    private static readonly object Gate = new();
    private static readonly Queue<LogLine> Buffer = new();
    private static readonly StringBuilder Pending = new();
    private static readonly Timer FlushTimer = new(_ => Flush(), null, 800, 800);
    private const int MaxBuffered = 3000;
    private const int MaxPendingChars = 32_000;

    public static event Action<LogLine>? LineWritten;

    public static IReadOnlyList<LogLine> Snapshot()
    {
        lock (Gate) return Buffer.ToArray();
    }

    public static void Write(LogLevel level, string source, string message)
    {
        var line = new LogLine(DateTime.Now, level, source, message);

        // Nothing here may touch the disk. This method is called from NetFilter driver
        // callbacks: blocking one of those blocks a connect() inside the kernel, and doing it
        // while holding Gate blocks every other callback too — which freezes the whole machine.
        lock (Gate)
        {
            Buffer.Enqueue(line);
            while (Buffer.Count > MaxBuffered) Buffer.Dequeue();

            Pending.AppendLine(line.ToString());

            // Over budget: drop the oldest text instead of flushing from the caller.
            if (Pending.Length > MaxPendingChars)
                Pending.Remove(0, Pending.Length - MaxPendingChars / 2);
        }

        try { LineWritten?.Invoke(line); } catch { }
    }

    /// <summary>
    /// Writes buffered lines to disk. Only the timer calls this, and the write happens outside
    /// the lock so a slow disk can never stall a caller.
    /// </summary>
    public static void Flush()
    {
        string text;
        lock (Gate)
        {
            if (Pending.Length == 0) return;
            text = Pending.ToString();
            Pending.Clear();
        }

        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(AppPaths.LogFile, text, Encoding.UTF8);
        }
        catch { /* logging must never throw */ }
    }

    public static void Info(string src, string msg) => Write(LogLevel.Info, src, msg);
    public static void Warn(string src, string msg) => Write(LogLevel.Warn, src, msg);
    public static void Error(string src, string msg) => Write(LogLevel.Error, src, msg);
    public static void Debug(string src, string msg) => Write(LogLevel.Debug, src, msg);

    public static void Clear()
    {
        lock (Gate)
        {
            Buffer.Clear();
            Pending.Clear();
        }
        try { File.Delete(AppPaths.LogFile); } catch { }
    }

    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogsDir) { UseShellExecute = true });
        }
        catch { }
    }
}

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogLine(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-5}] {Source,-14} {Message}";
}
