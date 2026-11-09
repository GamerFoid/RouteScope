using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RouteScope.App.ViewModels;
using RouteScope.App.Views;
using RouteScope.Core;
using RouteScope.Core.Storage;

namespace RouteScope.App;

public partial class App : Application
{
    public static StateStore Store { get; private set; } = null!;
    public static MainViewModel ViewModel { get; private set; } = null!;

    /// <summary>True for --demo / --render runs: no tray icon, no close prompt.</summary>
    public static bool IsHeadless { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandled;

        AppPaths.EnsureCreated();
        AppPaths.MigrateLegacyData();

        var isDemo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var isRender = e.Args.Contains("--render", StringComparer.OrdinalIgnoreCase);
        IsHeadless = isDemo || isRender;

        // Demo / screenshot runs get a throwaway profile so they can never overwrite a
        // real user's configuration.
        if (isDemo || isRender)
        {
            var demoProfile = Path.Combine(AppPaths.CacheDir, "demo-profile.json");
            try { if (File.Exists(demoProfile)) File.Delete(demoProfile); } catch { }
            Store = new StateStore(demoProfile);
        }
        else
        {
            Store = new StateStore();
        }

        Store.Load();

        ThemeService.ApplyAccent(Store.State.Settings.Accent);

        ViewModel = new MainViewModel(Store);
        MainWindow = new MainWindow { DataContext = ViewModel };

        if (isDemo || isRender)
        {
            DemoData.Seed(Store);
            ViewModel.ReloadFromStore();
            DemoData.PopulateLive(ViewModel);
            ViewModel.ApplyDemoRuntime();
            ViewModel.HideOnboarding();
        }

        if (isRender)
        {
            RenderHarness.Run(e.Args);
            return;
        }

        MainWindow.Show();

        if (e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase) ||
            Store.State.Settings.StartEngineOnLaunch)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(700);
                if (ViewModel.HasTargets) await ViewModel.StartAsync();
            }, DispatcherPriority.Background);
        }

        if (Store.State.Settings.CheckUpdatesOnLaunch)
        {
            Dispatcher.BeginInvoke(() => ViewModel.RefreshEngineState(), DispatcherPriority.Background);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogBus.Error("ui", e.Exception.Message);
        e.Handled = true;
        ViewModel?.ShowToast("Something went wrong: " + e.Exception.Message);
    }
}

/// <summary>Applies the accent colour before any view is parsed, so StaticResource picks it up.</summary>
public static class ThemeService
{
    private static readonly Dictionary<string, (Color Accent, Color Accent2)> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["violet"] = (Color.FromRgb(0x7C, 0x6B, 0xFF), Color.FromRgb(0x45, 0xC4, 0xF5)),
        ["cyan"] = (Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x60, 0xA5, 0xFA)),
        ["emerald"] = (Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x7D, 0xD3, 0xFC)),
        ["amber"] = (Color.FromRgb(0xF5, 0xB9, 0x44), Color.FromRgb(0xFB, 0x92, 0x3C)),
        ["rose"] = (Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xA7, 0x8B, 0xFA)),
    };

    public static IReadOnlyList<string> Names => Presets.Keys.ToList();

    public static void ApplyAccent(string? name)
    {
        var preset = Presets.TryGetValue(name ?? "violet", out var p) ? p : Presets["violet"];
        var resources = Application.Current.Resources;

        void SetColor(string key, Color color)
        {
            resources[key] = color;
            var brushKey = key.Replace("C.", "B.");
            var brush = new SolidColorBrush(color);
            resources[brushKey] = brush;
        }

        SetColor("C.Accent", preset.Accent);
        SetColor("C.Accent.Hover", Lighten(preset.Accent, 0.12));
        SetColor("C.Accent.Pressed", Darken(preset.Accent, 0.12));
        SetColor("C.Accent2", preset.Accent2);
        SetColor("C.Accent.Soft", Color.FromArgb(0x33, preset.Accent.R, preset.Accent.G, preset.Accent.B));
        SetColor("C.Accent.Glow", Color.FromArgb(0x4D, preset.Accent.R, preset.Accent.G, preset.Accent.B));

        resources["B.AccentGradient"] = new LinearGradientBrush(preset.Accent, preset.Accent2, new Point(0, 0), new Point(1, 1));
        resources["B.HeroGlow"] = BuildVerticalFade(Color.FromArgb(0x2E, preset.Accent.R, preset.Accent.G, preset.Accent.B));
        resources["B.RingGlow"] = BuildRadial(Color.FromArgb(0x59, preset.Accent.R, preset.Accent.G, preset.Accent.B));
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)Math.Min(255, c.R + 255 * amount),
        (byte)Math.Min(255, c.G + 255 * amount),
        (byte)Math.Min(255, c.B + 255 * amount));

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)Math.Max(0, c.R - 255 * amount),
        (byte)Math.Max(0, c.G - 255 * amount),
        (byte)Math.Max(0, c.B - 255 * amount));

    private static LinearGradientBrush BuildVerticalFade(Color c) => new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1),
        GradientStops = new GradientStopCollection
        {
            new(c, 0),
            new(Color.FromArgb(0, c.R, c.G, c.B), 1),
        },
    };

    private static RadialGradientBrush BuildRadial(Color c) => new()
    {
        Center = new Point(0.5, 0.5),
        GradientOrigin = new Point(0.5, 0.5),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops = new GradientStopCollection
        {
            new(c, 0.62),
            new(Color.FromArgb(0, c.R, c.G, c.B), 1),
        },
    };
}
