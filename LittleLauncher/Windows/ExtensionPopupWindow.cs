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
/// </remarks>
public sealed class ExtensionPopupWindow : Window
{
    private const int WindowWidthDips = 420;
    private const int WindowHeightDips = 560;

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly IntPtr _hwnd;
    private readonly WebView2 _webView = new();

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

            _completion.TrySetResult(true);
        };
    }

    private async Task LoadAsync(string url, string userDataFolder)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: "",
                userDataFolder: userDataFolder,
                // Identical to the flyout's, and not optional: a second environment on the same
                // folder with different options fails with ERROR_INVALID_STATE.
                options: new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });

            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2?.Navigate(url);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Opening the extension popup {Url} failed", url);
            Close();
        }
    }

    private void Resize()
    {
        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var appWindow = GetAppWindow();

        appWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(WindowWidthDips * scale),
            (int)Math.Ceiling(WindowHeightDips * scale)));

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        appWindow.Move(new PointInt32(
            area.X + ((area.Width - appWindow.Size.Width) / 2),
            area.Y + ((area.Height - appWindow.Size.Height) / 2)));
    }

    private AppWindow GetAppWindow() =>
        AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
}
