using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Settings;
using System.Windows;

namespace SelfHostedHelper;

/// <summary>
/// Application entry point.
///
/// Architecture notes:
///   - Registers an unhandled exception logger so crashes are captured in NLog.
///   - No localization or toast notifications in this stripped-down version.
///   - The MainWindow is the hidden host; SettingsWindow is shown on demand.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Log unhandled exceptions before the process dies
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(
                args.ExceptionObject as Exception,
                "Unhandled exception occurred");
            NLog.LogManager.Flush();
        };

        // Catch WPF dispatcher (UI thread) exceptions
        DispatcherUnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(
                args.Exception,
                "Unhandled UI exception");
            NLog.LogManager.Flush();
            args.Handled = false; // let it crash so the user sees something
        };

        // Restore settings before any window constructor accesses SettingsManager.Current
        new SettingsManager().RestoreSettings();

        base.OnStartup(e);
    }
}
