// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// The flyout a web launcher opens: a tray-anchored window hosting a WebView2 on the launcher's
/// <see cref="Launcher.WebUrl"/>. One instance per launcher, created on first use.
/// </summary>
/// <remarks>
/// <para>It behaves like <see cref="FlyoutWindow"/> — anchored above the tray, always on top,
/// out of Alt-Tab, dismissed on focus loss — but its resource model is the opposite one.
/// The flyout is warmed up at startup and merely parked when dismissed, because its content is
/// cheap to keep and expensive to re-rasterise. A browser is the reverse: a dashboard of camera
/// cards keeps decoding video, polling and holding hundreds of megabytes for as long as its
/// renderer lives. So nothing here is created until the user first opens the flyout, and
/// dismissing it suspends the browser and — under the default
/// <see cref="WebHiddenPolicies.UnloadWhenIdle"/> — tears it down entirely once the flyout has
/// sat unused. See <see cref="ApplyHiddenPolicy"/>.</para>
/// <para>The window itself outlives a dismissal (parked off the virtual screen, as the flyout
/// does) since an empty WinUI window costs nothing and reopening is then a pure move.</para>
/// </remarks>
public sealed class WebFlyoutWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>Per-launcher flyout instances (key = <see cref="Launcher.Id"/>).</summary>
    private static readonly Dictionary<string, WebFlyoutWindow> Instances = new();

    private const int HeaderHeightDips = 34;

    /// <summary>Width of the invisible edge strips that resize the flyout.</summary>
    private const int GripThickness = 6;

    /// <summary>Corner grips are bigger, because a corner is the harder target to hit.</summary>
    private const int CornerGripSize = 14;

    private const double SlideDistanceDip = 36;
    private const uint ShowAnimationDurationMs = 200;
    private const uint HideAnimationDurationMs = 160;

    /// <summary>See <c>FlyoutWindow.FadeOutCompleteAt</c> — the fade finishes before the park.</summary>
    private const double FadeOutCompleteAt = 0.8;

    /// <summary>Re-click grace period, so the tray click that dismissed the flyout cannot reopen it.</summary>
    private const int ReopenGuardMs = 300;

    private readonly Launcher _launcher;
    private readonly IntPtr _hwnd;
    private readonly Grid _contentHost;
    private readonly TextBlock _headerTitle;
    private readonly Button _pinButton;
    private readonly Button _backButton;
    private readonly StackPanel _statusPanel;
    private readonly TextBlock _statusText;
    private readonly ProgressRing _statusRing;
    private readonly Button _statusAction;
    private readonly SUBCLASSPROC _wndProcDelegate;

    private MainWindow? _owner;
    private WebView2? _webView;
    private bool _webViewInitializing;
    private string _navigatedUrl = "";

    private bool _isOpen;
    private bool _isShowing;
    private bool _isHiding;
    private bool _hasBeenShown;
    private bool _fadeStyleApplied;

    /// <summary>True while an owned window (launcher settings) is open. Pins the flyout.</summary>
    private bool _isModalOpen;
    private Window? _openModal;

    private ResizeEdges _resizeEdges;
    private bool _isResizing;
    private POINT _resizeStartCursor;
    private RECT _resizeStartRect;

    private int _animationVersion;
    private DateTime _lastDismissed = DateTime.MinValue;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleUnloadTimer;
    private SlideEdge _lastEntranceEdge = SlideEdge.Bottom;

    private enum SlideEdge
    {
        Top,
        Bottom,
    }

    /// <summary>Which window edges a grip drags. Corners combine two.</summary>
    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    private readonly record struct FlyoutPlacement(int Left, int Top, int StartTop, int Width, int Height, SlideEdge Edge);

    private static bool AreAnimationsEnabled => SettingsManager.Current.FlyoutAnimationsEnabled;

    // ── Construction ────────────────────────────────────────────────

    private WebFlyoutWindow(MainWindow owner, Launcher launcher)
    {
        _owner = owner;
        _launcher = launcher;
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = launcher.Name;

        // Borderless and always on top, exactly as FlyoutWindow presents itself: a tray flyout
        // has no non-client area at all, and the size is a setting rather than something dragged.
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        GetAppWindow().SetPresenter(presenter);

        SystemBackdrop = new DesktopAcrylicBackdrop();

        int cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        // ── Header ──────────────────────────────────────────────────
        _headerTitle = new TextBlock
        {
            Text = launcher.Name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Pinning is reachable from the flyout itself, not just its settings: whether it should
        // survive a click elsewhere is a per-moment decision — glance at a dashboard, or leave
        // the cameras up while working in another window.
        _pinButton = BuildHeaderButton(PinGlyph(launcher.WebPinFlyout), PinTooltip(launcher.WebPinFlyout), (_, _) => TogglePin());

        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        headerButtons.Children.Add(_pinButton);
        headerButtons.Children.Add(BuildHeaderButton("", "Launcher settings", (_, _) => _ = OpenLauncherSettingsAsync()));
        headerButtons.Children.Add(BuildHeaderButton("", "Reload", (_, _) => ReloadPage()));
        headerButtons.Children.Add(BuildHeaderButton("", "Open in browser", (_, _) => OpenInBrowser()));
        headerButtons.Children.Add(BuildHeaderButton("", "Close", (_, _) => HideFlyout()));

        // Back sits on the left, where every browser puts it, rather than among the window
        // controls on the right — it acts on the page, not on the flyout.
        //
        // U+E112 is Segoe Fluent's Back arrow — the code point behind WinUI's own
        // Symbol.Back, and the one the sibling Persistent app uses. Written as an escape, not
        // as a literal character: a literal private-use glyph does not survive every editing
        // round-trip, and when it was silently dropped this button rendered as a blank square.
        _backButton = BuildHeaderButton("\uE112", "Back", (_, _) => GoBack());
        _backButton.IsEnabled = false;
        _backButton.Margin = new Thickness(-6, 0, 4, 0);

        var header = new Grid
        {
            Height = HeaderHeightDips,
            Padding = new Thickness(12, 0, 6, 0),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_backButton, 0);
        Grid.SetColumn(_headerTitle, 1);
        Grid.SetColumn(headerButtons, 2);
        header.Children.Add(_backButton);
        header.Children.Add(_headerTitle);
        header.Children.Add(headerButtons);

        // ── Status overlay (loading / error) ────────────────────────
        _statusRing = new ProgressRing
        {
            IsActive = true,
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _statusText = new TextBlock
        {
            Text = "Loading…",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Opacity = 0.8,
        };
        _statusAction = new Button
        {
            Content = "Retry",
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _statusAction.Click += (_, _) => ReloadPage();

        _statusPanel = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusPanel.Children.Add(_statusRing);
        _statusPanel.Children.Add(_statusText);
        _statusPanel.Children.Add(_statusAction);

        _contentHost = new Grid
        {
            // Inset by the grip thickness so the browser never occupies the strip the resize
            // grips need. The grips sit above it in z-order anyway, but a hosted browser is not
            // an ordinary XAML sibling — keeping them physically disjoint means the edges stay
            // grabbable no matter how WebView2 routes its own input.
            Margin = new Thickness(GripThickness, 0, GripThickness, GripThickness),
        };
        _contentHost.Children.Add(_statusPanel);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_contentHost, 1);
        root.Children.Add(header);
        root.Children.Add(_contentHost);
        AddResizeGrips(root);

        // Escape closes the panel. This only fires while focus is on the XAML tree; once the
        // page has focus the browser owns the key, which is why the header keeps a close button.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; HideFlyout(); };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        ThemeManager.ApplySavedTheme(this);

        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 3, 0);

        Activated += WebFlyoutWindow_Activated;
    }

    private static Button BuildHeaderButton(string glyph, string tooltip, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += onClick;
        return button;
    }

    // ── Manual resize ───────────────────────────────────────────────

    /// <summary>
    /// Adds the invisible edge and corner strips that let the flyout be dragged to a new size.
    /// </summary>
    /// <remarks>
    /// The window is borderless (no non-client area at all), so there is no system sizing border
    /// to drag: `WM_NCHITTEST` never reaches this window for a point that lands on the XAML island
    /// or the hosted browser. Resizing is therefore done in XAML — capture the pointer on a strip,
    /// move the window with `SetWindowPos` as the cursor moves, and persist the result — which
    /// also keeps the flyout's chrome-free look intact.
    /// </remarks>
    private void AddResizeGrips(Grid root)
    {
        void Add(ResizeEdges edges, HorizontalAlignment h, VerticalAlignment v, double width, double height,
            Microsoft.UI.Input.InputSystemCursorShape cursor)
        {
            var grip = new ResizeGrip(cursor)
            {
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Tag = edges,
                // A null Background is not hit-testable; a transparent one is.
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            if (width > 0) grip.Width = width;
            if (height > 0) grip.Height = height;

            grip.PointerPressed += Grip_PointerPressed;
            grip.PointerMoved += Grip_PointerMoved;
            grip.PointerReleased += Grip_PointerReleased;
            grip.PointerCaptureLost += Grip_PointerCaptureLost;

            Grid.SetRow(grip, 0);
            Grid.SetRowSpan(grip, 2);
            root.Children.Add(grip);
        }

        var we = Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast;
        var ns = Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth;
        var nwse = Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast;
        var nesw = Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest;

        // Edges first, corners after, so a corner wins where the two overlap.
        Add(ResizeEdges.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, GripThickness, 0, we);
        Add(ResizeEdges.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, GripThickness, 0, we);
        Add(ResizeEdges.Top, HorizontalAlignment.Stretch, VerticalAlignment.Top, 0, GripThickness, ns);
        Add(ResizeEdges.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, 0, GripThickness, ns);

        Add(ResizeEdges.Top | ResizeEdges.Left, HorizontalAlignment.Left, VerticalAlignment.Top, CornerGripSize, CornerGripSize, nwse);
        Add(ResizeEdges.Top | ResizeEdges.Right, HorizontalAlignment.Right, VerticalAlignment.Top, CornerGripSize, CornerGripSize, nesw);
        Add(ResizeEdges.Bottom | ResizeEdges.Left, HorizontalAlignment.Left, VerticalAlignment.Bottom, CornerGripSize, CornerGripSize, nesw);
        Add(ResizeEdges.Bottom | ResizeEdges.Right, HorizontalAlignment.Right, VerticalAlignment.Bottom, CornerGripSize, CornerGripSize, nwse);
    }

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ResizeGrip grip || grip.Tag is not ResizeEdges edges) return;

        GetCursorPos(out _resizeStartCursor);
        if (!GetWindowRect(_hwnd, out _resizeStartRect)) return;

        _resizeEdges = edges;
        _isResizing = true;

        // A resize in flight must not be overtaken by a slide still finishing.
        _animationVersion++;
        _isShowing = false;
        _isHiding = false;

        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing) return;

        GetCursorPos(out var cursor);
        int dx = cursor.X - _resizeStartCursor.X;
        int dy = cursor.Y - _resizeStartCursor.Y;

        double scale = GetScale();
        int minWidth = (int)Math.Ceiling(Launcher.MinWebFlyoutWidth * scale);
        int minHeight = (int)Math.Ceiling(Launcher.MinWebFlyoutHeight * scale);

        int left = _resizeStartRect.Left;
        int top = _resizeStartRect.Top;
        int right = _resizeStartRect.Right;
        int bottom = _resizeStartRect.Bottom;

        if (_resizeEdges.HasFlag(ResizeEdges.Left)) left = Math.Min(left + dx, right - minWidth);
        if (_resizeEdges.HasFlag(ResizeEdges.Right)) right = Math.Max(right + dx, left + minWidth);
        if (_resizeEdges.HasFlag(ResizeEdges.Top)) top = Math.Min(top + dy, bottom - minHeight);
        if (_resizeEdges.HasFlag(ResizeEdges.Bottom)) bottom = Math.Max(bottom + dy, top + minHeight);

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
            SWP_NOZORDER | SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ResizeGrip grip)
            grip.ReleasePointerCapture(e.Pointer);
        CompleteResize();
        e.Handled = true;
    }

    private void Grip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => CompleteResize();

    /// <summary>Persists the dragged size so the flyout reopens at it.</summary>
    /// <remarks>
    /// Written only on release, and only from a real drag — the placement code clamps the flyout
    /// to the work area on every open, and persisting that would silently shrink the launcher on
    /// the first open on a smaller screen.
    /// </remarks>
    private void CompleteResize()
    {
        if (!_isResizing) return;
        _isResizing = false;
        _resizeEdges = ResizeEdges.None;

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || !GetWindowRect(_hwnd, out var rect)) return;

        double scale = GetScale();
        int width = (int)Math.Round((rect.Right - rect.Left) / scale);
        int height = (int)Math.Round((rect.Bottom - rect.Top) / scale);

        if (width == _launcher.ResolvedWebFlyoutWidth && height == _launcher.ResolvedWebFlyoutHeight)
            return;

        _launcher.WebFlyoutWidth = Math.Clamp(width, Launcher.MinWebFlyoutWidth, Launcher.MaxWebFlyoutDimension);
        _launcher.WebFlyoutHeight = Math.Clamp(height, Launcher.MinWebFlyoutHeight, Launcher.MaxWebFlyoutDimension);
        SettingsManager.SaveSettings();
    }

    /// <summary>An edge strip that shows a resize cursor and carries its edges in <c>Tag</c>.</summary>
    private sealed class ResizeGrip : Grid
    {
        private readonly Microsoft.UI.Input.InputSystemCursorShape _shape;

        public ResizeGrip(Microsoft.UI.Input.InputSystemCursorShape shape)
        {
            _shape = shape;
            PointerEntered += (_, _) => SetCursor(_shape);
            PointerExited += (_, _) => SetCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        private void SetCursor(Microsoft.UI.Input.InputSystemCursorShape shape) =>
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(shape);
    }

    /// <summary>
    /// Opens this launcher's settings from the flyout's own header.
    /// </summary>
    /// <remarks>
    /// Same contract as the item flyout's <c>RunModalAsync</c>: the flyout is pinned open for the
    /// duration (it dismisses on focus loss, and opening a window takes focus away), and it drops
    /// its always-on-top flag — owner relationship alone does not beat a topmost owner, so the
    /// settings window would otherwise open *behind* the flyout that spawned it.
    /// </remarks>
    private async Task OpenLauncherSettingsAsync()
    {
        if (_isModalOpen) return;

        _isModalOpen = true;
        SetTopmost(false);
        try
        {
            await LauncherSettingsWindow.ShowAsync(_launcher, _hwnd, w => _openModal = w);
        }
        finally
        {
            _isModalOpen = false;
            _openModal = null;

            // Switching this launcher's Kind in that window disposes this very flyout, so
            // everything past here has to tolerate the window already being gone.
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                SetTopmost(true);
                ApplyLauncherChanges();
                RestoreActivation();
            }
        }
    }

    private void SetTopmost(bool topmost)
    {
        try
        {
            if (GetAppWindow().Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = topmost;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Presenter unavailable while setting topmost");
        }
    }

    /// <summary>
    /// Returns foreground to the flyout after an owned window closes.
    /// </summary>
    /// <remarks>
    /// Dismissal is driven by <c>Deactivated</c>, which can only fire for a window that currently
    /// holds activation. Closing the settings window does not necessarily hand activation back,
    /// which would leave the flyout on screen and unable to ever dismiss itself.
    /// </remarks>
    private void RestoreActivation()
    {
        if (!_isOpen) return;
        try { SetForegroundWindow(_hwnd); } catch { /* foreground can be refused */ }
    }

    private static string PinGlyph(bool pinned) => pinned ? "" : "";

    private static string PinTooltip(bool pinned) =>
        pinned ? "Unpin — close when focus is lost" : "Pin open";

    /// <summary>Flips the launcher's pin state and re-labels the header button.</summary>
    private void TogglePin()
    {
        _launcher.WebPinFlyout = !_launcher.WebPinFlyout;
        SettingsManager.SaveSettings();

        if (_pinButton.Content is FontIcon icon)
            icon.Glyph = PinGlyph(_launcher.WebPinFlyout);
        ToolTipService.SetToolTip(_pinButton, PinTooltip(_launcher.WebPinFlyout));
    }

    private AppWindow GetAppWindow() =>
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

    // ── Public entry points ─────────────────────────────────────────

    /// <summary>Shows the launcher's web flyout, or dismisses it if it is already on screen.</summary>
    public static void Toggle(MainWindow owner, int screenX, int screenY, string launcherId)
    {
        var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
        if (launcher == null) return;

        if (!Instances.TryGetValue(launcherId, out var panel) || panel._hwnd == IntPtr.Zero || !IsWindow(panel._hwnd))
        {
            panel = new WebFlyoutWindow(owner, launcher);
            Instances[launcherId] = panel;
        }

        if (panel._isOpen)
        {
            if (!panel._isHiding)
                panel.HideFlyout();
            return;
        }

        // The click that dismissed the panel via focus loss must not immediately reopen it.
        if (!panel._isHiding && (DateTime.UtcNow - panel._lastDismissed).TotalMilliseconds < ReopenGuardMs)
            return;

        panel._owner = owner;
        panel.ShowFlyout(screenX, screenY);
    }

    /// <summary>Destroys the flyout for a launcher that was deleted or is no longer a web launcher.</summary>
    public static void DisposeLauncher(string launcherId)
    {
        if (!Instances.Remove(launcherId, out var panel)) return;

        // An owned settings window outlives its parent otherwise, and would commit into a
        // launcher the user has navigated away from.
        try { panel._openModal?.Close(); } catch (Exception ex) { Logger.Debug(ex, "Closing owned window failed"); }
        panel._openModal = null;

        panel.UnloadWebView();
        panel._idleUnloadTimer?.Stop();
        panel._idleUnloadTimer = null;
        try { panel.Close(); } catch (Exception ex) { Logger.Warn(ex, "Closing web panel failed"); }
    }

    /// <summary>
    /// Re-applies launcher settings (name, URL, size, zoom) to an existing flyout. A no-op when
    /// the flyout has never been opened, since it picks everything up on creation.
    /// </summary>
    public static void ApplyLauncherChanges(string launcherId)
    {
        if (!Instances.TryGetValue(launcherId, out var panel)) return;
        panel.ApplyLauncherChanges();
    }

    /// <summary>
    /// Clears cookies, storage and cache for a web launcher — the way to sign out of a page the
    /// flyout has stayed signed in to.
    /// </summary>
    public static async Task ClearBrowsingDataAsync(Launcher launcher)
    {
        if (Instances.TryGetValue(launcher.Id, out var panel) && panel._webView?.CoreWebView2 is { } core)
        {
            await core.Profile.ClearBrowsingDataAsync();
            return;
        }

        // Nothing loaded — the profile is just a folder on disk.
        DisposeLauncher(launcher.Id);
        string folder = GetUserDataFolder(launcher.Id);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>Where a web launcher's cookies, storage and cache live, so logins survive restarts.</summary>
    internal static string GetUserDataFolder(string launcherId) =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), "WebProfiles", launcherId);

    // ── Show / hide ─────────────────────────────────────────────────

    private void ShowFlyout(int screenX, int screenY)
    {
        var placement = CalculatePlacement(screenX, screenY);
        _lastEntranceEdge = placement.Edge;

        _idleUnloadTimer?.Stop();

        // The very first show of this window's life is not animated. WinUI has never drawn it,
        // so it has no composition surface yet and presents its extended frame — a black
        // rectangle — for the frames XAML takes to paint. The flyout hides that by pre-rendering
        // parked off screen at startup; a web launcher deliberately builds nothing until it is
        // asked for, so it takes the plain show instead and slides on every open after this one.
        if (AreAnimationsEnabled && _hasBeenShown)
            ShowAnimated(placement);
        else
            ShowWithoutAnimation(placement);

        _hasBeenShown = true;

        // Kicked off after the window is on screen so the panel (and its loading state) is
        // visible while the browser starts, rather than the click appearing to do nothing.
        _ = PrepareContentAsync();
    }

    private void ShowWithoutAnimation(FlyoutPlacement placement)
    {
        _animationVersion++;
        _isHiding = false;
        _isOpen = true;
        ClearFade();

        MoveResize(placement.Left, placement.Top, placement.Width, placement.Height);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);
    }

    private void ShowAnimated(FlyoutPlacement placement)
    {
        _isHiding = false;
        _isShowing = true;

        int startTop = placement.StartTop;
        if (_isOpen && GetWindowRect(_hwnd, out var rect))
            startTop = rect.Top;

        _isOpen = true;
        ClearFade();

        int animationVersion = ++_animationVersion;

        MoveResize(placement.Left, startTop, placement.Width, placement.Height);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);

        if (startTop == placement.Top)
        {
            _isShowing = false;
            return;
        }

        AnimateWindowPosition(animationVersion, placement.Left, startTop, placement.Top,
            placement.Width, placement.Height, ShowAnimationDurationMs, parkAtEnd: false);
    }

    /// <summary>Dismisses the flyout and hands the browser to <see cref="ApplyHiddenPolicy"/>.</summary>
    private void HideFlyout()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || !_isOpen)
            return;

        _lastDismissed = DateTime.UtcNow;
        _animationVersion++;

        if (!AreAnimationsEnabled)
        {
            _isShowing = false;
            _isHiding = false;
            ParkOffScreen();
            return;
        }

        _isHiding = true;
        _isShowing = false;

        GetWindowRect(_hwnd, out var rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int exitOffset = GetSlideDistancePx();
        int endTop = _lastEntranceEdge == SlideEdge.Top ? rect.Top - exitOffset : rect.Top + exitOffset;

        AnimateWindowPosition(++_animationVersion, rect.Left, rect.Top, endTop, width, height,
            HideAnimationDurationMs, parkAtEnd: true);
    }

    /// <summary>
    /// Parks the window outside the virtual screen rather than hiding it, for the reason given
    /// in <c>FlyoutWindow.ParkOffScreen</c>: a hidden WinUI window releases its composition
    /// surfaces and has to repaint from nothing on the next open.
    /// </summary>
    private void ParkOffScreen()
    {
        _isOpen = false;
        ClearFade();

        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            GetWindowRect(_hwnd, out var rect);
            int width = Math.Max(1, rect.Right - rect.Left);
            int height = Math.Max(1, rect.Bottom - rect.Top);
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 64;
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 64;
            MoveResize(left, top, width, height);
        }

        ApplyHiddenPolicy();
    }

    private void MoveResize(int left, int top, int width, int height)
    {
        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ── Resource policy ─────────────────────────────────────────────

    /// <summary>
    /// Applies the launcher's <see cref="Launcher.WebHiddenPolicy"/> now that the flyout is off
    /// screen. This is the whole point of the feature: a dismissed dashboard must stop costing
    /// CPU, network and memory.
    /// </summary>
    /// <remarks>
    /// Collapsing the control is what makes suspension possible — WebView2 refuses to suspend a
    /// visible browser — and it also stops rendering on its own, which is what quietens video.
    /// Suspension is best-effort (it declines while media is captured or a download is running),
    /// so the idle unload is the guarantee rather than the optimisation: after it fires there is
    /// no browser process left to consume anything.
    /// </remarks>
    private void ApplyHiddenPolicy()
    {
        // Stopped and unsubscribed up front: this window lives for the whole app session, so a
        // Tick handler left attached on every dismiss would accumulate — and a timer left running
        // after a switch to KeepRunning would unload the page the setting asked to keep.
        if (_idleUnloadTimer != null)
        {
            _idleUnloadTimer.Stop();
            _idleUnloadTimer.Tick -= IdleUnloadTimer_Tick;
        }

        if (_webView == null) return;

        int policy = WebHiddenPolicies.Normalize(_launcher.WebHiddenPolicy);
        if (policy == WebHiddenPolicies.KeepRunning)
            return;

        _webView.Visibility = Visibility.Collapsed;
        _ = SuspendWebViewAsync();

        if (policy != WebHiddenPolicies.UnloadWhenIdle)
            return;

        _idleUnloadTimer ??= DispatcherQueue.CreateTimer();
        _idleUnloadTimer.Stop();
        _idleUnloadTimer.IsRepeating = false;
        _idleUnloadTimer.Interval = TimeSpan.FromMinutes(_launcher.ResolvedWebIdleUnloadMinutes);
        _idleUnloadTimer.Tick += IdleUnloadTimer_Tick;
        _idleUnloadTimer.Start();
    }

    private void IdleUnloadTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= IdleUnloadTimer_Tick;
        if (_isOpen) return;
        UnloadWebView();
    }

    private async Task SuspendWebViewAsync()
    {
        var core = _webView?.CoreWebView2;
        if (core == null) return;

        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            bool suspended = await core.TrySuspendAsync();
            if (!suspended)
                Logger.Debug("WebView2 declined to suspend for launcher {Name}", _launcher.Name);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Suspending WebView2 failed for launcher {Name}", _launcher.Name);
        }
    }

    private void ResumeWebView()
    {
        var core = _webView?.CoreWebView2;
        if (core == null) return;

        try
        {
            core.Resume();
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Resuming WebView2 failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>Tears the browser down completely; the next open builds a fresh one.</summary>
    private void UnloadWebView()
    {
        if (_webView == null) return;

        var webView = _webView;
        _webView = null;
        _navigatedUrl = "";

        try
        {
            _contentHost.Children.Remove(webView);
            webView.Close();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Closing WebView2 failed for launcher {Name}", _launcher.Name);
        }

        SetStatus("Loading…", busy: true, showRetry: false);
        UpdateBackButton();   // the history went with the browser
    }

    // ── Content ─────────────────────────────────────────────────────

    private async Task PrepareContentAsync()
    {
        string url = NormalizeUrl(_launcher.WebUrl);
        if (string.IsNullOrEmpty(url))
        {
            UnloadWebView();
            SetStatus("No web address is set for this launcher. Add one in its launcher settings.",
                busy: false, showRetry: false);
            return;
        }

        if (_webView == null)
        {
            await CreateWebViewAsync();
            return;   // creation navigates once the core is ready
        }

        _webView.Visibility = Visibility.Visible;
        ResumeWebView();
        ApplyZoom();

        if (!string.Equals(_navigatedUrl, url, StringComparison.OrdinalIgnoreCase))
            Navigate(url);
        else if (_launcher.WebReloadOnShow)
            ReloadPage();
    }

    private async Task CreateWebViewAsync()
    {
        if (_webViewInitializing) return;
        _webViewInitializing = true;
        SetStatus("Loading…", busy: true, showRetry: false);

        var webView = new WebView2 { Visibility = Visibility.Visible };
        _contentHost.Children.Insert(0, webView);
        _webView = webView;

        try
        {
            string userDataFolder = GetUserDataFolder(_launcher.Id);
            Directory.CreateDirectory(userDataFolder);

            // A per-launcher profile keeps each panel signed in independently and, more to the
            // point, keeps it signed in at all — the alternative is re-authenticating to a home
            // dashboard on every app restart.
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: "",
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions());

            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "WebView2 initialisation failed for launcher {Name}", _launcher.Name);
            UnloadWebView();
            SetStatus(
                "This panel needs the Microsoft Edge WebView2 Runtime, which could not be started. " +
                "Install it from microsoft.com/edge/webview2 and try again.",
                busy: false, showRetry: true);
            _webViewInitializing = false;
            return;
        }
        finally
        {
            _webViewInitializing = false;
        }

        if (_webView == null) return;   // unloaded while initialising

        ConfigureCore(_webView.CoreWebView2);
        ApplyZoom();
        Navigate(NormalizeUrl(_launcher.WebUrl));
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;   // no Ctrl+N/Ctrl+P from a tray panel
        settings.IsPasswordAutosaveEnabled = true;
        settings.IsGeneralAutofillEnabled = true;

        // A panel has nowhere to put a second window, and a popup would be an unowned browser
        // window floating over the desktop — hand those to the real browser instead.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };

        core.WindowCloseRequested += (_, _) => HideFlyout();

        core.NavigationStarting += (_, _) => SetStatus("Loading…", busy: true, showRetry: false);

        core.HistoryChanged += (_, _) => UpdateBackButton();

        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                HideStatus();
                ApplyZoom();   // CSS zoom lives in the document, so each navigation drops it
                UpdateBackButton();
                return;
            }

            SetStatus($"Could not load {NormalizeUrl(_launcher.WebUrl)} ({e.WebErrorStatus}).",
                busy: false, showRetry: true);
        };

        core.ProcessFailed += (_, _) =>
        {
            SetStatus("The page stopped responding.", busy: false, showRetry: true);
        };

        // The page's own icon is the right default for a web launcher, and this is the only
        // source that can get it: FaviconService fetches over plain HTTP with no session, so a
        // self-hosted dashboard behind a login hands it a redirect instead of an icon. The
        // browser here is signed in and reads whatever the page actually declares.
        core.FaviconChanged += (_, _) => _ = AdoptPageIconAsync(core);
    }

    /// <summary>
    /// Where a web launcher's auto-adopted page icon is kept. The distinct name is what makes
    /// "did we choose this, or did the user?" answerable later.
    /// </summary>
    internal static string GetPageIconPath(string launcherId) =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), $"web-favicon-{launcherId}.png");

    /// <summary>
    /// True when the launcher's icon is ours to replace — either never chosen, or the page icon
    /// we adopted on a previous visit.
    /// </summary>
    /// <remarks>
    /// A user who picks a glyph, a colour or their own image has made a decision, and a page
    /// changing its favicon must not quietly undo it. Composite is the "never chosen" state:
    /// it is the model default, and it renders nothing for a web launcher anyway (it composes
    /// item icons, of which a web launcher has none).
    /// </remarks>
    internal static bool MayAdoptPageIcon(Launcher launcher)
    {
        if (launcher.TrayIconMode == TrayIconModes.Composite) return true;

        return launcher.TrayIconMode == TrayIconModes.Custom &&
               string.Equals(launcher.CustomTrayIconPath, GetPageIconPath(launcher.Id), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Saves the page's declared icon and makes it the launcher's tray icon.</summary>
    private async Task AdoptPageIconAsync(CoreWebView2 core)
    {
        if (!MayAdoptPageIcon(_launcher)) return;
        if (string.IsNullOrEmpty(core.FaviconUri)) return;

        try
        {
            var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null || stream.Size == 0) return;

            // A WinRT stream, not a .NET one — read it through DataReader rather than reaching
            // for the interop extension methods.
            uint size = (uint)stream.Size;
            var buffer = new byte[size];
            using (var reader = new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(buffer);
            }

            string path = GetPageIconPath(_launcher.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written to a temporary file first: the tray icon pipeline reads this path, and a
            // half-written PNG would be read as a corrupt image rather than simply an old one.
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, buffer);
            File.Move(temp, path, overwrite: true);

            _launcher.CustomTrayIconPath = path;
            _launcher.TrayIconMode = TrayIconModes.Custom;
            SettingsManager.SaveSettings();

            // Re-renders the tray icon and rewrites app-icon-{id}.ico, which is what the taskbar
            // pin flow copies from.
            MainWindow.Current?.UpdateTrayIcon(_launcher);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Adopting the page icon failed for launcher {Name}", _launcher.Name);
        }
    }

    private void Navigate(string url)
    {
        var core = _webView?.CoreWebView2;
        if (core == null || string.IsNullOrEmpty(url)) return;

        _navigatedUrl = url;
        try
        {
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Navigating to {Url} failed", url);
            SetStatus($"{url} is not a valid web address.", busy: false, showRetry: false);
        }
    }

    /// <summary>Steps back through the page's own history, not the flyout's.</summary>
    private void GoBack()
    {
        var core = _webView?.CoreWebView2;
        if (core?.CanGoBack != true) return;
        core.GoBack();
    }

    /// <summary>
    /// Greys out Back when there is nowhere to go back to.
    /// </summary>
    /// <remarks>
    /// Driven by <c>HistoryChanged</c> rather than <c>NavigationCompleted</c>: a dashboard is
    /// usually a single-page app, so most of its navigation is history pushed by script with no
    /// document load to hang this off.
    /// </remarks>
    private void UpdateBackButton()
    {
        _backButton.IsEnabled = _webView?.CoreWebView2?.CanGoBack == true;
    }

    private void ReloadPage()
    {
        if (_webView?.CoreWebView2 is not { } core)
        {
            _ = PrepareContentAsync();
            return;
        }

        SetStatus("Loading…", busy: true, showRetry: false);
        core.Reload();
    }

    private void OpenInBrowser()
    {
        string url = _webView?.CoreWebView2?.Source ?? NormalizeUrl(_launcher.WebUrl);
        OpenExternally(url);
    }

    private static void OpenExternally(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Opening {Url} in the default browser failed", url);
        }
    }

    /// <summary>
    /// Applies the launcher's zoom as a CSS zoom on the document element.
    /// </summary>
    /// <remarks>
    /// WebView2's real zoom lives on <c>CoreWebView2Controller.ZoomFactor</c>, and WinUI's
    /// WebView2 control — unlike the WPF and WinForms ones — never surfaces its controller.
    /// CSS zoom is the equivalent that is reachable from here: it reflows the page the same way
    /// browser zoom does, but it lives in the document, so it has to be re-applied on every
    /// navigation rather than set once on the control.
    /// </remarks>
    private void ApplyZoom()
    {
        var core = _webView?.CoreWebView2;
        if (core == null) return;

        double factor = _launcher.ResolvedWebZoomFactor;
        string script = factor == 1.0
            ? "document.documentElement.style.zoom='';"
            : $"document.documentElement.style.zoom='{factor.ToString(CultureInfo.InvariantCulture)}';";

        try { _ = core.ExecuteScriptAsync(script); }
        catch (Exception ex) { Logger.Debug(ex, "Setting zoom failed"); }
    }

    private void ApplyLauncherChanges()
    {
        _headerTitle.Text = _launcher.Name;
        Title = _launcher.Name;
        ApplyZoom();

        string url = NormalizeUrl(_launcher.WebUrl);
        if (!string.Equals(_navigatedUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            if (_webView?.CoreWebView2 != null)
                Navigate(url);
            else
                _navigatedUrl = "";
        }

        if (_isOpen)
            ResizeToConfiguredSize();
    }

    private void ResizeToConfiguredSize()
    {
        double scale = GetScale();
        GetWindowRect(_hwnd, out var rect);
        int width = (int)Math.Ceiling(_launcher.ResolvedWebFlyoutWidth * scale);
        int height = (int)Math.Ceiling(_launcher.ResolvedWebFlyoutHeight * scale);
        MoveResize(rect.Left, rect.Top, width, height);
    }

    /// <summary>Adds a scheme when the user typed a bare host, so "homeassistant.local:8123" works.</summary>
    internal static string NormalizeUrl(string? url)
    {
        string trimmed = (url ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (trimmed.Contains("://", StringComparison.Ordinal)) return trimmed;
        if (trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return trimmed;
        return "https://" + trimmed;
    }

    // ── Status overlay ──────────────────────────────────────────────

    private void SetStatus(string message, bool busy, bool showRetry)
    {
        _statusText.Text = message;
        _statusRing.IsActive = busy;
        _statusRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _statusAction.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        _statusRing.IsActive = false;
        _statusPanel.Visibility = Visibility.Collapsed;
    }

    // ── Window events ───────────────────────────────────────────────

    private void WebFlyoutWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        // A drag that leaves the window, and any owned window, both pin the flyout open — the
        // same rule the item flyout applies to edit mode and its editors.
        if (_isShowing || !_isOpen || _launcher.WebPinFlyout || _isModalOpen || _isResizing) return;

        // The browser's own HWNDs are children of this window, so clicking into the page
        // deactivates the XAML window without the user having gone anywhere. Read the
        // foreground window once the switch has settled rather than trusting the event alone.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isOpen || _isShowing) return;
            var foreground = GetForegroundWindow();
            if (foreground == _hwnd || IsChild(_hwnd, foreground)) return;
            HideFlyout();
        });
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (msg == 0x0100 && wParam == (IntPtr)0x1B) // WM_KEYDOWN + VK_ESCAPE
        {
            HideFlyout();
            return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    // ── Geometry ────────────────────────────────────────────────────

    private double GetScale()
    {
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        return scale <= 0 ? 1.0 : scale;
    }

    /// <summary>
    /// Anchors the flyout to the click, the same way the flyout does: above a bottom taskbar,
    /// below a top one, otherwise above the cursor — then clamped into the work area.
    /// </summary>
    private FlyoutPlacement CalculatePlacement(int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        int width = (int)Math.Ceiling(_launcher.ResolvedWebFlyoutWidth * scale);
        int height = (int)Math.Ceiling(_launcher.ResolvedWebFlyoutHeight * scale);
        width = Math.Min(width, workArea.Right - workArea.Left);
        height = Math.Min(height, workArea.Bottom - workArea.Top);

        int gap = Math.Max(4, (int)Math.Round(8 * scale));
        int slideDistance = Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));
        int edgeThreshold = (int)(16 * scale);

        bool nearBottom = screenY >= workArea.Bottom - edgeThreshold;
        bool nearTop = screenY <= workArea.Top + edgeThreshold;

        int left = screenX - (width / 2);
        int top = nearBottom
            ? workArea.Bottom - height - gap
            : nearTop
                ? workArea.Top + gap
                : screenY - height - gap;

        if (left < workArea.Left) left = workArea.Left;
        if (left + width > workArea.Right) left = workArea.Right - width;
        if (top + height > workArea.Bottom) top = workArea.Bottom - height;
        if (top < workArea.Top) top = workArea.Top;

        var edge = nearTop ? SlideEdge.Top : SlideEdge.Bottom;
        int startTop = edge == SlideEdge.Top ? top - slideDistance : top + slideDistance;
        return new FlyoutPlacement(left, top, startTop, width, height, edge);
    }

    private int GetSlideDistancePx() => Math.Max(18, (int)Math.Round(SlideDistanceDip * GetScale()));

    // ── Animation ───────────────────────────────────────────────────
    // Mirrors FlyoutWindow's slide-and-fade so both kinds of panel move the same way. The
    // easing rationale (and why it must be verified in pixels, not curve shape) is in
    // ARCHITECTURE.md.

    private void AnimateWindowPosition(int animationVersion, int left, int startTop, int endTop,
        int width, int height, uint durationMs, bool parkAtEnd)
    {
        if (startTop == endTop)
        {
            CompleteWindowAnimation(animationVersion, left, endTop, width, height, parkAtEnd);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (animationVersion != _animationVersion || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            {
                CompositionTarget.Rendering -= handler;
                return;
            }

            double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            double eased = parkAtEnd ? EaseOutExit(progress) : EaseOutCubic(progress);
            int currentTop = (int)Math.Round(startTop + ((endTop - startTop) * eased));

            if (parkAtEnd)
                SetFadeAlpha(1 - Math.Min(1, progress / FadeOutCompleteAt));

            SetWindowPos(_hwnd, IntPtr.Zero, left, currentTop, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

            if (progress >= 1)
            {
                CompositionTarget.Rendering -= handler;
                CompleteWindowAnimation(animationVersion, left, endTop, width, height, parkAtEnd);
            }
        };

        CompositionTarget.Rendering += handler;
    }

    private void CompleteWindowAnimation(int animationVersion, int left, int top, int width, int height, bool parkAtEnd)
    {
        if (animationVersion != _animationVersion || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        MoveResize(left, top, width, height);

        if (parkAtEnd)
        {
            ParkOffScreen();
            _isHiding = false;
        }
        else
        {
            _isShowing = false;
        }
    }

    private static double EaseOutCubic(double progress)
    {
        double inverse = 1 - progress;
        return 1 - (inverse * inverse * inverse);
    }

    private static double EaseOutExit(double progress) =>
        (0.35 * progress) + (0.65 * progress * progress);

    /// <summary>Per-window alpha for the hide fade. See the flyout's <c>SetFadeAlpha</c>.</summary>
    private void SetFadeAlpha(double opacity)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        if (!_fadeStyleApplied)
        {
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            _fadeStyleApplied = true;
        }

        byte alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
    }

    private void ClearFade()
    {
        if (!_fadeStyleApplied) return;
        _fadeStyleApplied = false;

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
    }
}
