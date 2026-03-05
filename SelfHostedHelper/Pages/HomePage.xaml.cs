using SelfHostedHelper.Classes.Settings;
using System.Windows;
using System.Windows.Controls;

namespace SelfHostedHelper.Pages;

public partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        VersionTextBlock.Text = SettingsManager.Current.LastKnownVersion;
    }

    private void LauncherItems_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to the Launcher Items page
        if (Window.GetWindow(this) is SettingsWindow sw)
        {
            sw.NavigateTo(typeof(LauncherItemsPage));
        }
    }

    private void SyncSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is SettingsWindow sw)
        {
            sw.NavigateTo(typeof(SyncPage));
        }
    }
}
