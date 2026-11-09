using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace RouteScope.App;

/// <summary>
/// A tray icon built directly on Shell_NotifyIcon. RouteScope hides to the tray instead of
/// exiting, so routing keeps running while the window is out of the way.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYMESSAGE = 0x8001; // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_INFO = 0x10;

    private HwndSource? _source;
    private IntPtr _icon;
    private bool _added;
    private string _tooltip;

    public event Action? ShowRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;
    public Func<bool>? IsRunning { get; set; }

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        try
        {
            var parameters = new HwndSourceParameters("RouteScopeTray")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
            };
            parameters.ParentWindow = new IntPtr(-3); // HWND_MESSAGE

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            _icon = LoadAppIcon();
            _added = Add();
        }
        catch (Exception ex)
        {
            Core.LogBus.Warn("tray", "Could not create the tray icon: " + ex.Message);
        }
    }

    public bool IsVisible => _added;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAYMESSAGE) return IntPtr.Zero;

        switch (lParam.ToInt32() & 0xFFFF)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                ShowRequested?.Invoke();
                handled = true;
                break;
            case WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var running = IsRunning?.Invoke() ?? false;

        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open RouteScope" };
        open.Click += (_, _) => ShowRequested?.Invoke();
        menu.Items.Add(open);

        menu.Items.Add(Views.TargetsView.MenuDivider());

        var toggle = new MenuItem { Header = running ? "Stop routing" : "Start routing" };
        toggle.Click += (_, _) => ToggleRequested?.Invoke();
        menu.Items.Add(toggle);

        menu.Items.Add(Views.TargetsView.MenuDivider());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;

        // Bring the hidden window forward so the menu dismisses when focus is lost.
        try
        {
            GetCursorPos(out var point);
            SetForegroundWindow(_source?.Handle ?? IntPtr.Zero);
            PostMessage(_source?.Handle ?? IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (string.IsNullOrEmpty(tooltip) || tooltip == _tooltip) return;
        _tooltip = tooltip;
        if (!_added) return;

        var data = CreateData();
        data.uFlags = NIF_TIP;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        try
        {
            var data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            data.dwInfoFlags = 0x1; // NIIF_INFO
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { }
    }

    private bool Add()
    {
        if (_source is null) return false;
        var data = CreateData();
        return Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _source?.Handle ?? IntPtr.Zero,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYMESSAGE,
        hIcon = _icon,
        szTip = Truncate(_tooltip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) &&
                ExtractIconEx(exe, 0, out _, out var small, 1) > 0 && small != IntPtr.Zero)
                return small;
        }
        catch { }

        return LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_added)
        {
            try
            {
                var data = CreateData();
                Shell_NotifyIcon(NIM_DELETE, ref data);
            }
            catch { }
            _added = false;
        }

        try { _source?.RemoveHook(WndProc); } catch { }
        try { _source?.Dispose(); } catch { }
        _source = null;

        if (_icon != IntPtr.Zero)
        {
            try { DestroyIcon(_icon); } catch { }
            _icon = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- native

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
