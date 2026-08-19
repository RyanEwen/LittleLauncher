// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// Tabs: every live browser this flyout owns, and the strip that switches between them.
/// </summary>
/// <remarks>
/// <para>There are two kinds, and the difference is who chose the address:</para>
/// <list type="bullet">
///   <item>The <b>home tab</b> (<see cref="WebTab.HomeKey"/> non-null) is the one the launcher
///   opened for itself. It is the only tab allowed to write the launcher's icon or to be
///   re-navigated by a settings change.</item>
///   <item><b>Link tabs</b> (<c>HomeKey</c> null) are pages the *page* asked to open — a
///   <c>target="_blank"</c> link, a <c>window.open</c>, a Ctrl-click. They belong to the user's
///   visit, not to the launcher, and nothing configured may drag them somewhere else.</item>
/// </list>
/// <para><see cref="WebFlyoutWindow._webView"/> deliberately stays the one field the rest of the
/// class talks to and always points at the active tab's browser. That is what lets zoom,
/// navigation, the header, the address bar, permissions, notifications and the service-worker
/// bridge carry on operating on "the browser" without any of them learning about tabs.</para>
/// <para>The strip appears on its own once there is a second tab, so a launcher that never opens
/// one never pays for it; <see cref="Launcher.WebAlwaysShowTabs"/> pins it on for a launcher used
/// as a small browser.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>
    /// The <see cref="WebTab.HomeKey"/> of the launcher's own page, outside bookmarks-as-tabs mode.
    /// </summary>
    /// <remarks>
    /// A sentinel rather than the empty string, because empty is what <c>NormalizeUrl</c> returns
    /// for an address that is not set — and "the tab for no address" must not be findable.
    /// </remarks>
    /// <remarks>
    /// <b>Written as an escape, never as a literal NUL byte.</b> A raw one in the source makes git
    /// treat the whole file as binary: no diffs, no line-ending normalisation, and a review that
    /// shows only "Bin 29227 -> 35880 bytes". Same rule as the private-use glyphs in the header.
    /// </remarks>
    private const string PrimaryTabKey = "\0home";

    /// <summary>Every live browser, in the order the strip shows them.</summary>
    private readonly List<WebTab> _tabs = [];

    /// <summary>When the page last reported a middle-click or a Ctrl/Shift-click. 0 if never.</summary>
    /// <remarks>
    /// The bridge between a gesture only the page can see and a request only the host is told
    /// about. <c>CoreWebView2NewWindowRequestedEventArgs</c> carries no modifier or button state, so
    /// there is nothing on the event itself that separates "middle-clicked this link" from "clicked
    /// this link" — and a browser puts the first behind and the second in front.
    /// </remarks>
    private long _backgroundIntentAt;

    /// <summary>How long a reported gesture stands as the reason for the next new window.</summary>
    /// <remarks>
    /// Long enough to cover a click whose navigation waits on a framework's own handler, short
    /// enough that an unrelated window opened later is not mistaken for the same gesture.
    /// </remarks>
    private const long BackgroundIntentWindowMs = 1000;

    /// <summary>The tab on screen. Its <see cref="WebTab.View"/> is <c>_webView</c>.</summary>
    private WebTab? _activeTab;

    /// <summary>
    /// The narrowest a tab may be squeezed before the strip scrolls instead.
    /// </summary>
    /// <remarks>
    /// Enough for the favicon, the chip's own padding and a few characters of title. Below this a
    /// tab is not a smaller tab, it is an unidentifiable one, so past this many tabs the strip goes
    /// back to scrolling and the active one is scrolled to. The close button costs nothing here
    /// because it is an overlay; it takes its room from the title, and only while hovered.
    /// </remarks>
    private const double MinTabWidthDips = 72;

    private readonly Controls.TabStripPanel _tabStrip = new()
    {
        Spacing = 2,
        MinItemWidth = MinTabWidthDips,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Grid _tabBar = new();
    private Button? _newTabButton;

    /// <summary>
    /// The strip's scroller. Kept because two things need it: the width the chips are squeezed
    /// into, which the panel cannot see for itself, and scrolling the active tab back into view
    /// once there are more tabs than even the squeeze can fit.
    /// </summary>
    private ScrollViewer? _tabScroller;

    /// <summary>The tab being dragged along the strip, or null.</summary>
    private WebTab? _draggingTab;

    /// <summary>Marks where a dragged tab will land. See the bookmark bar's, which it mirrors.</summary>
    private Border? _tabDropCaret;
    private Canvas? _tabBarOverlay;

    /// <summary>One live browser, its address and the chip that switches to it.</summary>
    private sealed class WebTab
    {
        public required WebView2 View { get; init; }

        /// <summary>
        /// <see cref="PrimaryTabKey"/> for the tab the launcher opened for itself; null for a tab
        /// the user asked for — a link, a middle-clicked bookmark, the "+".
        /// </summary>
        public string? HomeKey { get; init; }

        /// <summary>
        /// The address last handed to <see cref="WebFlyoutWindow.Navigate"/>, per tab rather than
        /// per window: the reload-on-open and settings-changed checks compare against the home
        /// tab's address, and a link tab must not be able to answer for it.
        /// </summary>
        public string NavigatedUrl { get; set; } = "";

        /// <summary>
        /// True once something has finished loading in this tab.
        /// </summary>
        /// <remarks>
        /// The difference between "there is nothing to look at yet" and "there is a page here and
        /// it is fetching another", which is what decides whether a navigation gets the whole
        /// status overlay or the hairline. See <c>WebFlyoutWindow.ShowLoading</c>.
        /// </remarks>
        public bool HasLoaded { get; set; }

        public Button? Chip { get; set; }
        public TextBlock? Label { get; set; }
        public Image? Icon { get; set; }
        public FontIcon? IconFallback { get; set; }

        /// <summary>The accent underline shown only while this tab is the active one.</summary>
        public Border? Indicator { get; set; }
    }

    // ── The strip ───────────────────────────────────────────────────

    /// <summary>
    /// Builds the tab strip, for the chrome stack between the header and the address bar.
    /// </summary>
    /// <remarks>
    /// A row of chrome rather than an overlay, for the reason the address bar and the permission
    /// prompt are: anything floating over a hosted browser depends on how WebView2 routes input,
    /// and a tab you cannot click is worse than one that costs a little height. Like the address
    /// bar it eats into the page rather than the window, so no geometry arithmetic depends on how
    /// tall it comes out.
    /// </remarks>
    private FrameworkElement BuildTabBar()
    {
        // U+E710 is Segoe Fluent's Add glyph, escaped rather than pasted for the reason recorded
        // on the header's back button.
        _newTabButton = BuildHeaderButton("\uE710", "New tab", (_, _) => OpenNewTab());

        var scroller = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _tabScroller = scroller;

        // The panel is measured with an infinite width in here (that is what a horizontal
        // scroller does), so the width that actually decides the squeeze has to be handed to it.
        // The scroller sits in a star column whose width does not depend on its content, so this
        // cannot feed back on itself.
        scroller.SizeChanged += (_, e) =>
        {
            if (Math.Abs(_tabStrip.AvailableWidth - e.NewSize.Width) < 0.5) return;
            _tabStrip.AvailableWidth = e.NewSize.Width;
        };

        _tabBar.Padding = new Thickness(8, 0, 6, 3);
        _tabBar.Visibility = Visibility.Collapsed;
        _tabBar.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        _tabBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(scroller, 0);
        Grid.SetColumn(_newTabButton, 1);
        _tabBar.Children.Add(scroller);
        _tabBar.Children.Add(_newTabButton);

        // Drawn in a layer of its own rather than by shuffling chips, for the reason recorded on the
        // bookmark bar's caret: the element that would move is the drag source, and taking it out of
        // the panel and putting it back unloads it mid-gesture.
        _tabDropCaret = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Visibility = Visibility.Collapsed,
        };
        _tabBarOverlay = new Canvas { IsHitTestVisible = false };
        _tabBarOverlay.Children.Add(_tabDropCaret);
        Grid.SetColumn(_tabBarOverlay, 0);
        _tabBar.Children.Add(_tabBarOverlay);

        // On the strip, not the chips: DragOver bubbles, so hovering a neighbour is answered here,
        // and the chips are rebuilt as tabs come and go while this is wired once.
        _tabStrip.AllowDrop = true;
        _tabStrip.DragOver += TabStrip_DragOver;
        _tabStrip.DragLeave += (_, _) => HideTabDropCaret();
        _tabStrip.Drop += TabStrip_Drop;

        // The empty part of the strip drags the window, exactly as the header and the bookmark bar
        // do. Chips and the "+" mark their own pointer events handled, so they never reach here.
        _tabBar.PointerPressed += BeginWindowMove;
        _tabBar.PointerMoved += ContinueWindowMove;
        _tabBar.PointerReleased += EndWindowMove;
        _tabBar.PointerCaptureLost += EndWindowMove;

        return _tabBar;
    }

    /// <summary>
    /// True when the strip has a job to do: more than one tab, or a launcher that asked for it.
    /// </summary>
    private bool ShouldShowTabBar =>
        _tabs.Count > 1 || (_launcher.WebAlwaysShowTabs && _tabs.Count > 0);

    /// <summary>
    /// Shows or hides the strip for the current tab count.
    /// </summary>
    /// <remarks>
    /// Gated on the header the same way the address bar is: everything that hides the header — a
    /// bookmark bar collapsed to a strip, a page gone fullscreen — means to hide the chrome with
    /// it, and a row of tabs over a fullscreen video is exactly the kind of thing that rule exists
    /// to stop.
    /// </remarks>
    private void ApplyTabBarVisibility()
    {
        _tabBar.Visibility = ShouldShowTabBar && _header.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Always available: it opens an empty tab, so there is no address it could be missing.
    }

    /// <summary>Re-syncs the strip's contents, selection and visibility with <see cref="_tabs"/>.</summary>
    /// <remarks>
    /// The active tab is marked the way Fluent marks a selection rather than the way the bookmark
    /// bar does. A solid accent fill is right for one tinted bookmark in a row of plain ones; across
    /// a strip of tabs it is a block of colour next to the page it belongs to, and it shouts. So:
    /// a quiet raised surface, a 2px accent underline, and the inactive tabs dimmed — which is both
    /// what a browser does and what makes the underline enough on its own.
    /// </remarks>
    private void RefreshTabBar()
    {
        var surface = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        var clear = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];

        // Every open, close and switch comes through here, which makes it the one place that has
        // to record the session — see WebFlyoutWindow.Session.cs. Cheap: the save compares against
        // what is stored and writes nothing when the set has not actually moved.
        SaveSession();

        _tabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            tab.Chip ??= BuildTabChip(tab);

            bool active = ReferenceEquals(tab, _activeTab);
            tab.Chip.Background = active ? surface : clear;

            // Dimming the whole chip takes the favicon with it, which is the point — an inactive
            // tab should read as further away, not merely as differently coloured.
            tab.Chip.Opacity = active ? 1.0 : 0.6;
            if (tab.Indicator != null)
                tab.Indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

            _tabStrip.Children.Add(tab.Chip);
        }

        ApplyTabBarVisibility();
        BringActiveTabIntoView();
    }

    /// <summary>
    /// Scrolls the strip so the tab in front is on screen.
    /// </summary>
    /// <remarks>
    /// <para>Only ever has anything to do once there are more tabs than fit even squeezed down to
    /// <see cref="MinTabWidthDips"/>, but that is exactly the moment a newly opened tab lands past
    /// the right edge, where opening it looks like it did nothing at all.</para>
    /// <para>Deferred to Low priority because the chip it is aiming at has usually just been added
    /// to the strip and has no layout yet, so its offset would read as zero and every switch would
    /// scroll back to the start.</para>
    /// </remarks>
    private void BringActiveTabIntoView()
    {
        var chip = _activeTab?.Chip;
        if (chip == null || _tabScroller == null) return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // The strip is rebuilt freely, so the chip aimed at may have been dropped in between.
            if (_tabScroller == null || !_tabStrip.Children.Contains(chip)) return;

            double viewport = _tabScroller.ViewportWidth;
            if (viewport <= 0 || chip.ActualWidth <= 0) return;

            try
            {
                double left = chip.TransformToVisual(_tabStrip)
                    .TransformPoint(new global::Windows.Foundation.Point(0, 0)).X;
                double right = left + chip.ActualWidth;
                double offset = _tabScroller.HorizontalOffset;

                // The least scroll that puts it fully in view, and nothing at all when it already
                // is: a strip that re-centres itself on every switch is its own kind of noise.
                if (left < offset) offset = left;
                else if (right > offset + viewport) offset = right - viewport;
                else return;

                _tabScroller.ChangeView(Math.Max(0, offset), null, null, disableAnimation: false);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Scrolling the tab strip failed for launcher {Name}", _launcher.Name);
            }
        });
    }

    /// <summary>
    /// Builds one tab's chip: favicon, title and a close button, in the shape a browser uses.
    /// </summary>
    /// <remarks>
    /// Built once per tab and kept on the tab, not rebuilt per refresh — a rebuild would throw away
    /// a decoded favicon and a measured label every time a tab was switched, which is the mistake
    /// the bookmark bar's rebuild signature exists to avoid.
    /// </remarks>
    private Button BuildTabChip(WebTab tab)
    {
        var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };

        // U+E774 is Segoe Fluent's Globe. A page with no icon yet gets it rather than a hole in
        // the row.
        var fallback = new FontIcon
        {
            Glyph = "\uE774",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Both share one fixed 16px slot rather than sitting side by side: an Image with no Source
        // still takes its Width, so a chip showing the placeholder would shrink the moment its
        // favicon arrived and shunt every chip after it along the strip.
        var slot = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        slot.Children.Add(icon);
        slot.Children.Add(fallback);

        // MaxWidth caps how wide a long title may make the chip; it is not what trims it. The
        // trimming comes from the star column below, which hands the label whatever room is left
        // once the panel has decided how wide this chip gets.
        var label = new TextBlock
        {
            Text = TabCaption(tab),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
        };

        // U+E711 is Segoe Fluent's Cancel — the ✕ every browser puts on a tab.
        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),

            // Revealed on hover, the same Opacity + IsHitTestVisible pair the item cards' menu
            // button uses. IsHitTestVisible is the half that matters; without it a click on what
            // looks like empty chip would silently close the tab.
            Opacity = 0,
            IsHitTestVisible = false,
        };
        ToolTipService.SetToolTip(close, "Close tab");
        close.Click += (_, _) => CloseTab(tab);

        // Auto / Star rather than a horizontal StackPanel. A stack gives its children an infinite
        // width in its own direction, so the label never learned how much room it had and
        // TextTrimming did nothing: a long title simply ran off the chip. The star column is what
        // makes a squeezed tab trim instead of overflow.
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(slot, 0);
        Grid.SetColumn(label, 1);
        row.Children.Add(slot);
        row.Children.Add(label);

        // The selection mark: a hairline of accent under the chip, shown only while it is active.
        // Inside the button's content rather than behind it, so it cannot widen the chip or shift
        // the strip when it appears — the same non-reflowing rule the flyout's edit-mode
        // affordances follow.
        var indicator = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Visibility = Visibility.Collapsed,
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        Grid.SetRow(row, 0);
        Grid.SetRow(close, 0);
        Grid.SetRow(indicator, 1);
        content.Children.Add(row);

        // Over the title's right-hand end, not in a column beside it. A reserved column is what a
        // browser gives a comfortable tab, but this strip squeezes: at the floor a column would
        // spend a quarter of the chip on a button that is invisible most of the time, and the
        // titles came out trimmed to three characters to pay for it. Overlaid, the title has the
        // whole chip while nothing is hovering it, and gives the button its room only when there is
        // a button to make room for. See the hover handlers, which is also where the chip's width
        // is pinned so that giving way costs the title and not the strip.
        content.Children.Add(close);
        content.Children.Add(indicator);

        var chip = new Button
        {
            Content = content,
            Tag = tab,
            // Tight, and even: the strip's own gap plus two chips' padding is all that separates one
            // title from the next, so anything generous here reads as the tabs being spaced out.
            Padding = new Thickness(6, 3, 4, 2),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            // Stretch, not the button template's default Center: a squeezed chip is arranged
            // narrower than its content asked for, and centred content keeps its own width and is
            // clipped rather than handing the loss to the label's star column.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
        };
        chip.Click += (_, _) => SwitchToTab(tab);

        // On the chip, not on the close button itself: it has to appear before it can be aimed at.
        //
        // The title gives it room rather than sitting under it, which is the only way a small glyph
        // over a word is legible, but the room comes out of the *title*, never out of the chip.
        // Pinning the chip's width first is what keeps that true: without it the strip re-measures,
        // the squeeze re-runs, and every tab after this one slides as the pointer crosses the row.
        chip.PointerEntered += (_, _) =>
        {
            if (chip.ActualWidth > 0) chip.Width = chip.ActualWidth;

            // Measured rather than assumed. The button is laid out even while it is invisible - an
            // overlay costs no width but is still arranged, so its real width is there to read,
            // and a constant here would be wrong at any font scale but this one.
            double slot = close.ActualWidth > 0 ? close.ActualWidth : 22;
            label.Margin = new Thickness(0, 0, slot + 2, 0);

            close.Opacity = 1;
            close.IsHitTestVisible = true;
        };
        chip.PointerExited += (_, _) =>
        {
            close.Opacity = 0;
            close.IsHitTestVisible = false;

            label.Margin = new Thickness(0);
            chip.ClearValue(FrameworkElement.WidthProperty);
        };

        // Reorderable, as every browser's tabs are. A press that does not move still raises Click,
        // so switching tabs is untouched; the text payload means a tab can also be dragged out to
        // anything that takes a URL.
        chip.CanDrag = true;
        WireStripDragSource(chip);
        chip.DragStarting += (_, e) =>
        {
            _draggingTab = tab;
            _isStripDragging = true;

            string url = "";
            try { url = tab.View.CoreWebView2?.Source ?? tab.NavigatedUrl; }
            catch (Exception ex) { Logger.Debug(ex, "Reading a dragged tab's address failed"); }

            // A drag with no payload is refused outright.
            e.Data.SetText(url);
            e.Data.RequestedOperation =
                global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move |
                global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        };
        chip.DropCompleted += (_, _) =>
        {
            _draggingTab = null;
            _isStripDragging = false;
            HideTabDropCaret();
        };

        // Middle-click closes, as it does in every browser. The window-move handler on the strip
        // only starts on the left button, so this cannot be mistaken for a drag.
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed) return;
            e.Handled = true;
            CloseTab(tab);
        };

        tab.Chip = chip;
        tab.Label = label;
        tab.Icon = icon;
        tab.IconFallback = fallback;
        tab.Indicator = indicator;
        UpdateTabChip(tab);
        return chip;
    }

    private void TabStrip_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingTab == null) return;

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.Handled = true;

        if (e.DragUIOverride is { } overrides)
        {
            overrides.IsCaptionVisible = false;
            overrides.IsGlyphVisible = false;
        }

        ShowTabDropCaretAt(TabDropIndexFor(e.GetPosition(_tabStrip).X));
    }

    private void TabStrip_Drop(object sender, DragEventArgs e)
    {
        var dragged = _draggingTab;
        HideTabDropCaret();
        if (dragged == null) return;

        e.Handled = true;
        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int from = _tabs.IndexOf(dragged);
        if (from < 0) return;

        // No adjustment for the gap the dragged item leaves behind: the index already counts only
        // the *others*, which is exactly the list that exists after it is removed — and both Move
        // and Remove-then-Insert take their destination in those post-removal terms. Subtracting one
        // as well double-counted the removal, so every drop to the right landed back where it
        // started and appeared to do nothing, while drops to the left (where the subtraction never
        // applied) worked.
        int to = TabDropIndexFor(e.GetPosition(_tabStrip).X);
        if (to == from) return;

        _tabs.RemoveAt(from);
        _tabs.Insert(to, dragged);

        // Rebuilds the strip from _tabs, and records the new order as the session.
        RefreshTabBar();
    }

    /// <summary>Where in the strip, counting only the tabs not being dragged, x falls.</summary>
    private int TabDropIndexFor(double x)
    {
        int index = 0;
        foreach (var child in _tabStrip.Children)
        {
            if (child is not Button { Tag: WebTab tab } chip) continue;
            if (ReferenceEquals(tab, _draggingTab)) continue;

            var origin = chip.TransformToVisual(_tabStrip)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));

            if (x <= origin.X + chip.ActualWidth / 2) break;
            index++;
        }

        return index;
    }

    private void ShowTabDropCaretAt(int index)
    {
        if (_tabBarOverlay == null || _tabDropCaret == null) return;

        var others = _tabStrip.Children
            .OfType<Button>()
            .Where(c => c.Tag is WebTab tab && !ReferenceEquals(tab, _draggingTab))
            .ToList();

        if (others.Count == 0)
        {
            HideTabDropCaret();
            return;
        }

        bool atEnd = index >= others.Count;
        var anchor = atEnd ? others[^1] : others[index];
        var origin = anchor.TransformToVisual(_tabBarOverlay)
            .TransformPoint(new global::Windows.Foundation.Point(0, 0));

        Canvas.SetLeft(_tabDropCaret, Math.Max(0, (atEnd ? origin.X + anchor.ActualWidth : origin.X) - 1));
        Canvas.SetTop(_tabDropCaret, origin.Y);
        _tabDropCaret.Height = Math.Max(16, anchor.ActualHeight);
        _tabDropCaret.Visibility = Visibility.Visible;
    }

    private void HideTabDropCaret()
    {
        if (_tabDropCaret != null) _tabDropCaret.Visibility = Visibility.Collapsed;
    }

    /// <summary>What the chip should say: the page's title, falling back to its host.</summary>
    private string TabCaption(WebTab tab)
    {
        var core = tab.View.CoreWebView2;

        string title = "";
        try { title = core?.DocumentTitle ?? ""; }
        catch (Exception ex) { Logger.Debug(ex, "Reading the document title failed for launcher {Name}", _launcher.Name); }

        if (!string.IsNullOrWhiteSpace(title)) return title;

        string source = "";
        try { source = core?.Source ?? tab.NavigatedUrl; }
        catch (Exception ex) { Logger.Debug(ex, "Reading the source failed for launcher {Name}", _launcher.Name); }

        if (string.IsNullOrWhiteSpace(source) || source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return "New tab";

        try { return new Uri(source).Host; }
        catch (UriFormatException) { return source; }
    }

    private void UpdateTabChip(WebTab tab)
    {
        if (tab.Label == null) return;
        string caption = TabCaption(tab);
        tab.Label.Text = caption;
        if (tab.Chip != null) ToolTipService.SetToolTip(tab.Chip, caption);
    }

    /// <summary>
    /// Puts the page's declared icon on the chip.
    /// </summary>
    /// <remarks>
    /// Read into a byte array and handed to the bitmap through a stream of our own, rather than
    /// pointing the bitmap at WebView2's stream: nothing that came out of the browser is left for
    /// the finalizer to release. See the notification bridge for what that costs when it goes
    /// wrong. Unlike the launcher's and the bookmark's icons this is never written to disk — it
    /// lives exactly as long as the tab does.
    /// </remarks>
    private async Task UpdateTabIconAsync(CoreWebView2 core, WebTab tab)
    {
        if (tab.Icon == null) return;

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

            using var memory = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(memory.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(buffer);
                await writer.StoreAsync();
            }

            if (tab.Icon == null) return;   // closed while the bytes were being read
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(memory);
            tab.Icon.Source = bitmap;
            if (tab.IconFallback != null) tab.IconFallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading the tab icon failed for launcher {Name}", _launcher.Name);
        }
    }

    // ── Switching ───────────────────────────────────────────────────

    /// <summary>The home tab for <paramref name="key"/>, or null when it has never been opened.</summary>
    private WebTab? FindHomeTab(string key) =>
        _tabs.FirstOrDefault(t => string.Equals(t.HomeKey, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Brings a tab to the front. A chip click, so it must tolerate a stale chip.</summary>
    private void SwitchToTab(WebTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        if (ReferenceEquals(tab, _activeTab)) return;
        ActivateTab(tab);
    }

    /// <summary>
    /// Makes one tab the visible one.
    /// </summary>
    /// <remarks>
    /// <para>Switching is a visibility change and nothing else. The outgoing tab is collapsed — so
    /// it stops rendering, exactly as a background tab in a browser — but deliberately <b>not</b>
    /// suspended: suspending freezes its scripts, and the whole reason the others are kept loaded
    /// is that they go on receiving. The launcher's hidden policy still governs all of them
    /// together once the flyout itself goes away.</para>
    /// <para>Everything downstream reads <c>_webView</c>, so pointing it at the new tab is what
    /// makes the header, the address bar, zoom and focus follow the switch.</para>
    /// </remarks>
    private void ActivateTab(WebTab tab)
    {
        foreach (var other in _tabs)
            other.View.Visibility = ReferenceEquals(other, tab) ? Visibility.Visible : Visibility.Collapsed;

        _activeTab = tab;
        _webView = tab.View;
        _revealOnNavigationCompleted = false;

        ResumeWebView();
        ApplyZoom();
        UpdateNavigationButtons();

        // Blankness is per tab, and an empty tab forces the address bar on whatever the launcher
        // says, so switching between an empty tab and a loaded one has to re-ask.
        ApplyAddressBarVisibility();
        RefreshTabBar();

        if (tab.View.CoreWebView2 != null)
        {
            HideStatus();

            // Switching back to an empty tab has to put its own notice back: HideStatus alone
            // leaves the window showing nothing at all, since an unnavigated browser paints
            // nothing.
            ShowEmptyTabStatus(tab);
        }

        RestorePageFocus();
    }

    // ── Opening ─────────────────────────────────────────────────────

    /// <summary>
    /// The "+" opens an empty tab, not the launcher's page.
    /// </summary>
    /// <remarks>
    /// It used to open the launcher's own address, which is what its tray icon is already for —
    /// asking for a new tab is asking for somewhere to go, not for another copy of where you are.
    /// An empty tab shows the address bar whatever the launcher's setting says (see
    /// <c>ApplyAddressBarVisibility</c>) and takes the caret, so it is ready to be typed into.
    /// </remarks>
    private void OpenNewTab() => _ = OpenBlankTabAsync();

    private async Task OpenBlankTabAsync()
    {
        var tab = await CreateTabAsync(homeKey: null, navigateTo: null);
        if (tab == null) return;

        // The address bar is the whole content of an empty tab, so put the caret in it.
        ApplyAddressBarVisibility();
        FocusAddressBar();
    }



    /// <summary>
    /// Opens an address in a tab of its own.
    /// </summary>
    /// <remarks>
    /// <para><paramref name="background"/> follows the browser convention: a middle-click, a
    /// Shift/Ctrl-click and <b>Open in new tab</b> leave you where you are, while the "+" and a
    /// plain <c>target="_blank"</c> come forward.</para>
    /// <para>For a link in the page the gesture is not on the event — <c>NewWindowRequested</c>
    /// carries no modifier or button state — so it is reported separately by the page and paired up
    /// by time; see <see cref="WantsBackgroundWindow"/>.</para>
    /// </remarks>
    private async Task OpenLinkTabAsync(string url, bool background = false)
    {
        var tab = await CreateTabAsync(homeKey: null, navigateTo: url, background: background);
        if (tab == null) OpenExternally(url);
    }

    /// <summary>
    /// Answers a page asking for a new window.
    /// </summary>
    /// <remarks>
    /// <para>The browser's own <see cref="CoreWebView2NewWindowRequestedEventArgs.NewWindow"/> is
    /// used rather than reading the URI and navigating a tab by hand, and the difference is not
    /// cosmetic: handing WebView2 the new browser keeps the opener relationship, so
    /// <c>window.opener.postMessage</c> works and an OAuth sign-in popup can hand its result back
    /// to the page that raised it. Navigating a tab ourselves severs that, and the sign-in simply
    /// never completes.</para>
    /// <para>The request is held open by a deferral, because building a browser is asynchronous and
    /// the answer is the browser. Every path completes it — a request left hanging leaves the page
    /// waiting forever, the same rule the permission prompts follow.</para>
    /// </remarks>
    private void HandleNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Marked handled either way: unhandled, WebView2 opens an unowned browser window floating
        // over the desktop, which is neither of the two things the user could have meant.
        //
        e.Handled = true;

        if (_launcher.WebLinksInBrowser)
        {
            OpenExternally(e.Uri);
            return;
        }

        var deferral = e.GetDeferral();
        _ = AdoptNewWindowAsync(e, WantsBackgroundWindow(e), deferral);
    }

    /// <summary>
    /// Whether this new window should open behind the page that asked for it.
    /// </summary>
    /// <remarks>
    /// <para>Two conditions, and both matter. The page must have just reported the gesture that
    /// means "leave me here" — see <see cref="_backgroundIntentAt"/> — and the request must not be
    /// for a <em>sized</em> window.</para>
    /// <para>That second guard is what protects sign-in. An OAuth popup is a
    /// <c>window.open</c> with width and height, so it arrives with <c>WindowFeatures.HasSize</c>;
    /// opening one behind the page that raised it would leave the user looking at a page waiting on
    /// a window they cannot see. It follows an ordinary left-click and so would fail the first test
    /// anyway — this is the belt to that pair of braces, because getting it wrong is invisible until
    /// someone cannot sign in.</para>
    /// </remarks>
    private bool WantsBackgroundWindow(CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (_backgroundIntentAt == 0) return false;
        if (Environment.TickCount64 - _backgroundIntentAt > BackgroundIntentWindowMs) return false;

        try
        {
            if (e.WindowFeatures is { } features && (features.HasSize || features.HasPosition)) return false;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Reading the window features failed for launcher {Name}", _launcher.Name);
            return false;
        }

        // Consumed, so one middle-click cannot send a second, unrelated window behind as well.
        _backgroundIntentAt = 0;
        return true;
    }

    private async Task AdoptNewWindowAsync(CoreWebView2NewWindowRequestedEventArgs e, bool background,
                                           global::Windows.Foundation.Deferral deferral)
    {
        string uri = e.Uri;
        try
        {
            // No navigateTo: WebView2 navigates the browser it is handed, and doing it here as well
            // would load the page twice.
            var tab = await CreateTabAsync(homeKey: null, navigateTo: null, background: background);
            if (tab?.View.CoreWebView2 is { } core)
            {
                e.NewWindow = core;
                return;
            }

            // A browser that could not be built is still a link the user asked to follow.
            OpenExternally(uri);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Opening {Uri} in a tab failed for launcher {Name}", uri, _launcher.Name);
            OpenExternally(uri);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ── Closing ─────────────────────────────────────────────────────

    /// <summary>
    /// Closes one tab, and decides what is left showing.
    /// </summary>
    /// <remarks>
    /// Closing the last one is the same gesture as closing a browser's last tab: the flyout goes
    /// away. That leaves the launcher with no browser at all, which the next open rebuilds — so
    /// closing the home tab is simply "unload this", and the tray icon brings it straight back.
    /// </remarks>
    private void CloseTab(WebTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;

        _tabs.RemoveAt(index);
        bool wasActive = ReferenceEquals(tab, _activeTab);

        // Before the browser goes, while its address can still be read — Ctrl+Shift+T reopens it.
        RememberClosedTab(tab);

        DestroyTab(tab);

        if (_tabs.Count == 0)
        {
            _activeTab = null;
            _webView = null;
            _revealOnNavigationCompleted = false;
            RefreshTabBar();

            // Closing the last tab is an explicit "I am done with this", so it is not a session
            // to come back to.
            _rememberedUrl = "";
            ClearSession();
            HideFlyout();
            return;
        }

        if (!wasActive)
        {
            RefreshTabBar();
            return;
        }

        // The one that took its place, or the one before it when the last tab was closed — which
        // is where a browser leaves you too.
        ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    /// <summary>Takes one tab's browser and chip down. Never decides what is shown next.</summary>
    private void DestroyTab(WebTab tab)
    {
        if (tab.Chip != null) _tabStrip.Children.Remove(tab.Chip);
        tab.Chip = null;
        tab.Label = null;
        tab.Icon = null;
        tab.IconFallback = null;
        tab.Indicator = null;

        try
        {
            _contentHost.Children.Remove(tab.View);
            tab.View.Close();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Closing a tab failed for launcher {Name}", _launcher.Name);
        }
    }

    /// <summary>Closes every tab. Used by the unload path, which takes the whole launcher down.</summary>
    private void CloseAllTabs()
    {
        foreach (var tab in _tabs)
            DestroyTab(tab);

        _tabs.Clear();
        _activeTab = null;
        _tabStrip.Children.Clear();
    }

    /// <summary>
    /// Every live browser this launcher owns.
    /// </summary>
    /// <remarks>
    /// Every browser is a tab, including the launcher's own — which is what makes this the single
    /// answer, and what removed the double-<c>Close</c> trap the earlier bookmark-tabs code had to
    /// guard against (a second <c>Close</c> on a dead control takes the process rather than
    /// throwing).
    /// </remarks>
    private IEnumerable<WebView2> LiveWebViews() => _tabs.Select(t => t.View);

    /// <summary>True when <paramref name="core"/> is the browser currently on screen.</summary>
    /// <remarks>
    /// Every handler that writes shared chrome — the status overlay, the back/forward buttons, the
    /// address box, fullscreen — has to ask this first. A background tab navigates on its own
    /// (a chat app pushing history, a dashboard refreshing), and without the check it would drive
    /// the header of a page the user is not looking at.
    /// </remarks>
    private bool IsActiveCore(CoreWebView2 core) => ReferenceEquals(_webView?.CoreWebView2, core);

    /// <summary>The tab a browser belongs to, or null once it has been closed.</summary>
    private WebTab? TabFor(CoreWebView2 core) =>
        _tabs.FirstOrDefault(t => ReferenceEquals(t.View.CoreWebView2, core));
}
