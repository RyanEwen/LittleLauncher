using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using Microsoft.UI.Xaml;

namespace SelfHostedHelper;

/// <summary>
/// Application entry point (WinUI 3).
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Log unhandled exceptions before the process dies
        AppDomain.CurrentDomain.UnhandledException += (sender, a) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(
                a.ExceptionObject as Exception,
                "Unhandled exception occurred");
            NLog.LogManager.Flush();
        };

        UnhandledException += (sender, e) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(
                e.Exception,
                "Unhandled UI exception");
            NLog.LogManager.Flush();
            e.Handled = true;
        };

        // Restore settings before any window constructor accesses SettingsManager.Current
        SettingsManager.RestoreSettings();

        m_window = new MainWindow();
        m_window.Activate();
    }

    internal Window? m_window;
}
