using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RouteScope.Core.SystemTools;

namespace RouteScope.App.Dialogs;

public sealed class PickerItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public ImageSource? Icon { get; init; }
    public int Count { get; init; }
    public string CountLabel => Count > 1 ? $"{Count} instances" : "";
}

public partial class ProcessPickerWindow : Window
{
    private readonly ObservableCollection<PickerItem> _all = new();
    private readonly ObservableCollection<PickerItem> _visible = new();

    public string? SelectedName { get; private set; }
    public string? SelectedPath { get; private set; }

    public ProcessPickerWindow()
    {
        InitializeComponent();
        ProcessList.ItemsSource = _visible;
        Loaded += (_, _) =>
        {
            LoadProcesses();
            SearchBox.Focus();
        };
    }

    private void LoadProcesses()
    {
        _all.Clear();
        try
        {
            var snapshot = ProcessScanner.Snapshot();
            var groups = snapshot
                .Where(p => !p.IsSystem && !string.IsNullOrWhiteSpace(p.ExePath))
                .GroupBy(p => p.ExePath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PickerItem
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(g.Key),
                    Path = g.Key,
                    Icon = IconHelper.GetIcon(g.Key),
                    Count = g.Count(),
                })
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var item in groups) _all.Add(item);
        }
        catch { }

        ApplyFilter("");
    }

    private void ApplyFilter(string term)
    {
        _visible.Clear();
        var matches = string.IsNullOrWhiteSpace(term)
            ? _all
            : _all.Where(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                              i.Path.Contains(term, StringComparison.OrdinalIgnoreCase));

        foreach (var m in matches.Take(300)) _visible.Add(m);
        if (_visible.Count > 0) ProcessList.SelectedIndex = 0;
    }

    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void ProcessList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Add_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ProcessList.SelectedItem is not PickerItem item) return;
        SelectedName = System.IO.Path.GetFileName(item.Path);
        SelectedPath = item.Path;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
