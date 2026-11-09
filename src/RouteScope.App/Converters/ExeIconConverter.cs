using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;

namespace RouteScope.App.Converters;

/// <summary>Turns an executable path into its shell icon, or null when there is none.</summary>
public sealed class ExeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path)) return null;
            return IconHelper.GetIcon(path);
        }
        catch { return null; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Collapses the fallback glyph when a real executable icon is available.</summary>
public sealed class NoIconVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return System.Windows.Visibility.Visible;
        try
        {
            if (Directory.Exists(path)) return System.Windows.Visibility.Visible;
            return IconHelper.GetIcon(path) is null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        catch { return System.Windows.Visibility.Visible; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
