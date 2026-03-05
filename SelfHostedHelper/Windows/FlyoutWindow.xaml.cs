using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using static SelfHostedHelper.Classes.NativeMethods;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SelfHostedHelper.Windows;

/// <summary>
/// A flyout popup window that displays launcher items.
/// Shown from the taskbar widget icon or the tray icon.
/// Positioned above the taskbar and dismissed on focus loss or Escape.
/// </summary>
public partial class FlyoutWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static FlyoutWindow? _instance;
    private DateTime _lastDismissed = DateTime.MinValue;

    private FlyoutWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Toggle the flyout at the given screen-coordinate anchor point.
    /// If already visible, hides it. If recently dismissed (within 300 ms), ignores
    /// the request to prevent the toggle-on-deactivate race condition.
    /// </summary>
    public static void Toggle(Point screenAnchor)
    {
        if (_instance != null && _instance.IsVisible)
        {
            _instance.Hide();
            return;
        }

        // Prevent re-opening immediately after dismiss (toggle race with Deactivated)
        if (_instance != null && (DateTime.UtcNow - _instance._lastDismissed).TotalMilliseconds < 300)
            return;

        _instance ??= new FlyoutWindow();
        _instance.RebuildItems();
        _instance.ApplyTheme();

        // Show off-screen first so layout runs, then reposition
        _instance.Left = -9999;
        _instance.Top = -9999;
        _instance.Show();

        // Hide from Alt-Tab
        var hwnd = new System.Windows.Interop.WindowInteropHelper(_instance).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        _instance.PositionAt(screenAnchor);
        _instance.Activate();
    }

    /// <summary>
    /// Hide the flyout if it is currently visible.
    /// </summary>
    public static void DismissIfOpen()
    {
        if (_instance is { IsVisible: true })
            _instance.Hide();
    }

    // ── Content ─────────────────────────────────────────────────────

    private void RebuildItems()
    {
        ItemsPanel.Children.Clear();

        var items = SettingsManager.Current.LauncherItems;
        if (items == null || items.Count == 0)
        {
            var placeholder = new TextBlock
            {
                Text = "No launcher items configured",
                Foreground = new SolidColorBrush(Colors.Gray),
                Margin = new Thickness(12, 8, 12, 8),
                FontSize = 13
            };
            ItemsPanel.Children.Add(placeholder);
            return;
        }

        foreach (var item in items)
        {
            ItemsPanel.Children.Add(CreateItemRow(item));
        }
    }

    private Border CreateItemRow(LauncherItem item)
    {
        bool isDark = IsDarkTheme();

        UIElement icon;

        // Prefer favicon image if available, fall back to symbol glyph
        if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(item.IconPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 24;
            bitmap.EndInit();
            bitmap.Freeze();

            icon = new Image
            {
                Source = bitmap,
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
        }
        else
        {
            SymbolRegular symbol = SymbolRegular.Open24;
            if (!string.IsNullOrEmpty(item.IconGlyph) && Enum.TryParse<SymbolRegular>(item.IconGlyph, out var parsed))
                symbol = parsed;

            icon = new SymbolIcon
            {
                Symbol = symbol,
                FontSize = 18,
                Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
        }

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 0, 12, 0)
        };
        stack.Children.Add(icon);
        stack.Children.Add(name);

        var border = new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(1, 128, 128, 128)),
            Child = stack,
            Cursor = Cursors.Hand,
            Tag = item,
            Margin = new Thickness(2)
        };

        border.MouseEnter += ItemRow_MouseEnter;
        border.MouseLeave += ItemRow_MouseLeave;
        border.MouseLeftButtonUp += ItemRow_Click;

        return border;
    }

    // ── Theme ───────────────────────────────────────────────────────

    private void ApplyTheme()
    {
        bool isDark = IsDarkTheme();
        RootBorder.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(245, 32, 32, 32)
            : Color.FromArgb(245, 243, 243, 243));
    }

    private static bool IsDarkTheme()
    {
        var appTheme = ApplicationThemeManager.GetAppTheme();
        if (appTheme == ApplicationTheme.Dark) return true;
        if (appTheme == ApplicationTheme.Light) return false;
        return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark;
    }

    // ── Positioning ─────────────────────────────────────────────────

    private void PositionAt(Point screenAnchor)
    {
        var pt = new POINT { X = (int)screenAnchor.X, Y = (int)screenAnchor.Y };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        UpdateLayout();

        // Flyout size in physical pixels
        double flyoutWidth = ActualWidth * scale;
        double flyoutHeight = ActualHeight * scale;

        // Bottom of flyout at bottom of work area (just above the taskbar)
        double left = screenAnchor.X - flyoutWidth / 2.0;
        double top = workArea.Bottom - flyoutHeight;

        // Clamp to work area
        if (left < workArea.Left) left = workArea.Left;
        if (left + flyoutWidth > workArea.Right) left = workArea.Right - flyoutWidth;
        if (top < workArea.Top) top = workArea.Top;

        // Convert physical pixels to WPF device-independent units
        Left = left / scale;
        Top = top / scale;
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Hide();
        _lastDismissed = DateTime.UtcNow;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            _lastDismissed = DateTime.UtcNow;
        }
    }

    private void ItemRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not LauncherItem item) return;

        Hide();
        _lastDismissed = DateTime.UtcNow;

        try
        {
            if (item.IsWebsite || item.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                               || item.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(item.Path)
                {
                    UseShellExecute = true,
                    Arguments = item.Arguments ?? ""
                });
            }

            Logger.Info($"Launched from flyout: {item.Name} ({item.Path})");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to launch from flyout: {item.Name} ({item.Path})");
        }
    }

    private void ItemRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;

        bool isDark = IsDarkTheme();
        var targetColor = isDark
            ? Color.FromArgb(40, 255, 255, 255)
            : Color.FromArgb(60, 0, 0, 0);

        var animation = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (border.Background is not SolidColorBrush)
            border.Background = new SolidColorBrush(Color.FromArgb(1, 128, 128, 128));

        border.Background.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void ItemRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;

        var animation = new ColorAnimation
        {
            To = Color.FromArgb(1, 128, 128, 128),
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        border.Background?.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
}
