// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using global::Windows.Graphics;
using WinRT.Interop;

namespace LittleLauncher.Windows;

/// <summary>
/// One browser extension's popup, in a window of its own.
/// </summary>
/// <remarks>
/// <para>This is the panel a browser would drop under its toolbar button. WebView2 has no such
/// toolbar and no browser-action API, so the popup is opened the only way it can be: it is an
/// ordinary page at <c>chrome-extension://{id}/{page}</c>, and a WebView2 pointed at it renders it.
/// </para>
/// <para><b>On the launcher's own profile, which is the whole trick.</b> An extension's popup talks
/// to the extension — its storage, its rules, its message port — and that only exists in the profile
/// the extension was installed into. Pointed at any other user-data folder it would load, look
/// right, and behave as though the extension had never run. So the caller passes the launcher's
/// folder through and the same <c>AreBrowserExtensionsEnabled</c> option is used, since the options
/// must match across every environment on a folder.</para>
/// <para>Sized generously and fixed: a popup declares its own size in CSS and there is no API to ask
/// it, so the window is a comfortable box rather than a guess that clips somebody's panel.</para>
/// <para><b>It answers two callers.</b> The header's extension button knows the page it wants and
/// calls <see cref="ShowAsync"/>. An extension asking for a window of its own arrives as a
/// new-window request, and that caller needs the browser itself to hand back to WebView2, so
/// <see cref="AdoptAsync"/> builds the window and returns it with its <c>CoreWebView2</c> ready and
/// unnavigated. See <c>WebFlyoutWindow.AdoptExtensionWindowAsync</c> for why both halves of that
/// are load-bearing.</para>
/// </remarks>
public sealed class ExtensionPopupWindow : Window
{
    private const int WindowWidthDips = 420;
    private const int WindowHeightDips = 560;

    private readonly TaskCompletionSource<bool> _completion = new();

    /// <summary>The browser, once it exists. Null when it could not be started.</summary>
    private readonly TaskCompletionSource<CoreWebView2?> _ready = new();

    private readonly IntPtr _hwnd;
    private readonly WebView2 _webView = new();

    /// <summary>True once something has finished loading here. See <see cref="LoadIfIdle"/>.</summary>
    private bool _navigated;

    /// <summary>Opens the popup and completes when it closes.</summary>
    public static Task ShowAsync(
        string name,
        string url,
        string userDataFolder,
        IntPtr ownerHwnd = default,
        Action<Window>? onCreated = null)
    {
        var window = new ExtensionPopupWindow(name, url, userDataFolder, ownerHwnd);
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    /// <summary>
    /// Opens an empty popup window and hands it back once its browser exists, unnavigated.
    /// </summary>
    /// <remarks>
    /// For a caller that has to give the browser to
    /// <c>CoreWebView2NewWindowRequestedEventArgs.NewWindow</c> rather than navigate it. The window
    /// rather than the browser is returned, so that caller can also ask, a moment later, whether
    /// anything actually loaded: see <see cref="LoadIfIdle"/>. Returns null when the browser could
    /// not be started, in which case the window has closed itself.
    /// </remarks>
    public static async Task<ExtensionPopupWindow?> AdoptAsync(
        string name,
        string userDataFolder,
        IntPtr ownerHwnd = default,
        Action<Window>? onCreated = null)
    {
        var window = new ExtensionPopupWindow(name, url: "", userDataFolder, ownerHwnd);
        onCreated?.Invoke(window);
        window.Activate();

        return await window._ready.Task == null ? null : window;
    }

    /// <summary>The browser, once it exists.</summary>
    public CoreWebView2? Core => _webView.CoreWebView2;

    /// <summary>
    /// Loads <paramref name="url"/> if nothing has finished loading in this window yet.
    /// </summary>
    /// <remarks>
    /// <b>Asks whether a navigation <em>completed</em>, not whether one started.</b> An adopted
    /// window comes back with a <c>Source</c> set almost immediately, so testing that reported the
    /// window as busy and left it empty for good. What was actually happening is that the
    /// navigation WebView2 began never finished, which is a state only <c>NavigationCompleted</c>
    /// can distinguish from a page that simply has not painted yet.
    /// </remarks>
    public void LoadIfIdle(string url)
    {
        if (_navigated || string.IsNullOrEmpty(url)) return;
        if (_webView.CoreWebView2 is not { } core) return;

        try
        {
            NLog.LogManager.GetCurrentClassLogger()
                .Info("Nothing finished loading in the extension window; loading {Url} into it", url);

            core.Navigate(url);
        }
        catch (Exception ex)
        {
            // Closed while we were waiting, most likely.
            NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Loading {Url} into the extension window failed", url);
        }
    }

    private ExtensionPopupWindow(string name, string url, string userDataFolder, IntPtr ownerHwnd)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = name;
        SystemBackdrop = new MicaBackdrop();

        // Above the flyout, which is always-on-top — the same rule every window it raises follows.
        if (ownerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = WindowChrome.BuildTitleBar(name);
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(_webView, 1);
        root.Children.Add(titleBar);
        root.Children.Add(_webView);

        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Close(); };
        root.KeyboardAccelerators.Add(escape);

        Content = root;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);
        WindowChrome.ApplyIcon(_hwnd);

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsResizable = true;
        GetAppWindow().SetPresenter(presenter);

