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

    /// <summary>Height of the bookmark bar, and so of the whole window while it is collapsed.</summary>
    private const int BookmarkBarHeightDips = 34;

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
    private readonly Button _forwardButton;
    private readonly Grid _header;
    private readonly Grid _root;
    private readonly StackPanel _bookmarkStrip;
    private readonly Grid _bookmarkBar;
    private readonly StackPanel _barActions;
    private readonly StackPanel _statusPanel;
    private readonly TextBlock _statusText;
    private readonly ProgressRing _statusRing;
    private readonly Button _statusAction;
    private readonly SUBCLASSPROC _wndProcDelegate;

    private MainWindow? _owner;
    private WebView2? _webView;
    private bool _webViewInitializing;

    /// <summary>
    /// Set while a reload or navigation is in flight and the browser is being kept hidden until
    /// it finishes. See <see cref="PrepareContentAsync"/>.
    /// </summary>
    private bool _revealOnNavigationCompleted;
    private string _navigatedUrl = "";

    private bool _isOpen;
    private bool _isShowing;
    private bool _isHiding;
    private bool _hasBeenShown;
    private bool _fadeStyleApplied;

    /// <summary>True while an owned window (launcher settings) is open. Pins the flyout.</summary>
    private bool _isModalOpen;
    private Window? _openModal;

    /// <summary>
    /// In bar mode, whether the content area is showing. A dismissed flyout is always collapsed
    /// again on the next open — the bar is what a bookmark launcher opens as.
    /// </summary>
    private bool _isExpanded;
    private WebBookmark? _activeBookmark;

    /// <summary>
    /// The bookmark showing when the flyout was last dismissed, restored on the next open.
    /// </summary>
    /// <remarks>
    /// Cleared when the user collapses back to the bar, because that is an explicit "close this
    /// page" — reopening onto something they just closed would be the wrong kind of memory.
    /// </remarks>
    private WebBookmark? _rememberedBookmark;

    /// <summary>
    /// Identifies the bookmark set the bar was last built from, so it is only rebuilt when the
    /// bookmarks actually change.
    /// </summary>
    /// <remarks>
    /// Rebuilding per open threw away laid-out buttons and decoded icons every time, so the
    /// first frames of each open showed labels and favicons arriving one by one and the buttons
    /// shifting as they measured. The window survives dismissal, so its bar can too.
    /// </remarks>
    private string _barSignature = "";

    /// <summary>True while the page has an element in fullscreen and the window has grown to suit.</summary>
    private bool _isFullScreen;
    private RECT _preFullScreenRect;

    private bool _isMovingWindow;
    private POINT _moveStartCursor;
    private RECT _moveStartRect;

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
        _backButton.Margin = new Thickness(-6, 0, 0, 0);

        // U+E111 is Segoe Fluent's Forward arrow — Symbol.Forward, the mirror of Back's E112.
        // Escaped rather than pasted, for the reason recorded on the back button.
        _forwardButton = BuildHeaderButton("\uE111", "Forward", (_, _) => GoForward());
        _forwardButton.IsEnabled = false;
        _forwardButton.Margin = new Thickness(0, 0, 4, 0);

        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        navButtons.Children.Add(_backButton);
        navButtons.Children.Add(_forwardButton);

        var header = _header = new Grid
        {
            Height = HeaderHeightDips,
            Padding = new Thickness(12, 0, 6, 0),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(navButtons, 0);
        Grid.SetColumn(_headerTitle, 1);
        Grid.SetColumn(headerButtons, 2);
        header.PointerPressed += BeginWindowMove;
        header.PointerMoved += ContinueWindowMove;
        header.PointerReleased += EndWindowMove;
        header.PointerCaptureLost += EndWindowMove;
        header.Children.Add(navButtons);
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

        // ── Bookmark bar (bar-mode launchers only) ──────────────────
        // Centred, not packed left. Once there are more bookmarks than fit, the scroller takes
        // over and they read as left-aligned anyway.
        _bookmarkStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stripScroller = new ScrollViewer
        {
            Content = _bookmarkStrip,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        // Collapsed, the bar is the entire window, so it carries the two actions there is no
        // header to hold. Expanded, the header takes over and these hide rather than duplicate.
        _barActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _barActions.Children.Add(BuildHeaderButton("", "Launcher settings", (_, _) => _ = OpenLauncherSettingsAsync()));
        _barActions.Children.Add(BuildHeaderButton("", "Close", (_, _) => HideFlyout()));

        _bookmarkBar = new Grid
        {
            Height = BookmarkBarHeightDips,
            Padding = new Thickness(6, 0, 4, 0),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Opaque, with a hairline above it. Without this the strip is acrylic over the page,
            // which makes its extent ambiguous — and a row of buttons floating over a website
            // reads as badly aligned even when it is centred in its own box.
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        _bookmarkBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bookmarkBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stripScroller, 0);
        Grid.SetColumn(_barActions, 1);
        _bookmarkBar.PointerPressed += BeginWindowMove;
        _bookmarkBar.PointerMoved += ContinueWindowMove;
        _bookmarkBar.PointerReleased += EndWindowMove;
        _bookmarkBar.PointerCaptureLost += EndWindowMove;
        _bookmarkBar.Children.Add(stripScroller);
        _bookmarkBar.Children.Add(_barActions);

        var root = _root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_contentHost, 1);
        Grid.SetRow(_bookmarkBar, 2);
        root.Children.Add(header);
        root.Children.Add(_contentHost);
        root.Children.Add(_bookmarkBar);
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
        if (_isFullScreen) return;
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
        ApplyRootAnchor();
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

    /// <summary>
    /// Builds bar-mode flyouts up front and composes their first frame off screen.
    /// </summary>
    /// <remarks>
    /// The same trick <c>FlyoutWindow.WarmUp</c> uses, and for the same reason: WinUI only draws
    /// a window that has actually been visible, so the first open otherwise showed the bar's
    /// buttons measuring and its icons decoding while it was already on screen.
    /// <para>This does not weaken the resource promise. What is built here is a strip of XAML —
    /// no browser is created until a bookmark is clicked, which is the expensive part and the
    /// whole reason web launchers are excluded from the flyout warm-up.</para>
    /// </remarks>
    public static void WarmUp(MainWindow owner, IEnumerable<Launcher> launchers)
    {
        foreach (var launcher in launchers)
        {
            if (Instances.ContainsKey(launcher.Id)) continue;

            var window = new WebFlyoutWindow(owner, launcher);
            Instances[launcher.Id] = window;
            window.PreRenderBarOffScreen();
        }
    }

    /// <summary>Parks the window outside the virtual screen at bar height so it composes a frame.</summary>
    private void PreRenderBarOffScreen()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        RebuildBookmarkBar(force: true);
        ApplyRootAnchor();

        double scale = GetScale();
        int width = (int)Math.Ceiling(_launcher.ResolvedWebFlyoutWidth * scale);
        int height = (int)Math.Ceiling(BookmarkBarHeightDips * scale);
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 64;
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 64;

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Drawn once, so the first real open can slide like every one after it.
        _hasBeenShown = true;
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
    /// <remarks>
    /// Scoped to the <em>profile</em>, not the launcher: on the shared profile there is one set
    /// of cookies behind several launchers, so this signs all of them out and every panel on it
    /// has to let go before the folder can be deleted.
    /// </remarks>
    public static async Task ClearBrowsingDataAsync(Launcher launcher)
    {
        var siblings = ProfileSiblings(launcher);

        foreach (string id in siblings)
        {
            if (Instances.TryGetValue(id, out var panel) && panel._webView?.CoreWebView2 is { } core)
            {
                // One clear does the whole profile — the siblings are views onto it, not copies.
                await core.Profile.ClearBrowsingDataAsync();
                return;
            }
        }

        // Nothing loaded — the profile is just a folder on disk.
        foreach (string id in siblings)
            DisposeLauncher(id);

        string folder = GetUserDataFolder(launcher);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>Where a web launcher's cookies, storage and cache live, so logins survive restarts.</summary>
    /// <remarks>
    /// A launcher on the shared profile answers with the one folder every such launcher uses, so
    /// they are one signed-in browser rather than several. Launcher ids are GUIDs, so the shared
    /// folder's name cannot collide with a private one.
    /// </remarks>
    internal static string GetUserDataFolder(Launcher launcher) =>
        launcher.WebSharedProfile ? SharedUserDataFolder : GetUserDataFolder(launcher.Id);

    internal static string GetUserDataFolder(string launcherId) =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), "WebProfiles", launcherId);

    /// <summary>The profile behind every launcher with <c>WebSharedProfile</c> set.</summary>
    internal static string SharedUserDataFolder =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), "WebProfiles", "Shared");

    /// <summary>
    /// Every launcher whose cookies live in the same folder as this one's — itself alone unless
    /// it is on the shared profile, in which case all of them are.
    /// </summary>
    private static List<string> ProfileSiblings(Launcher launcher)
    {
        if (!launcher.WebSharedProfile) return [launcher.Id];

        return SettingsManager.Current.Launchers
            .Where(l => l.IsWebLauncher && l.WebSharedProfile)
            .Select(l => l.Id)
            .ToList();
    }

    /// <summary>
    /// Drops the browser so the next open rebuilds it against whatever profile the launcher now
    /// points at. The user-data folder is fixed when the environment is created, so a launcher
    /// moved on or off the shared profile keeps using the old one until this runs.
    /// </summary>
    public static void ReloadProfile(string launcherId)
    {
        if (!Instances.TryGetValue(launcherId, out var panel)) return;

        bool wasLoaded = panel._webView != null;
        panel.UnloadWebView();

        // Only rebuild it if there is something on screen to rebuild; a dismissed panel — or a
        // collapsed bookmark bar — deliberately has no browser, and starting one here would
        // undo the whole resource promise.
        if (wasLoaded && panel._isOpen && !string.IsNullOrEmpty(panel.CurrentTargetUrl()))
            _ = panel.PrepareContentAsync();
    }

    // ── Bookmark bar ────────────────────────────────────────────────

    /// <summary>True when this launcher opens as a bar of bookmarks rather than a single page.</summary>
    private bool IsBarMode => _launcher.HasWebBookmarkBar;

    /// <summary>
    /// Rebuilds the bar from the launcher's bookmarks, in the shape a browser uses: a small icon
    /// with its label beside it, packed left, scrolling horizontally when there are too many.
    /// </summary>
    private void RebuildBookmarkBar(bool force = false)
    {
        if (!IsBarMode)
        {
            _bookmarkBar.Visibility = Visibility.Collapsed;
            _bookmarkStrip.Children.Clear();
            _barSignature = "";
            return;
        }

        string signature = string.Join("", _launcher.WebBookmarks.Select(b => $"{b.Name}{b.Url}{b.IconPath}"));
        if (!force && signature == _barSignature && _bookmarkStrip.Children.Count > 0)
        {
            // Same bookmarks as last time — the buttons are already built, laid out and decoded.
            _bookmarkBar.Visibility = Visibility.Visible;
            UpdateBookmarkBarSelection();
            return;
        }

        _barSignature = signature;
        _bookmarkStrip.Children.Clear();

        foreach (var bookmark in _launcher.WebBookmarks)
            _bookmarkStrip.Children.Add(BuildBookmarkButton(bookmark));

        _bookmarkBar.Visibility = Visibility.Visible;
        UpdateBookmarkBarSelection();
    }

    private Button BuildBookmarkButton(WebBookmark bookmark)
    {
        var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(bookmark.IconPath) && File.Exists(bookmark.IconPath))
            icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(bookmark.IconPath));

        // A page with no icon yet gets a globe rather than a hole in the row.
        var fallback = new FontIcon
        {
            Glyph = "",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = icon.Source == null ? Visibility.Visible : Visibility.Collapsed,
        };

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(bookmark.Name) ? bookmark.Url : bookmark.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
        };

        // Both icons share one fixed 16px slot rather than sitting side by side in the stack.
        // An Image with no Source still takes its Width, so a button showing the placeholder
        // reserved space for two icons and then shrank the moment its favicon arrived — which
        // shunted every button after it along the bar.
        var iconSlot = new Grid
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconSlot.Children.Add(icon);
        iconSlot.Children.Add(fallback);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(iconSlot);
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Tag = bookmark,
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, bookmark.Url);
        button.Click += (_, _) => OnBookmarkClicked(bookmark);

        // The icon arrives after the bookmark does — first from a favicon fetch, later replaced by
        // whatever the signed-in page declares — so the row keeps itself current.
        bookmark.PropertyChanged += (_, e) =>
        {
            string? changed = e.PropertyName;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (changed == nameof(WebBookmark.IconPath))
                {
                    if (string.IsNullOrEmpty(bookmark.IconPath) || !File.Exists(bookmark.IconPath)) return;
                    icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(bookmark.IconPath))
                    {
                        // The file is rewritten in place when a page reports a new icon, so the
                        // decoded-image cache would otherwise keep serving the old one.
                        CreateOptions = Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.IgnoreImageCache,
                    };
                    fallback.Visibility = Visibility.Collapsed;
                }
                else if (changed == nameof(WebBookmark.Name))
                {
                    label.Text = string.IsNullOrWhiteSpace(bookmark.Name) ? bookmark.Url : bookmark.Name;
                }
            });
        };

        return button;
    }

    /// <summary>Tints the bookmark whose page is currently showing.</summary>
    private void UpdateBookmarkBarSelection()
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var clear = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];

        foreach (var child in _bookmarkStrip.Children)
        {
            if (child is not Button button) continue;
            bool active = _isExpanded && ReferenceEquals(button.Tag, _activeBookmark);
            button.Background = active ? accent : clear;
        }
    }

    /// <summary>
    /// Clicking a bookmark expands the flyout onto it; clicking the one already showing collapses
    /// back to just the bar, so the same button both opens and closes it.
    /// </summary>
    private void OnBookmarkClicked(WebBookmark bookmark)
    {
        if (_isExpanded && ReferenceEquals(bookmark, _activeBookmark))
        {
            CollapseToBar();
            return;
        }

        _activeBookmark = bookmark;
        _rememberedBookmark = bookmark;
        UpdateBookmarkBarSelection();

        if (_isExpanded)
        {
            // Already open on another bookmark — just go there.
            _ = PrepareContentAsync();
            return;
        }

        ExpandToContent();
    }

    /// <summary>
    /// Grows the window from bar height to full height, away from the edge it is anchored to.
    /// </summary>
    /// <remarks>
    /// The flyout sits against the taskbar, so the anchored edge is the one that must not move:
    /// growing upwards from a bottom-anchored bar keeps the bar exactly where the pointer already
    /// is. Anchored at the top, the same rule means growing downwards instead.
    /// </remarks>
    private void ExpandToContent()
    {
        if (_isExpanded) return;
        _isExpanded = true;

        _header.Visibility = Visibility.Visible;
        _barActions.Visibility = Visibility.Collapsed;
        UpdateBookmarkBarSelection();

        ApplyExpansionGeometry();
        _ = PrepareContentAsync();
    }

    /// <summary>Returns to just the bar, and lets the browser go with it.</summary>
    private void CollapseToBar()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        _activeBookmark = null;
        _rememberedBookmark = null;

        _header.Visibility = Visibility.Collapsed;
        _barActions.Visibility = Visibility.Visible;
        UpdateBookmarkBarSelection();

        ApplyExpansionGeometry();

        // Collapsed is hidden as far as the page is concerned: none of it is on screen, so it gets
        // the same treatment as a dismissed flyout rather than being left running behind a bar.
        ApplyHiddenPolicy();
    }

    /// <summary>
    /// Fixes the XAML layout at its expanded size and pins it to the anchored edge.
    /// </summary>
    /// <remarks>
    /// This is what makes the expansion feel anchored. Left to itself the root grid is sized by
    /// the window, so every frame of a growing window is a fresh layout pass — and XAML's
    /// re-layout does not land in the same frame as the window move, so the content appears to
    /// drift downwards at its own rate while the frame grows. Sizing the root once, to the
    /// expanded height, and aligning it to the edge that is not moving turns the animation into
    /// a pure reveal: the content never re-flows, the window just uncovers more of it.
    /// </remarks>
    private void ApplyRootAnchor()
    {
        if (!IsBarMode)
        {
            _root.ClearValue(FrameworkElement.HeightProperty);
            _root.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        _root.Height = _launcher.ResolvedWebFlyoutHeight;
        _root.VerticalAlignment = _lastEntranceEdge == SlideEdge.Top
            ? VerticalAlignment.Top
            : VerticalAlignment.Bottom;
    }

    /// <summary>Moves and resizes the window for the current expanded state.</summary>
    private void ApplyExpansionGeometry()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || !GetWindowRect(_hwnd, out var rect)) return;

        ApplyRootAnchor();

        double scale = GetScale();
        int targetHeight = (int)Math.Ceiling(CurrentContentHeightDips() * scale);
        int width = rect.Right - rect.Left;
        int currentHeight = rect.Bottom - rect.Top;
        if (targetHeight == currentHeight) return;

        // Keep the anchored edge still and move the other one.
        int targetTop = _lastEntranceEdge == SlideEdge.Top ? rect.Top : rect.Bottom - targetHeight;

        // Deliberately not animated, after two attempts at it.
        //
        // A window hosting a browser cannot be smoothly resized frame by frame: the window frame,
        // the XAML island's surface and WebView2's own composition surface are resized by
        // different parts of the system and do not land in the same frame. The content therefore
        // lags the frame and appears to drift downwards while the window grows upwards, however
        // the geometry is eased — pinning the layout to the anchored edge reduced the reflow but
        // could not fix the surface lag. Snapping to the target has no in-between state to be
        // wrong, so the bar stays exactly where it was and the page is simply there.
        _animationVersion++;
        MoveResize(rect.Left, targetTop, width, targetHeight);
    }

    /// <summary>Window height for the current state, in DIPs.</summary>
    private double CurrentContentHeightDips() =>
        IsBarMode && !_isExpanded ? BookmarkBarHeightDips : _launcher.ResolvedWebFlyoutHeight;

    // ── Show / hide ─────────────────────────────────────────────────

    private void ShowFlyout(int screenX, int screenY)
    {
        // Rebuilt per open: bookmarks can have been added or renamed since the last one.
        RebuildBookmarkBar();

        // A bar launcher always opens as just the bar. Anything else would mean remembering a
        // page across dismissals, which is exactly the state the resource policy tears down.
        if (IsBarMode)
        {
            // Whatever was open when it was last dismissed wins; the configured default only
            // applies when there is nothing to restore. Without either, it opens as just the bar
            // and nothing loads until the user picks something.
            var remembered = _rememberedBookmark != null && _launcher.WebBookmarks.Contains(_rememberedBookmark)
                ? _rememberedBookmark
                : null;
            _activeBookmark = remembered ?? _launcher.DefaultWebBookmark;
            _isExpanded = _activeBookmark != null;
            _header.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
            _barActions.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            _header.Visibility = Visibility.Visible;
        }

        var placement = CalculatePlacement(screenX, screenY);
        _lastEntranceEdge = placement.Edge;
        ApplyRootAnchor();

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
        // A collapsed bar starts nothing at all: the whole point is that opening the flyout
        // costs nothing until a bookmark is actually chosen.
        if (!IsBarMode || _isExpanded)
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

        if (_isFullScreen) ApplyFullScreen(false);

        var webView = _webView;
        _webView = null;
        _navigatedUrl = "";
        _revealOnNavigationCompleted = false;

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
        UpdateNavigationButtons();   // the history went with the browser
    }

    // ── Content ─────────────────────────────────────────────────────

    /// <summary>The address the content area should be showing right now.</summary>
    private string CurrentTargetUrl() =>
        IsBarMode ? (_activeBookmark?.Url ?? "") : _launcher.ResolvedSingleWebUrl;

    private async Task PrepareContentAsync()
    {
        string url = NormalizeUrl(CurrentTargetUrl());
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

        ResumeWebView();
        ApplyZoom();

        bool navigating = !string.Equals(_navigatedUrl, url, StringComparison.OrdinalIgnoreCase);
        bool reloading = !navigating && _launcher.WebReloadOnShow;

        // Resuming a suspended browser puts its last painted frame back on screen instantly, so
        // showing it while a reload is already queued means the user watches the *old* page
        // appear and then get replaced. Keep it hidden until the new content is ready — hidden
        // only stops rendering, not loading, so the navigation still runs.
        if (navigating || reloading)
        {
            _revealOnNavigationCompleted = true;
            _webView.Visibility = Visibility.Collapsed;
            SetStatus("Loading…", busy: true, showRetry: false);

            if (navigating) Navigate(url);
            else ReloadPage();
            return;
        }

        // Nothing queued: the page is live and current, so show it straight away.
        _webView.Visibility = Visibility.Visible;
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
            string userDataFolder = GetUserDataFolder(_launcher);
            Directory.CreateDirectory(userDataFolder);

            // A per-launcher profile keeps each panel signed in independently and, more to the
            // point, keeps it signed in at all — the alternative is re-authenticating to a home
            // dashboard on every app restart. Launchers opted into the shared profile point at
            // one folder instead; safe to do from several environments in this process because
            // every one of them is created with identical options (WebView2 rejects a second
            // environment on the same folder only when the options differ).
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

        // CurrentTargetUrl, not WebUrl: in bar mode the address is whichever bookmark was
        // clicked. Reading the launcher's single address here meant the first click on any
        // bookmark loaded the launcher's own URL instead — and then the page's favicon was
        // adopted onto the bookmark that had been clicked, so it took that page's icon too.
        Navigate(NormalizeUrl(CurrentTargetUrl()));
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

        core.HistoryChanged += (_, _) => UpdateNavigationButtons();

        // A page going fullscreen only resizes its own element; making the window fill the
        // screen is the host's job. Without this, "fullscreen" video is still boxed inside
        // whatever size the flyout happens to be.
        core.ContainsFullScreenElementChanged += (_, _) => ApplyFullScreen(core.ContainsFullScreenElement);

        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                HideStatus();
                ApplyZoom();   // CSS zoom lives in the document, so each navigation drops it
                UpdateNavigationButtons();
                RevealWebViewIfPending();
                return;
            }

            // A failed navigation still has to give the browser back, or the flyout is stuck
            // showing a spinner over a hidden page with no way out but Reload.
            RevealWebViewIfPending();

            SetStatus($"Could not load {NormalizeUrl(_launcher.WebUrl)} ({e.WebErrorStatus}).",
                busy: false, showRetry: true);
        };

        core.ProcessFailed += (_, _) =>
        {
            SetStatus("The page stopped responding.", busy: false, showRetry: true);
            RevealWebViewIfPending();
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

    /// <summary>Where a bookmark's icon is cached. Keyed by URL so it survives reordering.</summary>
    internal static string GetBookmarkIconPath(string launcherId, string url)
    {
        byte[] hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(url ?? ""));
        string key = Convert.ToHexString(hash)[..12];
        return Path.Combine(MainWindow.GetPhysicalAppDataDir(), $"web-bookmark-{launcherId}-{key}.png");
    }

    /// <summary>Saves the page's declared icon onto the bookmark that opened it.</summary>
    private async Task AdoptBookmarkIconAsync(CoreWebView2 core, WebBookmark bookmark)
    {
        // Only adopt an icon from the site the bookmark actually points at. Belt and braces
        // against a mis-navigation: this is how a bookmark ended up wearing another site's logo,
        // and an icon quietly rewritten to the wrong thing is far harder to notice than a page
        // opening on the wrong address.
        if (!SameHost(core.Source, bookmark.Url))
        {
            Logger.Debug("Skipping icon adoption: {Source} does not match bookmark {Url}", core.Source, bookmark.Url);
            return;
        }

        try
        {
            var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null || stream.Size == 0) return;

            uint size = (uint)stream.Size;
            var buffer = new byte[size];
            using (var reader = new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(buffer);
            }

            string path = GetBookmarkIconPath(_launcher.Id, bookmark.Url);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, buffer);
            File.Move(temp, path, overwrite: true);

            // Assigning the same path still raises PropertyChanged, which is what refreshes the
            // bar button — the file changed even though the string did not.
            bookmark.IconPath = "";
            bookmark.IconPath = path;
            SettingsManager.SaveSettings();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Adopting the page icon for bookmark {Name} failed", bookmark.Name);
        }
    }

    /// <summary>Saves the page's declared icon and makes it the launcher's tray icon.</summary>
    private async Task AdoptPageIconAsync(CoreWebView2 core)
    {
        if (string.IsNullOrEmpty(core.FaviconUri)) return;

        // In bar mode the icon identifies the bookmark, not the launcher: the tray icon has to
        // keep standing for the launcher as a whole, which is several sites rather than one.
        if (IsBarMode)
        {
            if (_activeBookmark != null)
                await AdoptBookmarkIconAsync(core, _activeBookmark);
            return;
        }

        if (!MayAdoptPageIcon(_launcher)) return;

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

    /// <summary>
    /// Drags the flyout to a new position, following the pointer, and remembers where it lands.
    /// </summary>
    /// <remarks>
    /// <para>Tracked manually rather than handed to the system's move loop via
    /// <c>WM_NCLBUTTONDOWN</c>/<c>HTCAPTION</c>. That handoff does not track inside a XAML
    /// island: the window stayed put for the whole drag and jumped to the final position on
    /// release. The resize grips already move this window by hand for the same reason, so this
    /// follows them.</para>
    /// <para>Any flyout can be moved, pinned or not. Restricting it to pinned ones was wrong —
    /// a moved flyout simply reopens where it was put, which is what moving something is
    /// expected to mean.</para>
    /// <para>Clicks on the header's buttons never reach here: they mark the event handled.</para>
    /// </remarks>
    private void BeginWindowMove(object sender, PointerRoutedEventArgs e)
    {
        if (_isFullScreen) return;
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (!GetWindowRect(_hwnd, out _moveStartRect)) return;

        GetCursorPos(out _moveStartCursor);
        _isMovingWindow = true;

        // A slide still in flight would otherwise keep writing its own positions.
        _animationVersion++;
        _isShowing = false;
        _isHiding = false;

        if (sender is UIElement element) element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ContinueWindowMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMovingWindow) return;

        GetCursorPos(out var cursor);
        int width = _moveStartRect.Right - _moveStartRect.Left;
        int height = _moveStartRect.Bottom - _moveStartRect.Top;

        SetWindowPos(_hwnd, IntPtr.Zero,
            _moveStartRect.Left + (cursor.X - _moveStartCursor.X),
            _moveStartRect.Top + (cursor.Y - _moveStartCursor.Y),
            width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void EndWindowMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMovingWindow) return;
        _isMovingWindow = false;

        if (sender is UIElement element) element.ReleasePointerCapture(e.Pointer);
        RememberFlyoutPosition();
        e.Handled = true;
    }

    /// <summary>
    /// Stores where the flyout was dragged to, when the launcher is set to remember it.
    /// </summary>
    /// <remarks>
    /// Without <see cref="Launcher.WebRememberPosition"/> the move is deliberately not written
    /// anywhere: it holds for as long as the flyout stays open, and the next one anchors to the
    /// tray as usual.
    /// </remarks>
    private void RememberFlyoutPosition()
    {
        if (!_launcher.WebRememberPosition) return;
        if (!GetWindowRect(_hwnd, out var rect)) return;

        string position = $"{rect.Left},{rect.Top}";
        if (position == _launcher.WebFlyoutPosition) return;

        _launcher.WebFlyoutPosition = position;
        SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyLaunchersChanged();
    }

    /// <summary>
    /// Grows the flyout to fill its monitor while the page is showing something fullscreen, and
    /// puts it back afterwards.
    /// </summary>
    /// <remarks>
    /// The whole monitor, not the work area — fullscreen means over the taskbar too. The header
    /// and bookmark bar are hidden for the duration, and the root's fixed bar-mode height is
    /// released so the page can actually fill the window rather than being clipped to the size
    /// the flyout was.
    /// </remarks>
    private void ApplyFullScreen(bool fullScreen)
    {
        if (fullScreen == _isFullScreen) return;
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        if (fullScreen)
        {
            if (!GetWindowRect(_hwnd, out _preFullScreenRect)) return;
            _isFullScreen = true;

            _header.Visibility = Visibility.Collapsed;
            _bookmarkBar.Visibility = Visibility.Collapsed;
            _root.ClearValue(FrameworkElement.HeightProperty);
            _root.VerticalAlignment = VerticalAlignment.Stretch;

            // The page has to reach the actual edges. Two things otherwise frame it: the inset
            // that keeps the browser clear of the resize grips, which shows as a band of acrylic
            // down each side, and the window's rounded corners, which cut the corners off a
            // screen-filling video.
            _contentHost.Margin = new Thickness(0);
            int squareCorners = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref squareCorners, sizeof(int));

            var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), ref monitorInfo);
            var bounds = monitorInfo.rcMonitor;

            SetWindowPos(_hwnd, IntPtr.Zero, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return;
        }

        _isFullScreen = false;

        // Back to whatever the flyout was: bar mode restores its own chrome and anchoring.
        _header.Visibility = IsBarMode && !_isExpanded ? Visibility.Collapsed : Visibility.Visible;
        _contentHost.Margin = new Thickness(GripThickness, 0, GripThickness, GripThickness);
        int roundedCorners = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundedCorners, sizeof(int));

        RebuildBookmarkBar();
        ApplyRootAnchor();

        SetWindowPos(_hwnd, IntPtr.Zero, _preFullScreenRect.Left, _preFullScreenRect.Top,
            _preFullScreenRect.Right - _preFullScreenRect.Left,
            _preFullScreenRect.Bottom - _preFullScreenRect.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Steps back through the page's own history, not the flyout's.</summary>
    private void GoBack()
    {
        var core = _webView?.CoreWebView2;
        if (core?.CanGoBack != true) return;
        core.GoBack();
    }

    /// <summary>Steps forward again after a Back.</summary>
    private void GoForward()
    {
        var core = _webView?.CoreWebView2;
        if (core?.CanGoForward != true) return;
        core.GoForward();
    }

    /// <summary>
    /// Greys out Back and Forward when there is nowhere to go in that direction.
    /// </summary>
    /// <remarks>
    /// Driven by <c>HistoryChanged</c> rather than <c>NavigationCompleted</c>: a dashboard is
    /// usually a single-page app, so most of its navigation is history pushed by script with no
    /// document load to hang this off. That event covers Forward too — going back is exactly
    /// what makes Forward available, and it raises no navigation of its own to listen for.
    /// </remarks>
    private void UpdateNavigationButtons()
    {
        var core = _webView?.CoreWebView2;
        _backButton.IsEnabled = core?.CanGoBack == true;
        _forwardButton.IsEnabled = core?.CanGoForward == true;
    }

    /// <summary>Shows the browser again once the content it was waiting for has arrived.</summary>
    private void RevealWebViewIfPending()
    {
        if (!_revealOnNavigationCompleted) return;
        _revealOnNavigationCompleted = false;
        if (_webView != null) _webView.Visibility = Visibility.Visible;
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
        RebuildBookmarkBar();
        _headerTitle.Text = _launcher.Name;
        Title = _launcher.Name;
        ApplyZoom();

        // CurrentTargetUrl, not WebUrl. This runs whenever the launcher changes — including
        // after a bookmark's favicon fetch completes — so reading the launcher's single address
        // here yanked an open bookmark over to it, and the arriving page's icon was then adopted
        // onto whichever bookmark was showing. An empty target means the bar is collapsed with
        // nothing open, which is not an instruction to navigate anywhere.
        string url = NormalizeUrl(CurrentTargetUrl());
        if (!string.IsNullOrEmpty(url) && !string.Equals(_navigatedUrl, url, StringComparison.OrdinalIgnoreCase))
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
        int height = (int)Math.Ceiling(CurrentContentHeightDips() * scale);
        MoveResize(rect.Left, rect.Top, width, height);
    }

    /// <summary>True when two URLs point at the same host, ignoring scheme and path.</summary>
    private static bool SameHost(string? a, string? b)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(new Uri(NormalizeUrl(a)).Host, new Uri(NormalizeUrl(b)).Host,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
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
        if (_isShowing || !_isOpen || _launcher.WebPinFlyout || _isModalOpen || _isResizing || _isMovingWindow || _isFullScreen) return;

        // The browser's own HWNDs are children of this window, so clicking into the page
        // deactivates the XAML window without the user having gone anywhere. Read the
        // foreground window once the switch has settled rather than trusting the event alone.
        DispatcherQueue.TryEnqueue(() =>
        {
            // Re-checked, not just checked above. The decision to dismiss is made here, one
            // dispatcher turn after the event that prompted it, and the state can have moved on
            // in between — a pin toggled, a modal opened, a resize begun. Evaluating a condition
            // at one moment and acting on it at another is exactly how a pinned flyout ends up
            // dismissed anyway.
            if (!_isOpen || _isShowing || _isHiding) return;
            if (_launcher.WebPinFlyout || _isModalOpen || _isResizing || _isMovingWindow || _isFullScreen) return;

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
        int height = (int)Math.Ceiling(CurrentContentHeightDips() * scale);
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

        // A flyout that has been moved opens where it was left, not back at the tray — clamped
        // into the work area so a screen that has since gone away cannot strand it.
        if (_launcher.WebRememberPosition && _launcher.GetWebFlyoutPosition() is { } saved)
        {
            var savedPoint = new POINT { X = saved.X, Y = saved.Y };
            var savedMonitor = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(MonitorFromPoint(savedPoint, MONITOR_DEFAULTTONEAREST), ref savedMonitor);
            var area = savedMonitor.rcWork;

            int savedLeft = Math.Clamp(saved.X, area.Left, Math.Max(area.Left, area.Right - width));
            int savedTop = Math.Clamp(saved.Y, area.Top, Math.Max(area.Top, area.Bottom - height));
            return new FlyoutPlacement(savedLeft, savedTop, savedTop, width, height, SlideEdge.Bottom);
        }

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
