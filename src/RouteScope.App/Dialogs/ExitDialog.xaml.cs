using System.Windows;
using System.Windows.Input;

namespace RouteScope.App.Dialogs;

public partial class ExitDialog : Window
{
    public string? Choice { get; private set; }
    public bool Remember { get; private set; }

    public ExitDialog() => InitializeComponent();

    private void Tray_Click(object sender, RoutedEventArgs e) => Close("tray");
    private void Exit_Click(object sender, RoutedEventArgs e) => Close("exit");
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(null);

    private void Close(string? choice)
    {
        Choice = choice;
        Remember = RememberCheck.IsChecked == true;
        DialogResult = choice is not null;
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }
}
