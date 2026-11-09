using Microsoft.Win32;

namespace RouteScope.Core.SystemTools;

/// <summary>Registers RouteScope in the per-user Run key so it can start with Windows.</summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RouteScope";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static bool Set(bool enabled, string? exePath = null, string arguments = "--minimised")
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var exe = exePath ?? Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" {arguments}".Trim(), RegistryValueKind.String);
                LogBus.Info("autostart", "RouteScope will start with Windows.");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                LogBus.Info("autostart", "Start with Windows disabled.");
            }
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Warn("autostart", "Could not change the startup entry: " + ex.Message);
            return false;
        }
    }
}
