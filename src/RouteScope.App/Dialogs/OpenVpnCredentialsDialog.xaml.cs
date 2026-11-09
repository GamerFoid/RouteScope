using System.Windows;
using System.Windows.Input;

namespace RouteScope.App.Dialogs;

public partial class OpenVpnCredentialsDialog : Window
{
    public string Username { get; private set; } = "";
    public string Password { get; private set; } = "";

    public OpenVpnCredentialsDialog(string? name = null, string? username = null, string? password = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(name)) Subtitle.Text = $"{name} needs a username and password";
        UserBox.Text = username ?? "";
        PassBox.Password = password ?? "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text) || PassBox.Password.Length == 0)
        {
            Hint.Text = "Both fields are required.";
            return;
        }

        Username = UserBox.Text.Trim();
        Password = PassBox.Password;
        DialogResult = true;
        Close();
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
