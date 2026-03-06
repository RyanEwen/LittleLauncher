using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LittleLauncher;

/// <summary>
/// Application entry point (WinUI 3).
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main UI thread's DispatcherQueue, captured at launch.
    /// Use this to marshal calls back to the UI thread from background threads.
    /// </summary>
    public static DispatcherQueue MainDispatcherQueue { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainDispatcherQueue = DispatcherQueue.GetForCurrentThread();
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
