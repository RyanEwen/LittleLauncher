using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LittleLauncher.Windows;

/// <summary>
/// What a page is allowed to do — camera, microphone, location, notifications — and what happens
/// to a notification it raises.
/// </summary>
/// <remarks>
/// <para>Two things a browser provides that a hosted WebView2 does not: a place to ask, and a
/// place for a notification to go. WebView2 raises <c>PermissionRequested</c> and
/// <c>NotificationReceived</c> and leaves both to the host — an unhandled request falls back to
/// the browser's own prompt, which is drawn for a full browser window and is a poor fit for a
/// 400px tray flyout, and an unhandled notification is simply dropped.</para>
/// <para>So the flyout asks in a bar of its own and hands notifications to Windows as toasts. The
/// answers are stored in the launcher's WebView2 profile (<c>SavesInProfile</c>), which means they
/// are per launcher, exactly like its cookies — two launchers on the same site answer separately,
/// and launchers on the shared profile answer once between them.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>A question shown in the prompt bar, with the two answers it accepts.</summary>
    private sealed record PromptRequest(
        string Text,
        string AcceptLabel,
        string RejectLabel,
        Action<bool> OnAnswered);

    private Grid? _promptBar;
    private TextBlock? _promptText;
    private Button? _promptAccept;
    private Button? _promptReject;

    /// <summary>
    /// Questions waiting to be answered. A page asking for the camera and the microphone raises
    /// two separate requests, and a bar can only ask one thing at a time.
    /// </summary>
    private readonly Queue<PromptRequest> _prompts = new();

    /// <summary>Permission requests whose deferrals are still outstanding, in prompt order.</summary>
    private readonly Queue<(CoreWebView2PermissionRequestedEventArgs Args, global::Windows.Foundation.Deferral Deferral)> _permissionDeferrals = new();

    /// <summary>
    /// Launchers already offered the keep-running switch this session, so declining it once is not
    /// re-asked on every notification the page raises.
    /// </summary>
    private static readonly HashSet<string> _keepRunningOffered = [];

    /// <summary>
    /// Launchers whose stored site permissions are to be reset the next time a browser is created
    /// for them. <see cref="CoreWebView2Profile"/> is only reachable through a live browser, so a
    /// reset asked for while the launcher is unloaded has to wait for one.
    /// </summary>
    private static readonly HashSet<string> _pendingPermissionResets = [];

    /// <summary>Notifications currently on screen, so a toast click can be reported back to the page.</summary>
    /// <remarks>
    /// Entries leave on a click or when the page withdraws the notification, but a toast the user
    /// simply ignores reports nothing at all — so this is capped rather than trusted to drain. The
    /// cap is what a click can still reach; older toasts just open the launcher without telling the
    /// page they were clicked.
    /// </remarks>
    private static readonly Dictionary<string, CoreWebView2Notification> _liveNotifications = [];

    private const int MaxTrackedNotifications = 64;

    private static int _notificationSequence;

    /// <summary>True while the flyout is asking something. Pins it open, like an owned window.</summary>
    private bool IsPromptOpen => _promptBar?.Visibility == Visibility.Visible;

    // ── The prompt bar ──────────────────────────────────────────────

    /// <summary>
    /// Builds the bar the flyout asks in: a strip across the top of the content area.
    /// </summary>
    /// <remarks>
    /// Row 0 of the content host, above the browser rather than over it — see the row definitions
    /// in the constructor. Collapsed, an <c>Auto</c> row costs nothing, so the page is full height
    /// until something is actually asked.
    /// </remarks>
    private void BuildPromptBar()
    {
        _promptText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        _promptAccept = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        _promptReject = new Button();
        _promptAccept.Click += (_, _) => AnswerPrompt(true);
        _promptReject.Click += (_, _) => AnswerPrompt(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(_promptAccept);
        buttons.Children.Add(_promptReject);

        _promptBar = new Grid
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(12, 8, 12, 8),
            // Opaque over the page, for the same reason as the bookmark bar: a question floating
            // on acrylic over a website has no readable extent.
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        _promptBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _promptBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_promptText, 0);
        Grid.SetColumn(buttons, 1);
        _promptBar.Children.Add(_promptText);
        _promptBar.Children.Add(buttons);

        Grid.SetRow(_promptBar, 0);
        _contentHost.Children.Add(_promptBar);
    }

    private void EnqueuePrompt(PromptRequest request)
    {
        _prompts.Enqueue(request);
        ShowNextPrompt();
    }

    private void ShowNextPrompt()
    {
        if (_promptBar == null || IsPromptOpen) return;
        if (_prompts.Count == 0) return;

        var next = _prompts.Peek();
        _promptText!.Text = next.Text;
        _promptAccept!.Content = next.AcceptLabel;
        _promptReject!.Content = next.RejectLabel;
        _promptBar.Visibility = Visibility.Visible;
    }

    private void AnswerPrompt(bool accepted)
    {
        if (_prompts.Count == 0) return;

        var answered = _prompts.Dequeue();
        _promptBar!.Visibility = Visibility.Collapsed;
        answered.OnAnswered(accepted);
        ShowNextPrompt();
    }

    // ── Permissions ─────────────────────────────────────────────────

    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Handled either way: leaving it unhandled hands the decision back to WebView2's own
        // prompt, which is drawn for a browser window rather than a tray flyout.
        e.Handled = true;

        if (_launcher.WebAllowAllPermissions)
        {
            e.State = CoreWebView2PermissionState.Allow;
            e.SavesInProfile = false;   // the toggle is the decision — see Launcher.WebAllowAllPermissions
            OnPermissionGranted(e.PermissionKind);
            return;
        }

        // The deferral is what keeps the request open while the bar is on screen; without it the
        // handler returning *is* the answer.
        var deferral = e.GetDeferral();
        _permissionDeferrals.Enqueue((e, deferral));

        EnqueuePrompt(new PromptRequest(
            $"{DescribeOrigin(e.Uri)} wants to {DescribePermission(e.PermissionKind)}.",
            "Allow",
            "Block",
            allowed => ResolvePermission(allowed)));
    }

    private void ResolvePermission(bool allowed)
    {
        if (_permissionDeferrals.Count == 0) return;

        var (args, deferral) = _permissionDeferrals.Dequeue();
        try
        {
            args.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;

            // Remembered per launcher profile, so the same page is not asked again on every visit.
            // "Reset site permissions" in launcher settings is what undoes it.
            args.SavesInProfile = true;
        }
        finally
        {
            deferral.Complete();
        }

        if (allowed) OnPermissionGranted(args.PermissionKind);
    }

    /// <summary>
    /// Completes every outstanding request as a refusal.
    /// </summary>
    /// <remarks>
    /// A deferral that is never completed leaves the page waiting forever, so the browser going
    /// away — unloaded when idle, or rebuilt for a profile change — has to answer them. Refusal is
    /// the only honest answer to a question whose window has just gone; nothing is saved into the
    /// profile, so the page may ask again next time.
    /// </remarks>
    private void CancelPendingPermissions()
    {
        while (_permissionDeferrals.Count > 0)
        {
            var (args, deferral) = _permissionDeferrals.Dequeue();
            try
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Cancelling a permission request failed for launcher {Name}", _launcher.Name);
            }
            finally
            {
                deferral.Complete();
            }
        }

        _prompts.Clear();
        if (_promptBar != null) _promptBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Follows up a granted permission that needs more than a grant to actually work.
    /// </summary>
    /// <remarks>
    /// Notifications are the case: a dismissed flyout suspends its browser and then tears it down,
    /// and a page that is not running raises nothing. Rather than change the launcher's policy
    /// silently — it is the setting that decides whether a hidden launcher costs anything at all —
    /// the flyout says so and offers the switch. Declined once, it is not asked again this session.
    /// </remarks>
    private void OnPermissionGranted(CoreWebView2PermissionKind kind)
    {
        if (kind != CoreWebView2PermissionKind.Notifications) return;
        if (WebHiddenPolicies.Normalize(_launcher.WebHiddenPolicy) == WebHiddenPolicies.KeepRunning) return;
        if (!_keepRunningOffered.Add(_launcher.Id)) return;

        EnqueuePrompt(new PromptRequest(
            $"Notifications only arrive while this page is running, and {_launcher.Name} currently stops when its flyout is dismissed.",
            "Keep running",
            "Not now",
            accepted =>
            {
                if (!accepted) return;

                _launcher.WebHiddenPolicy = WebHiddenPolicies.KeepRunning;
                SettingsManager.SaveSettings();
                Services.AutoSyncService.NotifyLaunchersChanged();
            }));
    }

    private static string DescribeOrigin(string uri)
    {
        try
        {
            return new Uri(uri).Host;
        }
        catch
        {
            return "This page";
        }
    }

    private static string DescribePermission(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "use your microphone",
        CoreWebView2PermissionKind.Camera => "use your camera",
        CoreWebView2PermissionKind.Geolocation => "know your location",
        CoreWebView2PermissionKind.Notifications => "send you notifications",
        CoreWebView2PermissionKind.OtherSensors => "use your device's sensors",
        CoreWebView2PermissionKind.ClipboardRead => "read your clipboard",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "download several files at once",
        CoreWebView2PermissionKind.FileReadWrite => "read and change files you choose",
        CoreWebView2PermissionKind.Autoplay => "play audio and video automatically",
        CoreWebView2PermissionKind.LocalFonts => "see the fonts installed on this PC",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "control your MIDI devices",
        CoreWebView2PermissionKind.WindowManagement => "see your displays",
        _ => "use a device feature",
    };

    // ── Stored answers ──────────────────────────────────────────────

    /// <summary>
    /// Forgets every non-default permission answer stored for a launcher, so its pages ask again.
    /// </summary>
    /// <returns>
    /// True when it has been done. False means the launcher has no browser to do it through and it
    /// has been queued for the next one — the caller says so rather than reporting success.
    /// </returns>
    internal static async Task<bool> ResetSitePermissionsAsync(string launcherId)
    {
        var core = Instances.TryGetValue(launcherId, out var flyout) ? flyout._webView?.CoreWebView2 : null;
        if (core == null)
        {
            _pendingPermissionResets.Add(launcherId);
            return false;
        }

        await ClearPermissionsAsync(core);
        return true;
    }

    private static async Task ClearPermissionsAsync(CoreWebView2 core)
    {
        try
        {
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            foreach (var setting in settings)
            {
                await core.Profile.SetPermissionStateAsync(
                    setting.PermissionKind, setting.PermissionOrigin, CoreWebView2PermissionState.Default);
            }
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Resetting site permissions failed");
        }
    }

    /// <summary>Applies a reset that was asked for while this launcher had no browser.</summary>
    private void ApplyPendingPermissionReset(CoreWebView2 core)
    {
        if (!_pendingPermissionResets.Remove(_launcher.Id)) return;
        _ = ClearPermissionsAsync(core);
    }

    // ── Notifications ───────────────────────────────────────────────

    /// <summary>
    /// Turns a page's notification into a Windows toast.
    /// </summary>
    /// <remarks>
    /// <para>WebView2 does not display these itself — an unhandled <c>NotificationReceived</c> is a
    /// notification the user never sees. Marking it handled and showing a toast is what makes
    /// <c>new Notification(...)</c> in a dashboard behave the way it does in a browser.</para>
    /// <para>The page's own callbacks are driven from the toast: <c>ReportShown</c> as soon as
    /// Windows accepts it, <c>ReportClicked</c> when the toast is activated. Without those the
    /// page believes its notification never appeared.</para>
    /// </remarks>
    private void OnNotificationReceived(CoreWebView2 sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        var notification = e.Notification;
        e.Handled = true;

        string key = $"{_launcher.Id}:{++_notificationSequence}";

        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddArgument("launcher", _launcher.Id)
                .AddArgument("webNotification", key)
                .AddText(string.IsNullOrWhiteSpace(notification.Title) ? _launcher.Name : notification.Title);

            if (!string.IsNullOrWhiteSpace(notification.Body))
                builder.AddText(notification.Body);

            // The launcher's own icon, not the notification's: notification.IconUri is a page URL
            // that Windows would have to fetch itself, and for a self-hosted dashboard behind a
            // login it would fetch a redirect. The adopted page icon is already on disk.
            string iconPath = GetPageIconPath(_launcher.Id);
            if (System.IO.File.Exists(iconPath))
                builder.SetAppLogoOverride(new Uri(iconPath));

            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());

            while (_liveNotifications.Count >= MaxTrackedNotifications)
                _liveNotifications.Remove(_liveNotifications.Keys.First());

            _liveNotifications[key] = notification;
            notification.CloseRequested += (_, _) => _liveNotifications.Remove(key);
            notification.ReportShown();
        }
        catch (Exception ex)
        {
            _liveNotifications.Remove(key);
            Logger.Warn(ex, "Showing a page notification failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>
    /// Opens the launcher a clicked toast came from, and tells its page the toast was clicked.
    /// </summary>
    /// <returns>False when the toast was not one of ours, so the caller can handle it itself.</returns>
    internal static bool HandleNotificationActivation(IDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("launcher", out string? launcherId) || string.IsNullOrEmpty(launcherId))
            return false;

        if (arguments.TryGetValue("webNotification", out string? key) &&
            key != null &&
            _liveNotifications.Remove(key, out var notification))
        {
            try
            {
                notification.ReportClicked();
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Reporting a notification click failed");
            }
        }

        var owner = MainWindow.Current;
        if (owner == null) return false;
        if (SettingsManager.Current.Launchers.All(l => l.Id != launcherId)) return false;

        // Already on screen: the click has nothing left to do, and Toggle would dismiss it.
        if (Instances.TryGetValue(launcherId, out var panel) && panel._isOpen) return true;

        owner.OpenLauncherPanel(launcherId);
        return true;
    }
}
