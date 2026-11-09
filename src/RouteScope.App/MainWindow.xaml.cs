using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RouteScope.App.Dialogs;
using RouteScope.App.ViewModels;

namespace RouteScope.App;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeChrome();
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Loaded += (_, _) =>
        {
            if (!App.IsHeadless) SetupTray();
        };
    }

    // ------------------------------------------------------------- window chrome

    private void ApplyNativeChrome()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, sizeof(int));
            var dark = 1;
            DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, sizeof(int));
        }
        catch { /* older Windows */ }
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaxButton?.Content is System.Windows.Controls.TextBlock tb)
            tb.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); } catch { }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------ onboarding

    private void OnboardingMode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.OnboardingStep = 2;
    }

    private void OnboardingAdd_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.HasTargets) vm.FinishOnboardingCommand.Execute(null);
    }

    private void OnboardingPickRunning_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var picker = new Dialogs.ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() != true) return;

        if (!string.IsNullOrWhiteSpace(picker.SelectedPath))
        {
            vm.AddTarget(new Core.Models.ProxyTarget
            {
                Kind = Core.Models.TargetKind.Application,
                Value = picker.SelectedPath,
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(picker.SelectedPath),
            });
        }
        else
        {
            vm.AddProcessName(picker.SelectedName);
        }

        vm.FinishOnboardingCommand.Execute(null);
    }

    // ------------------------------------------------------------------ tray

    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon("RouteScope")
            {
                IsRunning = () => (DataContext as MainViewModel)?.IsRunning ?? false,
            };

            _tray.ShowRequested += RestoreFromTray;
            _tray.ToggleRequested += () =>
            {
                if (DataContext is MainViewModel vm) vm.ToggleConnectCommand.Execute(null);
            };
            _tray.ExitRequested += () =>
            {
                _ = ExitAsync();
            };

            UpdateTrayTooltip();
        }
        catch (Exception ex)
        {
            Core.LogBus.Warn("tray", ex.Message);
        }
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        var vm = DataContext as MainViewModel;
        var state = vm is null ? "" : vm.IsRunning ? $"routing {vm.EnabledTargetsCount} app(s)" : "idle";
        _tray.UpdateTooltip(string.IsNullOrEmpty(state) ? "RouteScope" : $"RouteScope — {state}");
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray()
    {
        Hide();
        UpdateTrayTooltip();
        _tray?.ShowBalloon("RouteScope is still running",
            "Routing continues in the background. Right-click the tray icon to exit.");
    }

    // ---------------------------------------------------------------- closing

    /// <summary>
    /// Any close path (our button, Alt+F4, the taskbar) goes through here, so the user always
    /// gets to choose between keeping routing alive in the tray and shutting everything down.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        var vm = DataContext as MainViewModel;
        var action = vm?.Settings.CloseAction ?? "ask";

        if (action == "ask")
        {
            var dialog = new ExitDialog { Owner = this };
            var accepted = dialog.ShowDialog() == true;
            if (!accepted || dialog.Choice is null)
            {
                e.Cancel = true;
                return;
            }

            if (dialog.Remember && vm is not null)
            {
                vm.Settings.CloseAction = dialog.Choice;
                vm.SaveSettingsCommand.Execute(null);
            }

            action = dialog.Choice;
        }

        if (action == "tray")
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Exit: stop the engine first, then close for real.
        e.Cancel = true;
        _ = ExitAsync();
    }

    /// <summary>Closes without asking. Used by the tray menu and by the render harness.</summary>
    public void PrepareForShutdown()
    {
        _allowClose = true;
        try { _tray?.Dispose(); } catch { }
        _tray = null;
    }

    private async Task ExitAsync()
    {
        if (DataContext is MainViewModel vm)
        {
            try { await vm.StopAsync(); } catch { }
        }

        _allowClose = true;
        try { _tray?.Dispose(); } catch { }
        _tray = null;

        // This often runs synchronously inside the Closing handler, where a nested Close()
        // is ignored by WPF. Post it so the current stack unwinds first.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Close(); } catch { }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
