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
/// cookies, so a dashboard behind a login still loads its real worker. What stops that inner
/// request being wrapped in turn is the request itself saying what it is for - see
/// <see cref="IsWorkerMainScriptRequest"/>.</para>
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

    /// <summary>Worker scripts registered as ES modules, so the wrap suits the realm it lands in.</summary>
    /// <remarks>
    /// <para><b>A module worker has no <c>importScripts</c>.</b> The classic wrap ends in a call to
    /// it, so serving that body to a module registration throws while the script is being evaluated,
    /// the install fails, and <c>register()</c> rejects. The origin is then left with no worker at
    /// all, which looks nothing like a bridge problem and everything like the site being broken.</para>
    /// <para>A module is wrapped with a static <c>import</c> instead. That runs the original first,
    /// which is the one ordering difference: a worker calling <c>showNotification</c> during its own
    /// evaluation would miss the patch. Nothing does that, and the alternative is a dynamic
    /// <c>import()</c>, which a service worker is not allowed to use.</para>
    /// </remarks>
    private readonly HashSet<string> _moduleWorkerScripts = new(StringComparer.OrdinalIgnoreCase);

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

            function relay(message) {
                return self.clients.matchAll({ type: 'window', includeUncontrolled: true })
                    .then(function (cs) {
                        if (cs.length) cs[0].postMessage(message);
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
                p.then(function () { return relay({ __ll: 'notify', title: title, options: o }); }).catch(function () { });
                return p;
            };

            // Closing is the other half of the same gap. A chat app closes its notification once
            // the thread has been read — here, or on a phone signed in to the same account — and it
            // closes a notification Chromium made, which the host has never been told about. Left
            // unrelayed the toast outlives the message it was announcing, and those are precisely
            // the toasts that pile up in the Action Center.
            var notification = self.Notification && self.Notification.prototype;
            if (notification && notification.close) {
                var nativeClose = notification.close;
                notification.close = function () {
                    if (this.tag) relay({ __ll: 'notifyClose', tag: this.tag });
                    return nativeClose.apply(this, arguments);
                };
            }

            self.addEventListener('message', function (ev) {
                var d = ev.data;
                if (!d) return;

                // How a page tells whether the worker it has is this one. The page cannot read
                // worker globals and cannot read the installed script, so asking is the only way,
                // and it is what stops the re-registration below running on a worker already ours.
                if (d.__ll === 'ping') {
                    try { if (ev.source && ev.source.postMessage) ev.source.postMessage({ __ll: 'pong' }); } catch (e) { }
                    return;
                }

                if (d.__ll !== 'action') return;

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
            function announce(url, type) {
                var href;
                try { href = new URL(url, location.href).href; } catch (e) { return Promise.resolve(); }

                return new Promise(function (resolve) {
                    acks[href] = function () { delete acks[href]; resolve(); };
                    // The type travels with it, because a module worker cannot run the classic
                    // wrap: importScripts does not exist there, so the wrapped script throws while
                    // it is being evaluated and the registration fails outright. Empty means the
                    // caller does not know, which is a third answer rather than a synonym for
                    // classic - the host declines to wrap at all rather than guess.
                    post({ __ll: 'swScript', url: href, type: type || '' });
                    setTimeout(function () { if (acks[href]) acks[href](); }, 1500);
                });
            }

            var container = Object.getPrototypeOf(navigator.serviceWorker) || ServiceWorkerContainer.prototype;
            var nativeRegister = container.register;
            if (nativeRegister && !nativeRegister.__littleLauncherShim) {
                var patched = function (url, opts) {
                    var self_ = this;
                    // Worth intercepting if the site can notify, **or has just asked whether it
                    // may**. That second half is the whole reason a push site was never bridged at
                    // its install: it registers its worker as part of the permission flow, while
                    // the answer is still 'default', so a test for 'granted' alone skips the one
                    // fetch that ever happens and leaves adoption to force another later - which is
                    // where every URL problem lives. Messenger registers `sw?s=push` seconds after
                    // asking, and a site that never asks (Home Assistant) is still not bridged.
                    var allowed = false;
                    try {
                        allowed = (window.Notification && Notification.permission === 'granted')
                            || (Date.now() - (window.__llPermissionAskedAt || 0) < 60000);
                    } catch (e) { }
                    if (!allowed) return nativeRegister.call(self_, url, opts);

                    // Stated rather than passed through: a classic registration omits the option
                    // entirely, so "absent" here means classic and must not reach the host as the
                    // "unknown" the sweep below sends.
                    var type = (opts && opts.type) === 'module' ? 'module' : 'classic';
                    return announce(url, type).then(function () { return nativeRegister.call(self_, url, opts); });
                };
                patched.__littleLauncherShim = true;
                container.register = patched;
            }

            // Substituted by the host: only a launcher that opted in re-registers anything.
            // Does the worker we have answer as one of ours? A page cannot read worker globals and
            // cannot read the installed script, so asking it is the only way to know, and it is
            // what keeps the re-registration below to once rather than every load.
            function isBridged(worker) {
                return new Promise(function (resolve) {
                    var settled = false;
                    function onMessage(ev) {
                        if (!ev.data || ev.data.__ll !== 'pong') return;
                        settled = true;
                        navigator.serviceWorker.removeEventListener('message', onMessage);
                        resolve(true);
                    }
                    navigator.serviceWorker.addEventListener('message', onMessage);
                    try { worker.postMessage({ __ll: 'ping' }); } catch (e) { }
                    setTimeout(function () {
                        if (settled) return;
                        navigator.serviceWorker.removeEventListener('message', onMessage);
                        resolve(false);
                    }, 2000);
                });
            }

            // A worker installed before the bridge existed runs the site's own script, and only a
            // genuinely fresh fetch can be intercepted. Measured, three ways: update() and a
            // register() naming the URL already registered never touch the network, and - the one
            // that cost a build to learn - neither does unregistering and registering the same URL
            // again, because that fetch is answered from cache.
            //
            // There was a fourth way and it is gone: unregister the worker and reload, so the site
            // installs it again against a cold cache. It worked, and it is not something to do to
            // somebody else's site. Unregistering destroys the registration's push subscription, so
            // a launcher that had server-pushed notifications stops getting them until the site
            // subscribes again; and "the site will register it again on the way back up" is an
            // assumption, not a fact, for anything that registers behind a login or on one route.
            // Its bounds were weaker than they read, too: the once-per-tab sessionStorage guard
            // resets on every new tab, and an idle unload builds a new tab on every open.
            //
            // So a pre-existing worker is left alone. It is picked up whenever the browser next
            // re-fetches the script on its own schedule, and every fresh install goes through the
            // patched register() above.

            // A site that cannot notify has nothing to bridge, and bridging it is not free: the wrap
            // changes the worker's bytes, so the browser installs a new one, and a frontend that
            // watches for that tells the user there is an update. Home Assistant does exactly that,
            // on a launcher that was never granted notification permission in the first place, so
            // the whole exchange was cost with no possible benefit.
            //
            // Read when the sweep runs rather than captured when this script does. Permission is
            // most often granted *during* the first visit, in the flyout's own prompt bar, and a
            // value read at document-created time is always the answer from before that.
            function mayNotify() {
                try { return !!(window.Notification && Notification.permission === 'granted'); }
                catch (e) { return false; }
            }

            var scriptUrlPolicy = null, scriptUrlPolicyTried = false;

            // A script URL this site will accept, or null if it will not accept one from us.
            //
            // Trusted Types is two directives and conflating them broke this the first time it ran:
            // `trusted-types` names which *policies* may exist, while `require-trusted-types-for
            // 'script'` is what makes a plain string unacceptable at the sink. A site can allow any
            // policy name and still refuse a string - Google Messages does exactly that - so "can a
            // policy be created" was never the question, and answering it yes and then passing a
            // string unregistered a worker that could not be put back.
            //
            // So create one and use it. Where nothing is enforced the string is returned untouched;
            // where a policy cannot be created at all, null, and the caller refuses before touching
            // the registration. That last case is Teams, whose CSP lists its allowed policies with
            // no 'allow-duplicates' and includes its own service-worker policies, so even taking a
            // name would break the thing being bridged.
            function trustedScriptUrl(url) {
                if (!window.trustedTypes || !window.trustedTypes.createPolicy) return url;

                if (!scriptUrlPolicyTried) {
                    scriptUrlPolicyTried = true;
                    try {
                        scriptUrlPolicy = window.trustedTypes.createPolicy('littleLauncherWorker', {
                            createScriptURL: function (u) { return u; }
                        });
                    } catch (e) {
                        scriptUrlPolicy = null;
                    }
                }

                if (!scriptUrlPolicy) return null;
                try { return scriptUrlPolicy.createScriptURL(url); } catch (e) { return null; }
            }

            var adoptedKey = '__llWorkerAdopted';

            // Take over a worker that was installed before this bridge existed.
            //
            // Only one thing has ever reached the host: a register() naming a script URL the
            // browser has not got. update(), a same-URL register(), and unregister-and-register-
            // again are all answered from what it already has, and emptying the HTTP cache does not
            // help because a worker's script is kept somewhere else. All measured, each at the cost
            // of a build, and one of them at the cost of a site's worker.
            //
            // So the registration is walked out to a marked URL and straight back to the site's
            // own. Both hops name a URL the browser has not got, so both are real fetches and both
            // are wrapped, and it ends where it started with the marker gone.
            //
            // **Nothing is unregistered and nothing is reloaded.** register() on a live
            // registration replaces it in place, so the origin always has a worker - unlike every
            // earlier attempt, each of which removed one first and could then fail with nothing to
            // put back.
            function adopt(r, w) {
                // Once per worker script, ever. localStorage rather than sessionStorage: an idle
                // unload builds a new tab on every open, so a per-tab guard is no guard at all.
                try {
                    if (localStorage.getItem(adoptedKey) === w.scriptURL) return;
                } catch (e) {
                    post({ __ll: 'swAdopt', url: w.scriptURL, ok: false,
                           error: 'no localStorage, so this could not be held to one attempt' });
                    return;
                }

                // A worker URL whose query already means something cannot be walked. Adoption marks
                // a URL by appending to the query to force a fetch, and a site that serves
                // `sw?s=push` does not serve `sw?s=push&llbridge=1` alike - Messenger answers the
                // marked URL with something that will not run, and the registration ends up alive
                // with a dead worker. Twice, before this guard existed.
                //
                // Nothing is lost by declining: a site like that is bridged at its install instead,
                // which is where it should have been caught in the first place.
                if (w.scriptURL.indexOf('?') >= 0) {
                    post({ __ll: 'swAdopt', url: w.scriptURL, ok: false,
                           error: 'its URL carries a query, which cannot be marked without changing what the site serves' });
                    return;
                }

                var sep = '?';
                var out = trustedScriptUrl(w.scriptURL + sep + 'llbridge=1');
                var back = trustedScriptUrl(w.scriptURL);

                // Both resolved before anything is registered: a URL we cannot mint means the
                // registration is left exactly where it is.
                if (out === null || back === null) {
                    post({ __ll: 'swAdopt', url: w.scriptURL, ok: false,
                           error: 'the site enforces Trusted Types and will not let us create a policy' });
                    return;
                }

                try { localStorage.setItem(adoptedKey, w.scriptURL); } catch (e) { }

                // Through the patched register, not the native one. The patch is what announces a
                // script URL to the host and so puts the filter in place before the fetch; going
                // straight to nativeRegister skips that, and the first run of this walk did exactly
                // that. It appeared to work only because the page had already registered its own
                // URL a moment earlier and left a filter behind for the return hop to land on. A
                // site that does not register on load - WhatsApp - would have got nothing.
                // Classic first, module if that fails, and the answer carried to the second hop.
                //
                // The sweep cannot read a worker's type - the ServiceWorker interface exposes
                // scriptURL and state and no scriptType - and the wrong wrap throws while the script
                // is being evaluated. Guessing would be dangerous if anything had been removed
                // first, but nothing has: a failed register() leaves the existing registration
                // exactly where it was. So the guess is free to be wrong once.
                function hop(url, type) {
                    var opts = { scope: r.scope };
                    if (type) opts.type = type;
                    return navigator.serviceWorker.register(url, opts);
                }

                hop(out).then(
                    function () { return ''; },
                    function () { return hop(out, 'module').then(function () { return 'module'; }); }
                ).then(function (type) {
                    return hop(back, type);
                }).then(function (again) {
                    post({ __ll: 'swAdopt', url: w.scriptURL, ok: true, scope: (again && again.scope) || r.scope });
                }, function (e) {
                    post({ __ll: 'swAdopt', url: w.scriptURL, ok: false, error: String((e && e.message) || e) });
                });
            }

            function sweep() {
            navigator.serviceWorker.getRegistrations().then(function (rs) {
                rs.forEach(function (r) {
                    var w = r.active || r.waiting || r.installing;
                    if (!w) return;

                    // A site that cannot notify has nothing to bridge, and wrapping it is not free:
                    // the wrap changes the worker's bytes, so a frontend watching for that tells the
                    // user there is an update. Home Assistant does exactly that, on a launcher never
                    // granted permission in the first place.
                    if (!mayNotify()) {
                        post({ __ll: 'swSkipped', reason: 'the site has no notification permission' });
                        return;
                    }

                    // No type, because the ServiceWorker interface does not carry one and guessing
                    // is not free here: a module worker served the classic wrap throws on
                    // importScripts while it is being evaluated, which fails the install and leaves
                    // the origin with no worker at all. The host treats an unknown type as "do not
                    // wrap" and logs the announcement, so this is a note that the site has a worker
                    // rather than an instruction to replace it. The type is known where it is
                    // knowable: the register() above is handed one.
                    announce(w.scriptURL, '').then(function () {
                        try { r.update(); } catch (e) { }

                        // An unwrapped worker on a site that can notify. update() will not pick it
                        // up - nothing that reuses what the browser already has will - so it is
                        // adopted, once, or left alone forever.
                        isBridged(w).then(function (ours) { if (!ours) adopt(r, w); });
                    });
                });
            }).catch(function () { });
            }

            sweep();

            // Again if permission arrives later, which on a first visit it usually does: the page
            // asks, the user answers in the flyout's prompt bar, and by then the sweep above has
            // already decided there was nothing here to bridge. Without this the launcher stays
            // silent for the rest of the session with nothing saying why.
            try {
                navigator.permissions.query({ name: 'notifications' }).then(function (status) {
                    status.onchange = function () { if (status.state === 'granted') sweep(); };
                }).catch(function () { });
            } catch (e) { }

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
                    var href = l.getAttribute('href') || '';
                    var declared = sizeOf(l.getAttribute('sizes'));

                    // An .ico is a container, and what it holds is not declared anywhere: a plain
                    // favicon.ico routinely carries a 256px frame alongside the 16 and 32 the tab
                    // uses. Discord is the case that proved it - no manifest, no apple-touch-icon,
                    // one <link rel=icon> to favicon.ico, and a 256px PNG inside it. Guessing high
                    // enough to be fetched is the only way to find out; the host measures what
                    // actually arrives and will not shrink what it already has, so guessing wrong
                    // costs a download and nothing else. Below a declared manifest icon on purpose,
                    // so a site that does say what it has still wins.
                    var isIco = /\.ico($|[?#])/i.test(href);
                    consider(href, declared || (isIco ? 128 : 32));
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
                // Every path says what happened. This ran silently for a long time and the only
                // symptom was a launcher that stayed blurry, which is indistinguishable from the
                // site simply not offering anything better.
                function probe(stage, best, extra) {
                    post({
                        __ll: 'pageIconProbe', stage: stage,
                        size: (best && best.size) || 0, url: (best && best.url) || '',
                        detail: extra || ''
                    });
                }

                bestIconUrl().then(function (best) {
                    // Not worth replacing Chromium's own favicon with something no larger.
                    if (!best.url || best.size < 96) {
                        probe('none', best);
                        return;
                    }

                    return fetch(best.url, { credentials: 'include' })
                        .then(function (r) { return r.ok ? r.blob() : null; })
                        .then(function (b) {
                            // Rasters only: a manifest icon is often an SVG, which nothing
                            // downstream of here can decode.
                            if (!b) { probe('unfetchable', best); return; }
                            if (b.size > 512 * 1024) { probe('toobig', best, String(b.size)); return; }
                            if (b.type.indexOf('svg') >= 0) { probe('svg', best, b.type); return; }

                            var fr = new FileReader();
                            fr.onload = function () {
                                probe('sending', best, b.type);
                                post({ __ll: 'pageIcon', icon: fr.result, size: best.size });
                            };
                            fr.readAsDataURL(b);
                        });
                }).catch(function (e) { probe('threw', null, String(e)); });
            }

            if (document.readyState === 'complete') setTimeout(reportBestIcon, 0);
            else window.addEventListener('load', function () { setTimeout(reportBestIcon, 0); });

            // The worker cannot construct a Notification, so it asks the page to.
            navigator.serviceWorker.addEventListener('message', function (ev) {
                var d = ev.data;
                if (!d || !d.__ll) return;

                // The worker closed one of its own notifications. The shim that raised the toast
                // for it lives here, so closing it there is what reaches the host — and prunes the
                // shim's map on the way. A page reloaded since the notification was raised has no
                // entry left for it, and the toast is still up, so that case tells the host direct.
                if (d.__ll === 'notifyClose') {
                    var closed = false;
                    try {
                        if (window.Notification && window.Notification.__close)
                            closed = window.Notification.__close(d.tag);
                    } catch (e) { }
                    if (!closed) post({ __ll: 'notifyClose', tag: d.tag });
                    return;
                }

                if (d.__ll !== 'notify') return;
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
    private async Task InstallServiceWorkerBridgeAsync(CoreWebView2 core, WebTab tab)
    {
        try
        {
            // The tab is captured rather than looked up from the event's sender. Every other
            // per-tab handler on this browser does the same (see the FaviconChanged wiring), and
            // the reason is that a lookup here has to match a CoreWebView2 handed back by an event
            // against the one held on the tab, which is an identity comparison across a COM
            // boundary and not one worth betting a feature on.
            core.WebMessageReceived += (_, e) => OnBridgeMessageReceived(core, tab, e);
            // Captured, not taken from the event's sender, for the same reason as the line above:
            // the handler has to find this browser's watched-script set in a dictionary keyed by
            // CoreWebView2, and matching an instance handed back by an event against the one used
            // as the key is an identity comparison across a COM boundary. It does not hold. That
            // lookup failed on every single request, so the wrap was never served and the worker
            // half of the bridge quietly did nothing at all - for every site, since it shipped.
            // Wired for every launcher. There is no switch on this and deliberately so: whether a
            // site notifies from its page or from a worker is an implementation detail of that
            // site, so a per-launcher toggle asks the user something they cannot answer, and a
            // launcher whose notifications silently depend on a setting nobody found is the failure
            // this area kept repeating. The decisions that matter are made from what can actually
            // be observed - see the sweep in ServiceWorkerBridgeScript, which bridges a site only
            // where there is something to gain and nothing installed to disturb.
            core.WebResourceRequested += (_, e) => OnServiceWorkerResourceRequested(core, e);

            await core.AddScriptToExecuteOnDocumentCreatedAsync(ServiceWorkerBridgeScript);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Installing the service worker bridge failed for launcher {Name}", _launcher.Name);
        }
    }

    private void OnBridgeMessageReceived(CoreWebView2 sender, WebTab tab, CoreWebView2WebMessageReceivedEventArgs e)
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
            string type = message?["type"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(url)) WatchServiceWorkerScript(sender, url, type);
        }
        else if (kind == "swAdopt")
        {
            bool ok = message?["ok"]?.GetValue<bool>() == true;
            string url = message?["url"]?.GetValue<string>() ?? "";

            if (ok)
                Logger.Info("Adopted {Name}'s existing service worker; it now runs wrapped: {Url}", _launcher.Name, url);
            else
                Logger.Warn("Could not adopt {Name}'s service worker: {Url} ({Error}). {State}",
                    _launcher.Name, url, message?["error"]?.GetValue<string>() ?? "",
                    message?["removed"]?.GetValue<bool>() == true
                        ? "It had already been unregistered, so the site has none until it registers one again."
                        : "Left as it was.");
        }
        else if (kind == "swSkipped")
        {
            Logger.Info("Not bridging {Name}'s service worker: {Reason}",
                _launcher.Name, message?["reason"]?.GetValue<string>() ?? "");
        }
        else if (kind == "pageIcon")
        {
            AdoptHighResPageIcon(tab, message);
        }
        else if (kind == "pageIconProbe")
        {
            Logger.Info("Icon probe for {Name}: {Stage} {Size}px {Url} {Detail}",
                _launcher.Name,
                message?["stage"]?.GetValue<string>() ?? "",
                message?["size"]?.GetValue<int>() ?? 0,
                message?["url"]?.GetValue<string>() ?? "",
                message?["detail"]?.GetValue<string>() ?? "");
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
        else if (kind == "notifyRaised")
        {
            // Deliberately its own line rather than folded into the toast's. A raise with no
            // "shown" following it means the notification was lost between the page and Windows,
            // which is ours; no raise at all means the page never notified, which is not.
            Logger.Info("Notification: {Name}'s page raised one (tag {Tag}, icon {Icon})",
                _launcher.Name,
                message?["tag"]?.GetValue<string>() ?? "",
                message?["hasIcon"]?.GetValue<bool>() == true ? "yes" : "none");
        }
        else if (kind == "notify")
        {
            ShowNotificationToast(sender, message!);
        }
        else if (kind == "notifyClose")
        {
            string? tag = message?["tag"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(tag)) CloseNotificationToast(tag);
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
    private void WatchServiceWorkerScript(CoreWebView2 core, string url, string type)
    {

        bool isModule = string.Equals(type, "module", StringComparison.Ordinal);
        bool isClassic = string.Equals(type, "classic", StringComparison.Ordinal);

        // Kept both ways round, so a site that switches its worker from a module to a classic one
        // at the same URL is not still served an `import` that a classic worker cannot parse.
        if (isModule) _moduleWorkerScripts.Add(url);
        else if (isClassic) _moduleWorkerScripts.Remove(url);

        // Neither: the page found an already-installed worker and the ServiceWorker interface does
        // not say how it was registered. Guessing is what makes this dangerous rather than merely
        // ineffective - the wrong wrap throws while the worker is being evaluated, which fails the
        // install and leaves the origin with no worker at all. So it is logged and left alone; no
        // filter, nothing to serve. Every worker whose type is knowable is announced by the
        // patched register(), which is handed one.
        if (!isModule && !isClassic)
        {
            Logger.Info("Service worker seen for {Name}, left alone (type unknown): {Url}",
                _launcher.Name, url);
            AcknowledgeServiceWorkerScript(core, url);
            return;
        }

        if (!_serviceWorkerScripts.TryGetValue(core, out var watched))
            _serviceWorkerScripts[core] = watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!watched.Add(url))
        {
            AcknowledgeServiceWorkerScript(core, url);
            return;
        }

        // Info rather than Debug, and worth the line: this happens once per worker per browser, and
        // it is the only outward sign that a launcher's notifications are bridged at all. A site
        // that never appears here never announced a worker, which is a different problem entirely
        // from one whose wrap failed.
        Logger.Info("Service worker announced for {Name}: {Url} ({Type})",
            _launcher.Name, url, type);

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

    private void OnServiceWorkerResourceRequested(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string uri = e.Request.Uri;

        if (!_serviceWorkerScripts.TryGetValue(core, out var watched) || !watched.Contains(uri)) return;

        // Anything but the browser fetching the worker's top-level script is left alone — above all
        // the wrap's own importScripts, come back for the real thing. Letting that through untouched
        // is the whole point: it is the browser's own fetch, with the profile's cookies on it.
        //
        // Logged rather than silent, because "declined every time" and "wrapped correctly" differ
        // only by the absence of a line, and the whole bridge rests on these headers reading the way
        // the spec says they do. A handful of lines per worker, and the normal one is the import.
        if (!IsWorkerMainScriptRequest(e.Request))
        {
            Logger.Info("Not wrapping a fetch of {Url} for {Name}: {Dest}",
                uri, _launcher.Name, DescribeRequestDestination(e.Request));
            return;
        }

        // Building the response body is asynchronous, so the request has to be held open — a
        // handler that returns without either a response or a deferral has declined to interfere.
        var deferral = e.GetDeferral();
        _ = RespondWithWrappedScriptAsync(core, e, uri, deferral);
    }

    /// <summary>
    /// Whether this request is the browser fetching a worker's own top-level script, as opposed to
    /// the wrap's <c>importScripts</c>/<c>import</c> of the original, or anything else that happens
    /// to ask for the same URL.
    /// </summary>
    /// <remarks>
    /// <para><b>This has to be read off the request, not remembered.</b> It used to be a table: the
    /// wrap's URL was noted when the wrap was served, and the next request for exactly that URL was
    /// taken to be the inner fetch and passed through. That holds only while one fetch is in flight,
    /// and a worker script is very often fetched twice at once — a site's own <c>register()</c>
    /// alongside the <c>update()</c> the page sweep calls, or two tabs on the same origin. The
    /// second main fetch then consumed the note meant for the first, whose <c>importScripts</c>
    /// arrived to find nothing armed and <b>was served the wrap again</b>. That recurses until the
    /// stack gives out: <c>Maximum call stack size exceeded</c>, the worker never evaluates, and the
    /// registration is torn down — a site left with no worker at all, which reads as the site being
    /// broken rather than as anything to do with us. Messenger hit it every time; WhatsApp, which
    /// registers once, never did.</para>
    /// <para>Both headers are part of the fetch, and only the top-level script fetch carries either:
    /// <c>Service-Worker: script</c> is specified for that request alone, and Fetch Metadata gives
    /// it <c>Sec-Fetch-Dest: serviceworker</c> where an imported script gets <c>script</c>. Absence
    /// is therefore a real answer rather than a gap to guess around: a service worker requires a
    /// secure context, and a secure context is exactly where Chromium sends Fetch Metadata.</para>
    /// </remarks>
    private static bool IsWorkerMainScriptRequest(CoreWebView2WebResourceRequest request)
    {
        try
        {
            var headers = request.Headers;

            if (headers.Contains("Service-Worker")
                && string.Equals(headers.GetHeader("Service-Worker"), "script", StringComparison.OrdinalIgnoreCase))
                return true;

            return headers.Contains("Sec-Fetch-Dest")
                && string.Equals(headers.GetHeader("Sec-Fetch-Dest"), "serviceworker", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A header collection that cannot be read is not grounds for serving a wrap that might
            // import itself. Left alone, the site keeps its own worker and only the bridge is lost.
            return false;
        }
    }

    /// <summary>What a declined request said it was for, for the log line above.</summary>
    private static string DescribeRequestDestination(CoreWebView2WebResourceRequest request)
    {
        try
        {
            var headers = request.Headers;
            string destination = headers.Contains("Sec-Fetch-Dest")
                ? headers.GetHeader("Sec-Fetch-Dest")
                : "no Sec-Fetch-Dest";

            return headers.Contains("Service-Worker")
                ? destination + ", Service-Worker: " + headers.GetHeader("Service-Worker")
                : destination;
        }
        catch
        {
            return "its headers could not be read";
        }
    }

    /// <summary>The site's own script URL, with the adoption walk's marker removed.</summary>
    /// <remarks>
    /// Adoption registers the script once under a marked URL so the browser treats it as one it has
    /// not got and actually fetches it. The wrap served for that URL still has to pull in the
    /// *real* script, so the marker comes off here rather than being passed back to the site.
    /// </remarks>
    private static string WithoutAdoptionMarker(string uri) => uri
        .Replace("?" + AdoptionMarker + "&", "?", StringComparison.Ordinal)
        .Replace("&" + AdoptionMarker, "", StringComparison.Ordinal)
        .Replace("?" + AdoptionMarker, "", StringComparison.Ordinal);

    /// <summary>The query the adoption walk adds to force a fetch. Kept in one place.</summary>
    private const string AdoptionMarker = "llbridge=1";

    private async Task RespondWithWrappedScriptAsync(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs e,
        string uri,
        global::Windows.Foundation.Deferral deferral)
    {
        try
        {
            // What the wrap should pull in is the site's own script, which is this URL with the
            // adoption marker taken back off - the outward hop of the walk registers
            // "<script>?llbridge=1", and asking the site for *that* is what broke Messenger, whose
            // server does not serve `sw?s=push` and `sw?s=push&llbridge=1` alike.
            string original = WithoutAdoptionMarker(uri);

            // Classic workers pull the original in with importScripts, which does not exist in a
            // module worker; a module pulls it in with a static import, which does not exist in a
            // classic one. Serving the wrong one fails the install and takes the whole registration
            // with it. See _moduleWorkerScripts.
            bool isModule = _moduleWorkerScripts.Contains(uri);

            string script = isModule
                ? "import " + JsonSerializer.Serialize(original) + ";\n" + ServiceWorkerShimScript
                : ServiceWorkerShimScript + "\nimportScripts(" + JsonSerializer.Serialize(original) + ");\n";

            Logger.Info("Wrapping service worker for {Name}: {Url} ({Type})",
                _launcher.Name, uri, isModule ? "module" : "classic");

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
