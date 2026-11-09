using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RouteScope.App.ViewModels;

namespace RouteScope.App;

/// <summary>
/// Development helper: renders each page to a PNG so the layout can be inspected without
/// a human at the keyboard. Invoked with <c>--render &lt;outputDir&gt;</c>.
/// </summary>
public static class RenderHarness
{
    private static string _logPath = "";

    private static void Log(string message)
    {
        try { File.AppendAllText(_logPath, message + Environment.NewLine); } catch { }
    }

    public static void Run(string[] args)
    {
        var outputDir = args.Length > 1 && !args[1].StartsWith("--")
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "render");

        Directory.CreateDirectory(outputDir);
        _logPath = Path.Combine(outputDir, "render.log");
        File.WriteAllText(_logPath, "start " + DateTime.Now + Environment.NewLine);

        var window = Application.Current.MainWindow!;
        var vm = (MainViewModel)window.DataContext;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 60;
        window.Top = 60;
        window.Width = 1240;
        window.Height = 890;
        window.ShowInTaskbar = false;

        window.ContentRendered += async (_, _) =>
        {
            try
            {
                // Let the first frame and the orb animation settle.
                await Task.Delay(900);

                var pages = new (string Name, Page Page)[]
                {
                    ("01-home", Page.Home),
                    ("02-targets", Page.Targets),
                    ("03-servers", Page.Servers),
                    ("04-live", Page.Live),
                    ("05-logs", Page.Logs),
                    ("06-settings", Page.Settings),
        ("06b-support", Page.Support),
                };

                foreach (var (name, page) in pages)
                {
                    vm.CurrentPage = page;
                    await Task.Delay(260);
                    window.UpdateLayout();
                    await Task.Delay(120);
                    Capture(window, outputDir, name);

                    if (page is Page.Settings or Page.Servers)
                    {
                        var scrollers = FindScrollableViewers(window);
                        if (scrollers.Count > 0)
                        {
                            foreach (var s in scrollers) s.ScrollToEnd();
                            await Task.Delay(240);
                            window.UpdateLayout();
                            await Task.Delay(100);
                            Capture(window, outputDir, name + "b");
                            foreach (var s in scrollers) s.ScrollToHome();
                        }
                    }
                }

                // Whole-computer mode warning on the targets page
                vm.SetModeCommand.Execute("Transparent");
                vm.CurrentPage = Page.Targets;
                await Task.Delay(260);
                window.UpdateLayout();
                await Task.Delay(100);
                Capture(window, outputDir, "10-targets-whole-computer");

                // System proxy gets its own wording (this used to say "whole computer")
                vm.SetModeCommand.Execute("SystemProxy");
                await Task.Delay(240);
                window.UpdateLayout();
                await Task.Delay(100);
                Capture(window, outputDir, "11-targets-system-proxy");

                vm.SetModeCommand.Execute("AppProxy");

                // Close-to-tray prompt
                var exitDialog = new Dialogs.ExitDialog
                {
                    Owner = window,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                exitDialog.Show();
                await Task.Delay(300);
                exitDialog.UpdateLayout();
                await Task.Delay(100);
                Capture(exitDialog, outputDir, "12-exit-dialog");
                exitDialog.Close();

                // Context menu styling (targets list / tray)
                try
                {
                    var menu = new System.Windows.Controls.ContextMenu();
                    foreach (var header in new[] { "Launch now", "Open in Explorer" })
                        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = header });
                    menu.Items.Add(Views.TargetsView.MenuDivider());
                    menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Remove" });

                    menu.Measure(new Size(220, 300));
                    menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
                    menu.UpdateLayout();

                    var size = menu.DesiredSize;
                    var rtb = new RenderTargetBitmap(
                        (int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2),
                        192, 192, PixelFormats.Pbgra32);
                    rtb.Render(menu);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    using var stream = File.Create(Path.Combine(outputDir, "13-context-menu.png"));
                    encoder.Save(stream);
                    Log("rendered context menu");
                }
                catch (Exception ex)
                {
                    Log("context menu render failed: " + ex.Message);
                }

                // Server editor
                try
                {
                    var node = vm.Nodes.FirstOrDefault();
                    if (node is not null)
                    {
                        var editor = new Dialogs.NodeEditorDialog(node) { Owner = window };
                        editor.Show();
                        await Task.Delay(400);
                        editor.UpdateLayout();
                        await Task.Delay(150);
                        Capture(editor, outputDir, "14-node-editor");
                        editor.Close();
                    }
                }
                catch (Exception ex)
                {
                    Log("node editor render failed: " + ex.Message);
                }

                // First-run wizard
                vm.CurrentPage = Page.Home;
                for (var step = 0; step <= 2; step++)
                {
                    vm.ShowOnboarding(step);
                    await Task.Delay(240);
                    window.UpdateLayout();
                    await Task.Delay(100);
                    Capture(window, outputDir, $"0{7 + step}-onboarding-{step + 1}");
                }
                vm.HideOnboarding();

                Log("done");
            }
            catch (Exception ex)
            {
                Log("harness failed: " + ex);
            }
            finally
            {
                (window as MainWindow)?.PrepareForShutdown();
                window.Close();
                Application.Current.Shutdown();
            }
        };

        window.Show();
    }

    /// <summary>Every scrollable view that is on screen — scrolling all of them is harmless.</summary>
    private static List<System.Windows.Controls.ScrollViewer> FindScrollableViewers(DependencyObject root)
    {
        var found = new List<System.Windows.Controls.ScrollViewer>();

        void Walk(DependencyObject node)
        {
            if (node is System.Windows.Controls.ScrollViewer sv &&
                sv.Visibility == Visibility.Visible &&
                sv.ScrollableHeight > 0)
            {
                found.Add(sv);
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }

        Walk(root);
        return found;
    }

    private static void Capture(Window window, string outputDir, string name)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var w = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0)
            {
                Log($"render {name}: zero size {w}x{h}");
                return;
            }

            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

            var background = new DrawingVisual();
            using (var ctx = background.RenderOpen())
            {
                ctx.DrawRectangle(window.Background ?? Brushes.Black, null,
                    new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            }
            rtb.Render(background);
            rtb.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            var path = Path.Combine(outputDir, name + ".png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            Log("rendered " + path);
        }
        catch (Exception ex)
        {
            Log($"render {name} failed: {ex.Message}");
        }
    }
}
