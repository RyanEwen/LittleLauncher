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
/// host) and served as the shim followed by the site's own script, <b>inlined</b>.</para>
/// <para><b>Inlined, and never imported.</b> The wrap used to end in <c>importScripts</c> of the
/// original, which cannot work when it is served at that same URL: a worker's <c>importScripts</c>
/// resolves against the registration's script resource map, and the main script is already in that
/// map under its own URL as the wrap — so the wrap imported itself until the stack gave out, with
/// no request made and nothing for the host to intercept or fix. It only ever appeared to work
/// through adoption, which registers a *marked* URL and so imported a different one. See
/// <see cref="TryFetchOriginalScriptAsync"/>.</para>
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
    /// Both realms now get the original inlined rather than imported, so the difference is only one
    /// of <b>order</b>: a module's own <c>import</c> declarations are hoisted and its body expects to
    /// run first, so the shim goes after it, while a classic worker gets the patch in place before
    /// anything it does. The type still has to be known, because it travels with the announcement
    /// and a worker whose type is unknown is not wrapped at all.
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
    /// Stamped into the served wrap, and **bumped whenever the wrap's behaviour changes**.
    /// </summary>
    /// <remarks>
    /// <para>An update check re-fetches a worker's top-level script and installs a new worker only
    /// if its <b>bytes differ</b>. That is ordinarily the point: it is what stops a launcher
    /// reinstalling a site's worker on every load. It becomes a trap when what is stored is
    /// <i>broken</i> — the v1 wrap imported itself and recursed to a stack overflow on every
    /// startup, and a corrected wrap that was textually identical to it would have changed nothing
    /// at all.</para>
    /// <para>Changing this constant changes the bytes, so the next update check installs the current
    /// wrap and a site poisoned by an older one heals without the user being told to go and clear
    /// anything.</para>
    /// </remarks>
    private const string WrapVersion = "4";

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
                    // The version travels with the answer, so a page can tell "wrapped, but by an
                    // older build" from "not wrapped at all". Silence means neither: a wrap old
                    // enough to predate this reply cannot be distinguished from no wrap, which is
                    // why what the host says about it is hedged.
                    try {
                        if (ev.source && ev.source.postMessage)
                            ev.source.postMessage({ __ll: 'pong', version: self.__llWrapVersion || '' });
                    } catch (e) { }
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

            // Substituted by the host with WrapVersion, so the page can tell a worker running the
            // current wrap from one running an older build of it.
            var CURRENT_WRAP = '__LL_WRAP_VERSION__';

            function post(m) { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch (e) { } }

            // Announcing is not enough on its own: the host must have its interception in place
            // *before* the browser fetches the script, and posting a message is asynchronous — the
            // first version of this raced and the worker installed unwrapped. So announcing returns
            // a promise the registration waits on. It fails open on a timeout, because a lost
            // acknowledgement must never stop a page registering its worker at all.
            var acks = {};
            var skipBody = {};

            // The site's own worker script, fetched *by the page*, and the reason the wrap no longer
            // pulls it in itself. A worker cannot importScripts its own URL - that resolves out of
            // the registration's script resource map, which already holds the wrap - and the host
            // fetching it instead gets a login page from anything that guards on more than cookies:
            // Messenger answered the app's request with HTML, and the worker died on `Unexpected
            // token '<'`. Here it is an ordinary same-origin credentialed fetch from the page that
            // is about to register it, which is as close to the browser's own request as it gets.
            // The marker header is how the host recognises this request on its way out and puts
            // `Service-Worker: script` on it. That header is what a site keys on to serve the worker
            // script rather than the app, and **a page cannot set it**: it is a forbidden header
            // name, so fetch() silently drops it. Without it Messenger answers this URL with 580KB
            // of its own HTML, and the app's own HttpClient — which can set it, but is not the
            // browser — gets an interstitial instead.
            //
            // **Bounded, because register() is waiting on it.** Without the race this holds the
            // site's own registration open for as long as the request takes, and a request that
            // never settles holds it forever — which looks exactly like the launcher hanging on
            // load. Failing here costs the bridge, never the worker.
            function fetchScript(href) {
                try {
                    var fetched = fetch(href, {
                        credentials: 'include',
                        cache: 'no-store',
                        headers: { 'X-LittleLauncher-Worker-Script': '1' }
                    })
                        .then(function (r) { return r.ok ? r.text() : ''; })
                        .catch(function () { return ''; });

                    var timedOut = new Promise(function (resolve) {
                        setTimeout(function () { resolve(''); }, 5000);
                    });

                    return Promise.race([fetched, timedOut]);
                } catch (e) {
                    return Promise.resolve('');
                }
            }

            // **Announce first, fetch second, and the order is the whole point.** The host puts its
            // resource filter in place when it is told about a script URL, and that filter is what
            // adds the header below. Fetching before announcing means the request goes out before
            // anything can touch it, which is exactly how the first version of this failed: the
            // fetch came back as the site's HTML every time and the injection never ran at all.
            function announce(url, type) {
                var href;
                try { href = new URL(url, location.href).href; } catch (e) { return Promise.resolve(); }

                return announceWith(href, type).then(function () {
                    // Only for a script that will actually be wrapped. The sweep announces with no
                    // type to say "there is a worker here", and the host does not wrap those.
                    if (!type) return;

                    // And not for one already known not to come back as a script. Messenger answers
                    // this URL with 580KB of its own HTML, and without this that download is repeated
                    // on every single load of a launcher that cannot be bridged anyway.
                    if (skipBody[href]) return;

                    return fetchScript(href).then(function (body) {
                        if (body) post({ __ll: 'swBody', url: href, body: body });
                    });
                });
            }

            function announceWith(href, type) {
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
            // Resolves with the wrap's version, '' for a wrap too old to report one, or null when
            // nothing answered at all. Five seconds rather than two: a sleeping worker has to be
            // started before it can receive the message, and calling a working launcher stale
            // because its worker was slow to wake is worse than waiting.
            function isBridged(worker) {
                return new Promise(function (resolve) {
                    var settled = false;
                    function onMessage(ev) {
                        if (!ev.data || ev.data.__ll !== 'pong') return;
                        settled = true;
                        navigator.serviceWorker.removeEventListener('message', onMessage);
                        resolve(ev.data.version || '');
                    }
                    navigator.serviceWorker.addEventListener('message', onMessage);
                    try { worker.postMessage({ __ll: 'ping' }); } catch (e) { }
                    setTimeout(function () {
                        if (settled) return;
                        navigator.serviceWorker.removeEventListener('message', onMessage);
                        resolve(null);
                    }, 5000);
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

                        // An unwrapped worker on a site that can notify. Nothing that reuses what
                        // the browser already has will pick the wrap up - not update(), not a
                        // reload - so the host is told, and offers to rebuild. Adoption is still
                        // tried first, for the sites it can still help.
                        // **Only an activated worker is asked, and only when nothing is being
                        // installed alongside it.** A worker still installing cannot answer a
                        // postMessage, and one that has just been registered is by definition the
                        // current wrap - so pinging mid-install reported "stale" on a launcher that
                        // had been rebuilt seconds earlier, which is the one flow this exists to
                        // support.
                        if (!r.active || r.installing || r.waiting) return;

                        isBridged(r.active).then(function (version) {
                            if (version === CURRENT_WRAP) return;
                            post({ __ll: 'swStale', url: r.active.scriptURL,
                                   version: version === null ? '' : (version || 'unversioned'),
                                   answered: version !== null });
                            if (version === null) adopt(r, r.active);
                        });
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
                    if (d.skipBody) skipBody[d.url] = true;
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

            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                ServiceWorkerBridgeScript.Replace("__LL_WRAP_VERSION__", WrapVersion, StringComparison.Ordinal));
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
            if (!string.IsNullOrEmpty(url)) _ = WatchServiceWorkerScriptAsync(sender, url, type);
        }
        else if (kind == "swBody")
        {
            // The script the page fetched, arriving after its announcement rather than with it -
            // the filter that lets the host mark that request has to exist before it is made.
            string url = message?["url"]?.GetValue<string>() ?? "";
            string body = message?["body"]?.GetValue<string>() ?? "";

            Logger.Info("The page fetched {Name}'s worker script {Url}: {Length} chars, starts {Start}",
                _launcher.Name, url, body.Length, Describe(body));

            if (!string.IsNullOrEmpty(url) && LooksLikeScript(body)) _serviceWorkerBodies[url] = body;
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
        else if (kind == "swStale")
        {
            NoteStaleWrap(
                message?["url"]?.GetValue<string>() ?? "",
                message?["version"]?.GetValue<string>() ?? "",
                message?["answered"]?.GetValue<bool>() == true);
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
    private async Task WatchServiceWorkerScriptAsync(CoreWebView2 core, string url, string type)
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

        // Armed before the acknowledgement, because the ack is what releases the page to fetch the
        // script and the interception has to already be running when that request goes out.
        if (!_unwrappableScripts.Contains(url))
            await InterceptWorkerScriptFetchAsync(core, url);

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


    /// <summary>
    /// Starts pausing requests for <paramref name="url"/> so the page's fetch of it can be given the
    /// header a page is not allowed to set.
    /// </summary>
    private async Task InterceptWorkerScriptFetchAsync(CoreWebView2 core, string url)
    {
        try
        {
            if (!_fetchInterceptUrls.TryGetValue(core, out var urls))
                _fetchInterceptUrls[core] = urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!urls.Add(url)) return;

            if (!_fetchReceivers.ContainsKey(core))
            {
                var receiver = core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
                receiver.DevToolsProtocolEventReceived += (_, e) => OnFetchRequestPaused(core, e);
                _fetchReceivers[core] = receiver;
            }

            // Re-enabled with the full set each time, because the patterns are given at enable time
            // and a second enable replaces the first rather than adding to it.
            var patterns = new JsonArray();
            foreach (string pattern in urls)
                patterns.Add(new JsonObject { ["urlPattern"] = pattern, ["requestStage"] = "Request" });

            await core.CallDevToolsProtocolMethodAsync(
                "Fetch.enable", new JsonObject { ["patterns"] = patterns }.ToJsonString());
        }
        catch (Exception ex)
        {
            // Not fatal: without it the page's fetch goes out unmarked, which for most sites is
            // still the real script and for the rest means the launcher is simply not bridged.
            Logger.Debug(ex, "Could not intercept the worker script fetch for {Name}", _launcher.Name);
        }
    }

    private void OnFetchRequestPaused(CoreWebView2 core, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        string requestId = "";
        JsonObject? continueWith = null;

        try
        {
            var parameters = JsonNode.Parse(e.ParameterObjectAsJson);
            requestId = parameters?["requestId"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(requestId)) return;

            var headers = parameters?["request"]?["headers"] as JsonObject;

            // Only the page's own fetch is marked. Anything else matching the pattern is resumed
            // exactly as it came in.
            bool ours = headers?.Any(h =>
                string.Equals(h.Key, PageFetchMarkerHeader, StringComparison.OrdinalIgnoreCase)) == true;

            if (ours && headers != null)
            {
                var rewritten = new JsonArray();
                foreach (var header in headers)
                {
                    if (string.Equals(header.Key, PageFetchMarkerHeader, StringComparison.OrdinalIgnoreCase))
                        continue;

                    rewritten.Add(new JsonObject
                    {
                        ["name"] = header.Key,
                        ["value"] = header.Value?.GetValue<string>() ?? "",
                    });
                }

                rewritten.Add(new JsonObject { ["name"] = "Service-Worker", ["value"] = "script" });

                continueWith = new JsonObject { ["requestId"] = requestId, ["headers"] = rewritten };
                Logger.Info("Marking {Name}'s page fetch of its worker script as a service-worker request",
                    _launcher.Name);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading a paused request failed for {Name}", _launcher.Name);
        }
        finally
        {
            // Always, on every path. A paused request that is never continued hangs, and this one is
            // a script a registration is waiting on.
            if (!string.IsNullOrEmpty(requestId))
            {
                try
                {
                    _ = core.CallDevToolsProtocolMethodAsync("Fetch.continueRequest",
                        (continueWith ?? new JsonObject { ["requestId"] = requestId }).ToJsonString());
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Could not resume a paused worker script request for {Name}", _launcher.Name);
                }
            }
        }
    }

    private void AcknowledgeServiceWorkerScript(CoreWebView2 core, string url)
    {
        try
        {
            core.PostWebMessageAsJson(new JsonObject
            {
                ["__ll"] = "swReady",
                ["url"] = url,
                ["skipBody"] = _unwrappableScripts.Contains(url),
            }.ToJsonString());
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

        // The page's own fetch of the script, on its way out. It is given the one header a page is
        // not allowed to set for itself and then left to go to the network as it is: what comes back
        // is the site's real worker script, fetched by the browser with everything the browser has.
        // See the note in fetchScript.
        if (ReadHeader(e.Request, PageFetchMarkerHeader).Length > 0)
        {
            try
            {
                e.Request.Headers.RemoveHeader(PageFetchMarkerHeader);
                e.Request.Headers.SetHeader("Service-Worker", "script");
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not mark the page's worker-script fetch for {Name}", _launcher.Name);
            }

            // No response set, so the request continues to the site with the header added.
            return;
        }

        // Anything but the browser fetching the worker's top-level script is left alone — above all
        // the wrap's own importScripts, come back for the real thing. Letting that through untouched
        // is the whole point: it is the browser's own fetch, with the profile's cookies on it.
        string marker = ReadHeader(e.Request, "Service-Worker");
        bool isMainScript = string.Equals(marker, "script", StringComparison.OrdinalIgnoreCase);

        // Both header values, on both branches, on every request. The distinction below is the one
        // thing standing between the wrap and importing itself, and getting it wrong is not visible
        // in any other way: an over-eager test recurses until the stack gives out, and an over-shy
        // one silently bridges nothing. Guessing at it cost two builds; this is a handful of lines
        // per worker and it settles the question from the log alone.
        Logger.Info("Worker script fetch for {Name}: {Url} (Service-Worker: {Marker}, Sec-Fetch-Dest: {Dest}) - {Decision}",
            _launcher.Name, uri,
            string.IsNullOrEmpty(marker) ? "absent" : marker,
            ReadHeader(e.Request, "Sec-Fetch-Dest") is { Length: > 0 } dest ? dest : "absent",
            isMainScript ? "wrapping it" : "left alone");

        if (!isMainScript) return;

        // Building the response body is asynchronous, so the request has to be held open — a
        // handler that returns without either a response or a deferral has declined to interfere.
        var deferral = e.GetDeferral();
        _ = RespondWithWrappedScriptAsync(core, e, uri, deferral);
    }

    /// <summary>
    /// How the page's own fetch of a worker script announces itself, so the host can add the one
    /// header the page is forbidden from setting.
    /// </summary>
    /// <remarks>
    /// <c>Service-Worker: script</c> is a forbidden header name, so <c>fetch()</c> drops it
    /// silently, and it is exactly what a site keys on to serve the worker script instead of the
    /// app. Messenger answers <c>/sw?s=push</c> without it with 580KB of its own HTML. The app's own
    /// <c>HttpClient</c> can set it and still gets an interstitial, because it is not the browser -
    /// so the request has to be the browser's, with the header put on as it passes.
    /// </remarks>
    private const string PageFetchMarkerHeader = "X-LittleLauncher-Worker-Script";

    /// <summary>
    /// Script URLs that have already come back as something other than a script, so neither the page
    /// nor the host asks for them again this session.
    /// </summary>
    /// <remarks>
    /// Messenger serves <c>/sw?s=push</c> as 580KB of its own HTML to anything but the browser's
    /// genuine service-worker request, which cannot be reproduced. Without this the launcher
    /// downloads that on every load, forever, to reach the same conclusion.
    /// </remarks>
    private readonly HashSet<string> _unwrappableScripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Browsers with DevTools <c>Fetch</c> interception running, and the URLs it covers.</summary>
    /// <remarks>
    /// <para><b>Why the DevTools protocol and not the resource handler.</b> The page's own fetch of
    /// a worker script has to carry <c>Service-Worker: script</c> or a site like Messenger answers it
    /// with the app's HTML instead. A page cannot set that header — it is a forbidden header name,
    /// so <c>fetch()</c> drops it — and putting it on through
    /// <c>CoreWebView2WebResourceRequestedEventArgs.Request.Headers</c> did not reach the wire
    /// either: the body came back as HTML regardless. <c>Fetch.continueRequest</c> is under no such
    /// restriction, because it is the same channel DevTools itself edits requests on.</para>
    /// <para><b>Scoped to exactly the worker script URLs, and it always continues.</b> An
    /// intercepted request that is never continued simply hangs, and this one is a script a
    /// registration is waiting on — so every path out of the handler resumes the request, including
    /// the ones that failed to parse it.</para>
    /// </remarks>
    private readonly Dictionary<CoreWebView2, HashSet<string>> _fetchInterceptUrls = new();

    /// <summary>Kept alive deliberately: dropping the receiver stops the events arriving.</summary>
    private readonly Dictionary<CoreWebView2, CoreWebView2DevToolsProtocolEventReceiver> _fetchReceivers = new();

    /// <summary>
    /// Script URLs this session has served the current wrap for, so a stale report for one is
    /// already out of date by the time it is read.
    /// </summary>
    private readonly HashSet<string> _wrappedThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Worker scripts as the page fetched them, keyed by script URL.</summary>
    /// <remarks>
    /// The page's fetch is preferred over the host's because it is the browser's own credentialed,
    /// same-origin request. Messenger answers an <c>HttpClient</c> asking for <c>sw?s=push</c> with
    /// an HTML login page, which inlined into the wrap gave the worker
    /// <c>Uncaught SyntaxError: Unexpected token '&lt;'</c>.
    /// </remarks>
    private readonly Dictionary<string, string> _serviceWorkerBodies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this is plausibly a script rather than a page telling us to sign in.
    /// </summary>
    /// <remarks>
    /// Deliberately crude and deliberately present. Anything that guards its worker script can
    /// answer a request the browser did not make with a 200 and an HTML body, and inlining that
    /// leaves the site with a worker that cannot parse — strictly worse than not bridging it at all.
    /// A leading <c>&lt;</c> is what that always looks like.
    /// </remarks>
    private static bool LooksLikeScript(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        string start = body.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return start.Length > 0 && start[0] != '<';
    }

    /// <summary>
    /// Records that a launcher's worker is not the current wrap. **Deliberately only a log line.**
    /// </summary>
    /// <remarks>
    /// <para>This offered a rebuild in the prompt bar, and it was removed after firing wrongly four
    /// times in a row: on a launcher rebuilt seconds earlier (it pinged a worker still installing,
    /// which cannot answer), on one that had just been granted permission (two notification prompts
    /// back to back), and twice on launchers whose workers the sweep went on to wrap by itself
    /// moments later.</para>
    /// <para>That last one is the reason it cannot be repaired by tightening the check. **The sweep
    /// reports and then repairs, in that order**: it announces, pings, and where the worker is not
    /// ours it adopts — and adoption re-registers, which is what produces the wrap. Measured at 38
    /// seconds between the report and the wrap on Messages, and four minutes on Teams. Anything that
    /// prompts on the report is racing the repair it is recommending, so it is right only when
    /// adoption fails and there is no way to know that at the moment of asking.</para>
    /// <para>The finding is still worth having, and the log is where it belongs: it says which
    /// launchers are running an older wrap, and <b>Rebuild notification bridge</b> is on the menu
    /// for anyone who wants to act on it.</para>
    /// </remarks>
    private void NoteStaleWrap(string url, string version, bool answered)
    {
        if (_unwrappableScripts.Contains(url) || _wrappedThisSession.Contains(url)) return;

        Logger.Info("{Name}'s service worker is not the current wrap ({State}): {Url}",
            _launcher.Name,
            answered ? "running " + (version == "unversioned" ? "a build too old to say" : "v" + version) : "did not answer",
            url);
    }

    /// <summary>The first line or so of a body, flattened onto one log line.</summary>
    private static string Describe(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "(nothing)";

        string start = body.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (start.Length > 120) start = start[..120];
        return "\"" + start.Replace('\r', ' ').Replace('\n', ' ') + "\"";
    }

    /// <summary>Fetches the site's own worker script, or null if it could not be had.</summary>
    /// <remarks>
    /// <para><b>Why the wrap no longer pulls the original in itself.</b> It used to end in
    /// <c>importScripts</c> of the script's own URL, and <b>that can never work</b>: a worker's
    /// <c>importScripts</c> resolves against the registration's script resource map, and the main
    /// script is already in that map under its own URL — as the wrap. So the wrap imported itself,
    /// every time, until <c>Maximum call stack size exceeded</c>. No request was ever made, which is
    /// why nothing on the host side could see or fix it: not a header rule, not <c>no-store</c>, not
    /// clearing the HTTP cache, not unregistering.</para>
    /// <para>It only ever appeared to work because of adoption. That walk registers a *marked* URL,
    /// so the wrap sat at <c>sw.js?llbridge=1</c> and imported <c>sw.js</c> — two different URLs, no
    /// self-reference. Every site that registered its own worker plainly, at a URL adoption declines
    /// to mark, was silently broken by the bridge: WhatsApp worked, Messenger never could.</para>
    /// <para><b>So the original is fetched here and inlined.</b> No import, no second URL, nothing
    /// for a script map to resolve back onto the wrap. The cost is that this request is the app's,
    /// not the browser's, so the profile's cookies are read and sent with it — a worker script
    /// behind a login is common and losing it would be worse than the bug this replaces.</para>
    /// <para><b>Failure declines the wrap rather than guessing.</b> Returning null leaves the
    /// response unset, so the request goes to the site untouched and it gets its own worker. A
    /// launcher with no bridge misses worker-scope notifications; a launcher served half a wrap has
    /// no working worker at all.</para>
    /// </remarks>
    private async Task<string?> TryFetchOriginalScriptAsync(CoreWebView2 core, string url)
    {
        try
        {
            string cookieHeader = "";
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(url);
                cookieHeader = string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));
            }
            catch (Exception ex)
            {
                // Worth trying anyway: plenty of worker scripts are served to anyone who asks.
                Logger.Debug(ex, "Could not read cookies for {Url}", url);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            // The header that tells the site this is a worker's top-level script request. Some
            // servers vary on it, and the browser would have sent it.
            request.Headers.TryAddWithoutValidation("Service-Worker", "script");
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            try
            {
                if (!string.IsNullOrEmpty(core.Settings.UserAgent))
                    request.Headers.TryAddWithoutValidation("User-Agent", core.Settings.UserAgent);
            }
            catch { }

            using var response = await WorkerScriptClient.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            Logger.Info("Fetched {Name}'s worker script {Url} for inlining: {Status} {Type}, {Length} chars, cookies {Cookies}",
                _launcher.Name, url, (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "no content-type",
                body.Length,
                string.IsNullOrEmpty(cookieHeader) ? "none" : "sent");

            return response.IsSuccessStatusCode ? body : null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Not wrapping {Name}'s service worker: {Url} could not be fetched. The site keeps its own worker.",
                _launcher.Name, url);
            return null;
        }
    }

    /// <summary>Shared client for the fetch above. Redirects followed; cookies supplied by hand.</summary>
    private static readonly HttpClient WorkerScriptClient = new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>One request header, or empty where it is absent or cannot be read.</summary>
    /// <remarks>
    /// <para><b>Which header tells the wrap's own fetch apart from a real one.</b> It is
    /// <c>Service-Worker: script</c>, and only that. The Update algorithm puts it on the request for
    /// a worker's top-level script and on nothing else - it exists precisely so a server can tell
    /// that request apart - so its absence is a real answer rather than a gap to guess around.</para>
    /// <para><b>Not <c>Sec-Fetch-Dest</c>, which was tried on the reasoning that the main script
    /// would be <c>serviceworker</c> and an imported one <c>script</c>.</b> Measured against
    /// Messenger, Chromium sends it on <b>neither</b> — a worker script request carries
    /// <c>Service-Worker: script</c> and no Fetch Metadata at all — so a test that fell back to it
    /// was reading a header that is never there.</para>
    /// <para><b>The wrap's own <c>importScripts</c> is never seen here either.</b> Not once, across
    /// repeated installs, with the filter set to
    /// <see cref="CoreWebView2WebResourceRequestSourceKinds.All"/>: an imported script is resolved
    /// through the registration's script resource map rather than as a request the host is offered.
    /// So this test guards against something that cannot currently reach it, and is kept for exactly
    /// that reason — the cost of being wrong is not a missed bridge but the recursion below, and
    /// nothing about that resolution is promised by an API.</para>
    /// <para><b>What the recursion does, since it is not obvious from the symptom.</b> The wrap ends
    /// in <c>importScripts</c> of the original; serve the wrap to that and it imports itself,
    /// forever, until <c>Maximum call stack size exceeded</c>. The worker never evaluates and the
    /// registration is torn down, so the origin is left with <b>no worker at all</b> and a console
    /// that blames the site.</para>
    /// <para><b>And it outlives the bug that caused it</b>, which is the part that cost the most
    /// time. Both halves are stored in the script resource map, so a poisoned registration recurses
    /// from there on every startup with no fetch for a corrected host to intercept. Fixing the
    /// serving rule changed nothing on a machine that already had one; only different bytes do, and
    /// that is what <see cref="WrapVersion"/> is for.</para>
    /// <para>The predecessor to this test was a pass-through table, noting the URL when the wrap was
    /// served and letting the next request for it through. That holds only while one fetch is in
    /// flight, and a worker script is very often fetched twice at once: a site's own
    /// <c>register()</c> alongside the <c>update()</c> the page sweep calls, or two tabs on the same
    /// origin. It is what poisoned Messenger in the first place.</para>
    /// </remarks>
    private static string ReadHeader(CoreWebView2WebResourceRequest request, string name)
    {
        try
        {
            var headers = request.Headers;
            return headers.Contains(name) ? headers.GetHeader(name) ?? "" : "";
        }
        catch
        {
            return "";
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

            // The site's own script, inlined rather than pulled in by the wrap. Preferably the copy
            // the *page* fetched, because that is the browser's own credentialed request; the host's
            // own fetch is the fallback for a script announced before a page could get it. If
            // neither yields something that is actually a script, nothing is served at all: the
            // request goes to the site untouched and the launcher simply has no worker bridge, which
            // is a great deal better than a worker that will not run.
            // Already established that this one cannot be had. Nothing about that changes within a
            // session, so the request goes straight to the site rather than being asked for twice.
            if (_unwrappableScripts.Contains(original) || _unwrappableScripts.Contains(uri)) return;

            string? originalBody = null;
            if (_serviceWorkerBodies.TryGetValue(original, out string? fromPage) && LooksLikeScript(fromPage))
                originalBody = fromPage;
            else if (_serviceWorkerBodies.TryGetValue(uri, out string? fromPageAtUri) && LooksLikeScript(fromPageAtUri))
                originalBody = fromPageAtUri;

            originalBody ??= await TryFetchOriginalScriptAsync(core, original);

            if (!LooksLikeScript(originalBody))
            {
                // The opening of whatever did come back, because "not a script" has several very
                // different causes and they are indistinguishable without it: an HTML login page, an
                // error page, an empty body from a fetch that was blocked. Bounded and one line.
                // Remembered, so the page is told not to fetch it again. Messenger's answer to this
                // URL is 580KB of HTML, and repeating that on every load of a launcher that cannot
                // be bridged is a cost with no possible benefit.
                _unwrappableScripts.Add(original);
                _unwrappableScripts.Add(uri);

                Logger.Warn("Not wrapping {Name}'s service worker: {Url} did not come back as a script ({Length} chars, starts {Start}). The site keeps its own worker.",
                    _launcher.Name, original,
                    originalBody?.Length ?? 0,
                    Describe(originalBody));
                return;
            }

            string banner = "// little-launcher service worker bridge v" + WrapVersion + "\n";

            // Order differs by realm for the same reason it always did: a module's own imports are
            // hoisted and its body expects to run first, while a classic worker wants the patch in
            // place before anything it does.
            string script = isModule
                ? banner + originalBody + "\n;\n" + ServiceWorkerShimScript
                : banner + ServiceWorkerShimScript + "\n;\n" + originalBody;

            // Noted so a stale-wrap report for it can be ignored: whatever the page's sweep saw,
            // this launcher's worker is being replaced with the current wrap right now.
            _wrappedThisSession.Add(uri);
            _wrappedThisSession.Add(original);

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
                //
                // **no-store, and it is load-bearing.** The wrap ends in importScripts of this very
                // URL, and that request is never offered to this handler - not once, measured
                // across many installs. Left to itself that is fine, because the browser then goes
                // to the network and gets the site's real script. It stops being fine the moment a
                // copy of the wrap is sitting in the HTTP cache under the same URL: the import is
                // answered from there, so the wrap imports itself, recursing until the stack gives
                // out. `no-cache` was not enough - it means revalidate before reuse, which still
                // permits storing, and the entry it stored was us.
                "Content-Type: text/javascript\r\nCache-Control: no-store\r\nService-Worker-Allowed: /");
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
