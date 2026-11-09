using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RouteScope.App.ViewModels;
using RouteScope.Core.Models;

namespace RouteScope.App.Views;

public partial class ServersView : UserControl
{
    public ServersView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>A .conf dropped on the servers card is imported as a WireGuard route.</summary>
    private void OnServersDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetConfigFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnServersDrop(object sender, DragEventArgs e)
    {
        var path = TryGetConfigFile(e);
        if (path is null) return;

        if (path.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)) Vm?.ImportOpenVpnFile(path);
        else Vm?.ImportWireGuardFile(path);
        e.Handled = true;
    }

    private static string? TryGetConfigFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return null;

        var path = files[0];
        if (path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) return path;
        if (path.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase)) return path;
        return null;
    }

    /// <summary>
    /// The "⋯" menu on a local proxy or a server. Everything destructive lives behind this
    /// one button, so a row stays a single click target for selecting.
    /// </summary>
    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: { } item } element) return;

        var menu = new ContextMenu();

        switch (item)
        {
            case ExternalProxy ext:
            {
                var add = new MenuItem { Header = "Add to chain" };
                add.Click += (_, _) => Vm?.AddChainHopCommand.Execute(ext);
                menu.Items.Add(add);

                var edit = new MenuItem { Header = "Edit…" };
                edit.Click += (_, _) => Vm?.EditExternalCommand.Execute(ext);
                menu.Items.Add(edit);

                menu.Items.Add(TargetsView.MenuDivider());

                var remove = new MenuItem { Header = "Remove" };
                remove.Click += (_, _) => Vm?.RemoveExternalCommand.Execute(ext);
                menu.Items.Add(remove);
                break;
            }

            case ProxyNode node:
            {
                var add = new MenuItem { Header = "Add to chain" };
                add.Click += (_, _) => Vm?.AddChainHopCommand.Execute(node);
                menu.Items.Add(add);

                var test = new MenuItem { Header = "Test latency" };
                test.Click += (_, _) => Vm?.TestLatencyCommand.Execute(node);
                menu.Items.Add(test);

                menu.Items.Add(TargetsView.MenuDivider());

                var remove = new MenuItem { Header = "Remove" };
                remove.Click += (_, _) => Vm?.RemoveNodeCommand.Execute(node);
                menu.Items.Add(remove);
                break;
            }

            default:
                return;
        }

        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