        Resize();
        _ = LoadAsync(url, userDataFolder);

        Closed += (_, _) =>
        {
            try { _webView.Close(); }
            catch (Exception) { /* already gone with the window */ }

            // Closed before the browser was ready: an adopting caller is still awaiting it, and a
            // task nobody completes leaves that page's new-window deferral open forever.
            _ready.TrySetResult(null);
            _completion.TrySetResult(true);
        };
    }

    private async Task LoadAsync(string url, string userDataFolder)
    {
        try
        {
            // The flyout's own, not a second one on the same folder: see
            // Services/WebViewEnvironments. This window is on the launcher's profile precisely so
            // that the extension is there, and every extra environment restarts that extension's
            // service worker underneath it.
            var environment = await Services.WebViewEnvironments.GetAsync(userDataFolder);

            await _webView.EnsureCoreWebView2Async(environment);
            if (_webView.CoreWebView2 is not { } core)
            {
                _ready.TrySetResult(null);
                return;
            }

            // The same pass every tab gets. Extensions belong to the profile and this window is on
            // the launcher's own folder, so they are usually already there and this does nothing.
            // "Usually" is not good enough for the page this window exists to show: an extension
            // page in a browser where that extension is not loaded resolves to nothing at all.
            await Services.BrowserExtensionService.ApplyAsync(core);

            // The popup decides its own size in CSS and there is no API to ask an extension how big
            // its panel is, so the window has to be told by the page once the page exists. Until
            // then it is a guess — which is what left uBlock Origin Lite's 280px panel sitting in
            // the corner of a 420x560 window.
            core.NavigationCompleted += async (_, _) =>
            {
                _navigated = true;
                await SizeToPopupAsync(core);
            };

            // window.close() from an extension's own popup closes this window, which is what an
            // extension expects once its prompt is answered.
            core.WindowCloseRequested += (_, _) => Close();

            // An adopted popup is navigated by whoever asked for it, so it arrives with no address.
            if (!string.IsNullOrEmpty(url)) core.Navigate(url);

            _ready.TrySetResult(core);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Opening the extension popup {Url} failed", url);
            _ready.TrySetResult(null);
            Close();
        }
    }

    /// <summary>
    /// Fits the window to the popup the extension actually rendered.
    /// </summary>
    /// <remarks>
    /// <para>Measured from the document rather than assumed, because an extension popup is sized by
    /// its own stylesheet — a browser gives it exactly the box it asks for, within limits, and
    /// anything else leaves it stranded in the corner of a window that is too big or clipped in one
    /// that is too small.</para>
    /// <para>Clamped at both ends: a popup that reports nothing useful before layout settles would
    /// otherwise collapse the window to nothing, and one that asks for the height of a document
    /// would fill the screen. Chromium's own popup limits are 800x600, which is the ceiling used
    /// here for the same reason.</para>
    /// </remarks>
    private async Task SizeToPopupAsync(CoreWebView2 core)
    {
        try
        {
            // scrollWidth/Height rather than the body's: a popup that sets its size on <html> —
            // which is the common shape — reports nothing useful on the body.
            string json = await core.ExecuteScriptAsync(
                "JSON.stringify([Math.max(document.documentElement.scrollWidth, document.body ? document.body.scrollWidth : 0),"
                + " Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0)])");

            var size = System.Text.Json.JsonSerializer.Deserialize<double[]>(json);
            if (size is not { Length: 2 }) return;

            double width = Math.Clamp(size[0], 240, 800);
            double height = Math.Clamp(size[1], 160, 600);

            // The title bar is chrome of ours, so the page's height is not the window's.
            Resize(width, height + TitleBarAllowanceDips);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Measuring the extension popup failed");
        }
    }

    /// <summary>Height of the window's own title bar, which the page does not know about.</summary>
    private const double TitleBarAllowanceDips = 44;

    private void Resize(double widthDips = WindowWidthDips, double heightDips = WindowHeightDips)
    {
        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var appWindow = GetAppWindow();

        appWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(widthDips * scale),
            (int)Math.Ceiling(heightDips * scale)));

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        appWindow.Move(new PointInt32(
            area.X + ((area.Width - appWindow.Size.Width) / 2),
            area.Y + ((area.Height - appWindow.Size.Height) / 2)));
    }

    private AppWindow GetAppWindow() =>
        AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
}
