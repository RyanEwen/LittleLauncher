using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// Edit mode for the flyout — the single place launcher items are edited now that the
/// in-settings items page is gone.
/// </summary>
/// <remarks>
/// <para><b>Geometry contract.</b> Edit mode may grow the flyout's <i>height</i> (it adds a
/// real toolbar row, measured via <see cref="EditChromeHeight"/> in both
/// <c>MeasureContentHeight</c> and <c>MeasureIconModeHeight</c>) but must never change its
/// width or the size of any item or group. Width is derived arithmetically by
/// <c>GetFlyoutWidth()</c> from fixed column widths and is not measured, so a toolbar wider
/// than the content would be clipped — hence the icon-only buttons, sized to fit the
/// narrowest case (small-icon mode, ~136px, leaving ~120px usable).</para>
/// <para>Per-item affordances must likewise be non-reflowing: <see cref="ApplyEditVisuals"/>
/// tints group containers and outlines them using the same negative-padding compensation
/// trick as <c>ShowInsertionIndicator</c>, so outer dimensions are unchanged and
/// <c>PackedIconPanel</c> does not repack rows.</para>
/// </remarks>
public partial class FlyoutWindow
{
    /// <summary>Padding inside the toolbar's tinted container.</summary>
    private const int EditToolbarPadding = 3;

    /// <summary>Bottom margin under the edit toolbar.</summary>
    private const int EditToolbarGap = 8;

    /// <summary>
    /// Height the edit toolbar adds to the flyout: none. It floats in its own window above the
    /// flyout, so edit mode no longer changes the flyout's height at all.
    /// </summary>
    private static double CurrentEditChromeHeight => 0;

    /// <summary>Size of the floating toolbar bar, in DIPs, including its padding.</summary>
    private (int Width, int Height) EditToolbarSizeDips
    {
        get
        {
            if (_editToolbar == null) return (0, 0);

            int count = _editToolbar.Children.Count;
            int perRow = Math.Max(1, _editToolbar.MaximumRowsOrColumns);
            int rows = (int)Math.Ceiling(count / (double)perRow);
            int columns = Math.Min(count, perRow);

            int width = (columns * EditButtonSlot) + (EditToolbarPadding * 2);
            int height = (rows * EditButtonSlot) + (EditToolbarPadding * 2);

            return (width, height);
        }
    }

    private const int EditButtonSize = 28;

    /// <summary>Grid slot per toolbar button: the button plus its surrounding margin.</summary>
    private const int EditButtonSlot = 32;

    private bool _isEditMode;
    private Button? _editPencil;
    private VariableSizedWrapGrid? _editToolbar;

    /// <summary>Padded container for the toolbar buttons; hosted by the floating bar window.</summary>
    private Border? _editToolbarHost;

    // ── Empty state ─────────────────────────────────────────────────

    /// <summary>Reserved height for the empty-launcher placeholder.</summary>
    private const int EmptyPlaceholderHeight = 56;

    private TextBlock? _emptyPlaceholder;

    /// <summary>True when the launcher has no real items (column breaks are not content).</summary>
    private bool LauncherIsEmpty => !_launcher.Items.Any(i => !i.IsColumnBreak);

    private double CurrentEmptyPlaceholderHeight => LauncherIsEmpty ? EmptyPlaceholderHeight : 0;

