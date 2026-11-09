using System.Diagnostics;
using System.Security.Principal;

namespace RouteScope.Core.SystemTools;

/// <summary>Elevation helpers. RouteScope runs unelevated and only asks for admin for TUN mode.</summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Restarts RouteScope elevated, preserving the command line arguments.</summary>
    public static bool RelaunchElevated(params string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(Quote)),
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Warn("elevation", "User declined or elevation failed: " + ex.Message);
            return false;
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
