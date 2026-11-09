using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RouteScope.App.Dialogs;
using RouteScope.App.ViewModels;
using RouteScope.Core.Models;

namespace RouteScope.App.Views;

public partial class TargetsView : UserControl
{
    public TargetsView()
    {
        InitializeComponent();
        ContextMenu = BuildMenu();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var launch = new MenuItem { Header = "Launch now" };
        launch.Click += (_, _) => Vm?.LaunchTargetCommand.Execute(_menuTarget);
        menu.Items.Add(launch);

        var open = new MenuItem { Header = "Open in Explorer" };
        open.Click += (_, _) => Vm?.BrowseToTargetCommand.Execute(_menuTarget);
        menu.Items.Add(open);

        menu.Items.Add(MenuDivider());

        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) => Vm?.RemoveTargetCommand.Execute(_menuTarget);
        menu.Items.Add(remove);

        return menu;
    }

    /// <summary>A plain 1px rule. The built-in Separator keeps the light default template.</summary>
    internal static Border MenuDivider() => new()
    {
        Height = 1,
        Margin = new Thickness(10, 5, 10, 5),
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("B.Border.Default"),
    };

    private ProxyTarget? _menuTarget;

    private void TargetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ProxyTarget target) return;
        _menuTarget = target;

        if (ContextMenu is not null)
        {
            ContextMenu.PlacementTarget = button;
            ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ContextMenu.IsOpen = true;
        }
    }

    private void PickRunningApp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var owner = Window.GetWindow(this);
        var picker = new ProcessPickerWindow();
        if (owner is not null) picker.Owner = owner;
        if (picker.ShowDialog() == true && picker.SelectedName is not null)
        {
            if (!string.IsNullOrWhiteSpace(picker.SelectedPath) && File.Exists(picker.SelectedPath))
            {
                Vm.AddTarget(new ProxyTarget
                {
                    Kind = TargetKind.Application,
                    Value = picker.SelectedPath,
                    DisplayName = Path.GetFileNameWithoutExtension(picker.SelectedPath),
                });
            }
            else
            {
                Vm.AddProcessName(picker.SelectedName);
            }
        }
    }

    // -------------------------------------------------------------- drag drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Vm.AddTarget(new ProxyTarget
                    {
                        Kind = TargetKind.Folder,
                        Value = path,
                        DisplayName = Path.GetFileName(path.TrimEnd('\\')),
                    });
                }
                else if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    Vm.AddTarget(new ProxyTarget
                    {
                        Kind = TargetKind.Application,
                        Value = path,
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                    });
                }
                else if (File.Exists(path))
                {
                    Vm.ShowToast("Only .exe files and folders can be dropped here.");
                }
            }
            catch (Exception ex)
            {
                Vm.ShowToast(ex.Message);
            }
        }
    }
}

