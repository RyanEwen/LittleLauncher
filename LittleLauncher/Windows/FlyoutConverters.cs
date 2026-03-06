using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LittleLauncher.Windows;

/// <summary>
/// Converts a LauncherItem's IconPath to a BitmapImage if the file exists, otherwise null.
/// </summary>
public sealed class IconPathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string path && !string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var bmp = new BitmapImage { DecodePixelWidth = 24 };
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            return bmp;
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visible when the string is non-empty and the file exists, Collapsed otherwise.
/// Pass parameter "invert" to invert the logic (visible when file does NOT exist).
/// </summary>
public sealed class IconPathToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool hasFile = value is string path && !string.IsNullOrEmpty(path) && File.Exists(path);
        bool invert = parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase);
        if (invert) hasFile = !hasFile;
        return hasFile ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
