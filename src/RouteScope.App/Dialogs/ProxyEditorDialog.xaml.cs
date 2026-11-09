using System.Windows;
using System.Windows.Input;
using RouteScope.Core.Models;

namespace RouteScope.App.Dialogs;

public partial class ProxyEditorDialog : Window
{
    private readonly ExternalProxy _target;

    public ProxyEditorDialog(ExternalProxy proxy, bool isNew)
    {
        InitializeComponent();
        _target = proxy;

        TitleText.Text = isNew ? "Add a local proxy" : "Edit local proxy";
        NameBox.Text = proxy.Name;
        HostBox.Text = proxy.Host;
        PortBox.Text = proxy.Port.ToString();
        TypeBox.SelectedValue = string.IsNullOrWhiteSpace(proxy.Type) ? "Auto" : proxy.Type;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        var type = TypeBox.SelectedValue as string ?? "Auto";

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port <= 0 || port > 65535)
        {
            Fail("That port is not valid.");
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            Fail("Enter a host, usually 127.0.0.1.");
            return;
        }

        _target.Name = string.IsNullOrWhiteSpace(name) ? $"Local proxy {port}" : name;
        _target.Host = host;
        _target.Port = port;
        _target.Type = type;

        DialogResult = true;
        Close();
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }
}
