using System.Windows;
using System.Windows.Input;
using RouteScope.Core.Models;
using RouteScope.Core.Nodes;

namespace RouteScope.App.Dialogs;

public partial class NodeEditorDialog : Window
{
    private readonly ProxyNode _node;

    public NodeEditorDialog(ProxyNode node)
    {
        InitializeComponent();
        _node = node;

        ProtocolBox.ItemsSource = Enum.GetValues<NodeProtocol>().Select(p => p.ToString()).ToList();
        SecurityBox.ItemsSource = Enum.GetValues<SecurityKind>().Select(s => s.ToString()).ToList();
        TransportBox.ItemsSource = Enum.GetValues<TransportKind>().Select(t => t.ToString()).ToList();

        NameBox.Text = node.Name;
        ServerBox.Text = node.Server;
        PortBox.Text = node.Port.ToString();
        ProtocolBox.SelectedItem = node.Protocol.ToString();

        UuidBox.Text = node.Uuid ?? "";
        PasswordBox.Text = node.Password ?? "";
        UsernameBox.Text = node.Username ?? "";
        MethodBox.Text = node.Method ?? "";
        FlowBox.Text = node.Flow ?? "";

        SecurityBox.SelectedItem = node.Security.ToString();
        SniBox.Text = node.Sni ?? "";
        FpBox.Text = node.Fingerprint ?? "";
        AlpnBox.Text = string.Join(", ", node.Alpn);
        InsecureCheck.IsChecked = node.AllowInsecure;
        PbkBox.Text = node.PublicKey ?? "";
        SidBox.Text = node.ShortId ?? "";
        SpxBox.Text = node.SpiderX ?? "";

        TransportBox.SelectedItem = node.Transport.ToString();
        HostBox.Text = node.Host ?? "";
        PathBox.Text = node.Path ?? "";
        ServiceBox.Text = node.ServiceName ?? "";

        if (node.Extra.Count > 0)
        {
            ExtraPanel.Visibility = Visibility.Visible;
            ExtraText.Text = string.Join("   ", node.Extra.Select(kv => $"{kv.Key}={Shorten(kv.Value)}"));
        }

        SubtitleText.Text = $"{node.ProtocolLabel} · imported {node.CreatedUtc.ToLocalTime():dd MMM yyyy}";
        Loaded += (_, _) => NameBox.Focus();
    }

    private static string Shorten(string value) =>
        value.Length <= 40 ? value : value[..40] + "…";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port <= 0 || port > 65535)
        {
            Fail("That port is not valid.");
            return;
        }

        var server = ServerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(server))
        {
            Fail("Enter a server address.");
            return;
        }

        _node.Name = Blank(NameBox.Text) ? server : NameBox.Text.Trim();
        _node.Server = server;
        _node.Port = port;
        _node.Protocol = Enum.TryParse<NodeProtocol>(ProtocolBox.SelectedItem as string, out var p) ? p : _node.Protocol;

        _node.Uuid = Blank(UuidBox.Text) ? null : UuidBox.Text.Trim();
        _node.Password = Blank(PasswordBox.Text) ? null : PasswordBox.Text;
        _node.Username = Blank(UsernameBox.Text) ? null : UsernameBox.Text.Trim();
        _node.Method = Blank(MethodBox.Text) ? null : MethodBox.Text.Trim();
        _node.Flow = Blank(FlowBox.Text) ? null : FlowBox.Text.Trim();

        _node.Security = Enum.TryParse<SecurityKind>(SecurityBox.SelectedItem as string, out var s) ? s : SecurityKind.None;
        _node.Sni = Blank(SniBox.Text) ? null : SniBox.Text.Trim();
        _node.Fingerprint = Blank(FpBox.Text) ? null : FpBox.Text.Trim();
        _node.Alpn = AlpnBox.Text
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
        _node.AllowInsecure = InsecureCheck.IsChecked == true;
        _node.PublicKey = Blank(PbkBox.Text) ? null : PbkBox.Text.Trim();
        _node.ShortId = Blank(SidBox.Text) ? null : SidBox.Text.Trim();
        _node.SpiderX = Blank(SpxBox.Text) ? null : SpxBox.Text.Trim();

        _node.Transport = Enum.TryParse<TransportKind>(TransportBox.SelectedItem as string, out var t) ? t : TransportKind.Tcp;
        _node.Host = Blank(HostBox.Text) ? null : HostBox.Text.Trim();
        _node.Path = Blank(PathBox.Text) ? null : PathBox.Text.Trim();
        _node.ServiceName = Blank(ServiceBox.Text) ? null : ServiceBox.Text.Trim();

        // The link now describes the edited state, not the original import.
        _node.RawLink = NodeLinkWriter.ToLink(_node);

        DialogResult = true;
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var link = NodeLinkWriter.ToLink(_node);
            Clipboard.SetText(link);
            SubtitleText.Text = "Link copied to the clipboard";
        }
        catch (Exception ex)
        {
            Fail("Could not copy: " + ex.Message);
        }
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

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

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
