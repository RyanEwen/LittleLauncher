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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// The flyout a web launcher opens: a tray-anchored window hosting a WebView2 on the launcher's
/// <see cref="Launcher.WebAddress"/>. One instance per launcher, created on first use.
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
/// <para>Site permissions (camera, microphone, location) and page notifications live in the
/// partial <c>WebFlyoutWindow.Permissions.cs</c>.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow : Window
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
    private readonly Button _maximizeButton;
    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly Button _moreButton;

    /// <summary>Shows and hides the address bar. Its glyph reports which it will do.</summary>
    private readonly Button _addressBarButton;
    private readonly Grid _addressBar;
    private readonly TextBox _addressBox;
    private readonly Grid _header;

    /// <summary>The header's right-hand button strip, which extension buttons are inserted into.</summary>
    private readonly StackPanel _headerButtons;
    private readonly Grid _root;
    private readonly Controls.OverflowStripPanel _bookmarkStrip;
    private readonly Grid _bookmarkBar;
    /// <summary>
    /// The thread of progress under the chrome, for a page that is already on screen.
    /// </summary>
    /// <remarks>
    /// A browser does not grey out the page you are reading to tell you the next one is coming, and
    /// neither should this. The status overlay is right when there is nothing else to look at and
    /// wrong the moment there is: it is a spinner and a word, centred over content the user can
    /// still read and scroll. This is the other half of that answer.
    /// </remarks>
    private readonly ProgressBar _loadingBar = new()
    {
        Height = 2,
        Minimum = 0,
        Maximum = 100,
        IsIndeterminate = false,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0),
    };

    private readonly StackPanel _statusPanel;
    private readonly TextBlock _statusText;
    private readonly ProgressRing _statusRing;
    private readonly Button _statusAction;
    private readonly SUBCLASSPROC _wndProcDelegate;

    private MainWindow? _owner;

    /// <summary>
    /// The browser currently on screen — the active tab's. Every browser this launcher owns is a
    /// tab; see <c>WebFlyoutWindow.Tabs.cs</c>.
    /// </summary>
    private WebView2? _webView;

    /// <summary>
    /// True once the user has put keyboard focus into the page, so a reopen should put it back.
    /// </summary>
    /// <remarks>
    /// <para>Chromium keeps the rest by itself: across a dismissal the document's
    /// <c>activeElement</c> and the caret position both survive — measured, a caret left at offset 9
    /// in a textarea came back at offset 9. The only thing missing was anyone handing focus back to
    /// the browser, because the show path calls <c>SetFocus</c> on the flyout's top-level window and
    /// nothing routes that into the WebView2.</para>
    /// <para><b>Only when the page had focus</b>, which is what keeps this from costing anything.
    /// Focus in the page means Escape belongs to the page — <c>WM_KEYDOWN</c> goes to the browser's
    /// child windows and never reaches this window's subclass — so restoring it unconditionally
    /// would quietly take Escape-to-dismiss away from every launcher. Restoring it only for someone
    /// who was already typing in the page trades the key away exactly where they had already given
    /// it up, and leaves it working everywhere else.</para>
    /// </remarks>
    private bool _pageHadFocus;
    private bool _webViewInitializing;

    /// <summary>
    /// Set while a reload or navigation is in flight and the browser is being kept hidden until
    /// it finishes. See <see cref="PrepareContentAsync"/>.
    /// </summary>
    private bool _revealOnNavigationCompleted;

    private bool _isOpen;
    private bool _isShowing;
    private bool _isHiding;
    private bool _hasBeenShown;
    private bool _fadeStyleApplied;

    /// <summary>True while an owned window (launcher settings) is open. Pins the flyout.</summary>
    private bool _isModalOpen;

    /// <summary>
    /// True while the header's More menu is open. Pins the flyout, like an owned window.
    /// </summary>
    /// <remarks>
    /// A <c>MenuFlyout</c> that is allowed to overflow this window is hosted in a popup of its own,
    /// so opening it deactivates the flyout — which would dismiss it and take the menu down in the
    /// same motion. Same reason <c>_isModalOpen</c> exists.
    /// </remarks>
    private bool _isMenuOpen;

    /// <summary>
    /// True when this launcher is a regular window that should not dismiss itself on focus loss.
    /// </summary>
    /// <remarks>
    /// The default for a window — an ordinary app window does not vanish when you click another
    /// one, and its taskbar button is how it gets closed instead. <c>WebWindowAutoHide</c> opts
    /// back into a flyout's dismissal while keeping the taskbar button and switcher entry, which
    /// is a combination neither mode offers on its own.
    /// </remarks>
    private bool StaysOpenAsWindow => _launcher.WebRegularWindow && !_launcher.WebWindowAutoHide;
    private Window? _openModal;

    /// <summary>
    /// Runs only while a dialog the flyout raised — a file picker, a passkey prompt — holds the
    /// foreground. See <see cref="StartForegroundWatch"/>.
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _foregroundWatchTimer;

    /// <summary>
    /// Where this launcher's own tab was last explicitly sent, so a reopen returns there.
    /// </summary>
    /// <remarks>
    /// <para>A URL rather than a <see cref="WebBookmark"/>, because the bar does not decide what is
    /// showing — the tab does. Typing an address moves this exactly as clicking a bookmark does,
    /// which is the whole of what "the bar is stateless" means here: it opens pages, it does not
    /// own the one that is open.</para>
    /// <para>Set only by the two gestures that mean "go here" — a bookmark click and the address
    /// box — never from inside <c>Navigate</c>. Empty therefore means "the user has not steered
    /// this launcher anywhere", which is what lets a settings change to its address still move the
    /// page while a page the user chose is left alone.</para>
    /// <para>Session state, not settings: it survives an idle unload, which is the point, and not a
    /// restart, which would be a promise the resource model does not make.</para>
    /// </remarks>
    private string _rememberedUrl = "";

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

    /// <summary>
    /// True while the user has grown the flyout to fill its monitor's work area from the header.
    /// </summary>
    /// <remarks>
    /// Deliberately not a launcher setting: this is "let me look at the whole dashboard for a
    /// minute", not a new size for the launcher. It is dropped when the flyout is dismissed
    /// (<see cref="ParkOffScreen"/>), so the next open is at the configured size — which is also
    /// why nothing on this path may write <see cref="Launcher.WebFlyoutWidth"/> /
    /// <see cref="Launcher.WebFlyoutHeight"/>.
    /// </remarks>
    private bool _isMaximized;
    private RECT _preMaximizeRect;

    /// <summary>
    /// True when a drag-resize has been made under <see cref="Launcher.WebLockSize"/> and so was
    /// never written to the launcher — the flyout is currently a size that must not outlive it.
    /// </summary>
    /// <remarks>
    /// No "pre-resize" rect is kept alongside it, unlike <see cref="_preMaximizeRect"/>: the size
    /// to go back to is always the launcher's configured one, since that is what an open produces.
    /// </remarks>
    private bool _hasTemporaryResize;

    /// <summary>The edge and corner grips, kept so they can be hidden when resizing is impossible.</summary>
    private readonly List<ResizeGrip> _resizeGrips = [];

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
        presenter.IsAlwaysOnTop = !launcher.WebRegularWindow || launcher.WebPinFlyout;

        // A context-menu presenter is not minimizable, and the shell will not send a minimize to a
        // window that says it cannot be — so in regular-window mode a click on the taskbar button
        // produced nothing at all, neither the minimize nor the close that is meant to intercept
        // it. This is what makes that click reach the window in the first place.
        presenter.IsMinimizable = launcher.WebRegularWindow;
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

        // Sized for the moment, not for good. A tray flyout is small because that is what it is
        // for, but a dashboard occasionally wants the whole screen — and being able to grow it
        // there and back beats resizing the launcher and putting it back afterwards.
        _maximizeButton = BuildHeaderButton(MaximizeGlyph(false), MaximizeTooltip(false), (_, _) => ToggleMaximized());

        // Reload acts on the page, not on the flyout, so it sits with Back and Forward on the
        // left where a browser puts it — the same reasoning that moved Back there. Built here,
        // added to the nav group below.
        //
        // U+E72C is Segoe Fluent's Refresh glyph, escaped rather than pasted for the reason
        // recorded on the back button.
        var reloadButton = BuildHeaderButton("\uE72C", "Reload", (_, _) => ReloadPage());
        reloadButton.Margin = new Thickness(0, 0, 4, 0);

        // The gear used to open launcher settings directly. It now opens a menu of the per-launcher
        // options that are per-moment decisions \u2014 how this launcher presents itself, and whether it
        // gets out of the way \u2014 with the full settings window still one item down. The "\u2026" is the
        // same idiom the item cards already use for their context menus, so the affordance is not a
        // new one to learn.
        //
        // U+E712 is Segoe Fluent's More glyph, escaped for the reason on the back button.
        _moreButton = BuildHeaderButton("\uE712", "More", (_, _) => ShowMoreMenu());

        var headerButtons = _headerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        headerButtons.Children.Add(_moreButton);
        // The address bar's toggle, where Open in browser used to be. Open in browser stays in the
        // "…" menu, which is where a once-in-a-while action belongs; showing the address is the
        // per-moment decision, and the one worth a button — it is how you see where you are, type
        // somewhere else, and reach the bookmark star.
        _addressBarButton = BuildHeaderButton("", "Address bar", (_, _) => ToggleAddressBar());
        headerButtons.Children.Add(_addressBarButton);
        // Pin sits beside maximize rather than at the head of the group: both decide how the
        // flyout behaves as a window, and the page actions between them made that read as two
        // unrelated buttons.
        headerButtons.Children.Add(_pinButton);
        headerButtons.Children.Add(_maximizeButton);
        headerButtons.Children.Add(BuildHeaderButton("", "Close", (_, _) => HideFlyout(), redOnHover: true));

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

        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        navButtons.Children.Add(_backButton);
        navButtons.Children.Add(_forwardButton);
        navButtons.Children.Add(reloadButton);

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

        // ── Address bar ─────────────────────────────────────────────
        // A row of chrome under the header rather than an overlay on the page, for the same
        // reason the permission prompt is a row: anything floating over a hosted browser depends
        // on how WebView2 routes input, and an address bar that cannot be clicked into is worse
        // than one that costs a little height.
        _addressBox = new TextBox
        {
            PlaceholderText = "Address",
            FontSize = 12,
            MinHeight = 0,
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _addressBox.KeyDown += AddressBox_KeyDown;
        // Clicking in selects the whole address, as every browser does — the common reasons to
        // come here are replacing it and copying it, and both want it selected.
        _addressBox.GotFocus += (_, _) => _addressBox.SelectAll();

        _addressBar = new Grid
        {
            // Sized by its content, not pinned: a fixed height clips the box on any scale or font
            // where the default TextBox is taller than the number chosen here. It eats into the
            // page rather than the window, so nothing downstream depends on how tall it comes out.
            //
            // Padded top as well as bottom. With no top padding the box sat directly on the tab
            // strip's hairline, which read as one control growing out of another rather than as two
            // rows of chrome.
            Padding = new Thickness(12, 6, 8, 6),
            Visibility = Visibility.Collapsed,
            // A hairline below, not above: the header and this read as one block of chrome, and
            // the line that matters is the one separating chrome from page.
            BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        // The star sits at the end of the address, where every browser puts it, because it is
        // about the address rather than about the window — the same reasoning that keeps Back and
        // Reload on the left of the header instead of among the window controls.
        _bookmarkStar = BuildBookmarkStar();

        _addressBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _addressBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_addressBox, 0);
        Grid.SetColumn(_bookmarkStar, 1);
        _addressBar.Children.Add(_addressBox);
        _addressBar.Children.Add(_bookmarkStar);

        // The star answers for whatever the box says, so it has to follow it while it is being
        // typed into — an address half-typed is not one the bar holds, and a star still showing
        // filled would be claiming otherwise.
        _addressBox.TextChanged += (_, _) => UpdateBookmarkStar();

        // Header, tab strip and address bar travel together in root row 0, so the root's own rows —
        // and the resize grips' row spans — stay exactly as they were. The tabs sit between the two
        // for the reason a browser puts them there: they choose the page, the address bar describes
        // whichever one they chose.
        var chrome = new StackPanel();
        chrome.Children.Add(header);
        chrome.Children.Add(BuildTabBar());
        chrome.Children.Add(_addressBar);

        // Last in the stack, so it sits directly on top of the page it describes, exactly where a
        // browser puts it.
        chrome.Children.Add(_loadingBar);

        // Everything that hides the header — collapsing to a bookmark bar, a page going
        // fullscreen — means to hide the chrome, so the address bar and the tab strip follow it
        // rather than needing a matching line added at each of those call sites.
        header.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            ApplyAddressBarVisibility();
            ApplyTabBarVisibility();
        });

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

        // Row 0 is the prompt bar; the browser and the status overlay share row 1. A row of its
        // own, not an overlay: the same reasoning as the resize grips above — buttons floating
        // over a hosted browser depend on how WebView2 routes input, and a permission prompt the
        // user cannot click is worse than one that costs a little height. This is also what a
        // browser does with an infobar.
        _contentHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _contentHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(_statusPanel, 1);
        _contentHost.Children.Add(_statusPanel);

        // The bar the flyout asks its questions in — camera, microphone, notifications. Built up
        // front and left collapsed; it takes no height until something is asked.
        BuildPromptBar();

        // ── Bookmark bar (bar-mode launchers only) ──────────────────
        // Centred while everything fits. Once it does not, the panel packs what it can from the
        // left and hands the rest to the chevron beside it, which is what a browser does and what
        // the horizontal scroller here could not: a 34px strip has no visible scrollbar, so the
        // bookmarks past the edge were not merely off screen, nothing said they existed.
        _bookmarkStrip = new Controls.OverflowStripPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _bookmarkStrip.VisibleCountChanged += _ => UpdateBookmarkOverflowButton();

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
        // The strip, and the chevron for whatever it could not fit. The chevron's own column is
        // what keeps the two from fighting: it is Auto, so while it is collapsed the strip has the
        // whole bar, and the moment it appears the strip re-measures against what is left. That
        // settles in one step rather than oscillating, because losing width can only ever push
        // more bookmarks into the menu, never pull them back out.
        _bookmarkOverflow = BuildBookmarkOverflowButton();
        _bookmarkBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bookmarkBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_bookmarkStrip, 0);
        Grid.SetColumn(_bookmarkOverflow, 1);
        _bookmarkBar.PointerPressed += BeginWindowMove;
        _bookmarkBar.PointerMoved += ContinueWindowMove;
        _bookmarkBar.PointerReleased += EndWindowMove;
        _bookmarkBar.PointerCaptureLost += EndWindowMove;
        _bookmarkBar.Children.Add(_bookmarkStrip);
        _bookmarkBar.Children.Add(_bookmarkOverflow);

        // The caret that marks where a dragged bookmark will land, in a layer of its own over the
        // strip's own cell. Drawn rather than mimed by shuffling the buttons, for the reason
        // recorded on BookmarkStrip_DragOver — and in an overlay so it adds nothing to the strip's
        // layout, which is what the caret's position is measured from.
        _dropCaret = new Border
        {
            Width = 2,
            Height = DropCaretHeightDips,
            CornerRadius = new CornerRadius(1),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Visibility = Visibility.Collapsed,
        };
        _barOverlay = new Canvas { IsHitTestVisible = false };
        _barOverlay.Children.Add(_dropCaret);
        Grid.SetColumn(_barOverlay, 0);
        _bookmarkBar.Children.Add(_barOverlay);

        WireBookmarkStripDrop();

        var root = _root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(chrome, 0);
        Grid.SetRow(_contentHost, 1);
        Grid.SetRow(_bookmarkBar, 2);
        root.Children.Add(chrome);
        root.Children.Add(_contentHost);
        root.Children.Add(_bookmarkBar);
        AddResizeGrips(root);

        // Escape closes the panel. This only fires while focus is on the XAML tree; once the
        // page has focus the browser owns the key, which is why the header keeps a close button.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // The browser keys, for when focus is on the chrome rather than in the page. The page has
        // its own copy; both end in InvokeShortcut.
        InstallShortcutAccelerators(root);

        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            // Escape while editing the address means "forget what I typed", not "close the
            // window" — checked here rather than relying on the TextBox marking the key handled
            // first, because an accelerator and a bubbling KeyDown do not resolve in a fixed order.
            if (CancelAddressEditing()) return;
            HideFlyout();
        };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        ThemeManager.ApplySavedTheme(this);

        // The header's visibility callback only fires on a change, so the first state is set here.
        ApplyAddressBarVisibility();
        ApplyTabBarVisibility();

        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 3, 0);

        // Whatever is installed already gets a button now; an install later refreshes them.
        RefreshExtensionButtons();

        Activated += WebFlyoutWindow_Activated;
    }

    /// <param name="redOnHover">
    /// Tints the glyph with the system's critical colour while the pointer is over it — for Close,
    /// which is the one button here that throws away what is on screen. A theme brush rather than
    /// a literal red, so it stays legible against both the light and dark acrylic header.
    /// </param>
    private static Button BuildHeaderButton(string glyph, string tooltip, RoutedEventHandler onClick, bool redOnHover = false)
    {
        var transparent = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];

        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = transparent,
            BorderThickness = new Thickness(0),
        };

        // A disabled button has to read as *less* than an enabled one. The default template paints
        // the disabled state with ControlFillColorDisabled, and against buttons that are otherwise
        // flat and transparent that fill was the only visible box in the header — so Forward, which
        // cannot be pressed, looked raised while Back, which can, looked like part of the
        // background. Dimming is left to ButtonForegroundDisabled, which is the whole signal.
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;

        // The glyph, not a red fill behind it. A title-bar-style red block would be the only solid
        // shape in a header of flat transparent buttons, and it is a flyout's close rather than an
        // application's — recolouring the glyph reads as the same warning at the right weight.
        // Pressed matches Pointer-over, or the colour drops away at the moment of committing to it.
        //
        // Overriding the template brushes on the button's own Resources is how the disabled state
        // above is handled too: the templated parent is in the lookup chain, so a ThemeResource in
        // the default template resolves here first. The FontIcon has no Foreground of its own, so
        // it inherits whatever the template resolves.
        if (redOnHover)
        {
            // TryGetValue rather than the indexer, which throws on a missing key. This runs while
            // the flyout window is being built, so a theme brush that turned out not to be there
            // would take the whole launcher down rather than merely losing a hover colour. The
            // literal is Windows' own critical red and is only reached if the system brush is gone.
            Brush critical = Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out object found) && found is Brush themeBrush
                ? themeBrush
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 196, 43, 28));

            button.Resources["ButtonForegroundPointerOver"] = critical;
            button.Resources["ButtonForegroundPressed"] = critical;
        }

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
            _resizeGrips.Add(grip);
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

    /// <summary>
    /// Hides the resize grips in the states that cannot be resized, and restores them after.
    /// </summary>
    /// <remarks>
    /// The grips are transparent, so "showing" one means showing its resize cursor — and a resize
    /// cursor is a promise. While maximized or fullscreen there is no edge to drag (one is the
    /// work area, the other the screen) and <see cref="Grip_PointerPressed"/> refuses the drag, so
    /// leaving them hit-testable offered a resize that could not happen. Collapsing them removes
    /// the cursor and the hit-testing together; the guard in the handler stays as the backstop for
    /// a drag already in flight when the state changes.
    /// </remarks>
    private void UpdateResizeGripVisibility()
    {
        var visibility = _isMaximized || _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        foreach (var grip in _resizeGrips)
            grip.Visibility = visibility;
    }

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Neither state has an edge to drag: one is the screen, the other is the work area. A
        // resize from here would also persist the maximized size onto the launcher, which is
        // exactly what "temporarily" rules out. The grips are collapsed in both states
        // (UpdateResizeGripVisibility); this stays as the backstop.
        if (_isFullScreen || _isMaximized) return;
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
    /// <para>With <see cref="Launcher.WebLockSize"/> set (Remember Size off) nothing is written at
    /// all: the drag holds for as long as the flyout stays open and <see cref="ParkOffScreen"/>
    /// undoes it, exactly as maximize is undone there.</para>
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

        // A locked size is the whole point of the toggle: the flyout can still be dragged to any
        // size for the session, it just does not become the launcher's size. Flagged rather than
        // reverted here, because snapping back under the pointer mid-drag would read as the resize
        // being refused rather than being temporary.
        if (_launcher.WebLockSize)
        {
            _hasTemporaryResize = true;
            return;
        }

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

    /// <summary>
    /// Lets the window be minimized, which is what makes its taskbar button's click reach it.
    /// </summary>
    private void SetMinimizable(bool minimizable)
    {
        try
        {
            if (GetAppWindow().Presenter is OverlappedPresenter presenter)
                presenter.IsMinimizable = minimizable;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Presenter unavailable while setting minimizable");
        }
    }

    private void SetTopmost(bool topmost)
    {
        try
        {
            // A regular window is on top only when its pin says so — a flyout is always-on-top by
            // nature, a window is not. Resolved here rather than at each caller: they are all
            // either dropping it for a modal (false stays false) or restoring the default, and
            // "the default" is exactly what differs between the two window kinds.
            if (_launcher.WebRegularWindow) topmost = topmost && _launcher.WebPinFlyout;

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

    /// <summary>
    /// What the pin button promises, which depends on what kind of window this is.
    /// </summary>
    /// <remarks>
    /// <see cref="Launcher.WebPinFlyout"/> is one stored flag with two readings, because the two
    /// window kinds make "keep this in front of me" mean different things — and in each kind the
    /// other reading is meaningless. A flyout's risk is that it vanishes when you click away, so
    /// pinning stops the dismissal. A regular window never dismisses itself in the first place; its
    /// risk is being buried, so pinning keeps it on top. Storing a second flag would mean carrying
    /// a setting that does nothing in whichever mode the launcher is actually in.
    /// </remarks>
    private string PinTooltip(bool pinned) => _launcher.WebRegularWindow
        ? (pinned ? "Turn off always on top" : "Keep always on top")
        : (pinned ? "Unpin — close when focus is lost" : "Pin open");

    /// <summary>Flips the launcher's pin state, applies it, and re-labels the header button.</summary>
    private void TogglePin()
    {
        _launcher.WebPinFlyout = !_launcher.WebPinFlyout;
        SettingsManager.SaveSettings();

        // In flyout mode this is read at dismissal time, so nothing needs applying. In regular
        // window mode it is window state and has to be pushed now, or the button would report a
        // change the window had not made.
        SetTopmost(true);
        UpdatePinButton();
    }

    /// <summary>Re-labels the pin button for the current state and window kind.</summary>
    private void UpdatePinButton()
    {
        if (_pinButton.Content is FontIcon icon)
            icon.Glyph = PinGlyph(_launcher.WebPinFlyout);
        ToolTipService.SetToolTip(_pinButton, PinTooltip(_launcher.WebPinFlyout));
    }

    // U+E922 is Segoe Fluent's ChromeMaximize and U+E923 its ChromeRestore — the pair every
    // window uses for this, so the button needs no explaining. Written as escapes rather than
    // pasted, for the reason recorded on the back button.
    private static string MaximizeGlyph(bool maximized) => maximized ? "\uE923" : "\uE922";

    private static string MaximizeTooltip(bool maximized) =>
        maximized ? "Restore size" : "Maximize";

    /// <summary>Grows the flyout to fill the screen, or puts it back to the size it was.</summary>
    private void ToggleMaximized()
    {
        if (_isMaximized)
            ExitMaximized(restoreGeometry: true);
        else
            EnterMaximized();
    }

    /// <summary>
    /// Fills the work area of the monitor the flyout is on, remembering the rect to come back to.
    /// </summary>
    /// <remarks>
    /// The <b>work area</b>, not the whole monitor — this is still a tray flyout, and covering the
    /// taskbar would hide the tray icon it was opened from. Page fullscreen is the other case and
    /// deliberately does take the whole screen (<see cref="ApplyFullScreen"/>).
    /// <para>Nothing here is persisted. The remembered rect is whatever the flyout was — its
    /// configured size, or a position it had been dragged to — so restoring puts it back exactly,
    /// and a dismissal while maximized simply drops the state.</para>
    /// </remarks>
    private void EnterMaximized()
    {
        if (_isMaximized || _isFullScreen) return;
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        if (!GetWindowRect(_hwnd, out _preMaximizeRect)) return;

        _isMaximized = true;
        UpdateMaximizeButton();
        UpdateResizeGripVisibility();

        // A slide still in flight would otherwise keep writing its own geometry over this one.
        _animationVersion++;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), ref monitorInfo);
        var area = monitorInfo.rcWork;

        MoveResize(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
    }

    /// <summary>
    /// Leaves the maximized state, optionally putting the window back where it came from.
    /// </summary>
    /// <param name="restoreGeometry">
    /// False only when the caller is about to place the window itself — <see cref="ParkOffScreen"/>
    /// parks it at the pre-maximize size — so the rect is not written twice.
    /// </param>
    private void ExitMaximized(bool restoreGeometry)
    {
        if (!_isMaximized) return;

        _isMaximized = false;
        UpdateMaximizeButton();
        UpdateResizeGripVisibility();

        if (!restoreGeometry || _hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        _animationVersion++;
        MoveResize(_preMaximizeRect.Left, _preMaximizeRect.Top,
            _preMaximizeRect.Right - _preMaximizeRect.Left,
            _preMaximizeRect.Bottom - _preMaximizeRect.Top);
    }

    private void UpdateMaximizeButton()
    {
        if (_maximizeButton.Content is FontIcon icon)
            icon.Glyph = MaximizeGlyph(_isMaximized);
        ToolTipService.SetToolTip(_maximizeButton, MaximizeTooltip(_isMaximized));
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
            window.RebuildBookmarkBar(force: true);
            window.PreRenderOffScreen();
        }
    }

    /// <summary>
    /// Opens the launchers that are set to keep running, off screen, at startup.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the deliberate exception to "warm-up skips web launchers".</b> That rule
    /// exists so nothing boots a renderer for a page the user may never look at — but
    /// <see cref="WebHiddenPolicies.KeepRunning"/> is the user saying the opposite outright, and
    /// the only reason to say it is notifications. Without this the promise only held for launchers
    /// that happened to have been opened by hand since the last restart, so every reboot silently
    /// switched notifications off until the user remembered to click each tray icon in turn — the
    /// one thing a tray launcher should never ask of anyone.</para>
    /// <para>A preload is exactly what opening and dismissing one by hand does: the window is
    /// parked off the virtual screen, the page loads normally — visible, so nothing defers work
    /// that a background tab would — and the hidden policy is applied once it has settled, leaving
    /// it collapsed but connected. Nothing appears on screen and nothing takes focus.</para>
    /// <para>Staggered rather than fired at once: this runs at sign-in, when the machine is at its
    /// busiest, and starting several browsers in the same instant is how a launcher earns a
    /// reputation for slowing boot. Bar-mode launchers are skipped — until a bookmark is picked
    /// there is no page to keep running.</para>
    /// </remarks>
    public static void PreloadKeepRunning(MainWindow owner, IEnumerable<Launcher> launchers)
    {
        // NeedsPreload, not ShouldPreload: warm-up runs again on every launcher change, so once
        // these are loaded this has to cost nothing rather than schedule a timer per sync.
        var queue = new Queue<Launcher>(launchers.Where(NeedsPreload));
        if (queue.Count == 0) return;

        // Held in a static field, not a local. A DispatcherQueueTimer is only kept alive by
        // something referencing it: a local one is collectable the moment this method returns, and
        // ten seconds is ample for that to happen — which is exactly what went wrong. Preload
        // silently never ran, with no log line and nothing to see, because the timer was gone
        // before it could tick. Every other timer in this class is a field for the same reason.
        _preloadQueueTimer?.Stop();
        var timer = _preloadQueueTimer = owner.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(PreloadFirstDelaySeconds);
        timer.IsRepeating = false;

        void Next(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Tick -= Next;

            if (queue.Count == 0) return;
            var launcher = queue.Dequeue();

            // Re-read the launcher rather than trusting the snapshot: sync or the settings window
            // may have changed the policy in the seconds since this was scheduled.
            var current = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcher.Id);
            if (current != null && NeedsPreload(current))
            {
                if (!Instances.TryGetValue(current.Id, out var panel))
                {
                    panel = new WebFlyoutWindow(owner, current);
                    Instances[current.Id] = panel;
                }

                panel._owner = owner;
                panel.BeginPreload();
            }

            if (queue.Count == 0) return;
            sender.Interval = TimeSpan.FromSeconds(PreloadStaggerSeconds);
            sender.Tick += Next;
            sender.Start();
        }

        timer.Tick += Next;
        timer.Start();
    }

    /// <summary>The staggering timer, rooted so the garbage collector cannot take it mid-wait.</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _preloadQueueTimer;

    private const int PreloadFirstDelaySeconds = 10;
    private const int PreloadStaggerSeconds = 6;

    /// <summary>How long a page gets to finish loading before it is collapsed regardless.</summary>
    private const int PreloadSettleSeconds = 45;

    private static bool ShouldPreload(Launcher launcher) =>
        launcher.IsWebLauncher &&
        WebHiddenPolicies.Normalize(launcher.WebHiddenPolicy) == WebHiddenPolicies.KeepRunning &&
        !string.IsNullOrWhiteSpace(launcher.WebAddress);

    /// <summary>
    /// True when this launcher wants preloading and has no browser yet.
    /// </summary>
    /// <remarks>
    /// A launcher whose page failed to load still counts as loaded — the browser exists, showing
    /// its error — so a site that is down does not get retried on a loop for the life of the app.
    /// Opening it offers Retry, which is where a second attempt belongs.
    /// </remarks>
    private static bool NeedsPreload(Launcher launcher) =>
        ShouldPreload(launcher) &&
        !(Instances.TryGetValue(launcher.Id, out var panel) && panel._webView != null);

    private bool _preloadPending;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _preloadTimer;

    /// <summary>Loads this launcher's page off screen and then leaves it in its hidden state.</summary>
    private void BeginPreload()
    {
        if (_webView != null || _isOpen || _preloadPending) return;

        Logger.Info("Preloading web launcher {Name} so its connection is live before it is opened", _launcher.Name);

        _preloadPending = true;
        PreRenderOffScreen();
        _ = ShowHomeContentAsync();

        // A page that never finishes loading must not be left rendering for the session. The
        // navigation is not cancelled — only the "it has settled" decision is forced.
        _preloadTimer ??= DispatcherQueue.CreateTimer();
        _preloadTimer.Stop();
        _preloadTimer.IsRepeating = false;
        _preloadTimer.Interval = TimeSpan.FromSeconds(PreloadSettleSeconds);
        _preloadTimer.Tick += PreloadSettleTimer_Tick;
        _preloadTimer.Start();
    }

    private void PreloadSettleTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= PreloadSettleTimer_Tick;
        CompletePreload();
    }

    /// <summary>Puts a preloaded launcher into the state a dismissal would leave it in.</summary>
    private void CompletePreload()
    {
        if (!_preloadPending) return;
        _preloadPending = false;

        if (_preloadTimer != null)
        {
            _preloadTimer.Stop();
            _preloadTimer.Tick -= PreloadSettleTimer_Tick;
        }

        // The user got there first. Their open owns the window now, and collapsing it under them
        // would blank the page they are looking at.
        if (_isOpen) return;

        ParkOffScreen();
    }

    /// <summary>Parks the window outside the virtual screen at its full size, so WinUI draws it.</summary>
    private void PreRenderOffScreen()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        double scale = GetScale();
        int width = (int)Math.Ceiling(_launcher.ResolvedWebFlyoutWidth * scale);
        int height = (int)Math.Ceiling(_launcher.ResolvedWebFlyoutHeight * scale);
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 64;
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 64;

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Drawn once off screen, so the first real open slides like every one after it.
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
    /// Forgets every login this app's browsers have saved, across every profile.
    /// </summary>
    /// <remarks>
    /// <para>All of them, not a list to pick from: WebView2 exposes no way to enumerate saved
    /// passwords, only to clear the category. So this is the honest shape of what the platform
    /// allows — "forget them all", not "manage them".</para>
    /// <para>Every profile, because a password manager replaces the built-in one everywhere, and
    /// leaving the old logins in the profiles that happen to be closed would mean they reappeared
    /// launcher by launcher. Profiles with no browser running are cleared by starting nothing — the
    /// data is a folder, and the next browser on it reads what is left.</para>
    /// </remarks>
    public static async Task<bool> ClearSavedPasswordsAsync(Launcher launcher)
    {
        const CoreWebView2BrowsingDataKinds kinds =
            CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill;

        // One clear does the whole profile, so the first sibling with a live browser is enough — the
        // others are views onto the same folder, not copies. Same shape as ClearBrowsingDataAsync.
        foreach (string id in ProfileSiblings(launcher))
        {
            if (!Instances.TryGetValue(id, out var panel)) continue;
            if (panel._webView?.CoreWebView2 is not { } core) continue;

            try
            {
                await core.Profile.ClearBrowsingDataAsync(kinds);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Clearing saved logins failed for launcher {Name}", panel._launcher.Name);
            }
        }

        return false;
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

    /// <summary>
    /// Names the profile a launcher's data lives in: <c>"Shared"</c>, or its own id.
    /// </summary>
    /// <remarks>
    /// The folder name under <c>WebProfiles</c>, which is what makes it a stable key for anything
    /// scoped to a profile rather than to a launcher. Launcher ids are GUIDs, so nothing private can
    /// collide with the shared name.
    /// </remarks>
    internal static string ProfileKey(Launcher launcher) =>
        launcher.WebSharedProfile ? "Shared" : launcher.Id;

    /// <summary>Whether this launcher's profile still offers to save and fill logins.</summary>
    internal static bool UsesBuiltInPasswordManager(Launcher launcher) =>
        SettingsManager.Current.ProfilesWithoutPasswordManager is not { } off ||
        !off.Contains(ProfileKey(launcher), StringComparer.OrdinalIgnoreCase);

    /// <summary>Turns the built-in password manager on or off for a launcher's whole profile.</summary>
    internal static void SetBuiltInPasswordManager(Launcher launcher, bool enabled)
    {
        var off = SettingsManager.Current.ProfilesWithoutPasswordManager ??= [];
        string key = ProfileKey(launcher);

        off.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (!enabled) off.Add(key);

        SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyLaunchersChanged();
    }

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


    // ── Show / hide ─────────────────────────────────────────────────

    private void ShowFlyout(int screenX, int screenY)
    {
        // Rebuilt per open: bookmarks can have been added or renamed since the last one.
        RebuildBookmarkBar();

        _header.Visibility = Visibility.Visible;

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

        // PROTOTYPE: light up the launcher's pinned taskbar button while the flyout is on screen.
        // After the show, so the button appears with the window rather than ahead of it.
        ApplyTaskbarButton(true);

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

        // Focus loss cannot dismiss the flyout while it is asking something, but closing it
        // outright still can — and the question goes with it. Refused rather than left pending:
        // nothing is written to the profile, so the page is free to ask again on the next open.
        CancelPendingPermissions();
        StopForegroundWatch();

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

        // Drop the taskbar button before the window goes off screen. The park leaves it visible in
        // the Win32 sense, so nothing else would ever take the button away.
        ApplyTaskbarButton(false);

        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            GetWindowRect(_hwnd, out var rect);

            // Maximizing is temporary by design, and this is where it ends: the state is dropped
            // rather than persisted, so the next open is at the launcher's own size. The window is
            // parked at the size it had before, because a park is also what the next open's first
            // frame is drawn at.
            if (_isMaximized)
            {
                rect = _preMaximizeRect;
                ExitMaximized(restoreGeometry: false);
            }

            // A drag-resize under a locked size ends here too, and for the same reason: the park
            // size is what the next open's first frame is drawn at, so leaving the dragged size in
            // place would show it for a frame before the placement code moved it back.
            if (_hasTemporaryResize)
            {
                _hasTemporaryResize = false;
                double parkScale = GetScale();
                rect.Right = rect.Left + (int)Math.Ceiling(_launcher.ResolvedWebFlyoutWidth * parkScale);
                rect.Bottom = rect.Top + (int)Math.Ceiling(_launcher.ResolvedWebFlyoutHeight * parkScale);
            }

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

        // Every live browser, not just the one on screen: in tabs mode the others are already
        // collapsed but still hold their memory target and, under a suspending policy, still need
        // freezing. A background tab that stayed awake through a dismissal would quietly undo the
        // resource promise N times over.
        int policy = WebHiddenPolicies.Normalize(_launcher.WebHiddenPolicy);
        if (policy == WebHiddenPolicies.KeepRunning)
        {
            // Collapse and drop the memory target, but never suspend. This used to return
            // immediately, which left the browser fully *visible* to Chromium on a window parked
            // off the virtual screen: still compositing, still decoding video, with the page
            // reporting visibilityState 'visible' and so declining to throttle anything. Collapsing
            // stops the rendering; script, websockets and notifications carry on exactly as they do
            // in a background tab, which is what a chat app is already written for.
            //
            // Suspending is the line not to cross here: a suspended page raises no notifications,
            // and that is the entire reason this policy exists.
            foreach (var view in LiveWebViews())
            {
                view.Visibility = Visibility.Collapsed;
                TrySetMemoryTarget(view, CoreWebView2MemoryUsageTargetLevel.Low);
            }
            return;
        }

        foreach (var view in LiveWebViews())
        {
            view.Visibility = Visibility.Collapsed;
            _ = SuspendWebViewAsync(view);
        }

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

    /// <summary>Best-effort memory hint. Never worth failing a dismissal over.</summary>
    private void TrySetMemoryTarget(WebView2? view, CoreWebView2MemoryUsageTargetLevel level)
    {
        var core = view?.CoreWebView2;
        if (core == null) return;

        try
        {
            core.MemoryUsageTargetLevel = level;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Setting the memory target failed for launcher {Name}", _launcher.Name);
        }
    }

    private async Task SuspendWebViewAsync(WebView2? view)
    {
        var core = view?.CoreWebView2;
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

    /// <summary>Tears every browser down completely; the next open builds a fresh one.</summary>
    private void UnloadWebView()
    {
        if (_tabs.Count == 0) return;

        if (_isFullScreen) ApplyFullScreen(false);

        _webView = null;
        _revealOnNavigationCompleted = false;

        // Every browser is a tab, the launcher's own included, so this is the whole teardown.
        CloseAllTabs();

        // A permission request whose deferral is never completed leaves the page waiting for an
        // answer that can no longer be given.
        CancelPendingPermissions();

        SetStatus("Loading…", busy: true, showRetry: false);
        UpdateNavigationButtons();   // the history went with the browser
        RefreshTabBar();
    }

    // ── Content ─────────────────────────────────────────────────────

    /// <summary>The address the content area should be showing right now.</summary>
    /// <remarks>
    /// Where the user last steered this launcher, falling back to its address — which is its first
    /// bookmark. Empty only for a launcher with no bookmarks at all, which is one that has not been
    /// set up yet and is told so rather than navigated anywhere.
    /// </remarks>
    private string CurrentTargetUrl() =>
        !string.IsNullOrEmpty(_rememberedUrl) ? _rememberedUrl : _launcher.WebAddress;

    /// <summary>
    /// Puts the right thing on screen for a show or an expansion.
    /// </summary>
    /// <remarks>
    /// A tab the user opened from a link is *their* place in this launcher, so a dismissal and
    /// reopen returns to it rather than yanking them back to the launcher's own page. Everything
    /// that names a page deliberately — a bookmark click, a settings change, a profile reload —
    /// goes through <see cref="ShowHomeContentAsync"/> instead and is unaffected by this.
    /// </remarks>
    private async Task PrepareContentAsync()
    {
        // First open of the run, with pages remembered from the last one — see
        // WebFlyoutWindow.Session.cs. Ahead of everything else, because it decides what the tabs
        // *are*; the branches below only choose between tabs that already exist.
        if (HasSessionToRestore)
        {
            await RestoreSessionAsync();
            if (_tabs.Count > 0) return;
        }

        if (_activeTab is { HomeKey: null } link && link.View.CoreWebView2 != null)
        {
            ActivateTab(link);
            return;
        }

        await ShowHomeContentAsync();
    }

    /// <summary>Shows the page the launcher is configured to show right now.</summary>
    private async Task ShowHomeContentAsync()
    {
        string url = NormalizeUrl(CurrentTargetUrl());
        if (string.IsNullOrEmpty(url))
        {
            UnloadWebView();
            SetStatus("No web address is set for this launcher. Add one in its launcher settings.",
                busy: false, showRetry: false);
            return;
        }

        // One home tab, whatever the launcher's bookmarks say. A bookmark is a place to send it,
        // not a browser of its own — extra browsers are made by the user asking for one, with a
        // middle-click, a Shift-click or Open in new tab.
        var tab = FindHomeTab(PrimaryTabKey);

        if (tab == null)
        {
            // First showing of this address. Everything already open stays open behind it.
            await CreateTabAsync(PrimaryTabKey, url);
            return;   // creation navigates once the core is ready
        }

        ActivateTab(tab);
        if (tab.View.CoreWebView2 == null) return;   // still starting; its own creation navigates

        bool navigating = !string.Equals(tab.NavigatedUrl, url, StringComparison.OrdinalIgnoreCase);
        bool reloading = !navigating && _launcher.WebReloadOnShow;

        // Resuming a suspended browser puts its last painted frame back on screen instantly, so
        // showing it while a reload is already queued means the user watches the *old* page
        // appear and then get replaced. Keep it hidden until the new content is ready — hidden
        // only stops rendering, not loading, so the navigation still runs.
        if (navigating || reloading)
        {
            _revealOnNavigationCompleted = true;
            tab.View.Visibility = Visibility.Collapsed;
            SetStatus("Loading…", busy: true, showRetry: false);

            if (navigating) Navigate(url);
            else ReloadPage();
            return;
        }

        // Nothing queued: the page is live and current, so show it straight away.
        tab.View.Visibility = Visibility.Visible;
        RestorePageFocus();
    }

    /// <summary>
    /// Builds one browser, files it as a tab and makes it the active one.
    /// </summary>
    /// <param name="homeKey">
    /// <see cref="PrimaryTabKey"/> for the launcher's own tab; null for one the user asked for,
    /// which nothing configured may re-navigate.
    /// </param>
    /// <param name="navigateTo">
    /// Where to send it once its core is ready, or null when the caller navigates it — which is
    /// what handing the browser to <c>NewWindowRequested.NewWindow</c> does.
    /// </param>
    /// <param name="background">
    /// Build it without bringing it forward, for the gestures that mean "open this but leave me
    /// where I am" — a middle-click, a Shift/Ctrl-click, <b>Open in new tab</b>. The chip appears
    /// and the page loads behind whatever is on screen.
    /// </param>
    /// <returns>The tab, with its core ready, or null when the browser could not be started.</returns>
    private async Task<WebTab?> CreateTabAsync(string? homeKey, string? navigateTo, bool background = false)
    {
        // Re-entrancy only matters for the launcher's own page: two shows racing must not build two
        // browsers for it. A link tab is a distinct request every time and is never deduplicated.
        if (homeKey != null && _webViewInitializing) return null;
        if (homeKey != null) _webViewInitializing = true;

        // The status overlay is shared chrome, so a background tab must not put a spinner over the
        // page the user is reading.
        if (!background) SetStatus("Loading…", busy: true, showRetry: false);

        var webView = new WebView2 { Visibility = background ? Visibility.Collapsed : Visibility.Visible };
        Grid.SetRow(webView, 1);   // row 0 belongs to the prompt bar
        _contentHost.Children.Insert(0, webView);

        var tab = new WebTab { View = webView, HomeKey = homeKey };
        _tabs.Add(tab);

        // Background: the strip has to learn about the chip, but nothing else moves — _webView goes
        // on pointing at the tab in front, which is what keeps the header, the address box, zoom and
        // focus describing the page the user is actually looking at.
        if (background) RefreshTabBar();
        else ActivateTab(tab);

        // Clicking into the page is what arms the focus restore; see _pageHadFocus.
        webView.GotFocus += (_, _) => _pageHadFocus = true;

        try
        {
            string userDataFolder = GetUserDataFolder(_launcher);
            Directory.CreateDirectory(userDataFolder);

            // A per-launcher profile keeps each panel signed in independently and, more to the
            // point, keeps it signed in at all — the alternative is re-authenticating to a home
            // dashboard on every app restart. Launchers opted into the shared profile point at
            // one folder instead; safe to do from several environments in this process because
            // every one of them is created with identical options (WebView2 rejects a second
            // environment on the same folder only when the options differ). Tabs of one launcher
            // share the folder for the same reason: they are tabs of one browser.
            // AreBrowserExtensionsEnabled is set for *every* launcher, never only for ones with an
            // extension. The options must match across every environment on a folder — connecting to
            // an already-running one with a different value fails outright with ERROR_INVALID_STATE
            // — and the shared profile puts several launchers on one folder, so a conditional flag
            // would break exactly the launchers that share while private ones carried on fine.
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: "",
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });

            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "WebView2 initialisation failed for launcher {Name}", _launcher.Name);

            // Only this tab, never the launcher: with the runtime missing there is nothing to lose,
            // but a link tab that failed for any other reason must not take the pages already open
            // down with it.
            _tabs.Remove(tab);
            DestroyTab(tab);
            _activeTab = null;
            _webView = null;

            if (_tabs.Count > 0)
            {
                ActivateTab(_tabs[^1]);
                return null;
            }

            RefreshTabBar();
            SetStatus(
                "This panel needs the Microsoft Edge WebView2 Runtime, which could not be started. " +
                "Install it from microsoft.com/edge/webview2 and try again.",
                busy: false, showRetry: true);
            return null;
        }
        finally
        {
            if (homeKey != null) _webViewInitializing = false;
        }

        // Closed while it was starting — a dismissal that unloaded, or the tab being closed.
        if (!_tabs.Contains(tab) || webView.CoreWebView2 == null) return null;

        ConfigureCore(webView.CoreWebView2, tab);

        // Awaited, and before Navigate: a document-created script added after the navigation has
        // started misses the very page the flyout was opened to show.
        await InstallNotificationBridgeAsync(webView.CoreWebView2);
        if (!_tabs.Contains(tab)) return null;

        await InstallServiceWorkerBridgeAsync(webView.CoreWebView2);
        if (!_tabs.Contains(tab)) return null;

        // Keys the host owns rather than Chromium — see WebFlyoutWindow.Shortcuts.cs. Same
        // document-created timing as the two bridges above, for the same reason.
        await InstallShortcutBridgeAsync(webView.CoreWebView2);
        if (!_tabs.Contains(tab)) return null;

        // Whatever is on the extension list, onto this profile. Per browser rather than once at
        // startup: an extension added while this launcher was closed has to arrive when it opens,
        // and a launcher on a private profile needs its own copy loaded.
        await Services.BrowserExtensionService.ApplyAsync(webView.CoreWebView2);
        if (!_tabs.Contains(tab)) return null;

        ApplyZoom(webView.CoreWebView2);
        UpdateTabChip(tab);

        // CurrentTargetUrl is resolved by the caller, not read again here: in bar mode the address
        // is whichever bookmark was clicked, and reading the launcher's single address at this
        // point meant the first click on any bookmark loaded the launcher's own URL instead — and
        // then the page's favicon was adopted onto the bookmark that had been clicked, so it took
        // that page's icon too.
        // NavigateTab, not Navigate: Navigate drives whichever tab is in front, so a background tab
        // would otherwise load its address into the page the user is reading. That was safe only
        // while every new tab was activated on creation.
        if (!string.IsNullOrEmpty(navigateTo)) NavigateTab(tab, navigateTo);
        else ShowEmptyTabStatus(tab);

        return tab;
    }

    /// <summary>
    /// Settles the status overlay for a tab that was built with nowhere to go.
    /// </summary>
    /// <remarks>
    /// <para>The overlay is raised on the way into <see cref="CreateTabAsync"/> because a tab being
    /// built is nearly always about to load something, and it is taken down again by
    /// <c>NavigationCompleted</c>. A tab with no address never navigates, so nothing was ever going
    /// to take it down: the "+" opened a spinner that span for the life of the tab.</para>
    /// <para>What belongs there instead is not nothing: an unnavigated browser paints nothing at
    /// all, so the tab would be a bare rectangle of window background. It is the one line that says
    /// what an empty tab is for, and it sits directly under the address bar that
    /// <see cref="IsActiveTabBlank"/> forces on for exactly this case.</para>
    /// <para>A tab whose caller navigates it (which is what handing the browser to
    /// <c>NewWindowRequested.NewWindow</c> does) raises the overlay again from
    /// <c>NavigationStarting</c> a moment later, so this is safe for that path too.</para>
    /// </remarks>
    private void ShowEmptyTabStatus(WebTab tab)
    {
        if (!ReferenceEquals(tab, _activeTab)) return;

        // Told to go somewhere, even if it has not arrived: the load owns the overlay, and a tab
        // mid-navigation still reports an empty Source until the first response commits.
        if (!string.IsNullOrEmpty(tab.NavigatedUrl)) return;
        if (!IsActiveTabBlank) return;

        SetStatus("Type an address above to open a page.", busy: false, showRetry: false);
    }

    private void ConfigureCore(CoreWebView2 core, WebTab tab)
    {
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;   // no Ctrl+N/Ctrl+P from a tray panel
        // Off when a password manager extension is doing the job — otherwise both offer, and the
        // built-in one keeps proposing older saved logins over the manager's. Read per browser, so
        // it applies to a launcher the next time it starts rather than to one already open.
        bool builtIn = UsesBuiltInPasswordManager(_launcher);
        settings.IsPasswordAutosaveEnabled = builtIn;
        settings.IsGeneralAutofillEnabled = builtIn;

        // A link asking for a new window becomes another tab of this launcher — see
        // HandleNewWindowRequested for why the browser's own NewWindow is used rather than reading
        // the URI and navigating by hand. Launchers set to WebLinksInBrowser hand it to the real
        // browser instead, which is what web launchers shipped with.
        core.NewWindowRequested += (_, e) => HandleNewWindowRequested(e);

        // The store's install button gets as far as handing over a .crx. See HandleDownloadStarting.
        core.DownloadStarting += (_, e) => HandleDownloadStarting(e);

        // Whether this page makes its own notification sound, which decides whether ours does.
        WatchPageAudio(core);

        // window.close() from a popup closes that popup, not the launcher. Only a page the launcher
        // itself opened is speaking for the whole flyout.
        core.WindowCloseRequested += (_, _) =>
        {
            if (tab.HomeKey == null) CloseTab(tab);
            else HideFlyout();
        };

        // Every handler below that writes shared chrome asks IsActiveCore first: a background tab
        // navigates on its own — a chat app pushing history, a dashboard refreshing — and without
        // the check it would drive the header of a page nobody is looking at.
        core.NavigationStarting += (_, e) =>
        {
            if (TryHandleBrowserPageNavigation(core, e)) return;

            if (IsActiveCore(core)) ShowLoading(tab);
        };

        core.HistoryChanged += (_, _) =>
        {
            if (IsActiveCore(core)) UpdateNavigationButtons();
            UpdateTabChip(tab);
        };

        // The chip is the only place a background tab says what it is, so its title follows the
        // page whether or not the tab is on screen.
        core.DocumentTitleChanged += (_, _) => UpdateTabChip(tab);

        // A page going fullscreen only resizes its own element; making the window fill the
        // screen is the host's job. Without this, "fullscreen" video is still boxed inside
        // whatever size the flyout happens to be.
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (IsActiveCore(core)) ApplyFullScreen(core.ContainsFullScreenElement);
        };

        // Every WebView2 object handed to a handler is released here, on the UI thread, rather than
        // left for the finalizer — see ReleaseWebViewObject. The captured crash was one of these
        // being collected on the .NET Finalizer thread.
        core.NavigationCompleted += (_, e) =>
        {
            try
            {
            bool active = IsActiveCore(core);

            if (e.IsSuccess)
            {
                ApplyZoom(core);   // CSS zoom lives in the document, so each navigation drops it
                UpdateTabChip(tab);
                tab.HasLoaded = true;

                // Per tab, not per active tab: this is the moment a tab's address is finally real,
                // and it is the only one that catches a page the user browsed to rather than one
                // opened by a gesture. RefreshTabBar alone recorded only opens, closes and
                // switches — so a launcher opened once and browsed within stored the address it
                // started at, or nothing. The save compares before writing, so this is cheap.
                SaveSession();

                if (!active) return;

                HideStatus();

                // about:blank is where a new-tab request is answered (see BrowserPages), and it
                // completes like any other navigation, so without these the notice raised on
                // creation, and the address bar an empty tab forces on, are both taken straight
                // back down again.
                ApplyAddressBarVisibility();
                ShowEmptyTabStatus(tab);

                UpdateNavigationButtons();
                RevealWebViewIfPending();
                CompletePreload();   // loaded off screen at startup: collapse it, keep it connected
                return;
            }

            UpdateTabChip(tab);
            if (!active) return;

            // A failed navigation still has to give the browser back, or the flyout is stuck
            // showing a spinner over a hidden page with no way out but Reload.
            RevealWebViewIfPending();

            // A preload that could not load — no network yet at sign-in, typically — still has to
            // stop rendering. The error is left on screen for whenever it is opened.
            CompletePreload();

            SetStatus($"Could not load {NormalizeUrl(_launcher.WebAddress)} ({e.WebErrorStatus}).",
                busy: false, showRetry: true);
            }
            finally { ReleaseWebViewObject(e); }
        };

        core.ProcessFailed += (_, _) =>
        {
            if (!IsActiveCore(core)) return;
            SetStatus("The page stopped responding.", busy: false, showRetry: true);
            RevealWebViewIfPending();
        };

        // The page's own icon is the right default for a web launcher, and this is the only
        // source that can get it: FaviconService fetches over plain HTTP with no session, so a
        // self-hosted dashboard behind a login hands it a redirect instead of an icon. The
        // browser here is signed in and reads whatever the page actually declares.
        core.FaviconChanged += (_, _) => _ = AdoptPageIconAsync(core, tab);

        // Camera, microphone, location, notifications: asked for in the flyout's own bar, since an
        // unhandled request falls back to WebView2's browser-sized prompt. See
        // WebFlyoutWindow.Permissions.cs.
        core.PermissionRequested += OnPermissionRequested;

        // NotificationReceived is deliberately NOT handled. Being handed its event args means being
        // handed WebView2 objects that cannot be released safely from managed code — see
        // NotificationBridgeScript. The page reports its notifications over the bridge instead.

        ApplyPendingPermissionReset(core);

        // Before the page loads, so its first read of Notification.permission already says granted
        // rather than prompting for something the launcher has already decided.
        _ = SeedTrustedPermissionsAsync(core);
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
    private async Task AdoptPageIconAsync(CoreWebView2 core, WebTab tab)
    {
        if (string.IsNullOrEmpty(core.FaviconUri)) return;

        // The chip's icon is per tab and is written nowhere, so every tab gets it.
        await UpdateTabIconAsync(core, tab);

        // A link tab is showing whatever site the page sent the user to. It must never rewrite the
        // launcher's tray icon or a bookmark's — both stand for an address the *user* chose, and an
        // icon quietly replaced by a page they merely passed through reads as data corruption.
        if (tab.HomeKey == null) return;

        // Whichever bookmark this page *is* — matched on its own address rather than remembered,
        // since the bar no longer tracks what is showing, and matching also declines to write an
        // icon onto a bookmark the user has navigated away from.
        string source = NormalizeUrl(core.Source);
        if (FindBookmark(source) is { } bookmark)
            await AdoptBookmarkIconAsync(core, bookmark);

        // The tray icon stands for the launcher, and the launcher is its address — its first
        // bookmark. Any page further along the bar is one of several this launcher holds, and
        // letting each one rewrite the tray icon as it was visited would leave the icon meaning
        // "the last thing I looked at" rather than "this launcher".
        if (!string.Equals(source, NormalizeUrl(_launcher.WebAddress), StringComparison.OrdinalIgnoreCase)) return;

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

    /// <summary>
    /// Takes the high-resolution icon the page reported and adopts it as the launcher's.
    /// </summary>
    /// <remarks>
    /// <para><c>CoreWebView2.GetFaviconAsync</c> returns what the page declared for a browser tab —
    /// commonly 32 or 64px — and a tray icon, and worse a taskbar pin, are rendered far larger than
    /// that. Upscaling is why a freshly created web launcher looked soft enough that choosing a
    /// replacement icon by hand was the obvious thing to do.</para>
    /// <para>A site that can be installed almost always declares something much better in its web
    /// app manifest or as an <c>apple-touch-icon</c>. Those are found and fetched **in the page**,
    /// for the same reason the notification avatar is: they sit behind the same login, so a
    /// host-side fetch gets a redirect where the page gets the image.</para>
    /// <para>Only ever replaces an icon that is still ours to replace (<see cref="MayAdoptPageIcon"/>)
    /// — a user who has chosen an icon has made a decision, and a better favicon is not a reason to
    /// undo it. In bar mode nothing is adopted at all: the tray icon stands for the launcher, which
    /// is several sites rather than one. Nor from a link tab, for the reason
    /// <see cref="AdoptPageIconAsync"/> gives: the launcher's icon must name an address the user
    /// chose, not one a page sent them to.
    /// </para>
    /// </remarks>
    private void AdoptHighResPageIcon(CoreWebView2 core, JsonNode? message)
    {
        if (IsBarMode || !MayAdoptPageIcon(_launcher)) return;
        if (TabFor(core) is not { HomeKey: not null }) return;

        string dataUrl = message?["icon"]?.GetValue<string>() ?? "";
        int comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.Ordinal)) return;

        try
        {
            byte[] bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            if (bytes.Length == 0) return;

            string path = GetPageIconPath(_launcher.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written aside first, as the favicon path does: the tray pipeline reads this file, and
            // a half-written PNG reads as corrupt rather than merely stale.
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);

            _launcher.CustomTrayIconPath = path;
            _launcher.TrayIconMode = TrayIconModes.Custom;
            SettingsManager.SaveSettings();

            Logger.Info("Adopted a {Size}px page icon for launcher {Name}",
                message?["size"]?.GetValue<int>() ?? 0, _launcher.Name);

            // Re-renders the tray icon, rewrites app-icon-{id}.ico for the pin flow, and refreshes
            // the notification icon alongside it.
            MainWindow.Current?.UpdateTrayIcon(_launcher);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Adopting a high-resolution page icon failed for {Name}", _launcher.Name);
        }
    }

    private void Navigate(string url) => NavigateTab(_activeTab, url);

    /// <summary>Sends one named tab somewhere, whether or not it is the tab in front.</summary>
    /// <remarks>
    /// The address is recorded on the tab rather than on the window: the reload-on-open and
    /// settings-changed checks both ask "is the home tab already showing this", and another tab
    /// answering for it would make them read the wrong page's address.
    /// </remarks>
    private void NavigateTab(WebTab? tab, string url)
    {
        var core = tab?.View.CoreWebView2;
        if (core == null || string.IsNullOrEmpty(url)) return;

        tab!.NavigatedUrl = url;
        SaveSession();

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
        // Nowhere to move a window that already fills the screen — and dragging one would write
        // its position to WebFlyoutPosition, outliving the state that produced it.
        if (_isFullScreen || _isMaximized) return;
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
    /// Under any other anchor the move is deliberately not written anywhere: it holds for as long as
    /// the flyout stays open, and the next one goes back to the tray or to the corner it is set to.
    /// That is the whole difference between the last-position anchor and the other ten.
    /// </remarks>
    private void RememberFlyoutPosition()
    {
        if (WebAnchors.Normalize(_launcher.WebAnchor) != WebAnchors.LastPosition) return;
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
            UpdateResizeGripVisibility();

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
        UpdateResizeGripVisibility();

        _header.Visibility = Visibility.Visible;
        _contentHost.Margin = new Thickness(GripThickness, 0, GripThickness, GripThickness);
        int roundedCorners = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundedCorners, sizeof(int));

        RebuildBookmarkBar();

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
        SyncAddressBox();

        // Not left to the box's own TextChanged: a switch back to a tab already showing this
        // address writes the same string, which raises nothing at all — and the star would then
        // still be answering for the page before it.
        UpdateBookmarkStar();
    }

    // ── Address bar ─────────────────────────────────────────────────

    /// <summary>
    /// Shows or hides the address bar for the launcher's current setting.
    /// </summary>
    /// <remarks>
    /// The header check is not optional: the bar lives with the header, so anything that hid the
    /// header — a bookmark bar collapsed to a strip, a page gone fullscreen — meant to hide this
    /// too.
    /// <para>There was a header button that revealed the bar for one visit without changing the
    /// launcher. It is gone, and with it the temporary state: the More menu carries the setting
    /// itself, which is one affordance instead of two that differed only in how long they lasted —
    /// a distinction the header had no room to explain.</para>
    /// </remarks>
    /// <summary>
    /// Turns the address bar on or off for this launcher.
    /// </summary>
    /// <remarks>
    /// It writes the launcher rather than lasting for the visit, which is the same thing the "…"
    /// menu's item does — there was once a reveal-for-this-visit button here and it was removed,
    /// because one affordance beats two that differ only in how long they last.
    /// </remarks>
    private void ToggleAddressBar()
    {
        _launcher.WebShowAddressBar = !_launcher.WebShowAddressBar;
        Classes.Settings.SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyLaunchersChanged();
        ApplyAddressBarVisibility();
    }

    private void ApplyAddressBarVisibility()
    {
        // An empty tab overrides the launcher's setting: it has nothing else in it, and the only
        // thing to do with one is type an address. Hiding the bar there would leave a blank panel
        // with no way to use it.
        bool visible = (_launcher.WebShowAddressBar || IsActiveTabBlank) && _header.Visibility == Visibility.Visible;

        _addressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible) SyncAddressBox();

        // The button reports what it will do, not what is on screen: an empty tab forces the bar on
        // regardless of the setting, and a button that went "checked" because of that would be
        // claiming the launcher had been changed when it had not.
        ToolTipService.SetToolTip(_addressBarButton,
            _launcher.WebShowAddressBar ? "Hide the address bar" : "Show the address bar");

        if (_addressBarButton.Content is FontIcon glyph)
            glyph.Opacity = _launcher.WebShowAddressBar ? 1.0 : 0.6;

        // Unconditional: the star is hidden for a launcher with no bookmark bar to write into, and
        // that is a launcher setting rather than something the address bar's own visibility decides.
        UpdateBookmarkStar();
    }

    /// <summary>True when the tab in front has never been sent anywhere.</summary>
    private bool IsActiveTabBlank
    {
        get
        {
            if (_activeTab == null) return false;

            try
            {
                string source = _activeTab.View.CoreWebView2?.Source ?? "";
                return string.IsNullOrEmpty(source) ||
                       source.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Reading the active tab's address failed for launcher {Name}", _launcher.Name);
                return false;
            }
        }
    }

    /// <summary>Puts the page's own address back in the box, unless the user is mid-edit.</summary>
    private void SyncAddressBox()
    {
        // Never while it has focus: this runs from HistoryChanged, which a single-page app raises
        // freely, and overwriting a half-typed address would be indistinguishable from a bug.
        if (_addressBox.FocusState != FocusState.Unfocused) return;

        string source = _webView?.CoreWebView2?.Source ?? "";
        // "about:blank" is what a browser that has not navigated yet reports. Showing it would
        // make an empty bar-mode flyout look like it had loaded something.
        _addressBox.Text = source.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ? "" : source;
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            GoToTypedAddress();
        }
    }

    /// <summary>Navigates to whatever is in the box, giving a bare host a scheme.</summary>
    /// <remarks>
    /// This drives the browser that is already there and never creates one. Falling back to
    /// <c>PrepareContentAsync</c> looks like the helpful thing to do and is not: that path
    /// navigates to <c>CurrentTargetUrl()</c>, so an address typed with no live browser would
    /// silently load the launcher's configured page instead of the one asked for. It is also the
    /// rule the three existing navigation paths follow — the address bar is a fourth caller of
    /// <c>Navigate</c>, not a fourth answer to "which URL".
    /// </remarks>
    private void GoToTypedAddress()
    {
        string url = NormalizeUrl(_addressBox.Text);
        if (string.IsNullOrEmpty(url)) return;

        if (_webView?.CoreWebView2 == null)
        {
            Logger.Debug("Address entered with no live browser for launcher {Name}", _launcher.Name);
            return;
        }

        Navigate(url);
        MoveFocusOffAddressBox();
    }

    /// <summary>
    /// Abandons an in-progress address edit, if there is one. Returns whether it consumed Escape.
    /// </summary>
    private bool CancelAddressEditing()
    {
        if (_addressBox.FocusState == FocusState.Unfocused) return false;

        MoveFocusOffAddressBox();
        SyncAddressBox();   // only lands once focus has actually left, which the call above does
        return true;
    }

    /// <summary>
    /// Takes the keyboard away from the address box — to the page where there is one.
    /// </summary>
    /// <remarks>
    /// The fallback is not decoration. Escape is consumed while the box has focus, so a failure
    /// to move focus off it would swallow every subsequent Escape too and leave the flyout with
    /// no way to be dismissed by keyboard at all. The pin button is the target because it is
    /// always present and always enabled whenever the header — and so the address bar — is
    /// showing; Back and Forward can both be disabled, and the reveal button is hidden outright
    /// once the bar is permanent.
    /// </remarks>
    private void MoveFocusOffAddressBox()
    {
        try
        {
            if (_webView is { Visibility: Visibility.Visible } view && view.Focus(FocusState.Programmatic)) return;
            _pinButton.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Moving focus off the address bar failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>Shows the browser again once the content it was waiting for has arrived.</summary>
    /// <summary>
    /// Hands keyboard focus back to the page, so the caret returns to wherever it was left.
    /// </summary>
    /// <remarks>
    /// Nothing is remembered here beyond "the page had focus": Chromium restores the document's
    /// own <c>activeElement</c> and selection by itself once the widget is focused again. Skipped
    /// while a prompt is up, because a permission question that cannot be typed into is worse than
    /// a caret that has to be clicked back into.
    /// </remarks>
    private void RestorePageFocus()
    {
        if (!_pageHadFocus || IsPromptOpen) return;
        if (_webView == null || _webView.Visibility != Visibility.Visible) return;

        // Queued rather than called inline: the browser has just been made visible, and focusing a
        // control in the same layout pass that revealed it does not take.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isOpen || _webView == null || _webView.Visibility != Visibility.Visible) return;
            try { _webView.Focus(FocusState.Programmatic); }
            catch (Exception ex) { Logger.Debug(ex, "Restoring page focus failed for launcher {Name}", _launcher.Name); }
        });
    }

    private void RevealWebViewIfPending()
    {
        if (!_revealOnNavigationCompleted) return;
        _revealOnNavigationCompleted = false;
        if (_webView != null) _webView.Visibility = Visibility.Visible;
        RestorePageFocus();
    }

    private void ReloadPage()
    {
        if (_webView?.CoreWebView2 is not { } core)
        {
            _ = PrepareContentAsync();
            return;
        }

        ShowLoading();
        core.Reload();
    }

    private void OpenInBrowser()
    {
        string url = _webView?.CoreWebView2?.Source ?? NormalizeUrl(_launcher.WebAddress);
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
    private void ApplyZoom() => ApplyZoom(_webView?.CoreWebView2);

    /// <inheritdoc cref="ApplyZoom()"/>
    /// <remarks>
    /// Takes the browser explicitly so a background tab finishing a navigation of its own re-applies
    /// its own zoom rather than the active tab's document's.
    /// </remarks>
    private void ApplyZoom(CoreWebView2? core)
    {
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
        ApplyAddressBarVisibility();
        ApplyTabBarVisibility();

        // Regular-window mode can have been switched on or off in the window that just closed, and
        // all of it is window state rather than something re-read per open. The pin button's label
        // goes with it: the same flag reads as "stay on top" in one mode and "stay open" in the
        // other, so the tooltip is wrong until it is rebuilt.
        SetTopmost(true);
        UpdatePinButton();
        SetMinimizable(_launcher.WebRegularWindow);
        if (_isOpen) ApplyTaskbarButton(true);

        // CurrentTargetUrl, not WebUrl. This runs whenever the launcher changes — including
        // after a bookmark's favicon fetch completes — so reading the launcher's single address
        // here yanked an open bookmark over to it, and the arriving page's icon was then adopted
        // onto whichever bookmark was showing. An empty target means the bar is collapsed with
        // nothing open, which is not an instruction to navigate anywhere.
        //
        // And never a link tab, which is the same trap one tab over: a favicon fetch completing
        // would otherwise pull a page the user opened from a link over to the launcher's own
        // address, with no user action at all.
        //
        // Safe to compare against CurrentTargetUrl now that _rememberedUrl tracks the two gestures
        // that mean "go here": a page the user chose answers for itself, so this fires only when
        // the launcher's own address has actually changed under a tab still sitting on the old one.
        string url = NormalizeUrl(CurrentTargetUrl());
        if (_activeTab is { HomeKey: not null } home &&
            !string.IsNullOrEmpty(url) &&
            !string.Equals(home.NavigatedUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            if (home.View.CoreWebView2 != null)
                Navigate(url);
            else
                home.NavigatedUrl = "";
        }

        // Not while the window has deliberately been grown past its configured size. This runs
        // whenever anything touches the launcher — including a favicon fetch completing — so
        // without the guard a maximized flyout snapped back to its normal size with no user
        // action at all, exactly as the navigation above once did.
        if (_isOpen && !_isMaximized && !_isFullScreen)
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

    /// <summary>
    /// Says a navigation has started, in whichever of the two ways fits what is on screen.
    /// </summary>
    /// <remarks>
    /// The overlay when the tab has nothing to show yet, or when its page is deliberately hidden
    /// waiting on a reload (see <see cref="_revealOnNavigationCompleted"/>), because then the
    /// overlay <em>is</em> the content. The hairline once the tab has a page, because covering a
    /// readable page with a spinner to announce the next one is the complaint this exists to answer.
    /// </remarks>
    private void ShowLoading(WebTab? tab = null)
    {
        tab ??= _activeTab;

        // The test is whether there is a page on screen right now, not what kind of navigation this
        // is. Anything else needs every call site to reason about visibility for itself, and the
        // one that did not was Reload: it left the page up and put a spinner over the middle of it.
        bool pageOnScreen = tab is { HasLoaded: true }
            && tab.View.Visibility == Visibility.Visible
            && !_revealOnNavigationCompleted;

        if (pageOnScreen)
        {
            ShowLoadingBar(true);
            return;
        }

        SetStatus("Loading…", busy: true, showRetry: false);
    }

    /// <summary>Shows or hides the hairline, stopping its animation when it is not being seen.</summary>
    private void ShowLoadingBar(bool running)
    {
        _loadingBar.IsIndeterminate = running;
        _loadingBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

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
        ShowLoadingBar(false);

        _statusRing.IsActive = false;
        _statusPanel.Visibility = Visibility.Collapsed;
    }

    // ── Window events ───────────────────────────────────────────────

    private void WebFlyoutWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            // Activation came back — whatever was being watched for is resolved.
            StopForegroundWatch();

            // WinUI replaces WM_SETICON as it initialises and activates the window, so the icon
            // has to be re-asserted here rather than only when it was first applied. Cheap: two
            // messages with handles that are already loaded, and a no-op until this launcher has
            // run as a regular window.
            PushWindowIcon();
            return;
        }

        // A drag that leaves the window, and any owned window, both pin the flyout open — the
        // same rule the item flyout applies to edit mode and its editors.
        // An open question pins the flyout too: a prompt that disappears when the user clicks
        // elsewhere is a request the page never gets an answer to.
        if (_isShowing || !_isOpen || _launcher.WebPinFlyout || StaysOpenAsWindow || _isModalOpen || _isResizing || _isMovingWindow || _isStripDragging || _isFullScreen || IsPromptOpen || _isMenuOpen) return;

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
            if (_launcher.WebPinFlyout || StaysOpenAsWindow || _isModalOpen || _isResizing || _isMovingWindow || _isStripDragging || _isFullScreen || IsPromptOpen || _isMenuOpen) return;

            if (IsForegroundStillOurs())
            {
                // Focus is somewhere that belongs to this flyout but is not the flyout itself —
                // a file picker, a passkey prompt. Nothing will deactivate this window a second
                // time when that closes, so watch for it rather than waiting for an event that
                // is not coming.
                StartForegroundWatch();
                return;
            }

            HideFlyout();
        });
    }

    /// <summary>
    /// True when the foreground window is this flyout, something inside it, or something it
    /// raised — so the user has not actually gone anywhere.
    /// </summary>
    /// <remarks>
    /// Three separate relationships, because a hosted browser produces all three:
    /// <list type="bullet">
    /// <item>the browser's own HWNDs are <b>children</b> of this window, so clicking into the page
    /// deactivates it without the user leaving;</item>
    /// <item>a file picker, the Windows Security passkey prompt or a print dialog is a top-level
    /// window <b>owned by</b> this one — not a child, which is why <c>IsChild</c> alone let an
    /// upload from Discord or WhatsApp dismiss the flyout the moment the picker appeared;</item>
    /// <item>some of what the browser shows is owned by <b>its own</b> windows instead, so the
    /// owner chain never reaches here. Those are matched by process against WebView2's browser
    /// process, which is exact — no guessing from window classes or titles.</item>
    /// </list>
    /// </remarks>
    private bool IsForegroundStillOurs()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == _hwnd || IsChild(_hwnd, foreground)) return true;

        // Bounded: an owner chain is short, and a malformed one must not spin here.
        var owner = foreground;
        for (int depth = 0; depth < 8; depth++)
        {
            owner = GetWindow(owner, GW_OWNER);
            if (owner == IntPtr.Zero) break;
            if (owner == _hwnd || IsChild(_hwnd, owner)) return true;
        }

        try
        {
            uint browserPid = _webView?.CoreWebView2?.BrowserProcessId ?? 0;
            if (browserPid == 0) return false;

            GetWindowThreadProcessId(foreground, out uint foregroundPid);
            return foregroundPid == browserPid;
        }
        catch (Exception ex)
        {
            // Reading the browser process id throws once the core is gone, which simply means
            // there is no browser to have raised anything.
            Logger.Debug(ex, "Could not read the browser process id for launcher {Name}", _launcher.Name);
            return false;
        }
    }

    /// <summary>
    /// Polls until whatever the flyout raised has gone away, then applies the dismissal that was
    /// deferred while it was up.
    /// </summary>
    /// <remarks>
    /// A window only deactivates once. Declining to dismiss because a file picker had focus would
    /// otherwise pin the flyout open for good — the user closes the picker, clicks another app,
    /// and no second <c>Deactivated</c> ever arrives to reconsider. Polling is the honest way to
    /// track a foreground window belonging to another process; it runs only while a dialog is
    /// actually up, and stops the moment the answer is known.
    /// </remarks>
    private void StartForegroundWatch()
    {
        _foregroundWatchTimer ??= DispatcherQueue.CreateTimer();
        if (_foregroundWatchTimer.IsRunning) return;

        _foregroundWatchTimer.Interval = TimeSpan.FromMilliseconds(400);
        _foregroundWatchTimer.IsRepeating = true;
        _foregroundWatchTimer.Tick -= ForegroundWatchTimer_Tick;
        _foregroundWatchTimer.Tick += ForegroundWatchTimer_Tick;
        _foregroundWatchTimer.Start();
    }

    private void StopForegroundWatch()
    {
        if (_foregroundWatchTimer == null) return;
        _foregroundWatchTimer.Stop();
        _foregroundWatchTimer.Tick -= ForegroundWatchTimer_Tick;
    }

    private void ForegroundWatchTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        // Nothing left to watch for: the flyout has gone, or something else is now holding it open.
        if (!_isOpen || _isHiding || _launcher.WebPinFlyout || _isModalOpen || _isResizing || _isMovingWindow || _isStripDragging || _isFullScreen || IsPromptOpen)
        {
            StopForegroundWatch();
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground == _hwnd)
        {
            // The dialog handed focus back to the flyout itself; a later click elsewhere will
            // deactivate it again, which is the normal path.
            StopForegroundWatch();
            return;
        }

        if (IsForegroundStillOurs()) return;   // still up

        StopForegroundWatch();
        HideFlyout();
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (msg == 0x0100 && wParam == (IntPtr)0x1B) // WM_KEYDOWN + VK_ESCAPE
        {
            HideFlyout();
            return IntPtr.Zero;
        }

        // In regular-window mode the taskbar button's click arrives as a minimize request.
        if (HandleTaskbarMinimize(msg, wParam)) return IntPtr.Zero;

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

        // Anchored to its last position, and there is one — clamped into the work area so a screen
        // that has since gone away cannot strand it. Nothing dragged yet falls through to the tray
        // placement below, which is what the first open of such a launcher gets.
        int anchor = WebAnchors.Normalize(_launcher.WebAnchor);
        if (anchor == WebAnchors.LastPosition && _launcher.GetWebFlyoutPosition() is { } saved)
        {
            var savedPoint = new POINT { X = saved.X, Y = saved.Y };
            var savedMonitor = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(MonitorFromPoint(savedPoint, MONITOR_DEFAULTTONEAREST), ref savedMonitor);
            var area = savedMonitor.rcWork;

            int savedLeft = Math.Clamp(saved.X, area.Left, Math.Max(area.Left, area.Right - width));
            int savedTop = Math.Clamp(saved.Y, area.Top, Math.Max(area.Top, area.Bottom - height));
            return new FlyoutPlacement(savedLeft, savedTop, savedTop, width, height, SlideEdge.Bottom);
        }

        // A fixed anchor replaces the tray-relative placement — on the monitor whose tray icon
        // was clicked, so a corner still means a corner of the screen the user is working on.
        //
        // LastPosition is excluded alongside Tray: it is not a corner, and reaching here means it had
        // no position to open at, so the tray placement below is the right fallback.
        if (anchor != WebAnchors.Tray && anchor != WebAnchors.LastPosition)
        {
            int anchoredLeft =
                WebAnchors.IsLeft(anchor) ? workArea.Left + gap :
                WebAnchors.IsRight(anchor) ? workArea.Right - width - gap :
                workArea.Left + ((workArea.Right - workArea.Left - width) / 2);

            int anchoredTop =
                WebAnchors.IsTop(anchor) ? workArea.Top + gap :
                WebAnchors.IsBottom(anchor) ? workArea.Bottom - height - gap :
                workArea.Top + ((workArea.Bottom - workArea.Top - height) / 2);

            // Slides down from a top anchor and up from anything else, so it always arrives from
            // the nearest edge rather than travelling across the screen.
            var anchoredEdge = WebAnchors.IsTop(anchor) ? SlideEdge.Top : SlideEdge.Bottom;
            int anchoredStart = anchoredEdge == SlideEdge.Top
                ? anchoredTop - slideDistance
                : anchoredTop + slideDistance;

            return new FlyoutPlacement(anchoredLeft, anchoredTop, anchoredStart, width, height, anchoredEdge);
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
