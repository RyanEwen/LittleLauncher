using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Windows;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Appearance;
using static SelfHostedHelper.Classes.NativeMethods;

namespace SelfHostedHelper.Controls;

/// <summary>
/// A single globe icon that lives inside the Windows taskbar.
/// Clicking it toggles the launcher flyout.
/// </summary>
public partial class TaskbarLauncherControl : UserControl
{
    public TaskbarLauncherControl()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }

    /// <summary>
    /// No-op — retained for API compatibility with TaskbarWindow.RefreshLauncher.
    /// </summary>
    public void RebuildButtons() { }

    /// <summary>
    /// Returns the fixed size for the single-icon widget.
    /// </summary>
    public (double logicalWidth, double logicalHeight) CalculateSize(double dpiScale)
    {
        return (40, 40);
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void IconButton_Click(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out POINT pt);
        FlyoutWindow.Toggle(new Point(pt.X, pt.Y));
    }

    private void IconButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.ContextMenu != null)
        {
            border.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void ContextMenu_Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow.ShowInstance();
    }

    private void ContextMenu_Exit_Click(object sender, RoutedEventArgs e)
    {
        Classes.Settings.SettingsManager.SaveSettings();
        Application.Current.Shutdown();
    }

    private void IconButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;

        var targetColor = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
            ? Color.FromArgb(50, 255, 255, 255)
            : Color.FromArgb(100, 255, 255, 255);

        var animation = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (border.Background is not SolidColorBrush)
            border.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        border.Background.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void IconButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;

        var animation = new ColorAnimation
        {
            To = Color.FromArgb(1, 0, 0, 0),
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        border.Background?.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
}
