using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Controls;
using LittleLauncher.Models;
using LittleLauncher.Pages;
using LittleLauncher.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Microsoft.UI.Xaml.Markup;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public partial class FlyoutWindow : Window
{
    private const int ColumnWidth = 175;
    private const int DefaultIconColumnWidth = 260;
    private const int IconCellWidth = 80;
    private const int IconCellHeight = 84;
    private const int IconSize = 32;
    private const int IconColumnChromeWidth = DefaultIconColumnWidth - (IconCellWidth * Launcher.DefaultIconModeIconsPerRow);
    private const int DefaultSmallIconColumnWidth = 136;
    private const int SmallIconCellWidth = 40;
    private const int SmallIconCellHeight = 40;
    private const int SmallIconSize = 20;
    private const int SmallIconColumnChromeWidth = DefaultSmallIconColumnWidth - (SmallIconCellWidth * Launcher.DefaultIconModeIconsPerRow);
    private const int IconGroupHeaderHeight = 30;
    private const int SmallIconGroupHeaderHeight = 30;
    private const int FlyoutOuterPadding = 8;
    private const int LauncherTitleHeight = 32;
    private const double DefaultMinFlyoutHeight = 80;
    private const double SmallIconMinFlyoutHeight = 52;
    private const int ResizeGripWidth = 4;
    private const double SlideDistanceDip = 36;
    private const uint ShowAnimationDurationMs = 200;
    private const uint HideAnimationDurationMs = 160;

    /// <summary>
    /// Fraction of the hide animation over which the flyout fades out, so the fade is complete
    /// before the window is taken off screen.
    /// </summary>
    /// <remarks>
    /// Deliberately less than 1. Fading right up to the final frame leaves the window still
    /// faintly visible when it parks — with ~8ms ticks the last painted alpha lands wherever the
    /// cadence happens to fall — so the content would wink out instead of finishing its fade.
    /// Completing early means the last stretch of the slide is already fully transparent and the
    /// park is guaranteed to be invisible.
    /// </remarks>
    private const double FadeOutCompleteAt = 0.8;

    /// <summary>How long a warmed-up flyout stays parked off screen to compose its first frame.</summary>
    private const int PreRenderDurationMs = 400;

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object BoundsFileLock = new();
    private static readonly ConcurrentDictionary<string, WindowBounds> CachedBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-launcher flyout window instances (key = Launcher.Id).</summary>
    private static readonly Dictionary<string, FlyoutWindow> _instances = new();

    private DateTime _lastDismissed = DateTime.MinValue;
    private bool _toolWindowStyleApplied;

    /// <summary>True while the window is parked off screen composing its first frame.</summary>
    private bool _isPreRendering;

    /// <summary>
    /// True while the flyout is placed on screen for the user. The window itself stays visible
    /// in the Win32 sense even when dismissed — see <see cref="ParkOffScreen"/> — so this, not
    /// <c>WS_VISIBLE</c>, is the test for "the flyout is open".
    /// </summary>
    private bool _isOpen;

    /// <summary>True while <c>WS_EX_LAYERED</c> is applied for a fade. See <see cref="SetFadeAlpha"/>.</summary>
    private bool _fadeStyleApplied;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _preRenderTimer;
    private int _lastItemsHash;
    private MainWindow? _owner;
    private LauncherItem? _dragItem;
    private ObservableCollection<LauncherItem>? _dragSourceCollection;
    private Control? _lastIndicatorContainer;
    private ListViewBase? _lastIndicatorListView;
    private Brush? _lastIndicatorContainerBorderBrush;
    private Thickness _lastIndicatorContainerBorderThickness;
    private Thickness _lastIndicatorContainerPadding;
    private Thickness _lastIndicatorContainerMargin;
    private Brush? _lastIndicatorListBorderBrush;
    private Thickness _lastIndicatorListBorderThickness;
    private ResizeGrip _leftResizeGrip = null!;
    private ResizeGrip _rightResizeGrip = null!;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;
    private bool _isShowing;
    private bool _isHiding;
    private int _animationVersion;
    private bool _isResizingIconWidth;
    private bool _resizeFromLeft;
    private bool _resizeChangedSetting;
    private int _resizeStartCursorX;
    private int _resizeStartIconsPerRow;
    private List<ObservableCollection<LauncherItem>> _columnLists = [];
    private readonly HashSet<ListView> _loadedIconChildLists = [];
    private readonly HashSet<FrameworkElement> _loadedIconGroupRoots = [];
    private readonly HashSet<LauncherItem> _syntheticGroups = [];
    private readonly Launcher _launcher;  // The launcher this window displays
    private FlyoutEntranceEdge _lastEntranceEdge = FlyoutEntranceEdge.Bottom;

    private readonly record struct FlyoutPlacement(
        int Left,
        int Top,
        int StartTop,
        int Width,
        int Height,
        FlyoutEntranceEdge Edge);

    private enum FlyoutEntranceEdge
    {
        Top,
        Bottom,
    }

    private static bool AreAnimationsEnabled => SettingsManager.Current.FlyoutAnimationsEnabled;
    private int CurrentViewMode => LauncherViewModes.Normalize(_launcher.ViewMode);
    private bool IsListMode => CurrentViewMode == LauncherViewModes.List;
    private bool IsSmallIconMode => CurrentViewMode == LauncherViewModes.SmallIcon;
    private bool IsIconMode => LauncherViewModes.IsIconMode(CurrentViewMode);
    private bool IsReadOnlyLauncher => _launcher is { IsShared: true, IsSharedOwner: false };

    private FlyoutWindow(MainWindow owner, Launcher launcher)
    {
        _owner = owner;
        _launcher = launcher;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Remove titlebar and make borderless, always on top so it
        // renders above the tray overflow popup.
        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        GetAppWindow().SetPresenter(presenter);

        InitializeResizeGrips();
        RebuildColumnsPanel();
        InitializeEmptyPlaceholder();
        InitializeEditChrome();

        // Desktop Acrylic blurs whatever is behind the window (including other windows),
        // unlike Mica which only samples the wallpaper.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // OS-level rounded corners (Windows 11) + DWM shadow
        int cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // Hook WndProc for deactivation detection
        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 2, 0);
        Activated += FlyoutWindow_Activated;

        // Apply saved app theme
        ThemeManager.ApplySavedTheme(this);
    }

    private void FlyoutWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Edit mode (and any modal it owns) pins the flyout open — otherwise opening the
        // item editor or a file picker would dismiss the very window being edited.
        if (_isShowing || _isResizingIconWidth || SuppressDismiss) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            HideFlyout();
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (msg == 0x0100 && wParam == (IntPtr)0x1B) // WM_KEYDOWN + VK_ESCAPE
        {
            // Two-stage: the first Escape leaves edit mode, the second dismisses.
            if (_isEditMode)
            {
                ExitEditMode();
                return IntPtr.Zero;
            }

            HideFlyout();
            _lastDismissed = DateTime.UtcNow;
            return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    internal static FlyoutWindow? GetCurrent(string? launcherId = null)
    {
        if (launcherId != null)
            return _instances.TryGetValue(launcherId, out var fw) ? fw : null;
        return _instances.Values.FirstOrDefault();
    }

    public static void Toggle(MainWindow owner, int screenX, int screenY, string launcherId)
    {
        if (!_instances.TryGetValue(launcherId, out var instance) || instance == null)
        {
            // Find the launcher
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher == null) return;
            instance = new FlyoutWindow(owner, launcher);
            _instances[launcherId] = instance;
        }

        // Ends the warm-up render, if one is still running, so the window is free to be placed.
        instance.EndPreRender();

        if (instance._isOpen && instance._hwnd != IntPtr.Zero && IsWindow(instance._hwnd))
        {
            if (!instance._isHiding)
                instance.HideFlyout();
            return;
        }

        if (!instance._isHiding && (DateTime.UtcNow - instance._lastDismissed).TotalMilliseconds < 300)
            return;

        instance._owner = owner;
        instance.RebuildItemsIfNeeded();

        // Calculate DPI-aware dimensions
        double dpiScale = GetDpiForWindow(instance._hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        int flyoutWidthPx = (int)Math.Ceiling(instance.GetFlyoutWidth() * dpiScale);
        int flyoutHeightPx = (int)Math.Ceiling(instance.MeasureContentHeight() * dpiScale);

        instance._isShowing = true;
        var appWindow = instance.GetAppWindow();
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(flyoutWidthPx, flyoutHeightPx));
        if (!instance._toolWindowStyleApplied)
        {
            int exStyle = GetWindowLong(instance._hwnd, GWL_EXSTYLE);
            SetWindowLong(instance._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            instance._toolWindowStyleApplied = true;
        }

        var placement = instance.CalculatePlacement(screenX, screenY);
        instance._lastEntranceEdge = placement.Edge;
        if (AreAnimationsEnabled)
            instance.ShowAnimated(placement);
        else
            instance.ShowWithoutAnimation(placement);
    }

    public static void DismissIfOpen()
    {
        foreach (var fw in _instances.Values)
            fw.HideFlyout();
    }

    public static void WarmUp(MainWindow owner, IEnumerable<Launcher> launchers)
    {
        foreach (var launcher in launchers)
        {
            if (!_instances.ContainsKey(launcher.Id))
            {
                var fw = new FlyoutWindow(owner, launcher);
                fw._lastItemsHash = ComputeItemsHash(launcher);
                int exStyle = GetWindowLong(fw._hwnd, GWL_EXSTYLE);
                SetWindowLong(fw._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
                fw._toolWindowStyleApplied = true;
                fw.PreRenderOffScreen();
                _instances[launcher.Id] = fw;
            }
        }
    }

    /// <summary>
    /// Composes the window's first frame while it is parked outside the desktop.
    /// </summary>
    /// <remarks>
    /// Constructing a window is not enough to give it a rendered surface: WinUI only draws a
    /// window that has actually been visible. Until then the DWM has nothing to present, so the
    /// first show flashed the window's extended frame — a black rectangle — for the few frames
    /// XAML took to lay out and paint. That first paint got slower as the flyout gained content
    /// (per-column containers, hover and edit chrome), which is when the flash became visible.
    ///
    /// Parked at the virtual screen's origin minus the window's own size, so no part of it can
    /// land on a real monitor whatever the display arrangement. The window then *stays* parked
    /// there for the life of the app whenever the flyout is dismissed — see
    /// <see cref="ParkOffScreen"/> — so this warm-up is the only time it has to compose from
    /// nothing.
    /// </remarks>
    private void PreRenderOffScreen()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        int width = (int)Math.Ceiling(GetFlyoutWidth() * dpiScale);
        int height = (int)Math.Ceiling(MeasureContentHeight() * dpiScale);

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 64;
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 64;

        _isPreRendering = true;
        SetWindowPos(_hwnd, 0, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        _preRenderTimer = DispatcherQueue.CreateTimer();
        _preRenderTimer.Interval = TimeSpan.FromMilliseconds(PreRenderDurationMs);
        _preRenderTimer.IsRepeating = false;
        _preRenderTimer.Tick += (_, _) => EndPreRender();
        _preRenderTimer.Start();
    }

    /// <summary>
    /// Ends the warm-up render. The window stays parked off screen rather than being hidden —
    /// see <see cref="ParkOffScreen"/>.
    /// </summary>
    private void EndPreRender()
    {
        if (!_isPreRendering) return;
        _isPreRendering = false;

        _preRenderTimer?.Stop();
        _preRenderTimer = null;
    }

    /// <summary>
    /// Moves the window outside the virtual screen instead of hiding it, and marks it closed.
    /// </summary>
    /// <remarks>
    /// The flyout used to be dismissed with <c>ShowWindow(SW_HIDE)</c>. Hiding a WinUI window
    /// lets its composition surfaces be released, so the next show had to re-rasterise the whole
    /// visual tree — every text run and every item icon — before anything could be presented.
    /// Measured on a cold open, that took ~100ms, during which the window was already on screen
    /// and sliding: the flyout slid in as an empty rectangle and the items all popped in at the
    /// end. Re-opening within a second or so was fine, because the surfaces were still resident;
    /// that is why this only ever looked broken in normal use, where opens are seconds apart.
    ///
    /// Keeping the window visible but off the virtual screen keeps those surfaces alive, so a
    /// show is a pure move and the first on-screen frame is already fully painted.
    ///
    /// <c>_isOpen</c> — not <c>WS_VISIBLE</c> — is now what "the flyout is open" means, since the
    /// window is visible in the Win32 sense for the whole life of the app.
    /// </remarks>
    private void ParkOffScreen()
    {
        _isOpen = false;

        // The hide fade leaves the window transparent; clearing it here means every path back on
        // screen starts opaque, without each having to remember.
        ClearFade();

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        GetWindowRect(_hwnd, out var rect);
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 64;
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 64;

        SetWindowPos(_hwnd, 0, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Destroys the flyout instance for a launcher that has been deleted.</summary>
    public static void DisposeLauncher(string launcherId)
    {
        if (_instances.TryGetValue(launcherId, out var fw))
        {
            // The edit chrome lives in its own top-level windows, which closing the flyout
            // does not touch — without this they outlive the launcher they belong to.
            fw.CloseEditChrome();
            fw.Close();
            _instances.Remove(launcherId);
        }
    }

    private void HideFlyout()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        // Ends a warm-up render rather than animating it off screen; the check below then
        // returns, since a warming window was never on screen to dismiss.
        EndPreRender();

        if (!_isOpen)
            return;

        // Closing ends editing, so the flyout never reopens mid-edit and the off-screen
        // measure path never has to account for edit chrome.
        if (_isEditMode)
        {
            _isEditMode = false;
            CloseOpenModal();
            _editToolbarBar?.HideBar();
            UpdateResizeGripVisibility();
            ApplyEditVisuals();
        }

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
        int endTop = _lastEntranceEdge == FlyoutEntranceEdge.Top ? rect.Top - exitOffset : rect.Top + exitOffset;
        int animationVersion = ++_animationVersion;

        AnimateWindowPosition(animationVersion, rect.Left, rect.Top, endTop, width, height, HideAnimationDurationMs, hideAtEnd: true);
    }

    private void ShowWithoutAnimation(FlyoutPlacement placement)
    {
        _animationVersion++;
        _isHiding = false;
        _isShowing = true;
        _isOpen = true;

        // A hide interrupted mid-fade never reached ParkOffScreen, so the window can still be
        // part-transparent here.
        ClearFade();

        SetWindowPos(_hwnd, 0, placement.Left, placement.Top, placement.Width, placement.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);

        _isShowing = false;
    }

    private void ShowAnimated(FlyoutPlacement placement)
    {
        _isHiding = false;
        _isShowing = true;

        // Only an already-open flyout continues from where it is; a parked one starts from the
        // placement's off-edge position, since its parked rect is nowhere near the screen.
        int startTop = placement.StartTop;
        if (_isOpen && GetWindowRect(_hwnd, out var rect))
            startTop = rect.Top;

        _isOpen = true;

        // Re-opening while a hide is still fading out would otherwise slide a part-transparent
        // (or fully invisible) window back in — the animation version bump below only stops the
        // fade, it does not undo it.
        ClearFade();

        int animationVersion = ++_animationVersion;

        SetWindowPos(_hwnd, 0, placement.Left, startTop, placement.Width, placement.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);

        if (startTop == placement.Top)
        {
            _isShowing = false;
            return;
        }

        AnimateWindowPosition(animationVersion, placement.Left, startTop, placement.Top,
            placement.Width, placement.Height, ShowAnimationDurationMs, hideAtEnd: false);
    }

    private void AnimateWindowPosition(int animationVersion, int left, int startTop, int endTop, int width, int height, uint durationMs, bool hideAtEnd)
    {
        if (startTop == endTop)
        {
            CompleteWindowAnimation(animationVersion, left, endTop, width, height, hideAtEnd);
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
            double eased = hideAtEnd ? EaseOutExit(progress) : EaseOutCubic(progress);
            int currentTop = (int)Math.Round(Lerp(startTop, endTop, eased));

            if (hideAtEnd)
                SetFadeAlpha(1 - Math.Min(1, progress / FadeOutCompleteAt));

            SetWindowPos(_hwnd, 0, left, currentTop, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

            if (progress >= 1)
            {
                CompositionTarget.Rendering -= handler;
                CompleteWindowAnimation(animationVersion, left, endTop, width, height, hideAtEnd);
            }
        };

        CompositionTarget.Rendering += handler;
    }

    private void CompleteWindowAnimation(int animationVersion, int left, int top, int width, int height, bool hideAtEnd)
    {
        if (animationVersion != _animationVersion || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        SetWindowPos(_hwnd, 0, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

        if (hideAtEnd)
        {
            ParkOffScreen();
            _isHiding = false;
        }
        else
        {
            _isShowing = false;
        }
    }

    /// <summary>
    /// Sets the window's overall opacity for the hide animation, so it fades as it slides.
    /// </summary>
    /// <remarks>
    /// Without this the flyout was fully opaque at the end of its travel and then simply stopped
    /// existing — the eye is still tracking a moving object when it is cut, which reads as an
    /// abrupt snap however smooth the slide itself is.
    ///
    /// Per-window alpha (<c>WS_EX_LAYERED</c> + <c>LWA_ALPHA</c>) rather than XAML opacity: the
    /// flyout's acrylic comes from a <c>SystemBackdrop</c>, which sits behind the XAML tree and
    /// so is untouched by <c>RootGrid.Opacity</c> — fading the content alone would leave the
    /// backdrop pane behind as a solid rectangle, which is the same abrupt cut one layer down.
    ///
    /// <c>ClearFade</c> must run before the window is shown again: the alpha is a window-level
    /// property that survives being parked, so a flyout re-opened after a fade would come back
    /// invisible.
    /// </remarks>
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

    /// <summary>Restores full opacity and drops the layered style. See <see cref="SetFadeAlpha"/>.</summary>
    private void ClearFade()
    {
        if (!_fadeStyleApplied) return;
        _fadeStyleApplied = false;

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
    }

    private int GetSlideDistancePx()
    {
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        return Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));
    }

    private static double Lerp(int start, int end, double progress)
    {
        return start + ((end - start) * progress);
    }

    private static double EaseOutCubic(double progress)
    {
        double inverse = 1 - progress;
        return 1 - (inverse * inverse * inverse);
    }

    /// <summary>
    /// Exit curve for the hide slide: accelerates away, but with a linear floor so that even the
    /// first frames move.
    /// </summary>
    /// <remarks>
    /// This was a pure cubic ease-in, which stalls at the start — and the flyout only travels
    /// <see cref="SlideDistanceDip"/> (~54px at 150% scale) in <see cref="HideAnimationDurationMs"/>.
    /// Over that little distance a cubic quantises to nothing: measured, the first five frames
    /// (~30ms) all rounded to the same pixel, so the close sat still and then lurched away at
    /// ~7px per frame before the window cut out. That read as choppy even though the animation
    /// loop itself was ticking cleanly at ~8ms.
    ///
    /// The linear term keeps every frame's step above a pixel; the quadratic term keeps the
    /// accelerating-away character an exit wants. Any easing used here has to be checked against
    /// the *pixel* deltas, not just the curve shape — a curve that looks smooth mathematically
    /// can still round to a stationary window over a short slide.
    /// </remarks>
    private static double EaseOutExit(double progress)
    {
        return (0.35 * progress) + (0.65 * progress * progress);
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>
    /// Sets AUMID and relaunch properties (icon, command, display name) on the
    /// flyout HWND. Called every time the flyout is shown so the taskbar picks up
    /// icon or name changes without requiring unpin+repin.
    /// </summary>
    // ── Content ─────────────────────────────────────────────────────

    private static int ComputeItemsHash(Launcher launcher)
    {
        var items = launcher.Items;
        if (items == null || items.Count == 0) return 0;
        var hash = new HashCode();
        hash.Add(LauncherViewModes.Normalize(launcher.ViewMode));
        hash.Add(Launcher.ClampIconModeIconsPerRow(launcher.IconModeIconsPerRow));
        hash.Add(launcher.ShowTitle);
        hash.Add(launcher.Name);
        foreach (var item in items)
        {
            HashItem(ref hash, item);
            if (item.IsGroup)
            {
                foreach (var child in item.Children)
                    HashItem(ref hash, child);
            }
        }
        return hash.ToHashCode();
    }

    private static void HashItem(ref HashCode hash, LauncherItem item)
    {
        hash.Add(item.Name);
        hash.Add(item.Path);
        hash.Add(item.IconPath);
        hash.Add(item.IconGlyph);
        hash.Add(item.IconColor);
        hash.Add(item.IsWebsite);
        hash.Add(item.OpenInAppWindow);
        hash.Add(item.AppWindowBrowser);
        hash.Add(item.AppWindowBrowserProfile);
        hash.Add(item.IsGroup);
        hash.Add(item.IsPwa);
        hash.Add(item.IsColumnBreak);
    }

    /// <summary>
    /// Splits the hierarchical item list into per-column display lists.
    /// Items after each <see cref="LauncherItem.IsColumnBreak"/> start a new column.
    /// Groups are flattened (children appended) unless the group is collapsed.
    /// </summary>
    private List<ObservableCollection<LauncherItem>> BuildColumnLists()
    {
        var columns = new List<ObservableCollection<LauncherItem>>();
        var current = new ObservableCollection<LauncherItem>();
        columns.Add(current);

        foreach (var item in _launcher.Items)
        {
            if (item.IsColumnBreak)
            {
                current = new ObservableCollection<LauncherItem>();
                columns.Add(current);
                continue;
            }

            current.Add(item);
        }

        return columns;
    }

    private void WrapUngroupedItemsIntoSyntheticGroups()
    {
        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            var column = _columnLists[columnIndex];
            var newColumn = new ObservableCollection<LauncherItem>();
            LauncherItem? currentSynthetic = null;

            foreach (var item in column)
            {
                if (item.IsGroup)
                {
                    currentSynthetic = null;
                    newColumn.Add(item);
                    continue;
                }

                if (currentSynthetic == null)
                {
                    currentSynthetic = LauncherItem.CreateGroup(string.Empty);
                    currentSynthetic.IsExpanded = true;
                    _syntheticGroups.Add(currentSynthetic);
                    newColumn.Add(currentSynthetic);
                }

                currentSynthetic.Children.Add(item);
            }

            _columnLists[columnIndex] = newColumn;
        }
    }

    private void SyncColumnsToFlatList()
    {
        _launcher.Items.Clear();

        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            if (columnIndex > 0)
                _launcher.Items.Add(LauncherItem.CreateColumnBreak());

            foreach (var item in _columnLists[columnIndex])
            {
                if (_syntheticGroups.Contains(item))
                {
                    foreach (var child in item.Children)
                        _launcher.Items.Add(child);
                }
                else
                {
                    _launcher.Items.Add(item);
                }
            }
        }
    }

    private ListViewBase CreateColumnListView(int columnIndex)
    {
        bool isIconMode = IsIconMode;
        string templateSelectorKey = isIconMode
            ? (IsSmallIconMode ? "SmallIconItemTemplateSelector" : "IconItemTemplateSelector")
            : "ListItemTemplateSelector";

        ListViewBase lv = isIconMode ? new GridView() : new ListView();
        lv.Width = isIconMode ? GetIconColumnWidth(_columnLists[columnIndex]) : ColumnWidth;
        lv.Padding = new Thickness(0);
        lv.IsItemClickEnabled = true;
        lv.SelectionMode = ListViewSelectionMode.None;
        lv.IsTabStop = false;
        lv.CanDragItems = true;
        lv.AllowDrop = true;
        lv.Tag = columnIndex;
        lv.TabNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Once;
        lv.ItemTemplateSelector = (DataTemplateSelector)RootGrid.Resources[templateSelectorKey];
        lv.ItemContainerTransitions = new TransitionCollection();
        lv.Transitions = new TransitionCollection();

        if (isIconMode)
        {
            lv.ItemContainerStyle = (Style)RootGrid.Resources["IconGroupGridItemContainerStyle"];
            lv.ItemsPanel = (ItemsPanelTemplate)RootGrid.Resources["IconColumnItemsPanel"];
            lv.ContainerContentChanging += IconColumn_ContainerContentChanging;
        }
        else
            lv.ItemContainerStyleSelector = (StyleSelector)RootGrid.Resources["ItemContainerStyleSelector"];

        ScrollViewer.SetVerticalScrollBarVisibility(lv, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        lv.Loaded += ColumnListView_Loaded;
        lv.ItemClick += ItemsListControl_ItemClick;
        lv.ContextRequested += ItemsListControl_ContextRequested;
        lv.DragItemsStarting += ColumnListView_DragItemsStarting;
        lv.DragOver += ColumnListView_DragOver;
        lv.DragLeave += ColumnListView_DragLeave;
        lv.Drop += ColumnListView_Drop;
        lv.DragItemsCompleted += ColumnListView_DragItemsCompleted;
        return lv;
    }

    private static void DisableListViewTransitions(ListViewBase listView)
    {
        listView.ItemContainerTransitions = new TransitionCollection();
        listView.Transitions = new TransitionCollection();

        if (listView.ItemsPanelRoot is Panel panel)
            panel.ChildrenTransitions = new TransitionCollection();
    }

    private void ColumnListView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            DisableListViewTransitions(listView);
            WireHoverAffordance(listView);
            // Containers are realised after a rebuild, so edit visuals are applied here
            // rather than at the point the mode was entered.
            if (_isEditMode)
                ApplyEditVisuals();
            if (IsIconMode)
                ApplyTopLevelIconSpans(listView);
        }
    }

    private void IconColumn_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not LauncherItem item || args.ItemContainer is not Control container)
            return;

        if (sender.ItemsPanelRoot is PackedIconPanel wrapGrid)
        {
            wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();
            wrapGrid.ItemWidth = GetActiveIconCellWidth();
            wrapGrid.ItemHeight = GetActiveIconCellHeight();
            wrapGrid.InvalidateMeasure();
        }

        VariableSizedWrapGrid.SetColumnSpan(container, GetTopLevelIconSpan(item));
        VariableSizedWrapGrid.SetRowSpan(container, GetTopLevelIconRowSpan(item));
    }

    private void InitializeResizeGrips()
    {
        _leftResizeGrip = CreateResizeGrip(HorizontalAlignment.Left);
        _rightResizeGrip = CreateResizeGrip(HorizontalAlignment.Right);
        RootGrid.Children.Add(_leftResizeGrip);
        RootGrid.Children.Add(_rightResizeGrip);
    }

    private ResizeGrip CreateResizeGrip(HorizontalAlignment alignment)
    {
        var grip = new ResizeGrip
        {
            Width = 10,
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        grip.SetValue(Canvas.ZIndexProperty, 10);
        grip.PointerPressed += ResizeGrip_PointerPressed;
        grip.PointerMoved += ResizeGrip_PointerMoved;
        grip.PointerReleased += ResizeGrip_PointerReleased;
        grip.PointerCaptureLost += ResizeGrip_PointerCaptureLost;
        return grip;
    }

    // ── Icon mode rendering ─────────────────────────────────────────

    private int GetIconModeIconsPerRow() => Launcher.ClampIconModeIconsPerRow(_launcher.IconModeIconsPerRow);

    private int GetActiveIconCellWidth() => IsSmallIconMode ? SmallIconCellWidth : IconCellWidth;

    private int GetActiveIconCellHeight() => IsSmallIconMode ? SmallIconCellHeight : IconCellHeight;

    private int GetActiveIconSize() => IsSmallIconMode ? SmallIconSize : IconSize;

    private int GetActiveIconColumnChromeWidth() => IsSmallIconMode ? SmallIconColumnChromeWidth : IconColumnChromeWidth;

    private int GetActiveGroupHeaderHeight() => IsSmallIconMode ? SmallIconGroupHeaderHeight : IconGroupHeaderHeight;

    private double GetMinimumFlyoutHeight() => IsSmallIconMode ? SmallIconMinFlyoutHeight : DefaultMinFlyoutHeight;

    private int GetIconColumnWidth() => GetActiveIconColumnChromeWidth() + (GetIconModeIconsPerRow() * GetActiveIconCellWidth());

    private int GetIconColumnWidth(ObservableCollection<LauncherItem> items)
    {
        return GetIconColumnWidth();
    }

    private int GetIconGroupContentWidth(LauncherItem group)
    {
        int maxIconsPerRow = GetIconModeIconsPerRow();
        // Must match GetTopLevelIconSpan, or the rendered width and the packing span disagree.
        int visibleIcons = Math.Clamp(group.Children.Count, 1, maxIconsPerRow);
        return visibleIcons * GetActiveIconCellWidth();
    }

    private ItemsPanelTemplate CreateIconGroupItemsPanel()
    {
        string xaml = "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                      $"<ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='{GetIconModeIconsPerRow()}'/>" +
                      "</ItemsPanelTemplate>";
        return (ItemsPanelTemplate)XamlReader.Load(xaml);
    }

    private int GetTopLevelIconSpan(LauncherItem item)
    {
        if (!item.IsGroup)
            return 1;

        // A group spans as many columns as it has children, so several narrow groups can
        // share a row. A group with more children than fit clamps to the full width, which
        // makes PackedIconPanel start it on a new row (see its row-flush check) and keeps
        // the group's outer shape rectangular instead of starting mid-row and wrapping.
        return Math.Clamp(item.Children.Count, 1, GetIconModeIconsPerRow());
    }

    private int GetTopLevelIconRowSpan(LauncherItem item)
    {
        if (!item.IsGroup)
            return 1;

        int childRows = Math.Max(1, (item.Children.Count + GetIconModeIconsPerRow() - 1) / GetIconModeIconsPerRow());
        return _syntheticGroups.Contains(item) ? childRows : childRows + 1;
    }

    private static bool IsGridItemsPanel(Panel? panel) =>
        panel is ItemsWrapGrid or PackedIconPanel;

    private void ApplyTopLevelIconSpans(ListViewBase listView)
    {
        if (listView.ItemsPanelRoot is not PackedIconPanel wrapGrid)
            return;

        wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();
        wrapGrid.ItemWidth = GetActiveIconCellWidth();
        wrapGrid.ItemHeight = GetActiveIconCellHeight();

        listView.UpdateLayout();

        for (int index = 0; index < listView.Items.Count; index++)
        {
            if (listView.Items[index] is not LauncherItem item || listView.ContainerFromIndex(index) is not Control container)
                continue;

            int columnSpan = GetTopLevelIconSpan(item);
            int rowSpan = GetTopLevelIconRowSpan(item);
            VariableSizedWrapGrid.SetColumnSpan(container, columnSpan);
            VariableSizedWrapGrid.SetRowSpan(container, rowSpan);

            if (index < wrapGrid.Children.Count && wrapGrid.Children[index] is UIElement panelChild)
            {
                VariableSizedWrapGrid.SetColumnSpan(panelChild, columnSpan);
                VariableSizedWrapGrid.SetRowSpan(panelChild, rowSpan);
            }
        }

        wrapGrid.InvalidateMeasure();
        wrapGrid.InvalidateArrange();
        listView.UpdateLayout();
    }

    private int GetFlyoutWidth()
    {
        int contentWidth;

        if (IsListMode)
            contentWidth = ColumnWidth * Math.Max(1, ColumnsPanel.Children.Count);
        else
        {
            contentWidth = 0;
            foreach (var column in _columnLists)
                contentWidth += GetIconColumnWidth(column);
        }

        return contentWidth + (FlyoutOuterPadding * 2);
    }

    private int GetIconResizeStepWidth()
    {
        int columnCount = Math.Max(1, _columnLists.Count);
        int cellWidth = GetActiveIconCellWidth();
        return Math.Max(cellWidth, columnCount * cellWidth);
    }

    /// <summary>
    /// Edge resize is an editing affordance, so the grips only exist in icon mode <i>and</i>
    /// while editing — otherwise a stray drag on the flyout's edge silently changes the
    /// launcher's layout.
    /// </summary>
    private void UpdateResizeGripVisibility()
    {
        var visibility = IsIconMode && _isEditMode ? Visibility.Visible : Visibility.Collapsed;
        _leftResizeGrip.Visibility = visibility;
        _rightResizeGrip.Visibility = visibility;
    }

    private ScrollViewer CreateIconModeColumn(ObservableCollection<LauncherItem> items)
    {
        int iconsPerRow = GetIconModeIconsPerRow();
        var column = new StackPanel { Width = GetIconColumnWidth(items), Padding = new Thickness(8, 2, 8, 2) };
        var currentRow = new StackPanel { Orientation = Orientation.Horizontal };
        int itemsInRow = 0;

        void FlushRow()
        {
            if (currentRow.Children.Count > 0)
            {
                column.Children.Add(currentRow);
                currentRow = new StackPanel { Orientation = Orientation.Horizontal };
                itemsInRow = 0;
            }
        }

        void AddTile(LauncherItem child)
        {
            currentRow.Children.Add(CreateIconTile(child));
            itemsInRow++;
            if (itemsInRow >= iconsPerRow)
                FlushRow();
        }

        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                FlushRow();
                column.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.7,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 4, 0, 4),
                });
            }
            else
            {
                AddTile(item);
            }
        }

        if (currentRow.Children.Count > 0)
            column.Children.Add(currentRow);

        return new ScrollViewer
        {
            Content = column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private Button CreateIconTile(LauncherItem item)
    {
        UIElement iconElement;
        int iconSize = GetActiveIconSize();
        if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
        {
            var bmp = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = iconSize + 4,
                // The stale-icon refresh rewrites cached files in place (same path),
                // so the per-URI decoded-image cache would keep showing the old bitmap.
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            };
            bmp.UriSource = new Uri(item.IconPath, UriKind.Absolute);
            iconElement = new Image
            {
                Source = bmp,
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        else if (Classes.IconGallery.IsFluentGlyph(item.IconGlyph))
        {
            iconElement = new FontIcon
            {
                Glyph = item.IconGlyph,
                FontSize = iconSize - 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (ParseIconColor(item.IconColor) is SolidColorBrush brush1)
                ((FontIcon)iconElement).Foreground = brush1;
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = item.IconGlyph,
                FontSize = iconSize - 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            if (ParseIconColor(item.IconColor) is SolidColorBrush brush2)
                ((TextBlock)iconElement).Foreground = brush2;
        }

        var nameText = new TextBlock
        {
            Text = item.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            MaxWidth = GetActiveIconCellWidth() - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        iconElement.SetValue(Grid.RowProperty, 0);
        iconElement.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        iconElement.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.Children.Add(iconElement);

        nameText.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(nameText, 1);
        content.Children.Add(nameText);

        var tile = new Button
        {
            Width = GetActiveIconCellWidth(),
            Height = GetActiveIconCellHeight(),
            Padding = new Thickness(4),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
            Tag = item,
        };
        tile.Click += IconTile_Click;
        tile.ContextRequested += IconTile_ContextRequested;
        return tile;
    }

    private void IconTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LauncherItem item && !item.IsGroup)
            LaunchItem(item);
    }

    private void IconTile_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LauncherItem item && !item.IsGroup)
        {
            ShowItemContextMenu(btn, item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The column list views, whether or not they are wrapped in a per-column header panel.
    /// Always use this rather than <c>ColumnsPanel.Children.OfType&lt;ListViewBase&gt;()</c>,
    /// which finds nothing once columns are wrapped.
    /// </summary>
    private IEnumerable<ListViewBase> ColumnListViews()
    {
        foreach (var child in ColumnsPanel.Children)
        {
            if (child is ListViewBase direct)
            {
                yield return direct;
            }
            else if (child is Panel wrapper)
            {
                foreach (var inner in wrapper.Children.OfType<ListViewBase>())
                    yield return inner;
            }
        }
    }

    private void RebuildColumnsPanel()
    {
        _columnLists = BuildColumnLists();
        _loadedIconChildLists.Clear();
        _syntheticGroups.Clear();
        // Containers are about to be discarded, so their saved edit styling is moot.
        _editStyledContainers.Clear();
        // ...and their hover wiring, which otherwise pins every container ever realised
        // (this window is permanent) — the root of a large native-memory leak.
        ClearHoverWiring();
        if (IsIconMode)
            WrapUngroupedItemsIntoSyntheticGroups();

        if (RootGrid.Resources["IconItemTemplateSelector"] is FlyoutItemTemplateSelector iconSelector)
            iconSelector.SyntheticGroups = _syntheticGroups;
        if (RootGrid.Resources["SmallIconItemTemplateSelector"] is FlyoutItemTemplateSelector smallIconSelector)
            smallIconSelector.SyntheticGroups = _syntheticGroups;

        ColumnsPanel.Children.Clear();
        UpdateResizeGripVisibility();

        // Show/hide launcher title at the top
        if (_launcher.ShowTitle)
        {
            LauncherTitle.Text = _launcher.Name;
            LauncherTitle.Visibility = Visibility.Visible;
            ContentStack.VerticalAlignment = VerticalAlignment.Top;
            ColumnsPanel.Margin = new Thickness(0);
        }
        else
        {
            LauncherTitle.Visibility = Visibility.Collapsed;
            if (IsIconMode)
            {
                ContentStack.VerticalAlignment = VerticalAlignment.Center;
                ColumnsPanel.Margin = new Thickness(0);
            }
            else
            {
                ContentStack.VerticalAlignment = VerticalAlignment.Top;
                ColumnsPanel.Margin = new Thickness(0, 0, 0, 4);
            }
        }

        bool multipleColumns = _columnLists.Count > 1;

        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            var lv = CreateColumnListView(columnIndex);
            lv.ItemsSource = _columnLists[columnIndex];

            // With a single column there is nothing to distinguish, so the list view goes in
            // bare and normal mode keeps its existing geometry exactly.
            if (!multipleColumns)
            {
                ColumnsPanel.Children.Add(lv);
                continue;
            }

            ColumnsPanel.Children.Add(BuildColumnContainer(lv, columnIndex));
        }

        ApplyColumnChrome();
        UpdateEmptyPlaceholder();
    }

    /// <summary>
    /// Resizes the window to its rebuilt content, but only while it is on screen.
    /// </summary>
    /// <remarks>
    /// A dismissed flyout resizes on its next <c>Toggle</c>, which is what <c>InvalidateItems</c>
    /// historically relied on. Edit mode pins the flyout open, so a change made from launcher
    /// settings — view mode or icon density especially — would otherwise rebuild the content
    /// without ever resizing the window around it. Resizing a parked window would fight the
    /// placement the next <c>Toggle</c> computes, hence the open check.
    /// </remarks>
    private void ResizeIfVisible()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        if (!_isOpen) return;

        if (_isEditMode)
            ResizeForEditChrome();
        else
            ResizeWindowToCurrentContent(keepRightEdge: false);
    }

    private void RebuildItemsIfNeeded()
    {
        int currentHash = ComputeItemsHash(_launcher);
        if (currentHash != _lastItemsHash)
        {
            _lastItemsHash = currentHash;
            RebuildColumnsPanel();
        }
    }

    /// <summary>
    /// Resets the cached items hash for a specific launcher so the next Toggle forces a full re-bind.
    /// Call after import, sync download, or any bulk item change.
    /// </summary>
    /// <param name="force">
    /// When true (the default, matching every prior caller), the flyout is rebuilt even if its
    /// item hash is unchanged — required after an in-place icon-cache rewrite, where the file
    /// content changed but <see cref="LauncherItem.IconPath"/> did not. Pass false from the
    /// periodic auto-sync: the hash already covers every render-relevant field, so a no-op
    /// download then skips the rebuild instead of tearing down and re-creating every container
    /// on a timer (the churn that fed the container leak).
    /// </param>
    internal static void InvalidateItems(string? launcherId = null, bool force = true)
    {
        if (launcherId != null)
        {

            if (_instances.TryGetValue(launcherId, out var fw))
            {
                if (force) fw._lastItemsHash = -1;
                fw.RebuildItemsIfNeeded();
                fw.ResizeIfVisible();
            }
            // Refresh composite tray icon (mode 13) since it derives from item icons
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher?.TrayIconMode == TrayIconModes.Composite)
                MainWindow.Current?.UpdateTrayIcon(launcher);
        }
        else
        {

            foreach (var fw in _instances.Values)
            {
                if (force) fw._lastItemsHash = -1;
                fw.RebuildItemsIfNeeded();
            }
            // Refresh all composite tray icons
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                if (launcher.TrayIconMode == TrayIconModes.Composite)
                    MainWindow.Current?.UpdateTrayIcon(launcher);
            }
        }
    }

    /// <summary>Invalidates all launcher flyout instances.</summary>
    internal static void InvalidateAllItems() => InvalidateItems(null);

    /// <summary>Parses a hex color string to a SolidColorBrush, or null if empty/invalid.</summary>
    private static SolidColorBrush? ParseIconColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
            }
        }
        catch { /* fall through */ }
        return null;
    }



    // ── Positioning ─────────────────────────────────────────────────

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

        int flyoutWidth = (int)Math.Ceiling(GetFlyoutWidth() * scale);
        int flyoutHeight = (int)Math.Ceiling(MeasureContentHeight() * scale);
        int gap = Math.Max(4, (int)Math.Round(8 * scale));
        int slideDistance = Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));

        int left = screenX - flyoutWidth / 2;
        int top;

        // Detect whether the click is near a taskbar edge or from the tray
        // overflow popup (which floats well inside the work area).
        int edgeThreshold = (int)(16 * scale);
        bool nearBottom = screenY >= workArea.Bottom - edgeThreshold;
        bool nearTop = screenY <= workArea.Top + edgeThreshold;

        if (nearBottom)
        {
            // Taskbar at bottom (common case): position just above taskbar
            top = workArea.Bottom - flyoutHeight - gap;
        }
        else if (nearTop)
        {
            // Taskbar at top: position just below taskbar
            top = workArea.Top + gap;
        }
        else
        {
            // Tray overflow or other mid-screen click: place flyout above the
            // cursor so it doesn't cover the overflow popup the user clicked on.
            top = screenY - flyoutHeight - gap;
        }

        // Clamp within work area
        if (left < workArea.Left) left = workArea.Left;
        if (left + flyoutWidth > workArea.Right) left = workArea.Right - flyoutWidth;
        if (top + flyoutHeight > workArea.Bottom) top = workArea.Bottom - flyoutHeight;
        if (top < workArea.Top) top = workArea.Top;

        var edge = nearTop ? FlyoutEntranceEdge.Top : FlyoutEntranceEdge.Bottom;
        int startTop = edge == FlyoutEntranceEdge.Top ? top - slideDistance : top + slideDistance;
        return new FlyoutPlacement(left, top, startTop, flyoutWidth, flyoutHeight, edge);
    }

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsIconMode || sender is not ResizeGrip grip)
            return;

        _isResizingIconWidth = true;
        _resizeFromLeft = ReferenceEquals(sender, _leftResizeGrip);
        _resizeChangedSetting = false;
        _resizeStartIconsPerRow = GetIconModeIconsPerRow();
        GetCursorPos(out var pt);
        _resizeStartCursorX = pt.X;
        _animationVersion++;
        _isHiding = false;
        _isShowing = false;

        grip.CapturePointer(e.Pointer);
        grip.SetResizeCursorActive(true);
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingIconWidth)
            return;

        GetCursorPos(out var pt);
        int dragDistance = _resizeFromLeft ? _resizeStartCursorX - pt.X : pt.X - _resizeStartCursorX;
        int stepWidth = GetIconResizeStepWidth();
        int deltaSteps = (int)Math.Round((double)dragDistance / stepWidth, MidpointRounding.AwayFromZero);
        int targetIconsPerRow = Launcher.ClampIconModeIconsPerRow(_resizeStartIconsPerRow + deltaSteps);

        ApplyIconModeResize(targetIconsPerRow, keepRightEdge: _resizeFromLeft);
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteIconResize(sender as ResizeGrip);
        e.Handled = true;
    }

    private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CompleteIconResize(sender as ResizeGrip);
    }

    private void CompleteIconResize(ResizeGrip? grip)
    {
        if (!_isResizingIconWidth) return;

        grip?.ReleasePointerCaptures();

        _isResizingIconWidth = false;
        _leftResizeGrip.SetResizeCursorActive(false);
        _rightResizeGrip.SetResizeCursorActive(false);

        if (_resizeChangedSetting)
        {
            // Must also mark the change pending for auto-sync: saving alone lets a periodic
            // sync download overwrite the new icons-per-row with the older remote value.
            SettingsManager.SaveSettings();
            AutoSyncService.NotifyItemsChanged();
        }
    }

    private void ApplyIconModeResize(int iconsPerRow, bool keepRightEdge)
    {
        int clamped = Launcher.ClampIconModeIconsPerRow(iconsPerRow);
        if (_launcher.IconModeIconsPerRow == clamped)
            return;

        _launcher.IconModeIconsPerRow = clamped;
        _resizeChangedSetting = true;
        UpdateFlyoutLayoutInPlace();
        _lastItemsHash = ComputeItemsHash(_launcher);
        ResizeWindowToCurrentContent(keepRightEdge);
    }

    private void UpdateFlyoutLayoutInPlace()
    {
        foreach (var columnListView in ColumnListViews())
        {
            DisableListViewTransitions(columnListView);

            if (IsIconMode && columnListView.Tag is int columnIndex && columnIndex >= 0 && columnIndex < _columnLists.Count)
            {
                columnListView.Width = GetIconColumnWidth(_columnLists[columnIndex]);
                ApplyTopLevelIconSpans(columnListView);
            }
        }

        // A group that just gained or lost its last child changes drop-target state.
        ApplyEmptyGroupDropTargets();

        if (!IsIconMode)
            return;

        foreach (var childListView in _loadedIconChildLists.ToList())
        {
            DisableListViewTransitions(childListView);

            if (childListView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
                wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();

            ApplyIconGroupChildListLayout(childListView);
        }

        foreach (var groupRoot in _loadedIconGroupRoots.ToList())
            ApplyIconGroupRootLayout(groupRoot);
    }

    /// <summary>
    /// Resizes to the current content. <paramref name="explicitHeightDips"/> overrides the
    /// arithmetic height estimate — pass it only when the window is <b>visible</b>, since it
    /// comes from a real layout pass (see <c>MeasureContentHeight</c> for why measuring a
    /// hidden window is fatal).
    /// </summary>
    private void ResizeWindowToCurrentContent(bool keepRightEdge, double? explicitHeightDips = null)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        GetWindowRect(_hwnd, out var rect);
        double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;

        int newWidth = (int)Math.Ceiling(GetFlyoutWidth() * dpiScale);
        int newHeight = (int)Math.Ceiling((explicitHeightDips ?? MeasureContentHeight()) * dpiScale);
        int left = keepRightEdge ? rect.Right - newWidth : rect.Left;
        int top = _lastEntranceEdge == FlyoutEntranceEdge.Top ? rect.Top : rect.Bottom - newHeight;

        var centerPoint = new POINT
        {
            X = rect.Left + ((rect.Right - rect.Left) / 2),
            Y = rect.Top + ((rect.Bottom - rect.Top) / 2)
        };
        IntPtr hMonitor = MonitorFromPoint(centerPoint, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        if (left < workArea.Left) left = workArea.Left;
        if (left + newWidth > workArea.Right) left = workArea.Right - newWidth;
        if (top < workArea.Top) top = workArea.Top;
        if (top + newHeight > workArea.Bottom) top = workArea.Bottom - newHeight;

        SetWindowPos(_hwnd, 0, left, top, newWidth, newHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    private void IconGroupChildList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        _loadedIconChildLists.Add(listView);
        listView.Unloaded -= IconGroupChildList_Unloaded;
        listView.Unloaded += IconGroupChildList_Unloaded;
        TrackGroupChildList(listView);

        DisableListViewTransitions(listView);
        ScrollViewer.SetVerticalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);

        listView.ItemsPanel = CreateIconGroupItemsPanel();
        listView.UpdateLayout();

        if (listView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();

        ApplyIconGroupChildListLayout(listView);

        if (listView.Tag is LauncherItem group && _syntheticGroups.Contains(group))
        {
            VariableSizedWrapGrid.SetColumnSpan(listView, GetTopLevelIconSpan(group));
            VariableSizedWrapGrid.SetRowSpan(listView, GetTopLevelIconRowSpan(group));
        }
    }

    private void IconGroupRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not LauncherItem group)
            return;

        _loadedIconGroupRoots.Add(element);
        element.Unloaded -= IconGroupRoot_Unloaded;
        element.Unloaded += IconGroupRoot_Unloaded;

        ApplyIconGroupRootLayout(element);
    }

    private void ApplyIconGroupRootLayout(FrameworkElement element)
    {
        if (element.DataContext is not LauncherItem group)
            return;

        element.ClearValue(FrameworkElement.WidthProperty);
        element.MaxWidth = GetIconGroupContentWidth(group);
        element.HorizontalAlignment = HorizontalAlignment.Center;
        VariableSizedWrapGrid.SetColumnSpan(element, GetTopLevelIconSpan(group));
        VariableSizedWrapGrid.SetRowSpan(element, GetTopLevelIconRowSpan(group));
    }

    private void ApplyIconGroupChildListLayout(ListView listView)
    {
        if (listView.Tag is not LauncherItem group)
            return;

        listView.Width = GetIconGroupContentWidth(group);
        listView.MaxWidth = GetIconModeIconsPerRow() * GetActiveIconCellWidth();
        listView.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private void IconGroupChildList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
            _loadedIconChildLists.Remove(listView);
    }

    private void IconGroupRoot_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            _loadedIconGroupRoots.Remove(element);
    }

    private void ListGroupChildList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
        {
            DisableListViewTransitions(listView);
            TrackGroupChildList(listView);
        }
    }

    private void ColumnListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListViewBase listView || listView.Tag is not int columnIndex)
            return;

        if (e.Items.FirstOrDefault() is not LauncherItem item)
            return;

        // Reordering is an edit-mode-only affordance.
        if (!_isEditMode)
        {
            e.Cancel = true;
            return;
        }

        if (_syntheticGroups.Contains(item))
        {
            e.Cancel = true;
            return;
        }

        HideHoverPencil();
        _dragItem = item;
        _dragSourceCollection = _columnLists[columnIndex];
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void ColumnListView_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListViewBase listView)
            return;

        // No _dragItem means the drag didn't start in this flyout — it came from Explorer,
        // the desktop, the Start Menu or a browser.
        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDragOver(listView, e, null);
            return;
        }

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int dropIndex = GetDropIndex(listView, e);
        ShowInsertionIndicator(listView, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;

        if (dropIndex < listView.Items.Count && listView.Items[dropIndex] is LauncherItem targetItem)
        {
            bool isGrid = IsGridItemsPanel(listView.ItemsPanelRoot);
            e.DragUIOverride.Caption = isGrid
                ? $"Move before {GetItemDisplayName(targetItem)}"
                : $"Move above {GetItemDisplayName(targetItem)}";
        }
        else
            e.DragUIOverride.Caption = "Move to end";

        e.Handled = true;
    }

    private void ColumnListView_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void ColumnListView_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (sender is not ListViewBase listView || listView.Tag is not int columnIndex
            || columnIndex < 0 || columnIndex >= _columnLists.Count)
            return;

        var targetColumn = _columnLists[columnIndex];

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDrop(listView, e, targetColumn);
            return;
        }

        int dropIndex = GetDropIndex(listView, e);

        int originalIndex = _dragSourceCollection == targetColumn ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;

        dropIndex = Math.Clamp(dropIndex, 0, targetColumn.Count);
        targetColumn.Insert(dropIndex, _dragItem);

        PersistFlyoutReorder();
        ClearDragState();
        e.Handled = true;
    }

    private void ColumnListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        ClearDragState();
    }

    private void GroupChildList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (e.Items.FirstOrDefault() is not LauncherItem item)
            return;

        // Reordering is an edit-mode-only affordance.
        if (!_isEditMode)
        {
            e.Cancel = true;
            return;
        }

        HideHoverPencil();
        _dragItem = item;
        _dragSourceCollection = group.Children;
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void GroupChildList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDragOver(listView, e, group);
            return;
        }

        if (_dragItem.IsGroup || _dragItem.IsColumnBreak)
        {
            e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int dropIndex = GetDropIndex(listView, e);
        ShowInsertionIndicator(listView, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;

        if (dropIndex < group.Children.Count)
        {
            bool isGrid = listView.ItemsPanelRoot is ItemsWrapGrid;
            e.DragUIOverride.Caption = isGrid
                ? $"Move before {GetItemDisplayName(group.Children[dropIndex])}"
                : $"Move above {GetItemDisplayName(group.Children[dropIndex])}";
        }
        else if (group.Children.Count == 0)
        {
            e.DragUIOverride.Caption = $"Move into {GetItemDisplayName(group)}";
        }
        else
        {
            e.DragUIOverride.Caption = $"Move to end of {GetItemDisplayName(group)}";
        }

        e.Handled = true;
    }

    private void GroupChildList_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void GroupChildList_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDrop(listView, e, group.Children);
            return;
        }

        if (_dragItem.IsGroup || _dragItem.IsColumnBreak)
            return;

        int dropIndex = GetDropIndex(listView, e);
        int originalIndex = _dragSourceCollection == group.Children ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;

        dropIndex = Math.Clamp(dropIndex, 0, group.Children.Count);
        group.Children.Insert(dropIndex, _dragItem);

        PersistFlyoutReorder();
        ClearDragState();
        e.Handled = true;
    }

    private void GroupChildList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        ClearDragState();
    }

    private void PersistFlyoutReorder()
    {
        SyncColumnsToFlatList();
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyItemsChanged();

        if (_launcher.TrayIconMode == TrayIconModes.Composite)
            MainWindow.Current?.UpdateTrayIcon(_launcher);

        // Full rebuild rather than an in-place update. Synthetic groups are derived from which
        // items are loose, so moving an item between a group and the top level invalidates
        // them: the item that left needs wrapping, and the wrapper it left behind is now empty
        // but still rendered — which is where the stray tinted box came from.
        _lastItemsHash = -1;
        RebuildItemsIfNeeded();

        if (_isEditMode)
        {
            ApplyEditVisuals();
            // Dropping into or out of a group can change the required height.
            ResizeForEditChrome();
        }
    }

    private void ClearDragState()
    {
        _dragItem = null;
        _dragSourceCollection = null;
    }

    private static string GetItemDisplayName(LauncherItem item)
    {
        if (item.IsGroup && string.IsNullOrWhiteSpace(item.Name))
            return "section";

        return string.IsNullOrWhiteSpace(item.Name) ? "item" : item.Name;
    }

    /// <summary>
    /// True when an icon-grid drop position lands at the start of a row rather than beside
    /// the item the indicator is drawn on.
    /// </summary>
    /// <remarks>
    /// Two cases: inserting before an item that is first in its row, and appending when the
    /// final row is already full so the new item wraps below it.
    /// </remarks>
    private bool GridDropWrapsToNewRow(ListViewBase listView, int dropIndex, Control container, bool appending)
    {
        try
        {
            if (appending)
            {
                // Does another cell fit to the right of the last item?
                //
                // Measured against the widest a row may become, not the list's current width:
                // a group's child list is only as wide as the children it already has, but it
                // grows up to the icons-per-row limit. Using ActualWidth reported "wraps" for
                // any group that still had room to expand.
                double maxRowWidth = GetIconModeIconsPerRow() * GetActiveIconCellWidth();
                double available = Math.Max(listView.ActualWidth, maxRowWidth);

                var origin = container.TransformToVisual(listView)
                    .TransformPoint(new global::Windows.Foundation.Point(0, 0));
                return origin.X + (container.ActualWidth * 2) > available + 1;
            }

            if (dropIndex <= 0) return false;

            // Starts a row if the preceding item sits on an earlier row.
            if (listView.ContainerFromIndex(dropIndex - 1) is not Control previous) return false;

            double targetY = container.TransformToVisual(listView)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;
            double previousY = previous.TransformToVisual(listView)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;

            return targetY > previousY + 1;
        }
        catch
        {
            return false;
        }
    }

    private void ShowInsertionIndicator(ListViewBase listView, int dropIndex)
    {
        ClearInsertionIndicator();

        if (listView.Items.Count == 0)
        {
            _lastIndicatorListView = listView;
            _lastIndicatorListBorderBrush = listView.BorderBrush;
            _lastIndicatorListBorderThickness = listView.BorderThickness;
            listView.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            listView.BorderThickness = new Thickness(2);
            return;
        }

        bool isGrid = IsGridItemsPanel(listView.ItemsPanelRoot);
        int targetIndex = dropIndex < listView.Items.Count ? dropIndex : listView.Items.Count - 1;
        if (targetIndex < 0)
            return;

        if (listView.ContainerFromIndex(targetIndex) is not Control container)
            return;

        _lastIndicatorContainer = container;
        _lastIndicatorContainerBorderBrush = container.BorderBrush;
        _lastIndicatorContainerBorderThickness = container.BorderThickness;
        _lastIndicatorContainerPadding = container.Padding;
        _lastIndicatorContainerMargin = container.Margin;
        container.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        if (isGrid)
        {
            bool appending = dropIndex >= listView.Items.Count;

            // The border's width is given back out of the container's own padding so the grid
            // doesn't reflow — but only as much as it actually has. The icon-mode container
            // style has no padding to give, and WinUI throws on a negative Padding rather than
            // clamping it, which killed the whole drag. The list branch below already clamps.
            var padding = _lastIndicatorContainerPadding;

            // A vertical line reads as "goes beside this item", which is wrong when the drop
            // position actually wraps onto the next row. In that case draw a horizontal line
            // on the row boundary instead, so the landing spot matches what is shown.
            if (GridDropWrapsToNewRow(listView, dropIndex, container, appending))
            {
                container.BorderThickness = appending
                    ? new Thickness(0, 0, 0, 3)
                    : new Thickness(0, 3, 0, 0);
                container.Padding = appending
                    ? new Thickness(padding.Left, padding.Top, padding.Right, Math.Max(0, padding.Bottom - 3))
                    : new Thickness(padding.Left, Math.Max(0, padding.Top - 3), padding.Right, padding.Bottom);
            }
            else if (!appending)
            {
                container.BorderThickness = new Thickness(3, 0, 0, 0);
                container.Padding = new Thickness(Math.Max(0, padding.Left - 3), padding.Top, padding.Right, padding.Bottom);
            }
            else
            {
                container.BorderThickness = new Thickness(0, 0, 3, 0);
                container.Padding = new Thickness(padding.Left, padding.Top, Math.Max(0, padding.Right - 3), padding.Bottom);
            }
        }
        else
        {
            bool insertBefore = dropIndex < listView.Items.Count;
            var padding = _lastIndicatorContainerPadding;

            if (insertBefore)
            {
                container.BorderThickness = new Thickness(0, 2, 0, 0);
                container.Padding = new Thickness(
                    padding.Left,
                    Math.Max(0, padding.Top - 2),
                    padding.Right,
                    padding.Bottom);
            }
            else
            {
                container.BorderThickness = new Thickness(0, 0, 0, 2);
                container.Padding = new Thickness(
                    padding.Left,
                    padding.Top,
                    padding.Right,
                    Math.Max(0, padding.Bottom - 2));
            }

            container.Margin = _lastIndicatorContainerMargin;
        }
    }

    private void ClearInsertionIndicator()
    {
        if (_lastIndicatorContainer != null)
        {
            _lastIndicatorContainer.BorderBrush = _lastIndicatorContainerBorderBrush;
            _lastIndicatorContainer.BorderThickness = _lastIndicatorContainerBorderThickness;
            _lastIndicatorContainer.Padding = _lastIndicatorContainerPadding;
            _lastIndicatorContainer.Margin = _lastIndicatorContainerMargin;
            _lastIndicatorContainer = null;
            _lastIndicatorContainerBorderBrush = null;
            _lastIndicatorContainerBorderThickness = new Thickness(0);
            _lastIndicatorContainerPadding = new Thickness(0);
            _lastIndicatorContainerMargin = new Thickness(0);
        }

        if (_lastIndicatorListView != null)
        {
            _lastIndicatorListView.BorderBrush = _lastIndicatorListBorderBrush;
            _lastIndicatorListView.BorderThickness = _lastIndicatorListBorderThickness;
            _lastIndicatorListView = null;
            _lastIndicatorListBorderBrush = null;
            _lastIndicatorListBorderThickness = new Thickness(0);
        }
    }

    private static int GetDropIndex(ListViewBase listView, DragEventArgs e)
    {
        if (IsGridItemsPanel(listView.ItemsPanelRoot))
            return GetDropIndexGrid(listView, e);

        var position = e.GetPosition(listView);
        for (int index = 0; index < listView.Items.Count; index++)
        {
            if (listView.ContainerFromIndex(index) is not Control container)
                continue;

            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
            if (position.Y < point.Y + (container.ActualHeight / 2))
                return index;
        }

        return listView.Items.Count;
    }

    private static int GetDropIndexGrid(ListViewBase listView, DragEventArgs e)
    {
        var position = e.GetPosition(listView);
        int count = listView.Items.Count;
        if (count == 0)
            return 0;

        int bestIndex = count;
        double bestDistance = double.MaxValue;
        double contentBottom = 0;

        for (int index = 0; index < count; index++)
        {
            if (listView.ContainerFromIndex(index) is not Control container)
                continue;

            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));

            double top = point.Y;
            double bottom = top + container.ActualHeight;
            double left = point.X;
            double midX = left + (container.ActualWidth / 2);
            contentBottom = Math.Max(contentBottom, bottom);

            if (position.Y >= top && position.Y < bottom)
            {
                if (position.X < midX)
                    return index;

                bestIndex = index + 1;
                bestDistance = 0;
            }
            else if (bestDistance > 0)
            {
                double verticalDistance = position.Y < top ? top - position.Y : position.Y - bottom;
                if (verticalDistance < bestDistance)
                {
                    bestDistance = verticalDistance;
                    bestIndex = position.X < midX ? index : index + 1;
                }
            }
        }

        // Anywhere in the empty space below the last row means "put it at the end". Without
        // this the nearest-band fallback above could return an index *before* the last item
        // whenever the pointer happened to sit left of its midpoint, so appending required
        // landing precisely beside the final item.
        if (position.Y >= contentBottom)
            return count;

        return Math.Min(bestIndex, count);
    }

    private sealed class ResizeGrip : Grid
    {
        private bool _forceResizeCursor;

        public ResizeGrip()
        {
            PointerEntered += ResizeGrip_PointerEntered;
            PointerExited += ResizeGrip_PointerExited;
            PointerCaptureLost += ResizeGrip_PointerCaptureLost;
        }

        public void SetResizeCursorActive(bool active)
        {
            _forceResizeCursor = active;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                active ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        private void ResizeGrip_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        }

        private void ResizeGrip_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_forceResizeCursor)
                return;

            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _forceResizeCursor = false;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void ItemsListControl_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LauncherItem item) return;

        // In edit mode a click edits rather than launches.
        if (_isEditMode)
        {
            if (item.IsGroup)
            {
                if (!_syntheticGroups.Contains(item))
                    _ = RenameGroupAsync(item);
                return;
            }
            _ = EditItemAsync(item);
            return;
        }

        if (item.IsGroup) return;
        LaunchItem(item);
    }

    private void LaunchItem(LauncherItem item)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;

        Launch(item);
    }

    /// <summary>
    /// Runs one item, whatever kind it is, with no flyout involved.
    /// </summary>
    /// <remarks>
    /// Static because the flyout is not always the caller: a task on a pinned taskbar button
    /// launches an item without anything being on screen, and duplicating this switch is how the
    /// two would come to disagree about, say, what a <c>.bat</c> or a folder means.
    /// </remarks>
    internal static void Launch(LauncherItem item)
    {
        try
        {
            if (item.IsWebsite || item.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                               || item.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                LaunchWebsite(item);
            }
            else if (item.IsPwa)
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"shell:AppsFolder\\{item.Path}",
                    UseShellExecute = false
                });
            }
            else if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(item.Path)
                {
                    UseShellExecute = true,
                    Arguments = item.Arguments ?? ""
                });
            }
            else
            {
                var args = item.Arguments ?? "";
                var path = item.Path;
                ProcessStartInfo psi;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    psi = new ProcessStartInfo(path) { UseShellExecute = true };
                }
                else if (Directory.Exists(path))
                {
                    // A folder isn't a process — hand it to Explorer.
                    psi = new ProcessStartInfo("explorer.exe")
                    {
                        Arguments = $"\"{path}\"",
                        UseShellExecute = false
                    };
                }
                else if (path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    psi = new ProcessStartInfo("cmd.exe")
                    {
                        Arguments = $"/c \"{path}\" {args}",
                        UseShellExecute = false
                    };
                }
                else if (IsDocumentPath(path))
                {
                    // A document has no launch semantics of its own — the shell picks the
                    // handler. CreateProcess would just fail with "not a valid application".
                    psi = new ProcessStartInfo(path) { UseShellExecute = true };
                }
                else
                {
                    psi = new ProcessStartInfo(path)
                    {
                        Arguments = args,
                        UseShellExecute = false
                    };
                }
                Process.Start(psi);
            }
            Logger.Info($"Launched item: {item.Name} ({item.Path})");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to launch item: {item.Name} ({item.Path})");
        }
    }

    /// <summary>
    /// Runs the item a taskbar jump list task names, and reports whether it found one.
    /// </summary>
    /// <remarks>
    /// <para>The position is only a hint. It is where the item sat when the list was published,
    /// which is right almost every time and wrong exactly when it matters - after an edit, a
    /// drag, or a sync that brought another machine's items in. So the token has the final say:
    /// it identifies the item by what it launches, and the position merely saves searching for
    /// it.</para>
    /// <para>Returning false rather than guessing is deliberate. Launching whatever now occupies
    /// that position would be the one outcome worse than doing nothing, and the caller has a
    /// better answer anyway - see <see cref="LauncherPanels.LaunchFromJumpList"/>.</para>
    /// </remarks>
    internal static bool LaunchItemFromJumpList(Launcher launcher, int index, int token)
    {
        var target = ResolveJumpListItem(launcher, index, token);
        if (target == null)
        {
            Logger.Info($"Jump list task no longer matches an item in {launcher.Name}; opening the launcher instead");
            return false;
        }

        Launch(target);
        return true;
    }

    /// <summary>
    /// The item a jump list entry stands for, or null when the launcher no longer has it.
    /// </summary>
    /// <remarks>
    /// The position is only a hint. It is where the item sat when the list was published, which is
    /// right almost every time and wrong exactly when it matters - after an edit, a drag, or a sync
    /// that brought another machine's items in. So the token has the final say: it identifies the
    /// item by what it launches, and the position merely saves searching for it. Guessing when
    /// neither matches would mean launching, or deleting, whatever now occupies that position.
    /// </remarks>
    private static LauncherItem? ResolveJumpListItem(Launcher launcher, int index, int token)
    {
        var items = new List<LauncherItem>();
        MainWindow.CollectLaunchableItems(launcher.Items, items, int.MaxValue);

        if (index >= 0 && index < items.Count && JumpListService.ItemToken(items[index]) == token)
            return items[index];

        return items.FirstOrDefault(i => JumpListService.ItemToken(i) == token);
    }

    /// <summary>
    /// True for an existing file the OS wouldn't accept as an executable image. A bare name
    /// like <c>notepad</c> is left alone — it resolves through PATH, not the filesystem.
    /// </summary>
    private static bool IsDocumentPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return false;

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(path);
    }

    private void ItemsListControl_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is ListViewBase listView
            && e.TryGetPosition(listView, out var point)
            && TryGetContextMenuItem(listView, point, out var item))
        {
            ShowItemContextMenu(listView, item, point);
            e.Handled = true;
        }
    }

    private bool TryGetContextMenuItem(ListViewBase listView, global::Windows.Foundation.Point point, out LauncherItem item)
    {
        for (int i = 0; i < listView.Items.Count; i++)
        {
            if (listView.ContainerFromIndex(i) is not Control container) continue;
            // Column breaks are invisible sentinels and synthetic groups are ephemeral, so
            // neither is a valid context-menu target. Real groups are, in edit mode.
            if (listView.Items[i] is not LauncherItem candidate || candidate.IsColumnBreak) continue;
            // Groups are only a context-menu target while editing; synthetic ones never are.
            if (candidate.IsGroup && !(_isEditMode && IsEditableGroup(candidate))) continue;

            var origin = container.TransformToVisual(listView).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var bounds = new global::Windows.Foundation.Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);
            if (bounds.Contains(point))
            {
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private void ShowItemContextMenu(FrameworkElement anchor, LauncherItem item, global::Windows.Foundation.Point? position = null)
    {
        if (item.IsColumnBreak)
            return;
        if (item.IsGroup && !IsEditableGroup(item))
            return;

        var flyout = new MenuFlyout();

        // Outside edit mode the menu carries only the launcher-level entries — reorder,
        // move, edit and remove are editing affordances and would be misleading (and, for a
        // read-only shared launcher, simply unavailable).
        if (!_isEditMode || IsReadOnlyLauncher)
        {
            AppendLegacyContextMenuItems(flyout);
            if (position is global::Windows.Foundation.Point p)
                flyout.ShowAt(anchor, p);
            else
                flyout.ShowAt(anchor);
            return;
        }

        string moveBackwardText = IsIconMode ? "Move left" : "Move up";
        string moveForwardText = IsIconMode ? "Move right" : "Move down";
        string moveBackwardGlyph = IsIconMode ? "\uE76B" : "\uE70E";
        string moveForwardGlyph = IsIconMode ? "\uE76C" : "\uE70D";

        var moveUp = new MenuFlyoutItem { Text = moveBackwardText, Icon = new FontIcon { Glyph = moveBackwardGlyph } };
        moveUp.Click += (_, _) =>
        {
            var parent = FindParentCollection(item);
            if (parent == null) return;

            int index = parent.IndexOf(item);
            if (index <= 0) return;

            parent.Move(index, index - 1);
            PersistFlyoutItemChanges();
        };
        flyout.Items.Add(moveUp);

        var moveDown = new MenuFlyoutItem { Text = moveForwardText, Icon = new FontIcon { Glyph = moveForwardGlyph } };
        moveDown.Click += (_, _) =>
        {
            var parent = FindParentCollection(item);
            if (parent == null) return;

            int index = parent.IndexOf(item);
            if (index < 0 || index >= parent.Count - 1) return;

            parent.Move(index, index + 1);
            PersistFlyoutItemChanges();
        };
        flyout.Items.Add(moveDown);

        ObservableCollection<LauncherItem>? currentParent = null;
        LauncherItem? currentGroup = null;
        foreach (var group in _launcher.Items.Where(candidate => candidate.IsGroup))
        {
            if (group.Children.Contains(item))
            {
                currentParent = group.Children;
                currentGroup = group;
                break;
            }
        }

        var moveToSub = new MenuFlyoutSubItem { Text = "Move to\u2026", Icon = new FontIcon { Glyph = "\uE8DE" } };

        if (currentParent != null)
        {
            var topLevel = new MenuFlyoutItem { Text = "Top Level", Icon = new FontIcon { Glyph = "\uE74B" } };
            topLevel.Click += (_, _) =>
            {
                currentParent.Remove(item);
                _launcher.Items.Add(item);
                PersistFlyoutItemChanges();
            };
            moveToSub.Items.Add(topLevel);
        }

        // Groups cannot nest, so a group is never offered another group as a destination.
        foreach (var group in item.IsGroup ? [] : _launcher.Items.Where(candidate => candidate.IsGroup))
        {
            if (group == currentGroup) continue;

            var targetGroup = group;
            var groupOption = new MenuFlyoutItem { Text = group.Name, Icon = new FontIcon { Glyph = "\uF168" } };
            groupOption.Click += (_, _) =>
            {
                (currentParent ?? _launcher.Items).Remove(item);
                targetGroup.Children.Add(item);
                PersistFlyoutItemChanges();
            };
            moveToSub.Items.Add(groupOption);
        }

        var otherLaunchers = SettingsManager.Current.Launchers.Where(launcher => launcher != _launcher).ToList();
        if (otherLaunchers.Count > 0)
        {
            if (moveToSub.Items.Count > 0)
                moveToSub.Items.Add(new MenuFlyoutSeparator());

            foreach (var launcher in otherLaunchers)
            {
                var targetLauncher = launcher;
                var launcherOption = new MenuFlyoutItem
                {
                    Text = $"{targetLauncher.Name} (launcher)",
                    Icon = new FontIcon { Glyph = "\uF0E2" }
                };
                launcherOption.Click += (_, _) =>
                {
                    (currentParent ?? _launcher.Items).Remove(item);
                    targetLauncher.Items.Add(item);
                    PersistFlyoutItemChanges(targetLauncher);
                };
                moveToSub.Items.Add(launcherOption);
            }
        }

        if (moveToSub.Items.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(moveToSub);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var edit = new MenuFlyoutItem
        {
            Text = item.IsGroup ? "Rename\u2026" : "Edit",
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        edit.Click += (_, _) =>
        {
            if (item.IsGroup)
                _ = RenameGroupAsync(item);
            else
                _ = EditItemAsync(item);
        };
        flyout.Items.Add(edit);

        var remove = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += (_, _) => RemoveItem(item);
        flyout.Items.Add(remove);

        AppendLegacyContextMenuItems(flyout);

        if (position is global::Windows.Foundation.Point point)
            flyout.ShowAt(anchor, point);
        else
            flyout.ShowAt(anchor);
    }

    private ObservableCollection<LauncherItem>? FindParentCollection(LauncherItem item)
    {
        if (_launcher.Items.Contains(item))
            return _launcher.Items;

        foreach (var group in _launcher.Items.Where(candidate => candidate.IsGroup))
        {
            if (group.Children.Contains(item))
                return group.Children;
        }

        // A synthetic group is a display wrapper and is deliberately not in the launcher. Anything
        // else means this row is bound to an item the launcher no longer contains, which is what
        // silently disables remove, move and edit for it: each of them looks the item up here and
        // gives up when it is not found, so the user sees a menu entry that does nothing. See
        // LauncherPayload.ItemsMatch for the sync merge that used to leave rows in that state.
        if (!_syntheticGroups.Contains(item))
            Logger.Warn($"Flyout item '{item.Name}' is not in launcher '{_launcher.Name}' " +
                        "(row bound to a stale object); remove and move will do nothing");

        return null;
    }

    private void PersistFlyoutItemChanges(params Launcher[] additionalLaunchers)
    {
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyItemsChanged();

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var launcher in new[] { _launcher }.Concat(additionalLaunchers))
        {
            if (string.IsNullOrEmpty(launcher.Id) || !seenIds.Add(launcher.Id))
                continue;

            InvalidateItems(launcher.Id);

            if (_instances.TryGetValue(launcher.Id, out var flyout))
                flyout.UpdateFlyoutLayoutInPlace();
        }
    }

    private void AppendLegacyContextMenuItems(MenuFlyout flyout)
    {
        // Only separate from preceding entries if there are any — outside edit mode these
        // launcher-level entries are the whole menu.
        if (flyout.Items.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        // Enters edit mode rather than opening launcher settings directly \u2014 settings are
        // reachable from the edit toolbar, so this keeps one obvious way in.
        var editMode = new MenuFlyoutItem
        {
            Text = "Edit items",
            Icon = new FontIcon { Glyph = "" }
        };
        editMode.Click += ContextEditMode_Click;
        flyout.Items.Add(editMode);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var settingsItem = new MenuFlyoutItem
        {
            Text = "App Settings",
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += ContextSettingsItem_Click;
        flyout.Items.Add(settingsItem);
    }

    private static void LaunchWebsite(LauncherItem item)
    {
        if (!item.OpenInAppWindow)
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            return;
        }

        if (TryLaunchInAppWindow(item.Path, item.AppWindowBrowser, item.AppWindowBrowserProfile))
            return;

        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    private static bool TryLaunchInAppWindow(string url, string browserPath, string browserProfile)
    {
        string profileId = GetAppWindowProfileId(url);
        string browserExe = ResolveBrowserExe(browserPath);
        if (browserExe == "") return false;

        var engine = BrowserCatalog.DetectEngine(browserExe);
        var existingWindows = GetBrowserWindows(engine);

        try
        {
            string args = engine == BrowserEngine.Gecko
                ? BuildGeckoArgs(url, profileId)
                : BuildChromiumArgs(url, browserProfile, profileId);

            Process.Start(new ProcessStartInfo { FileName = browserExe, Arguments = args, UseShellExecute = false });
            _ = RestoreAndTrackWindowBoundsAsync(existingWindows, profileId, engine);
            return true;
        }
        catch { return false; }
    }

    private static string BuildChromiumArgs(string url, string browserProfile, string profileId)
    {
        string args = $"--app=\"{url}\"";
        if (string.IsNullOrEmpty(browserProfile))
        {
            string appProfileDir = GetAppWindowProfileDirectory(profileId);
            Directory.CreateDirectory(appProfileDir);
            args += $" --user-data-dir=\"{appProfileDir}\"";
        }
        else if (browserProfile != "__default__")
        {
            args += $" --profile-directory=\"{browserProfile}\"";
        }
        return args;
    }

    private static string BuildGeckoArgs(string url, string profileId)
    {
        string appProfileDir = GetAppWindowProfileDirectory(profileId);
        Directory.CreateDirectory(appProfileDir);
        EnsureGeckoAppWindowProfile(appProfileDir);
        return $"--new-window \"{url}\" --profile \"{appProfileDir}\" --no-remote";
    }

    private static void EnsureGeckoAppWindowProfile(string profileDir)
    {
        string chromeDir = Path.Combine(profileDir, "chrome");
        Directory.CreateDirectory(chromeDir);

        string userChromePath = Path.Combine(chromeDir, "userChrome.css");
        if (!File.Exists(userChromePath))
        {
            File.WriteAllText(userChromePath,
                "@namespace url(\"http://www.mozilla.org/keymaster/gatekeeper/there.is.only.xul\"); #navigator-toolbox { visibility: collapse !important; }");
        }

        string userJsPath = Path.Combine(profileDir, "user.js");
        if (!File.Exists(userJsPath))
        {
            File.WriteAllText(userJsPath,
                "user_pref(\"toolkit.legacyUserProfileCustomizations.stylesheets\", true);\n" +
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
                "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
                "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);\n");
        }
    }

    private static string ResolveBrowserExe(string browserPath)
    {
        if (!string.IsNullOrEmpty(browserPath))
            return File.Exists(browserPath) ? browserPath : "";
        return BrowserCatalog.GetDefaultBrowserExePath() ?? "";
    }

    private static string GetAppWindowProfileId(string url)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string GetAppWindowProfileDirectory(string profileId)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LittleLauncher", "AppWindowProfiles", profileId);
    }

    private static string GetBoundsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LittleLauncher", "edge-window-bounds.json");
    }

    private static async Task RestoreAndTrackWindowBoundsAsync(HashSet<IntPtr> existingWindows, string profileId, BrowserEngine engine)
    {
        IntPtr hwnd = await WaitForNewBrowserWindowAsync(existingWindows, engine, TimeSpan.FromSeconds(10));
        if (hwnd == IntPtr.Zero) return;

        if (TryGetSavedBounds(profileId, out var savedBounds))
        {
            SetWindowPos(hwnd, 0, savedBounds.Left, savedBounds.Top, savedBounds.Width, savedBounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (savedBounds.IsMaximized) ShowWindow(hwnd, SW_MAXIMIZE);
        }

        InstallBoundsTrackingHooks(hwnd, profileId);
    }

    private static readonly HashSet<WinEventProc> ActiveHookDelegates = new();

    private static void InstallBoundsTrackingHooks(IntPtr hwnd, string profileId)
    {
        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        WindowBounds? lastBounds = null;
        IntPtr hookLocation = IntPtr.Zero;
        IntPtr hookDestroy = IntPtr.Zero;
        WinEventProc? handler = null;

        handler = (hHook, eventType, eventHwnd, idObject, idChild, eventThread, time) =>
        {
            if (eventHwnd != hwnd || idObject != OBJID_WINDOW) return;
            if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    bool maximized = IsZoomed(hwnd);
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w >= 320 && h >= 240)
                        lastBounds = new WindowBounds(rect.Left, rect.Top, w, h, maximized);
                }
            }
            else if (eventType == EVENT_OBJECT_DESTROY)
            {
                if (lastBounds is not null) SaveBounds(profileId, lastBounds);
                if (hookLocation != IntPtr.Zero) UnhookWinEvent(hookLocation);
                if (hookDestroy != IntPtr.Zero) UnhookWinEvent(hookDestroy);
                lock (ActiveHookDelegates) ActiveHookDelegates.Remove(handler!);
            }
        };

        lock (ActiveHookDelegates) ActiveHookDelegates.Add(handler);

        hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
        hookDestroy = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
    }

    private static async Task<IntPtr> WaitForNewBrowserWindowAsync(HashSet<IntPtr> existingWindows, BrowserEngine engine, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var currentWindows = GetBrowserWindows(engine);
            foreach (var hwnd in currentWindows)
            {
                if (existingWindows.Contains(hwnd)) continue;
                if (!GetWindowRect(hwnd, out RECT rect)) continue;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width >= 200 && height >= 120) return hwnd;
            }
            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    private static readonly string[] ChromiumWindowClasses = { "Chrome_WidgetWin_1" };
    private static readonly string[] GeckoWindowClasses = { "MozillaWindowClass", "MozillaDialogClass" };

    private static HashSet<IntPtr> GetBrowserWindows(BrowserEngine engine)
    {
        var windowClasses = engine == BrowserEngine.Gecko ? GeckoWindowClasses : ChromiumWindowClasses;
        var windows = new HashSet<IntPtr>();
        var className = new StringBuilder(256);
        EnumWindows((hWnd, _) =>
        {
            className.Clear();
            GetClassName(hWnd, className, className.Capacity);
            string cls = className.ToString();
            foreach (string target in windowClasses)
            {
                if (cls == target) { windows.Add(hWnd); break; }
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool TryGetSavedBounds(string profileId, out WindowBounds bounds)
    {
        bounds = default!;
        lock (BoundsFileLock)
        {
            if (CachedBounds.TryGetValue(profileId, out var cachedBounds)) { bounds = cachedBounds; return true; }
            var all = LoadAllBounds();
            foreach (var kv in all) CachedBounds[kv.Key] = kv.Value;
            if (CachedBounds.TryGetValue(profileId, out var loadedBounds)) { bounds = loadedBounds; return true; }
            return false;
        }
    }

    private static void SaveBounds(string profileId, WindowBounds bounds)
    {
        lock (BoundsFileLock)
        {
            CachedBounds[profileId] = bounds;
            var all = LoadAllBounds();
            all[profileId] = bounds;
            string filePath = GetBoundsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
    }

    private static Dictionary<string, WindowBounds> LoadAllBounds()
    {
        string filePath = GetBoundsFilePath();
        if (!File.Exists(filePath)) return new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(json) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private sealed record WindowBounds(int Left, int Top, int Width, int Height, bool IsMaximized = false);

    /// <summary>
    /// Enters edit mode. Launcher settings are reached from the edit toolbar rather than
    /// from here, so there is a single obvious entry point into editing.
    /// </summary>
    private void ContextEditMode_Click(object sender, RoutedEventArgs e)
    {
        EnterEditMode();
    }

    private async Task OpenLauncherSettingsAsync()
    {
        await RunModalAsync(track => LauncherSettingsWindow.ShowAsync(_launcher, _hwnd, track));

        // View mode, density and title all affect layout, so rebuild rather than assume.
        InvalidateItems(_launcher.Id);

        // Resize only after the rebuilt containers have actually been laid out — measuring
        // immediately would read stale desired sizes.
        if (_isEditMode)
            DispatcherQueue.TryEnqueue(ResizeForEditChrome);
    }


    private void ContextSettingsItem_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        if (_owner != null)
            SettingsWindow.ShowInstance(_owner);
    }

    private double _lastMeasuredHeight = 80;

    private double MeasureContentHeight()
    {
        if (IsIconMode)
            return MeasureIconModeHeight();

        // Calculate height arithmetically instead of calling UpdateLayout()/Measure()
        // on a potentially hidden window. Forcing a XAML layout pass on a window hidden
        // via ShowWindow(SW_HIDE) while another WinUI 3 window is active causes a fatal
        // ExecutionEngineException in Microsoft.WinUI.dll.
        //
        // Each ListViewItem container: MinHeight=0, Padding="8,6" → 12px vertical padding.
        // Regular item content: Icon 20px tall → total ~32px.
        // Group header content: 12px label with 4px top/bottom margin → ~24px.
        var items = _launcher.Items;
        if (items == null) return _lastMeasuredHeight;

        const double itemHeight = 32;
        const double groupHeight = 24;

        // Compute the height of each column and take the tallest.
        double maxColumnHeight = 0;
        foreach (var column in BuildColumnLists())
        {
            double currentColumnHeight = 0;

            foreach (var item in column)
            {
                if (item.IsGroup)
                {
                    currentColumnHeight += groupHeight;
                    foreach (var child in item.Children)
                        currentColumnHeight += itemHeight;

                    // An empty group still occupies one item-sized row, matching the
                    // MinHeight applied to its child list.
                    if (item.Children.Count == 0)
                        currentColumnHeight += EmptyGroupListModeHeight;
                }
                else
                {
                    currentColumnHeight += itemHeight;
                }
            }

            maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);
        }

        // Add a small buffer to cover accumulated sub-pixel font-height rounding.
        // Clamp to the available work-area height so the flyout never exceeds the screen.
        double titleHeight = _launcher.ShowTitle ? LauncherTitleHeight : 0;
        double editHeight = CurrentEditChromeHeight + CurrentColumnHeaderHeight + CurrentEmptyPlaceholderHeight;
        double outerPadding = FlyoutOuterPadding * 2;
        double maxContentHeight = GetWorkAreaHeightDips() - 16; // 16 = gap from taskbar edges
        _lastMeasuredHeight = Math.Clamp(maxColumnHeight + titleHeight + editHeight + outerPadding + 2, GetMinimumFlyoutHeight(), maxContentHeight);
        return _lastMeasuredHeight;
    }

    private double MeasureIconModeHeight()
    {
        int cellHeight = GetActiveIconCellHeight();
        double groupHeight = GetActiveGroupHeaderHeight();

        double maxColumnHeight = 0;

        foreach (var column in _columnLists)
        {
            double currentColumnHeight = 0;
            int currentRowSpan = 0;
            double currentRowHeight = 0;

            void FlushRow()
            {
                if (currentRowSpan == 0)
                    return;

                currentColumnHeight += currentRowHeight;
                currentRowSpan = 0;
                currentRowHeight = 0;
            }

            foreach (var item in column)
            {
                int span = GetTopLevelIconSpan(item);
                int perRow = GetIconModeIconsPerRow();
                int iconRows = Math.Max(1, (item.Children.Count + perRow - 1) / perRow);

                // Groups are sized like regular items: even an empty one occupies a full
                // cell row (plus its heading). ApplyEmptyGroupDropTarget gives the empty
                // group's child list a matching MinHeight so the rendering agrees.
                double itemHeight = item.IsGroup && !_syntheticGroups.Contains(item)
                    ? groupHeight + (iconRows * cellHeight)
                    : iconRows * cellHeight;

                if (currentRowSpan > 0 && currentRowSpan + span > GetIconModeIconsPerRow())
                    FlushRow();

                currentRowSpan += span;
                currentRowHeight = Math.Max(currentRowHeight, itemHeight);

                if (currentRowSpan >= GetIconModeIconsPerRow())
                    FlushRow();
            }

            FlushRow();
            maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);
        }

        double titleHeight = _launcher.ShowTitle ? LauncherTitleHeight : 0;
        double editHeight = CurrentEditChromeHeight + CurrentColumnHeaderHeight + CurrentEmptyPlaceholderHeight;
        double outerPadding = FlyoutOuterPadding * 2;
        double maxContentHeight = GetWorkAreaHeightDips() - 16;
        _lastMeasuredHeight = Math.Clamp(maxColumnHeight + titleHeight + editHeight + outerPadding + 2, GetMinimumFlyoutHeight(), maxContentHeight);
        return _lastMeasuredHeight;
    }

    private double GetWorkAreaHeightDips()
    {
        var pt = new POINT();
        GetCursorPos(out pt);
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiY, out _);
        double scale = dpiY / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        int workAreaHeightPx = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
        return workAreaHeightPx / scale;
    }

}