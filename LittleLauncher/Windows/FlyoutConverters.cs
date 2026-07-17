using System.IO;
using LittleLauncher.Classes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
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
            int decodePixelWidth = 24;
            if (parameter is string parameterText && int.TryParse(parameterText, out int parsedWidth) && parsedWidth > 0)
                decodePixelWidth = parsedWidth;

            var bmp = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = decodePixelWidth,
                // The stale-icon refresh rewrites cached files in place (same path),
                // so the per-URI decoded-image cache would keep showing the old bitmap.
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            };
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

/// <summary>
/// Returns Visible when the glyph is a Segoe Fluent Icons character (PUA range), Collapsed for emojis.
/// Pass parameter "invert" to invert the logic (visible for emojis, collapsed for Fluent glyphs).
/// </summary>
public sealed class IsFluentGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isFluent = IconGallery.IsFluentGlyph(value as string);
        bool invert = parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase);
        if (invert) isFluent = !isFluent;
        return isFluent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a hex color string (e.g. "#FF0000") to a SolidColorBrush.
/// Returns null (inherits theme default) when the string is null or empty.
/// </summary>
public sealed class IconColorToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6)
                {
                    byte r = System.Convert.ToByte(hex[..2], 16);
                    byte g = System.Convert.ToByte(hex[2..4], 16);
                    byte b = System.Convert.ToByte(hex[4..6], 16);
                    return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
                }
                if (hex.Length == 8)
                {
                    byte a = System.Convert.ToByte(hex[..2], 16);
                    byte r = System.Convert.ToByte(hex[2..4], 16);
                    byte g = System.Convert.ToByte(hex[4..6], 16);
                    byte b = System.Convert.ToByte(hex[6..8], 16);
                    return new SolidColorBrush(global::Windows.UI.Color.FromArgb(a, r, g, b));
                }
            }
            catch { /* fall through to null */ }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
