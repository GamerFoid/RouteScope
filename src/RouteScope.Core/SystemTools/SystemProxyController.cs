using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RouteScope.Core.SystemTools;

/// <summary>
/// Windows system proxy (the same switch v2rayN flips). Values are snapshotted before
/// being changed so "Stop" always puts the machine back exactly how it was.
/// </summary>
public sealed class SystemProxyController
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private ProxySnapshot? _snapshot;

    public bool IsApplied { get; private set; }

    public bool Apply(int port, string bypassList = "<local>;localhost;127.*;10.*;172.16.*-172.31.*;192.168.*")
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return false;

            _snapshot ??= ProxySnapshot.Capture(key);

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);

            Refresh();
            IsApplied = true;
            LogBus.Info("system-proxy", $"System proxy -> 127.0.0.1:{port}");
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Error("system-proxy", "Could not set the system proxy: " + ex.Message);
            return false;
        }
    }

    public void Revert()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return;

            if (_snapshot is not null)
            {
                key.SetValue("ProxyEnable", _snapshot.Enable, RegistryValueKind.DWord);
                if (_snapshot.Server is null) key.DeleteValue("ProxyServer", throwOnMissingValue: false);
                else key.SetValue("ProxyServer", _snapshot.Server, RegistryValueKind.String);
                if (_snapshot.Override is null) key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
                else key.SetValue("ProxyOverride", _snapshot.Override, RegistryValueKind.String);
            }
            else
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            }

            Refresh();
            IsApplied = false;
            _snapshot = null;
            LogBus.Info("system-proxy", "System proxy restored.");
        }
        catch (Exception ex)
        {
            LogBus.Error("system-proxy", "Could not restore the system proxy: " + ex.Message);
        }
    }

    /// <summary>Tells WinINet (Edge, Chrome, IE, many desktop apps) that the settings changed.</summary>
    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39 /*INTERNET_OPTION_SETTINGS_CHANGED*/, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /*INTERNET_OPTION_REFRESH*/, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    private sealed record ProxySnapshot(int Enable, string? Server, string? Override)
    {
        public static ProxySnapshot Capture(RegistryKey key) => new(
            key.GetValue("ProxyEnable") is int e ? e : 0,
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string);
    }
}
