using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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

    /// <summary>How close to a toast a page's own sound has to start to count as that toast's.</summary>
    /// <remarks>
    /// Generous on purpose. The two are triggered by the same arriving message but travel different
    /// routes: the sound is played synchronously, while the notification goes through the bridge's
    /// icon fetch — a network round-trip — before the host hears about it at all. In practice the
    /// page is already making its noise by the time the toast is built, which is what lets this
    /// work without holding the toast back to wait and see.
    /// </remarks>
    private const long PageSoundWindowMs = 2500;

    /// <summary>How long "this launcher announces itself" is believed once it has been observed.</summary>
    /// <remarks>
    /// The sound can also arrive just <em>after</em> the toast, and nothing can un-ring a toast that
    /// has already played. So the first one may double up, and every one after it defers to the
    /// page. It lapses rather than latching, so a launcher whose sounds the user later turns off in
    /// the site's own settings gets the Windows one back instead of going quiet for good.
    /// </remarks>
    private const long PageSoundMemoryMs = 60 * 60 * 1000;

    /// <summary>When a page of this launcher last started making a sound. 0 if never.</summary>
    private long _pageAudioStartedAt;

    /// <summary>When a page sound last landed close enough to a toast to be that toast's. 0 if never.</summary>
    private long _pageSoundedForToastAt;

    /// <summary>When this launcher last raised a toast, so a sound just after it can be attributed.</summary>
    private long _lastToastAt;

    /// <summary>Notifications currently on screen, so a toast click can be reported back to the page.</summary>
    /// <remarks>
    /// Entries leave on a click or when the page withdraws the notification, but a toast the user
    /// simply ignores reports nothing at all — so this is capped rather than trusted to drain. The
    /// cap is what a click can still reach; older toasts just open the launcher without telling the
    /// page they were clicked.
    /// </remarks>
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

    /// <summary>
    /// Lets go of a WebView2 object here, on the thread that received it, instead of leaving it to
    /// the garbage collector.
    /// </summary>
    /// <remarks>
    /// <para>Releasing a WebView2 object on the finalizer thread runs its native destructor there,
    /// and objects owning a mojo <c>Remote</c> <c>CHECK</c> that this happens on the sequence they
    /// were bound to. A failed <c>CHECK</c> is a breakpoint, so the process dies and no
    /// <c>try</c>/<c>catch</c> sees it.</para>
    /// <para><b>This is a mitigation, not a guarantee</b>, and the notification path proved it: a
    /// CsWinRT wrapper keeps an <c>IObjectReference</c> per queried interface, so disposing the
    /// primary one still leaves others for the finalizer. Where an object type is known to be
    /// dangerous, the answer is not to be handed it at all — see
    /// <see cref="NotificationBridgeScript"/>.</para>
    /// </remarks>
    private static void ReleaseWebViewObject(object? projected)
    {
        if (projected == null) return;

        try
        {
            if (projected is WinRT.IWinRTObject winrt) { winrt.NativeObject.Dispose(); return; }
            if (projected is IDisposable disposable) disposable.Dispose();
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Releasing a WebView2 object failed");
        }
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

            // Saved, like any other answer. Granting without recording looks equivalent — the page
            // asked, the page was allowed — but it is not: navigator.permissions.query() and
            // Notification.permission read the stored setting and never see the grant, so an app
            // that checks before asking believes it has nothing. Teams then shows "we can't access
            // your microphone" over a working microphone, and offers to turn on notifications that
            // are already on, every time the browser restarts. Turning the toggle off clears these
            // again (ClearOnTrustDisabledAsync), which is what keeps "the toggle is the decision"
            // true without lying to the page.
            e.SavesInProfile = true;
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

        // Answered and completed, so the args are ours to let go of — on this thread.
        ReleaseWebViewObject(args);
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

    /// <summary>
    /// Clears the grants "Trust This Site" made, for a launcher that has just stopped trusting it.
    /// </summary>
    /// <remarks>
    /// The toggle is still the decision — this is what makes that true now that the grants are
    /// written into the profile. Without it, switching Trust off would leave a profile full of
    /// silent allows behind and the launcher would never ask about anything again.
    /// </remarks>
    internal static Task<bool> ClearOnTrustDisabledAsync(string launcherId) =>
        ResetSitePermissionsAsync(launcherId);

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

    /// <summary>
    /// Writes the grants "Trust This Site" implies into the profile before the page asks for them.
    /// </summary>
    /// <remarks>
    /// <para>Saving a grant when the page asks (see <see cref="OnPermissionRequested"/>) is not
    /// enough on its own, because a well-built app <b>checks before it asks</b>. Teams reads
    /// <c>Notification.permission</c> on load, finds <c>default</c>, and shows "Stay in the know.
    /// Turn on desktop notifications." — then renders its own in-page banners instead of real ones,
    /// because as far as it can tell the desktop cannot show them. Nothing is ever requested, so
    /// nothing is ever saved, and the prompt returns on every load forever.</para>
    /// <para>Seeding breaks that loop: the answers the toggle already implies are written for the
    /// launcher's own origins, so the very first read reports <c>granted</c> and the app goes
    /// straight to real notifications. Only the four the toggle names, and only for this launcher's
    /// own addresses — a trusted launcher is a statement about *its* site, not about every site its
    /// pages happen to link to.</para>
    /// </remarks>
    private async Task SeedTrustedPermissionsAsync(CoreWebView2 core)
    {
        if (!_launcher.WebAllowAllPermissions) return;

        CoreWebView2PermissionKind[] kinds =
        [
            CoreWebView2PermissionKind.Notifications,
            CoreWebView2PermissionKind.Microphone,
            CoreWebView2PermissionKind.Camera,
            CoreWebView2PermissionKind.Geolocation,
        ];

        foreach (string origin in TrustedOrigins())
        {
            foreach (var kind in kinds)
            {
                try
                {
                    await core.Profile.SetPermissionStateAsync(kind, origin, CoreWebView2PermissionState.Allow);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Seeding {Kind} for {Origin} failed", kind, origin);
                }
            }
        }
    }

    /// <summary>The origins this launcher is actually pointed at — its address, or its bookmarks.</summary>
    private IEnumerable<string> TrustedOrigins()
    {
        var urls = _launcher.WebBookmarks.Select(b => b.Url);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri)) continue;

            // The form WebView2 stores and reports: scheme, host, port, trailing slash.
            string origin = uri.GetLeftPart(UriPartial.Authority) + "/";
            if (seen.Add(origin)) yield return origin;
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
    /// Script that routes <c>registration.showNotification()</c> through <c>new Notification()</c>,
    /// which is the only notification API WebView2 tells the host about.
    /// </summary>
    /// <remarks>
    /// <para>See <see cref="InstallNotificationBridgeAsync"/> for why this is needed at all. The
    /// shim keeps the two behaviours a page can actually observe: replacing a notification that
    /// shares a <c>tag</c>, and <c>getNotifications()</c> returning what is still on screen — chat
    /// apps use both to collapse an updated conversation into one entry and to clear it once the
    /// thread is read.</para>
    /// <para>It never throws away the real call: anything unexpected falls back to the original
    /// method, so a page on a future WebView2 that does support this is left exactly as it was.</para>
    /// </remarks>
    /// <summary>
    /// Replaces the page's <c>Notification</c> with one that reports to the host instead of asking
    /// the browser for a real one.
    /// </summary>
    /// <remarks>
    /// <para><b>This exists to keep WebView2 notification objects out of the process entirely.</b>
    /// Handling <c>NotificationReceived</c> means being handed a
    /// <c>CoreWebView2NotificationReceivedEventArgs</c> and its <c>CoreWebView2Notification</c>,
    /// and those cannot be let go of safely: their native destructors tear down a mojo
    /// <c>Remote</c> bound to the browser's sequence, mojo <c>CHECK</c>s that it happens on that
    /// sequence, and a failed <c>CHECK</c> is a breakpoint that takes the process down. Releasing
    /// them by hand does not solve it — CsWinRT keeps an <c>IObjectReference</c> per queried
    /// interface and disposing the primary one still leaves others for the finalizer. Three
    /// separate attempts at disposing our way out of it were caught under a debugger doing exactly
    /// that, on the .NET Finalizer thread.</para>
    /// <para>So the page never creates one. Everything the toast needs travels as a plain web
    /// message, which is a string, and the events the page expects are raised back from the host.
    /// The permission API is left completely alone — it is delegated to the real implementation,
    /// because that is what pages check before they decide they are allowed to notify.</para>
    /// <para><b>The icon is fetched here, in the page</b>, and sent as a data URL. That is the whole
    /// reason it can be the right icon: a message notification's icon is the sender's avatar, behind
    /// the same login as the page, so the host fetching it would get a redirect while the page gets
    /// the image.</para>
    /// </remarks>
    private const string NotificationBridgeScript = """
        (function () {
            if (!window.Notification || window.Notification.__littleLauncher) return;
            if (!window.chrome || !window.chrome.webview) return;

            var Native = window.Notification;
            var live = new Map();
            var seq = 0;

            function post(m) { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch (e) { } }

            // Fetched in page context so cookies apply; a host-side fetch of an avatar behind a
            // login gets a redirect. Capped, because this rides a web message.
            function iconDataUrl(url) {
                if (!url) return Promise.resolve('');

                // Already inline. Chat apps commonly hand the avatar over as a data URL, and
                // fetching one of those with credentials rejects outright — which is exactly how
                // this shipped broken the first time.
                if (url.lastIndexOf('data:', 0) === 0)
                    return Promise.resolve(url.length > 400 * 1024 ? '' : url);

                // Credentials for the page's own origin, since an avatar usually sits behind the
                // same login; without them for anything else, because a CDN that does not allow
                // credentialed CORS rejects the request outright.
                var sameOrigin = url.lastIndexOf(location.origin, 0) === 0 || url.lastIndexOf('/', 0) === 0;
                return fetch(url, { credentials: sameOrigin ? 'include' : 'omit' })
                    .then(function (r) { return r.ok ? r.blob() : null; })
                    .then(function (b) {
                        if (!b || b.size > 400 * 1024) return '';
                        return new Promise(function (res) {
                            var fr = new FileReader();
                            fr.onload = function () { res(fr.result); };
                            fr.onerror = function () { res(''); };
                            fr.readAsDataURL(b);
                        });
                    })
                    .catch(function () { return ''; });
            }

            function LLNotification(title, options) {
                var o = options || {};
                var self = this;

                this.title = String(title == null ? '' : title);
                this.body = o.body || '';
                this.tag = o.tag || ('__ll-' + (++seq) + '-' + Date.now().toString(36));
                this.data = o.data;
                this.icon = o.icon || '';
                this.dir = o.dir || 'auto';
                this.lang = o.lang || '';
                this.silent = !!o.silent;
                this.onclick = null; this.onshow = null; this.onclose = null; this.onerror = null;

                // Same tag replaces, as the platform would.
                var previous = live.get(this.tag);
                if (previous) { try { previous.close(); } catch (e) { } }
                live.set(this.tag, this);

                iconDataUrl(this.icon).then(function (icon) {
                    post({
                        __ll: 'notify', tag: self.tag, title: self.title, body: self.body,
                        icon: icon, iconUrl: self.icon, silent: self.silent, actions: o.actions || []
                    });
                });
            }

            LLNotification.prototype.close = function () {
                if (!live.has(this.tag)) return;
                live.delete(this.tag);
                post({ __ll: 'notifyClose', tag: this.tag });
                if (typeof this.onclose === 'function') this.onclose.call(this, new Event('close'));
            };
            LLNotification.prototype.addEventListener = function (type, fn) {
                this['on' + type] = fn;
            };
            LLNotification.prototype.removeEventListener = function (type) {
                this['on' + type] = null;
            };

            // Permission is the real thing's business, not ours.
            LLNotification.requestPermission = function () { return Native.requestPermission.apply(Native, arguments); };
            Object.defineProperty(LLNotification, 'permission', { get: function () { return Native.permission; } });
            Object.defineProperty(LLNotification, 'maxActions', { get: function () { return Native.maxActions || 2; } });
            LLNotification.__littleLauncher = true;
            LLNotification.__native = Native;

            window.Notification = LLNotification;

            window.chrome.webview.addEventListener('message', function (ev) {
                var d = ev.data;
                try { if (typeof d === 'string') d = JSON.parse(d); } catch (e) { return; }
                if (!d || !d.__ll || !d.tag) return;

                var n = live.get(d.tag);
                if (!n) return;

                if (d.__ll === 'notifyShown' && typeof n.onshow === 'function') {
                    n.onshow.call(n, new Event('show'));
                } else if (d.__ll === 'notifyClicked') {
                    if (typeof n.onclick === 'function') n.onclick.call(n, new Event('click'));
                } else if (d.__ll === 'notifyClosed') {
                    live.delete(d.tag);
                    if (typeof n.onclose === 'function') n.onclose.call(n, new Event('close'));
                }
            });
        })();
        """;

    /// <summary>
    /// Installs the shim that makes a page's notifications reach the host at all.
    /// </summary>
    /// <remarks>
    /// <para><b>WebView2 raises <c>NotificationReceived</c> for non-persistent notifications only</b>
    /// — the ones built with <c>new Notification(...)</c>. A notification raised through
    /// <c>ServiceWorkerRegistration.showNotification()</c> is created inside Chromium, is visible to
    /// the page through <c>getNotifications()</c>, and is never surfaced to the host, so it is shown
    /// nowhere and dropped in silence. That is the API's contract, not a bug here: the interface is
    /// <c>ICoreWebView2_24</c> and its documentation says "non-persistent" outright.</para>
    /// <para>It matters because the persistent API is the one real apps use — WhatsApp, Discord,
    /// Messenger, Teams and Google Messages all raise notifications that way, so without this every
    /// messaging launcher is silent no matter what its permissions or hidden policy say. Rewriting
    /// the call in the page turns them back into the flavour the host can see.</para>
    /// <para><b>What this cannot reach is the service worker itself.</b> Document-created scripts
    /// run in document contexts only, and a <c>showNotification</c> called from inside a worker —
    /// a push handler, typically — has no host-visible path at all in this SDK. A page-raised
    /// notification is the case that matters for a launcher that is kept running; a push arriving
    /// while nothing is loaded was never going to work anyway, since the resource model has already
    /// closed the browser by then.</para>
    /// <para>Awaited before the first navigation, or the page the flyout opens on is the one page
    /// that misses it.</para>
    /// </remarks>
    private async Task InstallNotificationBridgeAsync(CoreWebView2 core)
    {
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(NotificationBridgeScript);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Installing the notification bridge failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>
    /// Starts watching whether this browser is making a sound of its own.
    /// </summary>
    /// <remarks>
    /// <para>The honest answer to "will the page announce this itself": there is no declarative
    /// signal. A chat app plays its notification sound with an <c>Audio</c> element or WebAudio,
    /// which has nothing to do with the Notification API and cannot be predicted from it. What
    /// <em>can</em> be observed is the browser making noise, which is what this listens for.</para>
    /// <para>Not perfect and not meant to be: a launcher playing a video, or sitting in a call, is
    /// making noise for other reasons and its toasts go quiet for as long as that lasts. That is
    /// the right way round — a toast chiming over a call is worse than one that does not.</para>
    /// </remarks>
    private void WatchPageAudio(CoreWebView2 core)
    {
        core.IsDocumentPlayingAudioChanged += (sender, _) =>
        {
            if (!sender.IsDocumentPlayingAudio) return;

            long now = Environment.TickCount64;
            _pageAudioStartedAt = now;

            // Started just after a toast of ours: this launcher answers for itself, so the next
            // one is silent. This is the half that catches a page whose sound trails its
            // notification, which no check made at toast time could see.
            if (_lastToastAt != 0 && now - _lastToastAt <= PageSoundWindowMs)
                _pageSoundedForToastAt = now;
        };
    }

    /// <summary>True when the page is expected to make this notification's sound itself.</summary>
    private bool PageAnnouncesItself(CoreWebView2? core)
    {
        long now = Environment.TickCount64;

        try
        {
            if (core?.IsDocumentPlayingAudio == true) return true;
        }
        catch (Exception ex)
        {
            // The browser can go between the notification arriving and the toast being built.
            Logger.Debug(ex, "Reading the audio state failed for launcher {Name}", _launcher.Name);
        }

        if (_pageAudioStartedAt != 0 && now - _pageAudioStartedAt <= PageSoundWindowMs) return true;

        return _pageSoundedForToastAt != 0 && now - _pageSoundedForToastAt <= PageSoundMemoryMs;
    }

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
    /// <para><b>Every touch of the notification happens before the toast is built</b>, and the rest
    /// of the method must not reach back to it. See <see cref="ForgetNotifications"/> — getting this
    /// order wrong takes the whole process down.</para>
    /// </remarks>
    /// <summary>
    /// Shows a toast for a notification the page reported over the bridge.
    /// </summary>
    /// <remarks>
    /// Everything arrives as JSON — no WebView2 objects are involved, which is the point; see
    /// <see cref="NotificationBridgeScript"/>. The icon is whatever the page sent: for a chat app
    /// that is the sender's avatar, which is what the notification is actually about, so it leads
    /// and the launcher's own icon is only the fallback.
    /// </remarks>
    private void ShowNotificationToast(CoreWebView2 source, JsonNode message)
    {
        string tag = message["tag"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(tag)) return;

        string title = message["title"]?.GetValue<string>() ?? "";
        string body = message["body"]?.GetValue<string>() ?? "";
        string icon = message["icon"]?.GetValue<string>() ?? "";

        // Which browser raised it, so a click goes back to that tab rather than to whichever is in
        // front. Previously only recorded for notifications carrying action buttons.
        RememberNotificationSource(tag, source);

        // Two reasons to keep quiet, and they are not the same reason. The page asking for a silent
        // notification is a statement about this notification; the page already making a sound is
        // an observation about this launcher — see PageAnnouncesItself.
        bool silent = message["silent"]?.GetValue<bool>() == true || PageAnnouncesItself(source);

        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddArgument("launcher", _launcher.Id)
                .AddArgument("webNotification", tag)
                .AddText(string.IsNullOrWhiteSpace(title) ? _launcher.Name : title);

            if (!string.IsNullOrWhiteSpace(body))
                builder.AddText(body);

            builder.SetTag(ToastIdentifier(tag));
            builder.SetGroup(ToastIdentifier(_launcher.Id));

            // The notification's own icon first — an avatar, usually — circle-cropped the way every
            // other messaging app shows one. The launcher icon is what is left when there is none.
            string? avatar = SaveNotificationIcon(tag, icon);

            // Truncated deliberately: a page icon is often an inline data URL tens of kilobytes
            // long, and logging it whole makes the log unreadable and enormous.
            string iconUrl = message["iconUrl"]?.GetValue<string>() ?? "";
            Logger.Debug("Toast icon for {Name}: source={Source} bytes={Bytes} saved={Saved}",
                _launcher.Name,
                iconUrl.Length <= 60 ? iconUrl : iconUrl[..60] + "…",
                icon.Length, avatar ?? "(none)");

            if (avatar != null)
            {
                builder.SetAppLogoOverride(new Uri(avatar),
                    Microsoft.Windows.AppNotifications.Builder.AppNotificationImageCrop.Circle);
            }
            else
            {
                string? launcherIcon = MainWindow.EnsureToastIconSaved(_launcher);
                bool exists = launcherIcon != null && System.IO.File.Exists(launcherIcon);
                Logger.Debug("Toast falling back to the launcher icon: {Path} exists={Exists}",
                    launcherIcon ?? "(null)", exists);

                if (exists)
                    builder.SetAppLogoOverride(new Uri(launcherIcon!));
            }

            if (message["actions"] is JsonArray actions && actions.Count > 0)
            {
                _pendingActions[tag] = (JsonArray)actions.DeepClone();
                AddNotificationActions(builder, tag);
            }

            if (silent) builder.MuteAudio();

            // Before the Show, so a sound the page starts while Windows is putting the toast up is
            // still attributed to it rather than being missed by a few milliseconds.
            _lastToastAt = Environment.TickCount64;

            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
            PostNotificationEvent("notifyShown", tag);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Showing a page notification failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>Writes the page-supplied icon to disk, or null when there was not a usable one.</summary>
    private string? SaveNotificationIcon(string tag, string dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return null;

        int comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.Ordinal)) return null;

        try
        {
            byte[] bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            if (bytes.Length == 0) return null;

            string path = System.IO.Path.Combine(
                MainWindow.GetPhysicalAppDataDir(), $"notif-icon-{_launcher.Id}-{ToastIdentifier(tag)}.png");
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Saving a notification icon failed for launcher {Name}", _launcher.Name);
            return null;
        }
    }

    /// <summary>Tells the page what became of one of its notifications.</summary>
    private void PostNotificationEvent(string kind, string tag)
    {
        var core = _webView?.CoreWebView2;
        if (core == null) return;

        try
        {
            core.PostWebMessageAsJson(
                new JsonObject { ["__ll"] = kind, ["tag"] = tag }.ToJsonString());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Posting a notification event failed for launcher {Name}", _launcher.Name);
        }
    }




    /// <summary>
    /// Makes a page-supplied string safe to use as a Windows toast tag or group.
    /// </summary>
    /// <remarks>
    /// Both are capped at 64 characters, and a page's tag is arbitrary text — a URL, a thread id,
    /// occasionally something long enough to be rejected outright. Hashing anything that does not
    /// fit keeps the one property that matters: the same tag maps to the same identifier, which is
    /// what makes Windows replace the earlier toast rather than stack another one.
    /// </remarks>
    private static string ToastIdentifier(string value)
    {
        var cleaned = new string([.. value.Where(char.IsLetterOrDigit)]);
        if (cleaned.Length is > 0 and <= 64) return cleaned;

        byte[] hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Opens the launcher a clicked toast came from, and tells its page the toast was clicked.
    /// </summary>
    /// <returns>False when the toast was not one of ours, so the caller can handle it itself.</returns>
    internal static bool HandleNotificationActivation(IDictionary<string, string> arguments, string reply = "")
    {
        if (!arguments.TryGetValue("launcher", out string? launcherId) || string.IsNullOrEmpty(launcherId))
            return false;

        // An action button is answered in the page, not by opening the flyout. Replying to a
        // message from the toast and then having the window fly open would undo the point of
        // replying from the toast.
        if (TryDeliverAction(launcherId, arguments, reply)) return true;

        // Clicked. Two audiences, and a page has only one of them:
        //
        //   * a page that built this with `new Notification(...)` is listening on the object it
        //     handed us, so `onclick` is raised on the shim;
        //   * a page that used `registration.showNotification()` — Teams, WhatsApp, Messenger — is
        //     listening for `notificationclick` **in its service worker**, and never sees that
        //     `onclick` at all.
        //
        // Only the first was told, which is why clicking a Teams toast could be followed by Teams
        // showing the same notification inside itself: its handler never ran, so the real
        // persistent notification was never closed and was still sitting in `getNotifications()`
        // when the flyout came forward. The worker is now sent the same empty-action message a
        // toast *button* sends, which its handler already answers by closing the notification and
        // dispatching a genuine `notificationclick` — see the worker bridge.
        //
        // Both are sent, because there is no way to know which kind of page it was, and each is
        // inert for the other: a `new Notification` page has nothing under that tag in
        // `getNotifications()`, and a `showNotification` page has no `onclick` listener.
        if (arguments.TryGetValue("webNotification", out string? tag) && !string.IsNullOrEmpty(tag) &&
            Instances.TryGetValue(launcherId, out var source))
        {
            source.PostNotificationEvent("notifyClicked", tag);
            source.DispatchNotificationAction(tag, action: "", reply: "");
        }

        var owner = MainWindow.Current;
        if (owner == null) return false;
        if (SettingsManager.Current.Launchers.All(l => l.Id != launcherId)) return false;

        // Already open: bring it forward rather than toggling, which would dismiss it.
        //
        // This used to do nothing at all, on the reasoning that an open flyout is already on
        // screen. True of a flyout — it is always-on-top, so open means visible and in front — and
        // false of a regular window, which can sit open and buried behind whatever the user was
        // working in, or minimized to its taskbar button. Clicking the toast then appeared to do
        // nothing whatsoever.
        if (Instances.TryGetValue(launcherId, out var panel) && panel._isOpen)
        {
            panel.ActivateForNotification();
            return true;
        }

        owner.OpenLauncherPanel(launcherId);
        return true;
    }
}