    /// <summary>
    /// Creates the empty-state text.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <c>InitializeEditChrome</c>: that returns early for read-only
    /// launchers and runs after the first rebuild, so the placeholder was still null at the
    /// exact moment an empty launcher needed it.
    /// </remarks>
    private void InitializeEmptyPlaceholder()
    {
        _emptyPlaceholder = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(8, 4, 8, 8),
            Visibility = Visibility.Collapsed,
        };
        ContentStack.Children.Add(_emptyPlaceholder);
        UpdateEmptyPlaceholder();
    }

    /// <summary>
    /// Shows guidance inside an empty launcher, so it never renders as a blank box.
    /// </summary>
    /// <remarks>
    /// In the flyout rather than a one-off tip on creation: an empty launcher needs this
    /// explanation whenever it is empty, not only in the moments after it is made.
    /// </remarks>
    private void UpdateEmptyPlaceholder()
    {
        if (_emptyPlaceholder == null) return;

        bool empty = LauncherIsEmpty;
        _emptyPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        _emptyPlaceholder.Text = _isEditMode
            ? "This launcher is empty.\nAdd your first item with + above."
            : "This launcher is empty.\nHover here and click the pencil to add items.";

        _emptyPlaceholder.MaxWidth = Math.Max(120, GetFlyoutWidth() - (FlyoutOuterPadding * 4));
    }

    /// <summary>
    /// Opens a launcher's flyout directly in edit mode.
    /// </summary>
    /// <remarks>
    /// Removing the settings-side items page left no answer to "how do I edit this?" — most
    /// acutely for a launcher just created, which is empty and so has nothing on screen to
    /// hover. This is the actionable route from settings into editing.
    /// </remarks>
    internal static void ShowInEditMode(MainWindow owner, string launcherId)
    {
        // A flyout still warming up is visible off screen, not on it — Toggle brings it in.
        bool alreadyVisible =
            _instances.TryGetValue(launcherId, out var existing) &&
            existing._hwnd != IntPtr.Zero &&
            IsWindow(existing._hwnd) &&
            !existing._isPreRendering &&
            (GetWindowLong(existing._hwnd, GWL_STYLE) & WS_VISIBLE) != 0;

        // Toggle would *close* an already-open flyout.
        if (!alreadyVisible)
        {
            // A launcher hidden from the tray has no icon to anchor to, so the tray corner
            // would point at nothing — centre it instead.
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            bool hasTrayIcon = launcher is { NIconHide: false };

            var (x, y) = hasTrayIcon ? GetTrayAnchorPoint() : GetScreenCentreAnchor();
            Toggle(owner, x, y, launcherId);
        }

        if (!_instances.TryGetValue(launcherId, out var flyout)) return;

        if (alreadyVisible)
        {
            flyout.DispatcherQueue.TryEnqueue(flyout.EnterEditMode);
            return;
        }

        // Wait out the open animation before entering edit mode.
        //
        // ShowAnimated drives the window's position on a rendering loop for
        // ShowAnimationDurationMs. Resizing partway through gets overwritten when the
        // animation's next frame writes the geometry it was aiming for — which is why the
        // flyout opened from settings ignored the height of the column headers.
        var settle = flyout.DispatcherQueue.CreateTimer();
        settle.Interval = TimeSpan.FromMilliseconds(
            AreAnimationsEnabled ? ShowAnimationDurationMs + 60 : 0);
        settle.IsRepeating = false;
        settle.Tick += (s, _) =>
        {
            s.Stop();
            flyout.EnterEditMode();
        };
        settle.Start();
    }

    /// <summary>Approximate on-screen position of the notification area, for anchoring.</summary>
    private static (int X, int Y) GetTrayAnchorPoint()
    {
        try
        {
            GetCursorPos(out var cursor);
            IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            // Bottom-right of the work area: where the tray sits in the default layout.
            // CalculatePlacement re-detects the real taskbar edge and clamps from there.
            return (info.rcWork.Right - 8, info.rcWork.Bottom - 8);
        }
        catch
        {
            GetCursorPos(out var fallback);
            return (fallback.X, fallback.Y);
        }
    }

    /// <summary>
    /// Horizontally centred on the work area, but at the taskbar edge — so a launcher with no
    /// tray icon still opens like a normal flyout, just centred rather than off in the corner.
    /// </summary>
    private static (int X, int Y) GetScreenCentreAnchor()
    {
        try
        {
            GetCursorPos(out var cursor);
            IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            var work = info.rcWork;
            return (work.Left + ((work.Right - work.Left) / 2), work.Bottom - 8);
        }
        catch
        {
            GetCursorPos(out var fallback);
            return (fallback.X, fallback.Y);
        }
    }

    /// <summary>The floating bar above the flyout. Created lazily on first entry to edit mode.</summary>
    private EditToolbarWindow? _editToolbarBar;

    /// <summary>True while an owned editor window or picker is open, so focus loss must not dismiss.</summary>
    private bool _isModalOpen;

    /// <summary>
    /// The currently open owned editor/prompt window, so it can be closed if editing ends
    /// underneath it. Without this an "Add item" window outlives the edit session that
    /// opened it and would commit into a launcher the user has already navigated away from.
    /// </summary>
    private Window? _openModal;

    /// <summary>
    /// Tears down every window this flyout owns. Called when its launcher is deleted.
    /// </summary>
    /// <remarks>
    /// The floating toolbar and any open editor are separate top-level windows, so closing the
    /// flyout leaves them on screen — orphaned UI for a launcher that no longer exists.
    /// </remarks>
    internal void CloseEditChrome()
    {
        _isEditMode = false;

        var modal = _openModal;
        _openModal = null;
        try { modal?.Close(); } catch { /* already closing */ }

        var bar = _editToolbarBar;
        _editToolbarBar = null;
        try { bar?.Close(); } catch { /* already closing */ }
    }

    /// <summary>Closes any open editor/prompt window. Safe to call when none is open.</summary>
    private void CloseOpenModal()
    {
        var modal = _openModal;
        _openModal = null;
        try { modal?.Close(); } catch { /* already closing */ }
        SetFlyoutTopmost(true);
    }

    /// <summary>
    /// Toggles the flyout's always-on-top flag.
    /// </summary>
    /// <remarks>
    /// The flyout is created always-on-top so it renders above the tray overflow popup, but
    /// that also puts it above our own owned editor windows — owner relationship alone does
    /// not win against a topmost owner. Dropping the flag while a modal is open is what
    /// actually lets the editor sit in front.
    /// </remarks>
    /// <summary>
    /// Tints the flyout's window border while editing, so the whole surface reads as "in edit
    /// mode" rather than only the floating toolbar.
    /// </summary>
    /// <remarks>
    /// Uses the DWM border colour rather than a XAML border: it costs no layout at all, so it
    /// cannot change the flyout's width or inset its content — which a <c>BorderThickness</c>
    /// on <c>RootGrid</c> would.
    /// </remarks>
    private void SetEditModeBorder(bool editing)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        try
        {
            int value;
            if (editing)
            {
                var accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush;
                var c = accent?.Color ?? global::Windows.UI.Color.FromArgb(255, 0x60, 0xA0, 0xE0);
                // COLORREF is 0x00BBGGRR, the reverse of the usual RGB order.
                value = (c.B << 16) | (c.G << 8) | c.R;
            }
            else
            {
                value = unchecked((int)DWMWA_COLOR_DEFAULT);
            }

            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int));
        }
        catch { /* border tint is cosmetic */ }
    }

    private void SetFlyoutTopmost(bool topmost)
    {
        try
        {
            if (GetAppWindow().Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = topmost;
        }
        catch { /* presenter unavailable during teardown */ }
    }

    /// <summary>Runs an owned modal window, keeping the flyout pinned open and out of its way.</summary>
    private async Task<T> RunModalAsync<T>(Func<Action<Window>, Task<T>> show)
    {
        _isModalOpen = true;
        SetFlyoutTopmost(false);
        // The bar is topmost in its own right, so dropping only the flyout still leaves it
        // floating over the editor.
        _editToolbarBar?.HideBar();
        try
        {
            return await show(w => _openModal = w);
        }
        finally
        {
            _isModalOpen = false;
            _openModal = null;
            SetFlyoutTopmost(true);
            RepositionEditToolbarBar();
            RestoreFlyoutActivation();
        }
    }

    /// <summary>
    /// Returns foreground to the flyout after an owned window closes.
    /// </summary>
    /// <remarks>
    /// Dismissal is driven by the <c>Deactivated</c> event, which can only fire for a window
    /// that currently holds activation. Opening a modal takes activation away, and closing it
    /// does not necessarily give it back — leaving the flyout visible but unable to ever
    /// deactivate, so it stayed on screen until the user re-triggered it.
    /// </remarks>
    private void RestoreFlyoutActivation()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        if ((GetWindowLong(_hwnd, GWL_STYLE) & WS_VISIBLE) == 0) return;

        try { SetForegroundWindow(_hwnd); } catch { /* foreground can be refused */ }
    }

    /// <summary>Edit mode pins the flyout open; so does any modal it owns.</summary>
    private bool SuppressDismiss => _isEditMode || _isModalOpen;

    /// <summary>
    /// True for groups the user can act on. Synthetic groups are ephemeral wrappers created
    /// per-rebuild for icon-mode packing and unwrapped again by <c>SyncColumnsToFlatList</c>,
    /// so they must never be renamed, removed, reordered, or persisted.
    /// </summary>
    private bool IsEditableGroup(LauncherItem item) =>
        item.IsGroup && !_syntheticGroups.Contains(item);

    // ── Chrome ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds both pieces of edit chrome. The pencil is an <b>overlay</b> in
    /// <c>RootGrid</c> (like the resize grips) so it costs zero measured height and normal
    /// mode stays geometrically identical; the toolbar is a real row in
    /// <c>ContentStack</c>, collapsed until edit mode is entered.
    /// </summary>
    private void InitializeEditChrome()
    {
        // Shared launchers the user doesn't own are read-only — no entry point at all.
        if (IsReadOnlyLauncher) return;

        _editPencil = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        ToolTipService.SetToolTip(_editPencil, "Edit items");
        _editPencil.SetValue(Canvas.ZIndexProperty, 11);
        _editPencil.Click += (_, _) => EnterEditMode();
        RootGrid.Children.Add(_editPencil);

        // Hover reveal — the pencil is invisible until the pointer is over the flyout.
        RootGrid.PointerEntered += (_, _) => SetPencilVisible(true);
        RootGrid.PointerExited += (_, _) => SetPencilVisible(false);

        // Lives in its own floating bar above the flyout, not inside ContentStack — see
        // EditToolbarWindow for why.
        _editToolbar = BuildEditToolbar();

        _editToolbarHost = new Border
        {
            Child = _editToolbar,
            Padding = new Thickness(EditToolbarPadding),
        };

        InitializeHoverPencil();
    }

    // ── Hover affordance ────────────────────────────────────────────

    private Border? _hoverPencil;
    private Control? _hoverTarget;

    /// <summary>
    /// Creates the single hover overlay reused for every item and group.
    /// </summary>
    /// <remarks>
    /// One floating element in <c>RootGrid</c> rather than a pencil baked into each of the
    /// six item/group DataTemplates: it keeps the affordance out of the templates entirely,
    /// so it cannot affect item measurement, and it works identically in list, icon and
    /// small-icon modes.
    /// </remarks>
    private void InitializeHoverPencil()
    {
        _hoverPencil = new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x9E, 0x1F, 0x1F, 0x1F)),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new FontIcon
            {
                Glyph = "",
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _hoverPencil.SetValue(Canvas.ZIndexProperty, 12);
        RootGrid.Children.Add(_hoverPencil);

        RootGrid.AddHandler(
            UIElement.PointerExitedEvent,
            new PointerEventHandler((_, _) => HideHoverPencil()),
            handledEventsToo: true);
    }

    private readonly HashSet<Control> _hoverWiredContainers = [];

    /// <summary>
    /// Subscribes a list so its item containers get hover handlers as they are realised.
    /// </summary>
    /// <remarks>
    /// Handlers go on the containers themselves rather than on <c>RootGrid</c>. A root-level
    /// <c>PointerMoved</c> handler does not work here: <c>ListViewItem</c> marks the event
    /// handled for its own hover visuals, so the handler never fires over an actual item —
    /// only over empty space, which is why empty groups appeared to work and real items did
    /// not. Container-level <c>PointerEntered</c> is the same mechanism the item's own hover
    /// visual uses, so it is reliable.
    /// </remarks>
    private void WireHoverAffordance(ListViewBase list)
    {
        list.ContainerContentChanging -= EditHover_ContainerContentChanging;
        list.ContainerContentChanging += EditHover_ContainerContentChanging;


        // ContainerContentChanging only fires for containers realised after subscribing,
        // so pick up anything already on screen.
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is Control container)
                WireHoverContainer(container);
        }
    }

    private void EditHover_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemContainer is Control container)
            WireHoverContainer(container);
    }

    private void WireHoverContainer(Control container)
    {
        bool isNew = _hoverWiredContainers.Add(container);
        if (!isNew) return;
        container.PointerEntered += Container_PointerEnteredForEdit;
        container.PointerExited += Container_PointerExitedForEdit;
    }

    /// <summary>
    /// Gets the <see cref="LauncherItem"/> a container is displaying.
    /// </summary>
    /// <remarks>
    /// Reads <c>Content</c>, not <c>DataContext</c>. These lists are populated via
    /// <c>ItemsSource</c>, which puts the item in the container's <c>Content</c> and leaves
    /// <c>DataContext</c> null — checking <c>DataContext</c> silently rejects every item.
    /// Re-read on each event rather than captured, since containers are recycled.
    /// </remarks>
    private static LauncherItem? GetContainerItem(Control container) =>
        (container as ContentControl)?.Content as LauncherItem
        ?? container.DataContext as LauncherItem;

    private void Container_PointerEnteredForEdit(object sender, PointerRoutedEventArgs e)
    {
        if (!_isEditMode || sender is not Control container) return;

        var item = GetContainerItem(container);
        if (item == null) return;
        if (item.IsColumnBreak) return;
        if (item.IsGroup && !IsEditableGroup(item)) return;

        PositionHoverPencil(container);

        // Stop the enclosing group container from claiming the hover: the child's handler
        // runs first, so without this the parent would immediately overwrite the position
        // with the whole group's bounds.
        e.Handled = true;
    }

    private void Container_PointerExitedForEdit(object sender, PointerRoutedEventArgs e)
    {
        HideHoverPencil();

        // Deliberately not marked handled. Starting a drag moves the pointer out of the
        // container, and swallowing that exit interfered with the ListView's own drag
        // gesture — items inside groups became hard to pick up.
    }

    private void PositionHoverPencil(Control container)
    {
        if (_hoverPencil == null) return;

        try
        {
            var origin = container.TransformToVisual(RootGrid)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));

            _hoverPencil.Width = container.ActualWidth;
            _hoverPencil.Height = container.ActualHeight;
            _hoverPencil.Margin = new Thickness(origin.X, origin.Y, 0, 0);
            _hoverPencil.Visibility = Visibility.Visible;
        }
        catch { HideHoverPencil(); }
    }

    private void HideHoverPencil()
    {
        _hoverTarget = null;
        if (_hoverPencil != null)
            _hoverPencil.Visibility = Visibility.Collapsed;
    }

    private void SetPencilVisible(bool visible)
    {
        if (_editPencil == null || _isEditMode) return;
        _editPencil.Opacity = visible ? 1 : 0;
        _editPencil.IsHitTestVisible = visible;
    }

    /// <summary>
    /// Builds the edit toolbar as a <b>wrapping</b> grid.
    /// </summary>
    /// <remarks>
    /// Six buttons do not fit one row in small-icon mode (~136px wide, ~120px usable), and the
    /// flyout's width is fixed arithmetically, so anything wider than the content is clipped
    /// rather than widening the window. Wrapping to a second row is fine: edit mode may grow
    /// height, and <see cref="ResizeForEditChrome"/> measures the real laid-out height, so a
    /// two-row toolbar is accounted for automatically.
    /// </remarks>
    private VariableSizedWrapGrid BuildEditToolbar()
    {
        var toolbar = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = EditButtonSlot,
            ItemHeight = EditButtonSlot,
            // Placement, spacing and visibility belong to the host border.
        };

        toolbar.Children.Add(EditButton("", "Add item", () => _ = AddItemAsync()));
        toolbar.Children.Add(EditButton("", "Add group", () => _ = AddGroupAsync()));
        toolbar.Children.Add(EditButtonDrawn(BuildAddColumnIcon(), "Add column", AddColumn));
        toolbar.Children.Add(EditButton("", "Launcher settings", () => _ = OpenLauncherSettingsAsync()));
        toolbar.Children.Add(EditButton("", "Done", ExitEditMode));
        return toolbar;
    }

    /// <summary>
    /// Recomputes how many toolbar buttons fit per row at the current flyout width. Must run
    /// before measuring, since view mode and icons-per-row both change that width.
    /// </summary>
    private void UpdateEditToolbarColumns()
    {
        if (_editToolbar == null) return;

        double usable = GetFlyoutWidth() - (FlyoutOuterPadding * 2);
        int perRow = Math.Max(1, (int)(usable / EditButtonSlot));
        _editToolbar.MaximumRowsOrColumns = Math.Min(perRow, _editToolbar.Children.Count);
    }

    /// <summary>
    /// Single overload by design. A second <c>Func&lt;Task&gt;</c> overload is not safe here:
    /// <c>null</c> converts to both it and <c>Action?</c>, so a delegating call would bind to
    /// itself and recurse until the stack overflows. Async handlers pass
    /// <c>() =&gt; _ = SomethingAsync()</c> instead.
    /// </summary>
    private static Button EditButton(string glyph, string tooltip, Action? onClick = null) =>
        EditButtonCore(new FontIcon { Glyph = glyph, FontSize = 12 }, tooltip, onClick);

    /// <summary>Same button, but with arbitrary drawn content instead of a font glyph.</summary>
    private static Button EditButtonDrawn(UIElement content, string tooltip, Action? onClick = null) =>
        EditButtonCore(content, tooltip, onClick);

    private static Button EditButtonCore(UIElement content, string tooltip, Action? onClick)
    {
        var button = new Button
        {
            Content = content,
            Width = EditButtonSize,
            Height = EditButtonSize,
            Padding = new Thickness(0),
            MinWidth = 0,
            // Centres the button inside its EditButtonSlot grid cell.
            Margin = new Thickness((EditButtonSlot - EditButtonSize) / 2.0),
        };
        ToolTipService.SetToolTip(button, tooltip);
        if (onClick != null)
            button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Draws the "add column" icon: two solid bars plus an outlined one.
    /// </summary>
    /// <remarks>
    /// Drawn rather than a Segoe Fluent glyph. There is no obviously column-shaped glyph in
    /// the set, and the one first chosen read as something else entirely — three bars where
    /// the new one is hollow states the intent without depending on font contents.
    /// </remarks>
    private static UIElement BuildAddColumnIcon()
    {
        var fg = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (int i = 0; i < 2; i++)
        {
            panel.Children.Add(new Border
            {
                Width = 3,
                Height = 13,
                Background = fg,
                Opacity = 0.55,
                CornerRadius = new CornerRadius(1),
            });
        }

        panel.Children.Add(new Border
        {
            Width = 5,
            Height = 13,
            BorderBrush = fg,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
        });

        return panel;
    }

    // ── Mode transitions ────────────────────────────────────────────

    internal void EnterEditMode()
    {
        if (_isEditMode || IsReadOnlyLauncher) return;
        _isEditMode = true;

        if (_editPencil != null)
        {
            _editPencil.Opacity = 0;
            _editPencil.IsHitTestVisible = false;
        }
        UpdateEditToolbarColumns();
        UpdateResizeGripVisibility();
        SetEditModeBorder(true);
        ApplyEditVisuals();
        ResizeForEditChrome();
        ShowEditToolbarBar();
    }

    /// <summary>Creates the floating bar if needed, then parks it above the flyout.</summary>
    private void ShowEditToolbarBar()
    {
        if (_editToolbarHost == null) return;

        _editToolbarBar ??= new EditToolbarWindow(_editToolbarHost);

        var (width, height) = EditToolbarSizeDips;
        if (width <= 0 || height <= 0) return;

        _editToolbarBar.PositionAbove(_hwnd, width, height);
    }

    /// <summary>
    /// Keeps the bar parked over the flyout after a move or resize.
    /// </summary>
    /// <remarks>
    /// Deferred to the dispatcher because <c>ResizeWindowToCurrentContent</c> moves the flyout
    /// with <c>SWP_ASYNCWINDOWPOS</c>: reading its rect immediately afterwards returns the
    /// <i>old</i> bounds, so the bar would centre itself on the flyout's previous width and
    /// end up visibly off to one side.
    /// </remarks>
    private void RepositionEditToolbarBar()
    {
        if (!_isEditMode || _editToolbarBar == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isEditMode) ShowEditToolbarBar();
        });
    }

    internal void ExitEditMode()
    {
        if (!_isEditMode) return;
        _isEditMode = false;

        // Leaving edit mode dismisses anything opened from it.
        CloseOpenModal();

        _editToolbarBar?.HideBar();
        SetEditModeBorder(false);

        UpdateResizeGripVisibility();
        ApplyEditVisuals();
        ResizeForEditChrome();
    }

    /// <summary>
    /// Resizes after an edit-mode toggle using a <b>real</b> layout pass rather than the
    /// arithmetic estimate.
    /// </summary>
    /// <remarks>
    /// The arithmetic in <c>MeasureContentHeight</c> exists because measuring a hidden window
    /// is fatal, but a mode toggle only ever happens while the flyout is visible, so a real
    /// measure is both safe and exact here. It also avoids having to keep a hand-tuned
    /// toolbar-height constant in sync with the toolbar's actual rendered height — getting
    /// that wrong clips content at both edges, since icon mode centres
    /// <c>ContentStack</c> vertically.
    /// </remarks>
    private void ResizeForEditChrome()
    {
        UpdateEditToolbarColumns();
        ResizeWindowToCurrentContent(keepRightEdge: false);
        // The flyout just moved and/or changed width — keep the bar parked over it.
        RepositionEditToolbarBar();
    }

    // ── Non-reflowing edit visuals ──────────────────────────────────

    /// <summary>
    /// Tints and outlines group containers so containers read as containers in edit mode.
    /// Every change here is dimension-neutral: <c>Background</c> has no layout effect, and
    /// the border is compensated by an equal negative padding so the container's outer size
    /// is unchanged (the same technique <c>ShowInsertionIndicator</c> uses to stop the grid
    /// reflowing).
    /// </summary>
    private void ApplyEditVisuals()
    {
        if (_isEditMode)
        {
            foreach (var columnListView in ColumnListViews())
            {
                if (columnListView.ItemsSource is not IEnumerable<LauncherItem> items) continue;

                foreach (var item in items)
                {
                    if (!IsEditableGroup(item)) continue;
                    if (columnListView.ContainerFromItem(item) is not Control container) continue;
                    ApplyGroupEditVisual(container);
                }
            }
        }
        else
        {
            // Containers no longer realised (rebuilt columns) can simply be dropped — they
            // were discarded along with their styling.
            foreach (var container in _editStyledContainers)
                ClearGroupEditVisual(container);
            _editStyledContainers.Clear();
        }

        // These three must run on *both* paths — each handles its own edit-mode check, and
        // an early return here is what left column headers visible in normal mode.
        ApplyEmptyGroupDropTargets();
        ApplyEmptyColumnPlaceholders();
        ApplyColumnChrome();
        // Wording differs between modes ("+ above" only makes sense while editing).
        UpdateEmptyPlaceholder();
    }

    /// <summary>
    /// Gives an empty column a droppable area while editing.
    /// </summary>
    /// <remarks>
    /// Size only, no tint: the column header already shows that the column exists, so a
    /// background as well was redundant. Without the <c>MinHeight</c> an empty column is
    /// zero-height and there is nothing to drag onto.
    /// </remarks>
    private void ApplyEmptyColumnPlaceholders()
    {
        // When the whole launcher is empty the placeholder text is the empty state, and there
        // is nothing to drag anyway. Reserving column height on top of it pushed the
        // placeholder out of the window, since only one of the two is in the arithmetic.
        bool launcherEmpty = LauncherIsEmpty;

        foreach (var columnListView in ColumnListViews())
        {
            bool isEmpty = columnListView.Items.Count == 0;

            if (isEmpty && _isEditMode && !launcherEmpty)
            {
                columnListView.MinHeight = IsIconMode ? GetActiveIconCellHeight() : EmptyGroupListModeHeight;
                columnListView.AllowDrop = true;
            }
            else
            {
                columnListView.ClearValue(FrameworkElement.MinHeightProperty);
            }
        }
    }

    /// <summary>Containers currently carrying edit styling, so it can be cleared again.</summary>
    private readonly HashSet<Control> _editStyledContainers = [];

    /// <summary>
    /// Every realised group child list, across all view modes, so empty groups can be given
    /// a drop target in edit mode.
    /// </summary>
    private readonly HashSet<ListView> _loadedGroupChildLists = [];

    /// <summary>
    /// Registers a group's child list. Called from the per-mode <c>Loaded</c> handlers.
    /// </summary>
    private void TrackGroupChildList(ListView listView)
    {
        _loadedGroupChildLists.Add(listView);
        listView.Unloaded -= GroupChildList_UnloadedForEdit;
        listView.Unloaded += GroupChildList_UnloadedForEdit;
        ApplyEmptyGroupDropTarget(listView);
        WireHoverAffordance(listView);
    }

    private void GroupChildList_UnloadedForEdit(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
            _loadedGroupChildLists.Remove(listView);
    }

    private void ApplyEmptyGroupDropTargets()
    {
        foreach (var listView in _loadedGroupChildLists.ToList())
            ApplyEmptyGroupDropTarget(listView);
    }

    /// <summary>
    /// Keeps an empty group the same size as a regular item, and tints it as a drop target
    /// while editing.
    /// </summary>
    /// <remarks>
    /// An empty group otherwise renders as a bare heading over a zero-height child list —
    /// nothing to drag onto, and inconsistent with how every other entry is sized. The
    /// <c>MinHeight</c> is applied in <b>both</b> modes so the rendering matches what
    /// <c>MeasureIconModeHeight</c>/<c>MeasureContentHeight</c> already reserve for a group;
    /// applying it only while editing left dead space outside edit mode. Only the tint is
    /// edit-mode-specific.
    /// </remarks>
    private void ApplyEmptyGroupDropTarget(ListView listView)
    {
        bool isEmpty = listView.Tag is LauncherItem group
            ? group.Children.Count == 0
            : listView.Items.Count == 0;

        if (isEmpty)
            listView.MinHeight = IsIconMode ? GetActiveIconCellHeight() : EmptyGroupListModeHeight;
        else
            listView.ClearValue(FrameworkElement.MinHeightProperty);

        if (isEmpty && _isEditMode)
        {
            listView.Background = new SolidColorBrush(EditTintColor(0x2E));
            listView.CornerRadius = new CornerRadius(6);
            listView.AllowDrop = true;
        }
        else
        {
            listView.ClearValue(Control.BackgroundProperty);
            listView.ClearValue(Control.CornerRadiusProperty);
        }
    }

    private const double EmptyGroupListModeHeight = 32;

    /// <summary>
    /// Deliberately background-only: <c>Background</c> and <c>CornerRadius</c> have no effect
    /// on layout, so group containers cannot change size and <c>PackedIconPanel</c> cannot
    /// repack. A border was tried and rejected — compensating for it with negative padding
    /// works only when the container already has padding on every side to give back, which
    /// the icon-mode container style does not.
    /// </summary>
    private void ApplyGroupEditVisual(Control container)
    {
        if (!_editStyledContainers.Add(container)) return;

        container.Background = new SolidColorBrush(EditTintColor(0x59));
        container.CornerRadius = new CornerRadius(6);
    }

    /// <summary>Accent-derived tint, so the highlight is clearly visible in both themes.</summary>
    private static global::Windows.UI.Color EditTintColor(byte alpha)
    {
        if (Application.Current.Resources["AccentFillColorDefaultBrush"] is SolidColorBrush accent)
            return global::Windows.UI.Color.FromArgb(alpha, accent.Color.R, accent.Color.G, accent.Color.B);
        return global::Windows.UI.Color.FromArgb(alpha, 0x60, 0xA0, 0xE0);
    }

    private static void ClearGroupEditVisual(Control container)
    {
        container.ClearValue(Control.BackgroundProperty);
        container.ClearValue(Control.CornerRadiusProperty);
    }

    // ── Structural operations ───────────────────────────────────────

    /// <summary>
    /// Persists a structural edit and rebuilds.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately does <b>not</b> call <c>SyncColumnsToFlatList()</c>. That method
    /// clears <c>_launcher.Items</c> and regenerates it from <c>_columnLists</c>, so calling
    /// it here would resurrect anything just removed from the flat list — which is exactly
    /// what broke remove. Column lists are a derived view; <c>_launcher.Items</c> is the
    /// source of truth, and the rebuild triggered below regenerates the columns from it.</para>
    /// <para>Drag-and-drop is the opposite case: it mutates <c>_columnLists</c> directly, so
    /// it must flush the other way via <c>PersistFlyoutReorder</c>.</para>
    /// </remarks>
    private void PersistStructuralChange()
    {
        PersistFlyoutItemChanges();
        ApplyEditVisuals();
        ResizeForEditChrome();
    }

    /// <summary>
    /// Target collection for newly added top-level content. Appending to the flat list lands
    /// after the final column break, i.e. in the last column.
    /// </summary>
    private ObservableCollection<LauncherItem> GetAddTarget() => _launcher.Items;

    private async Task AddItemAsync()
    {
        var target = GetAddTarget();
        var result = await RunModalAsync(track => ItemEditorWindow.ShowAsync(null, target, _hwnd, track));
        if (result == ItemEditorResult.Saved)
            PersistStructuralChange();
    }

    internal async Task EditItemAsync(LauncherItem item)
    {
        var parent = FindParentCollection(item) ?? GetAddTarget();
        var result = await RunModalAsync(track => ItemEditorWindow.ShowAsync(item, parent, _hwnd, track));

        switch (result)
        {
            case ItemEditorResult.Saved:
                PersistStructuralChange();
                break;
            // RemoveItem persists on its own.
            case ItemEditorResult.Deleted:
                RemoveItem(item);
                break;
        }
    }

    private async Task AddGroupAsync()
    {
        string? name = await PromptForGroupNameAsync(null);
        if (name == null) return;

        var group = LauncherItem.CreateGroup(name);
        GetAddTarget().Add(group);
        PersistStructuralChange();
    }

    internal async Task RenameGroupAsync(LauncherItem group)
    {
        string? name = await PromptForGroupNameAsync(group.Name);
        if (name == null) return;

        group.Name = name;
        PersistStructuralChange();
    }

    /// <summary>
    /// Small name prompt for groups. Uses a standalone window, not a <c>ContentDialog</c> —
    /// a dialog cannot overflow the flyout's HWND, and the flyout is frequently shorter than
    /// even a one-field dialog, which clips the input and buttons.
    /// </summary>
    private async Task<string?> PromptForGroupNameAsync(string? existingName)
    {
        bool isEdit = existingName != null;

        string? name = await RunModalAsync(track => TextPromptWindow.ShowAsync(
            title: isEdit ? "Rename Group" : "Add Group",
            placeholder: "Group name",
            initialText: existingName,
            acceptText: isEdit ? "Save" : "Add",
            ownerHwnd: _hwnd,
            onCreated: track));

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private void AddColumn()
    {
        _launcher.Items.Add(LauncherItem.CreateColumnBreak());
        PersistFlyoutItemChanges();
        ApplyEditVisuals();
        ResizeForEditChrome();
    }

    internal void RemoveItem(LauncherItem item)
    {
        // Synthetic groups are ephemeral wrappers, not real content — never removable.
        if (item.IsGroup && !IsEditableGroup(item)) return;

        var parent = FindParentCollection(item);
        if (parent == null || !parent.Remove(item)) return;
        PersistStructuralChange();
    }

    // ── Column chrome ───────────────────────────────────────────────

    /// <summary>Height of a column header row (edit mode only).</summary>
    private const int ColumnHeaderHeight = 24;

    /// <summary>
    /// Height contributed by per-column headers. Added once, not per column: every column
    /// carries a header, and the flyout's height is driven by the tallest column.
    /// </summary>
    private double CurrentColumnHeaderHeight =>
        _isEditMode && _columnLists.Count > 1 ? ColumnHeaderHeight : 0;

    /// <summary>
    /// Wraps a column's list view with a header so multi-column layouts are legible and each
    /// column can be removed individually.
    /// </summary>
    /// <remarks>
    /// Only used when there is more than one column — a lone column needs no distinguishing
    /// chrome, and skipping the wrapper keeps the single-column case byte-identical to before.
    /// The header is edit-mode-only (it adds height, which is allowed); the column tint is
    /// <c>Background</c>-only, so it never affects width or item sizing.
    /// </remarks>
    private FrameworkElement BuildColumnContainer(ListViewBase columnListView, int columnIndex)
    {
        var header = new Grid
        {
            Height = ColumnHeaderHeight,
            Tag = "ColumnHeader",
            Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed,
        };

        // Remove button first, then the title.
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Width = 20,
            Height = 20,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        ToolTipService.SetToolTip(remove, "Remove this column");
        int captured = columnIndex;
        remove.Click += (_, _) => RemoveColumnAt(captured);

        var label = new TextBlock
        {
            Text = $"Column {columnIndex + 1}",
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 4, 0),
        };

        Grid.SetColumn(remove, 0);
        Grid.SetColumn(label, 1);
        header.Children.Add(remove);
        header.Children.Add(label);

        // A Grid, not a StackPanel: the list view must *fill* the remaining column height so
        // empty space below the items is still a drop target. A StackPanel gives children only
        // their desired height, which is why dropping required landing precisely on the last
        // item, and why an empty column could only be dropped onto at its very top.
        //
        // Deliberately no explicit Width — the Grid sizes to its widest child, so the wrapper
        // tracks the list view when icons-per-row changes. Pinning it to the list view's width
        // froze wrapped columns at their build-time size.
        var container = new Grid();
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(header, 0);
        Grid.SetRow(columnListView, 1);
        columnListView.VerticalAlignment = VerticalAlignment.Stretch;

        container.Children.Add(header);
        container.Children.Add(columnListView);
        return container;
    }

    /// <summary>Shows or hides the per-column headers for the current mode.</summary>
    private void ApplyColumnChrome()
    {
        foreach (var child in ColumnsPanel.Children.OfType<Panel>())
        {
            foreach (var header in child.Children.OfType<Grid>())
            {
                if (header.Tag as string == "ColumnHeader")
                    header.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Removes a specific column, merging its items into the neighbouring one.
    /// </summary>
    /// <remarks>
    /// Column <c>i</c> is preceded by break <c>i-1</c>; removing that break concatenates the
    /// column into its predecessor. For the first column the following break is removed
    /// instead, which merges it with the second — the same resulting flat list either way.
    /// </remarks>
    private void RemoveColumnAt(int columnIndex)
    {
        var breaks = _launcher.Items.Where(i => i.IsColumnBreak).ToList();
        if (breaks.Count == 0) return;

        int breakIndex = Math.Clamp(columnIndex == 0 ? 0 : columnIndex - 1, 0, breaks.Count - 1);
        _launcher.Items.Remove(breaks[breakIndex]);

        PersistFlyoutItemChanges();
        ApplyEditVisuals();
        ResizeForEditChrome();
    }}
