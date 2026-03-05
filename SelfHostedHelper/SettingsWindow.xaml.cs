using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper.Models;
using SelfHostedHelper.Pages;
using SelfHostedHelper.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace SelfHostedHelper;

/// <summary>
/// SettingsWindow — the main user-facing settings UI.
///
/// Architecture notes:
///   - Singleton pattern: only one instance can exist at a time.
///   - Uses WPF-UI's FluentWindow for Mica backdrop support.
///   - NavigationView drives page-based navigation (Home, Launcher Items, Sync, Settings, About).
///   - DataContext is bound to SettingsManager.Current (the global UserSettings instance).
///   - Settings are saved automatically when the window closes.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static SettingsWindow? instance;
    private ScrollViewer? _contentScrollViewer;

    public SettingsWindow()
    {
        if (instance != null)
        {
            instance.Activate();
            instance.Focus();
            Close();
            return;
        }

        InitializeComponent();
        instance = this;
        Closed += (s, e) => instance = null;
        DataContext = SettingsManager.Current;

        RootNavigation.SetCurrentValue(NavigationView.IsPaneOpenProperty, false);
    }

    /// <summary>
    /// Show the singleton settings window (create if needed, activate if exists).
    /// </summary>
    public static void ShowInstance()
    {
        if (instance == null)
        {
            new SettingsWindow().Show();
            instance?.Activate();
        }
        else
        {
            if (instance.WindowState == WindowState.Minimized)
                instance.WindowState = WindowState.Normal;

            instance.Activate();
            instance.Focus();
        }
    }

    /// <summary>
    /// Navigate to a specific page type (used from HomePage dashboard cards).
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        RootNavigation.Navigate(pageType);
    }

    /// <summary>
    /// Open the settings window, navigate to the Launcher Items page, and
    /// immediately open the edit dialog for the given item.
    /// </summary>
    public static void NavigateToEditItem(LauncherItem item)
    {
        LauncherItemsPage.PendingEditItem = item;
        instance?.NavigateTo(typeof(LauncherItemsPage));
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.IsPaneOpen = false;
        RootNavigation.Navigate(typeof(HomePage));

        // Workaround for WPF-UI NavigationView theme bug
        await Task.Delay(50);
        RootNavigation.IsPaneOpen = true;
        await Task.Delay(1);
        RootNavigation.IsPaneOpen = false;

        RootNavigation.Navigated += (s, args) => ResetScrollPosition();
    }

    private async void SettingsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SettingsManager.SaveSettings();

        if (SettingsManager.Current.SftpAutoSyncOnClose
            && !string.IsNullOrWhiteSpace(SettingsManager.Current.SftpHost))
        {
            try
            {
                await SftpSyncService.UploadSettingsAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Auto-sync on close failed");
            }
        }
    }

    private void ResetScrollPosition()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _contentScrollViewer ??= FindScrollableScrollViewer(RootNavigation);
                _contentScrollViewer?.ScrollToVerticalOffset(0);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error resetting scroll position");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScrollableScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv && sv.ScrollableHeight > 0) return sv;
            var result = FindScrollableScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
}
