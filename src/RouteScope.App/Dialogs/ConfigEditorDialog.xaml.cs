using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using RouteScope.Core.Models;

namespace RouteScope.App.Dialogs;

public partial class ConfigEditorDialog : Window
{
    private readonly ExternalProxy _config;
    private readonly string _original;
    private readonly bool _isWireGuard;
    private readonly bool _isText;

    public ConfigEditorDialog(ExternalProxy config)
    {
        InitializeComponent();

        _config = config;
        _isWireGuard = config.IsWireGuard;
        _isText = config.IsWireGuard || config.IsOpenVpn;
        _original = _isText
            ? config.ImportedConfigText ?? ""
            : config.CustomConfigJson ?? "";

        NameBox.Text = config.Name;
        JsonBox.Text = _isText ? _original : Pretty(_original);
        HintText.Text = $"{_original.Length:N0} characters";
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var json = JsonBox.Text.Trim();

        if (json.Length == 0)
        {
            Fail("The config is empty.");
            return;
        }

        if (_config.IsOpenVpn)
        {
            var openVpn = Core.Nodes.OpenVpnConfig.Parse(json);
            if (!openVpn.IsUsable)
            {
                Fail("No remote server found in that .ovpn file.");
                return;
            }

            _config.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _config.Name : NameBox.Text.Trim();
            _config.ImportedConfigText = json;
            _config.Host = openVpn.RemoteHost;
            _config.Port = openVpn.RemotePort;
            DialogResult = true;
            Close();
            return;
        }

        if (_isWireGuard)
        {
            var parsed = Core.Nodes.WireGuardConfig.Parse(json);
            if (!parsed.IsUsable)
            {
                Fail("Missing a key, an address or an endpoint.");
                return;
            }

            _config.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _config.Name : NameBox.Text.Trim();
            _config.ImportedConfigText = json;
            _config.Host = parsed.EndpointHost;
            _config.Port = parsed.EndpointPort;
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    Fail("The config has to be a JSON object.");
                    return;
                }
            }
            catch (JsonException ex)
            {
                Fail("That is not valid JSON: " + ex.Message);
                return;
            }

            _config.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _config.Name : NameBox.Text.Trim();
            _config.CustomConfigJson = json;
        }

        DialogResult = true;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        JsonBox.Text = _isText ? _original : Pretty(_original);
        HintText.Text = "restored the imported version";
    }

    private void Fail(string message) => HintText.Text = message;

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
