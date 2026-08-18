using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace LittleLauncher.Windows;

/// <summary>
/// Reaches the one place a hosted WebView2 otherwise cannot: inside the page's service worker.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> `WebFlyoutWindow.Permissions.cs` bridges notifications raised by
/// the *page*, and that is as far as a document-created script can reach — a
/// <c>showNotification</c> called from worker scope has no host-visible path at all, and neither
/// does the <c>notificationclick</c> that a toast button is supposed to raise. Both live in the
/// worker, so the shim has to live there too.</para>
/// <para><b>How the worker is reached.</b> The worker's script is intercepted as it is fetched
/// (<see cref="CoreWebView2WebResourceRequestSourceKinds"/> makes worker traffic visible to the
/// host) and served as the shim followed by <c>importScripts</c> of the original. Nothing is
/// re-fetched by the app: <c>importScripts</c> is the browser's own request, carrying the profile's
/// cookies, so a dashboard behind a login still loads its real worker. The marker query is what
/// stops that inner request being wrapped again.</para>
/// <para><b>What the shim does.</b> Two directions:</para>
/// <list type="bullet">
/// <item><description><b>Out:</b> <c>showNotification</c> in the worker still makes the real
/// notification — WebView2 never displays it, but it is what <c>getNotifications()</c> returns and
/// what a click event must carry — and then asks a window client to raise the page-level
/// notification the host does see.</description></item>
/// <item><description><b>In:</b> a toast button posts back through the page to the worker, which
/// dispatches a genuine <c>notificationclick</c> carrying <c>action</c>, <c>reply</c> and the app's
/// own <c>notification.data</c>. The app's own handler runs, so replying from the toast does what
/// replying in the app does.</description></item>
/// </list>
/// <para>Verified end to end against a probe worker: <c>action="reply"</c>,
/// <c>reply="typed from the toast"</c> and <c>data={"convo":42}</c> all arrived at a listener
/// registered the ordinary way.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>Marks the inner request for the original worker script, so it is not wrapped twice.</summary>
    private const string OriginalScriptMarker = "__llsw";

    /// <summary>
    /// Worker scripts each browser has been told about, and so wraps when they are fetched.
    /// </summary>
    /// <remarks>
    /// Keyed by browser, not by URL alone. A resource filter belongs to one <c>CoreWebView2</c>, so
    /// with tabs on, two tabs on the same site share a worker script URL — and a flat set would see
    /// the second as already known and never give that tab its filter, leaving one tab bridged and
    /// the other silently not.
    /// </remarks>
    private readonly Dictionary<CoreWebView2, HashSet<string>> _serviceWorkerScripts = new();

    /// <summary>
    /// Action buttons a notification declared, keyed by its tag, waiting for the notification
    /// itself to arrive.
    /// </summary>
    /// <remarks>
    /// The two arrive separately because <see cref="CoreWebView2Notification"/> has no actions on it
    /// — it only ever carries non-persistent notifications, which the spec forbids actions on — so
    /// the shim sends them alongside. The tag is the join: the shim gives every notification one,
    /// inventing a unique one where the app supplied none, precisely so this lookup always works.
    /// </remarks>
    private readonly Dictionary<string, JsonArray> _pendingActions = new(StringComparer.Ordinal);

    /// <summary>Windows caps a toast at five buttons; Chromium reports two, so two is the honest number.</summary>
    private const int MaxNotificationActions = 2;

    /// <summary>
    /// Which browser raised the notification behind each toast tag, so a clicked button goes back
    /// to the page that will understand it.
    /// </summary>
    /// <remarks>
    /// With tabs on, the notification that produced a toast is very often <b>not</b> the tab on
    /// screen — that is the point of keeping the others loaded. Posting the action to the active
    /// browser would hand a reply for one conversation to whichever page happened to be in front.
    /// Bounded, because a toast the user ignores reports nothing back.
    /// </remarks>
    private readonly Dictionary<string, CoreWebView2> _notificationSources = new(StringComparer.Ordinal);

    // ── The worker-scope shim ───────────────────────────────────────

    private const string ServiceWorkerShimScript = """
        (function () {
            if (self.__littleLauncherShim) return;
            self.__littleLauncherShim = true;

            var proto = self.ServiceWorkerRegistration && self.ServiceWorkerRegistration.prototype;
            if (!proto || !proto.showNotification) return;
            var nativeShow = proto.showNotification;

            function relay(title, options) {
                return self.clients.matchAll({ type: 'window', includeUncontrolled: true })
                    .then(function (cs) {
                        if (cs.length) cs[0].postMessage({ __ll: 'notify', title: title, options: options });
                    })
                    .catch(function () { });
            }

            proto.showNotification = function (title, options) {
                var o = {};
                var src = options || {};
                for (var k in src) { try { o[k] = src[k]; } catch (e) { } }

                // Every notification gets a tag, because the tag is how the host pairs a toast with
                // the actions it should carry and with the click that comes back. An invented tag
                // is unique, so it behaves exactly like having none.
                if (!o.tag) o.tag = '__ll-' + Math.random().toString(36).slice(2) + Date.now().toString(36);

                // The real notification is still created. It is never displayed — WebView2 does not
                // surface persistent notifications — but getNotifications() returns it, and a
                // notificationclick has to carry a real one for event.notification.data to work.
                var p = nativeShow.call(this, title, o);
                p.then(function () { return relay(title, o); }).catch(function () { });
                return p;
            };

            self.addEventListener('message', function (ev) {
                var d = ev.data;
                if (!d || d.__ll !== 'action') return;

                ev.waitUntil(self.registration.getNotifications({ tag: d.tag }).then(function (list) {
                    var n = list && list[0];
                    if (!n) return;

                    // A real notificationclick, dispatched to whatever the app registered the
                    // ordinary way. Closing first matches what the browser does before dispatching.
                    if (!d.action) { try { n.close(); } catch (e) { } }
                    self.dispatchEvent(new NotificationEvent('notificationclick', {
                        notification: n,
                        action: d.action || '',
                        reply: d.reply || ''
                    }));
                }).catch(function () { }));
            });

            // Deliberate: wrapping changes the script's bytes, so the browser installs a new worker.
            // Without these it would sit in "waiting" until every client went away, and the bridge
            // would not take effect for the session the user is actually in.
            self.addEventListener('install', function () { self.skipWaiting(); });
            self.addEventListener('activate', function (e) { e.waitUntil(self.clients.claim()); });
        })();
        """;

    // ── The page half of the relay ──────────────────────────────────

    private const string ServiceWorkerBridgeScript = """
        (function () {
            if (!navigator.serviceWorker || !window.chrome || !window.chrome.webview) return;

            function post(m) { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch (e) { } }

            // Announcing is not enough on its own: the host must have its interception in place
            // *before* the browser fetches the script, and posting a message is asynchronous — the
            // first version of this raced and the worker installed unwrapped. So announcing returns
            // a promise the registration waits on. It fails open on a timeout, because a lost
            // acknowledgement must never stop a page registering its worker at all.
            var acks = {};
            function announce(url) {
                var href;
                try { href = new URL(url, location.href).href; } catch (e) { return Promise.resolve(); }

                return new Promise(function (resolve) {
                    acks[href] = function () { delete acks[href]; resolve(); };
                    post({ __ll: 'swScript', url: href });
                    setTimeout(function () { if (acks[href]) acks[href](); }, 1500);
                });
            }

            var container = Object.getPrototypeOf(navigator.serviceWorker) || ServiceWorkerContainer.prototype;
            var nativeRegister = container.register;
            if (nativeRegister && !nativeRegister.__littleLauncherShim) {
                var patched = function (url, opts) {
                    var self_ = this;
                    return announce(url).then(function () { return nativeRegister.call(self_, url, opts); });
                };
                patched.__littleLauncherShim = true;
                container.register = patched;
            }

            // A worker registered on an earlier visit is already installed, so register() may never
            // be called again. Announce it and ask for an update check, which re-fetches the script
            // and is the only moment the wrap can be applied.
            navigator.serviceWorker.getRegistrations().then(function (rs) {
                rs.forEach(function (r) {
                    var w = r.active || r.waiting || r.installing;
                    if (!w) return;
                    announce(w.scriptURL).then(function () { try { r.update(); } catch (e) { } });
                });
            }).catch(function () { });

            // ── The page's best icon ────────────────────────────────
            // Chromium's favicon is whatever the page declared for a browser tab — 32 or 64px, and
            // upscaling that into a tray icon or a taskbar pin is why web launchers looked soft
            // enough that picking a replacement by hand was the obvious workaround. A site that
            // can be installed almost always declares something far better in its web app
            // manifest, or as an apple-touch-icon, and fetching it *here* is what makes it
            // reachable: these live behind the same login as the page.
            function bestIconUrl() {
                var best = { url: '', size: 0 };

                function consider(url, size) {
                    if (!url || size <= best.size) return;
                    try { best = { url: new URL(url, location.href).href, size: size }; } catch (e) { }
                }

                function sizeOf(attr) {
                    if (!attr) return 0;
                    var m = /(\d+)\s*x\s*(\d+)/i.exec(attr);
                    return m ? parseInt(m[1], 10) : 0;
                }

                document.querySelectorAll('link[rel~="icon"]').forEach(function (l) {
                    consider(l.getAttribute('href'), sizeOf(l.getAttribute('sizes')) || 32);
                });
                document.querySelectorAll('link[rel~="apple-touch-icon"], link[rel~="apple-touch-icon-precomposed"]').forEach(function (l) {
                    consider(l.getAttribute('href'), sizeOf(l.getAttribute('sizes')) || 180);
                });

                var manifest = document.querySelector('link[rel~="manifest"]');
                if (!manifest || !manifest.getAttribute('href')) return Promise.resolve(best);

                return fetch(new URL(manifest.getAttribute('href'), location.href).href, { credentials: 'include' })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (j) {
                        (j && j.icons ? j.icons : []).forEach(function (i) {
                            consider(i.src, sizeOf(i.sizes));
                        });
                        return best;
                    })
                    .catch(function () { return best; });
            }

            function reportBestIcon() {
                bestIconUrl().then(function (best) {
                    // Not worth replacing Chromium's own favicon with something no larger.
                    if (!best.url || best.size < 96) return;

                    return fetch(best.url, { credentials: 'include' })
                        .then(function (r) { return r.ok ? r.blob() : null; })
                        .then(function (b) {
                            // Rasters only: a manifest icon is often an SVG, which nothing
                            // downstream of here can decode.
                            if (!b || b.size > 512 * 1024 || b.type.indexOf('svg') >= 0) return;
                            var fr = new FileReader();
                            fr.onload = function () {
                                post({ __ll: 'pageIcon', icon: fr.result, size: best.size });
                            };
                            fr.readAsDataURL(b);
                        });
                }).catch(function () { });
            }

            if (document.readyState === 'complete') setTimeout(reportBestIcon, 0);
            else window.addEventListener('load', function () { setTimeout(reportBestIcon, 0); });

            // The worker cannot construct a Notification, so it asks the page to.
            navigator.serviceWorker.addEventListener('message', function (ev) {
                var d = ev.data;
                if (!d || d.__ll !== 'notify') return;
                var o = d.options || {};

                if (o.actions && o.actions.length)
                    post({ __ll: 'actions', tag: o.tag, actions: o.actions });

                try {
                    // Note the absent 'actions': the constructor throws on it, because a
                    // non-persistent notification may not have any. They travel beside it instead.
                    new Notification(d.title, {
                        body: o.body, icon: o.icon, badge: o.badge, image: o.image,
                        tag: o.tag, data: o.data, dir: o.dir, lang: o.lang,
                        silent: o.silent, timestamp: o.timestamp,
                        requireInteraction: o.requireInteraction
                    });
                } catch (e) { }
            });

            // A toast button, on its way back to the worker.
            window.chrome.webview.addEventListener('message', function (ev) {
                var d = ev.data;
                try { if (typeof d === 'string') d = JSON.parse(d); } catch (e) { return; }
                if (!d) return;

                if (d.__ll === 'swReady') {
                    if (acks[d.url]) acks[d.url]();
                    return;
                }

                if (d.__ll !== 'action') return;

                navigator.serviceWorker.getRegistration().then(function (r) {
                    var w = (r && (r.active || r.waiting)) || navigator.serviceWorker.controller;
                    if (w) w.postMessage({ __ll: 'action', tag: d.tag, action: d.action, reply: d.reply });
                }).catch(function () { });
            });
        })();
        """;

    // ── Wiring ──────────────────────────────────────────────────────

    /// <summary>
    /// Installs the page half of the bridge and starts watching for worker scripts to wrap.
    /// </summary>
    /// <remarks>
    /// Awaited before the first navigation, for the same reason as the notification bridge: a
    /// document-created script added after the navigation has started misses the page the flyout
    /// was opened to show.
    /// </remarks>
    private async Task InstallServiceWorkerBridgeAsync(CoreWebView2 core)
    {
        try
        {
            core.WebMessageReceived += OnBridgeMessageReceived;
            core.WebResourceRequested += OnServiceWorkerResourceRequested;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ServiceWorkerBridgeScript);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Installing the service worker bridge failed for launcher {Name}", _launcher.Name);
        }
    }

    private void OnBridgeMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(e.TryGetWebMessageAsString());
        }
        catch
        {
            return;   // not ours; a page is free to post whatever it likes
        }

        // Released on this thread once read; the finalizer releasing it is what kills the process.
        try
        {
        string? kind = message?["__ll"]?.GetValue<string>();
        if (kind == "swScript")
        {
            string? url = message?["url"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(url)) WatchServiceWorkerScript(sender, url);
        }
        else if (kind == "pageIcon")
        {
            AdoptHighResPageIcon(sender, message);
        }
        else if (kind == "bgIntent")
        {
            _backgroundIntentAt = Environment.TickCount64;
        }
        else if (kind == "key")
        {
            string? id = message?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id)) InvokeShortcut(id);
        }
        else if (kind == "notify")
        {
            ShowNotificationToast(sender, message!);
        }
        else if (kind == "actions")
        {
            string? tag = message?["tag"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(tag) && message?["actions"] is JsonArray actions)
            {
                _pendingActions[tag] = (JsonArray)actions.DeepClone();
                RememberNotificationSource(tag, sender);
            }
        }
        }
        finally { ReleaseWebViewObject(e); }
    }

    /// <summary>Starts intercepting one worker script, so the next fetch of it arrives wrapped.</summary>
    /// <remarks>
    /// Always acknowledges, including for a script already being watched: the page is holding its
    /// <c>register()</c> open until this comes back, and a silent return would stall it until the
    /// fail-open timeout.
    /// </remarks>
    private void WatchServiceWorkerScript(CoreWebView2 core, string url)
    {
        if (url.Contains(OriginalScriptMarker, StringComparison.Ordinal)) return;

        if (!_serviceWorkerScripts.TryGetValue(core, out var watched))
            _serviceWorkerScripts[core] = watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!watched.Add(url))
        {
            AcknowledgeServiceWorkerScript(core, url);
            return;
        }

        try
        {
            // The overload taking source kinds is what makes worker traffic visible at all; the
            // plain two-argument one only ever sees the document's own requests.
            core.AddWebResourceRequestedFilter(
                url,
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not watch service worker script {Url}", url);
        }

        AcknowledgeServiceWorkerScript(core, url);
    }

    private void AcknowledgeServiceWorkerScript(CoreWebView2 core, string url)
    {
        try
        {
            core.PostWebMessageAsJson(new JsonObject { ["__ll"] = "swReady", ["url"] = url }.ToJsonString());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Acknowledging a service worker script failed");
        }
    }

    private void OnServiceWorkerResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string uri = e.Request.Uri;

        // The inner request for the real script. Letting it through untouched is the whole point:
        // it is the browser's own fetch, with the profile's cookies on it.
        if (uri.Contains(OriginalScriptMarker, StringComparison.Ordinal)) return;
        if (!_serviceWorkerScripts.TryGetValue(sender, out var watched) || !watched.Contains(uri)) return;

        // Building the response body is asynchronous, so the request has to be held open — a
        // handler that returns without either a response or a deferral has declined to interfere.
        var deferral = e.GetDeferral();
        _ = RespondWithWrappedScriptAsync(sender, e, uri, deferral);
    }

    private async Task RespondWithWrappedScriptAsync(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs e,
        string uri,
        global::Windows.Foundation.Deferral deferral)
    {
        try
        {
            string separator = uri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            string original = $"{uri}{separator}{OriginalScriptMarker}=1";

            string script = ServiceWorkerShimScript +
                            "\nimportScripts(" + JsonSerializer.Serialize(original) + ");\n";

            var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            var writer = new global::Windows.Storage.Streams.DataWriter(stream);
            writer.WriteBytes(Encoding.UTF8.GetBytes(script));
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);

            e.Response = core.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                // Service-Worker-Allowed keeps a broader registration scope working: the header on
                // the real response is not ours to read, and refusing the scope would fail the
                // registration outright.
                "Content-Type: text/javascript\r\nCache-Control: no-cache\r\nService-Worker-Allowed: /");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Wrapping the service worker script failed for launcher {Name}", _launcher.Name);
        }
        finally
        {
            // Never skipped: an incomplete deferral leaves the worker's script request hanging, and
            // with it the whole registration. The args are released only *after* it completes —
            // they are live for as long as the deferral is.
            deferral.Complete();
            ReleaseWebViewObject(e);
        }
    }

    // ── Actions ─────────────────────────────────────────────────────

    /// <summary>The actions a notification declared, if the shim sent any for this tag.</summary>
    private JsonArray? TakePendingActions(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        if (!_pendingActions.Remove(tag, out var actions)) return null;

        // The dictionary is drained by lookups, and a notification that never arrives would leave
        // its entry behind, so it is also bounded.
        while (_pendingActions.Count > 64)
            _pendingActions.Remove(_pendingActions.Keys.First());

        return actions;
    }

    /// <summary>Id of the toast's reply box. Fixed, because only one action can carry text.</summary>
    private const string ReplyInputId = "llReply";

    /// <summary>
    /// Puts the page's declared actions on the toast as buttons, and a reply box where one asks
    /// for text.
    /// </summary>
    /// <remarks>
    /// <para>A web action is <c>{action, title, type, placeholder}</c>. <c>type: "text"</c> is the
    /// web platform's inline reply — the same declaration that produces a reply field on Android —
    /// and it maps onto a Windows toast text box attached to its button. The rest are plain
    /// buttons.</para>
    /// <para>Capped at what Chromium itself reports through <c>Notification.maxActions</c>: showing
    /// more than the page believes exist would be inventing UI the app has no handler for.</para>
    /// </remarks>
    private void AddNotificationActions(
        Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder builder, string? tag)
    {
        var actions = TakePendingActions(tag);
        if (actions == null || string.IsNullOrEmpty(tag)) return;

        bool replyBoxAdded = false;
        int added = 0;

        foreach (var node in actions)
        {
            if (added >= MaxNotificationActions) break;
            if (node is not JsonObject action) continue;

            string id = action["action"]?.GetValue<string>() ?? "";
            string title = action["title"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;

            bool wantsText = string.Equals(action["type"]?.GetValue<string>(), "text", StringComparison.Ordinal);

            var button = new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton(title)
                .AddArgument("launcher", _launcher.Id)
                .AddArgument("webNotificationTag", tag)
                .AddArgument("webNotificationAction", id);

            if (wantsText && !replyBoxAdded)
            {
                string placeholder = action["placeholder"]?.GetValue<string>() ?? "";
                builder.AddTextBox(ReplyInputId, placeholder, "");
                button.SetInputId(ReplyInputId);
                replyBoxAdded = true;
            }

            builder.AddButton(button);
            added++;
        }
    }

    /// <summary>
    /// Hands a clicked toast button to the launcher it belongs to.
    /// </summary>
    /// <returns>True when the click was an action this launcher could deliver.</returns>
    internal static bool TryDeliverAction(string launcherId, IDictionary<string, string> arguments, string reply)
    {
        if (!arguments.TryGetValue("webNotificationAction", out string? action) || string.IsNullOrEmpty(action))
            return false;
        if (!arguments.TryGetValue("webNotificationTag", out string? tag) || string.IsNullOrEmpty(tag))
            return false;
        if (!Instances.TryGetValue(launcherId, out var flyout)) return false;

        flyout.DispatchNotificationAction(tag, action, reply);
        return true;
    }

    /// <summary>
    /// Sends a clicked toast button back to the page, which relays it to the worker.
    /// </summary>
    /// <remarks>
    /// Nothing happens if the browser has gone: the notification the click refers to went with it,
    /// and the launcher opening is the only sensible remainder — which the caller does anyway.
    /// </remarks>
    /// <summary>Notes which browser a toast tag belongs to, keeping the map bounded.</summary>
    private void RememberNotificationSource(string tag, CoreWebView2 core)
    {
        while (_notificationSources.Count > 64)
            _notificationSources.Remove(_notificationSources.Keys.First());

        _notificationSources[tag] = core;
    }

    private void DispatchNotificationAction(string tag, string action, string reply)
    {
        // The tab that raised it, not the one on screen. Falling back to the active browser covers
        // the single-browser case and a tag whose source has since been closed.
        if (!_notificationSources.TryGetValue(tag, out var core) || core == null)
            core = _webView?.CoreWebView2;
        if (core == null) return;

        try
        {
            var message = new JsonObject
            {
                ["__ll"] = "action",
                ["tag"] = tag,
                ["action"] = action,
                ["reply"] = reply,
            };
            core.PostWebMessageAsJson(message.ToJsonString());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Dispatching a notification action failed for launcher {Name}", _launcher.Name);
        }
    }
}
