// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// The bookmark bar: the strip a bar-mode launcher opens as, and every way a bookmark is made,
/// changed or thrown away from inside the flyout itself.
/// </summary>
/// <remarks>
/// <para>Launcher settings can still do all of this, and for a launcher being set up that is the
/// right place — a form listing every bookmark at once. But the moments that actually produce a
/// bookmark happen <em>here</em>: a page is on screen and worth keeping, a name reads badly in the
/// bar, two bookmarks are in the wrong order. Each of those is a glance away from the thing it
/// changes, and walking to a settings window and back to make the judgement is the whole cost.</para>
/// <para>So the bar carries three affordances, and none of them is a second settings window:</para>
/// <list type="bullet">
///   <item>a <b>star in the address bar</b>, which adds or removes whatever the address bar is
///   showing — the same gesture, and the same two glyphs, every browser uses. The More menu carries
///   it as well, because the address bar is off by default and the page still has to be
///   bookmarkable without turning it on.</item>
///   <item>a <b>context menu</b> on each bookmark — rename, re-address, copy, default, remove.</item>
///   <item><b>drag to reorder</b>, which is the only one of the three with no equivalent worth
///   using: the settings window's move-up/move-down buttons are a poor way to express "third, not
///   first" about a row you are looking at.</item>
/// </list>
/// <para>There is one kind of web launcher: a list of bookmarks whose first entry is the address
/// it opens (<see cref="Launcher.WebAddress"/>). "A single address" is a launcher with one
/// bookmark and no bar; a second bookmark — added in settings, or with the star — is what makes
/// the bar appear. There is no setting for it, because adding the second page and wanting somewhere
/// to click it are the same act.</para>
/// <para><b>The bar itself holds no state.</b> It opens pages; it does not own the one that is
/// open, mark it, or close it. A click navigates the tab in front, a middle-click or a
/// Shift/Ctrl-click opens a new one, and clicking the bookmark for the page you are already on
/// simply loads it again — all of which is what a browser's bookmarks bar does. What is showing is
/// the tab's business, and <see cref="_rememberedUrl"/> is only asked once every tab is gone.</para>
/// <para>Every edit here writes the launcher, saves, and tells the sync service — see
/// <see cref="PersistBookmarks"/>, which is the single place that does all three.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>Height of the caret that marks where a dragged bookmark will land.</summary>
    private const double DropCaretHeightDips = 20;

    /// <summary>
    /// The per-bookmark <see cref="INotifyPropertyChanged"/> handlers the current buttons hold.
    /// </summary>
    /// <remarks>
    /// Tracked so they can be detached. A <see cref="WebBookmark"/> lives in settings and outlives
    /// every bar built from it, so a handler left attached pins the button's <c>Image</c> and
    /// <c>TextBlock</c> — and the bitmap it decoded — for the life of the app. That cost little
    /// while the bar was rebuilt once per open; adding, removing and reordering from the bar itself
    /// rebuilds it far more often, which is exactly how the item flyout's container leak grew.
    /// </remarks>
    private readonly List<(WebBookmark Bookmark, PropertyChangedEventHandler Handler)> _bookmarkHandlers = [];

    /// <summary>The bookmark being dragged along the bar, or null.</summary>
    private WebBookmark? _draggingBookmark;

    /// <summary>
    /// True while a bookmark or a tab is being dragged, which pins the flyout open.
    /// </summary>
    /// <remarks>
    /// Both strips carry an address as text, so a drag can end in another application — and that
    /// deactivates this window. Without this the flyout would dismiss itself mid-gesture and take
    /// the strip being reordered with it, exactly as a resize or a window move would. One flag for
    /// both, because the guards only ever ask "is a drag in progress".
    /// </remarks>
    private bool _isStripDragging;

    private Button? _bookmarkStar;
    private Border? _dropCaret;
    private Canvas? _barOverlay;

    /// <summary>The chevron at the end of the bar, holding whatever did not fit on it.</summary>
    private Button? _bookmarkOverflow;

    // ── Bookmark bar ────────────────────────────────────────────────

    /// <summary>True when this launcher's bar has something to show and is allowed to show it.</summary>
    private bool IsBarMode => _launcher.ShowsBookmarkBar;

    /// <summary>
    /// Identifies the bookmark set the bar was last built from, so it is only rebuilt when the
    /// bookmarks actually change. Order is part of it, so a drag invalidates it as an edit does.
    /// </summary>
    private string BookmarkBarSignature() =>
        _launcher.WebBookmarkIconsOnly + "|" +
        string.Join("", _launcher.WebBookmarks.Select(b => $"{b.Name}{b.Url}{b.IconPath}{b.IconsOnly}"));

    /// <summary>
    /// Rebuilds the bar from the launcher's bookmarks, in the shape a browser uses: a small icon
    /// with its label beside it, centred while they fit and packed left once they do not, with
    /// whatever is left over reached through the chevron at the end.
    /// </summary>
    private void RebuildBookmarkBar(bool force = false)
    {
        if (!IsBarMode)
        {
            _bookmarkBar.Visibility = Visibility.Collapsed;
            ClearBookmarkButtons();
            _barSignature = "";
            return;
        }

        // Icons-only is part of the signature: it changes what every button contains, so
        // toggling it has to rebuild rather than hand back the buttons built for the other mode.
        string signature = BookmarkBarSignature();
        if (!force && signature == _barSignature && _bookmarkStrip.Children.Count > 0)
        {
            // Same bookmarks as last time — the buttons are already built, laid out and decoded.
            _bookmarkBar.Visibility = Visibility.Visible;
            return;
        }

        _barSignature = signature;
        ClearBookmarkButtons();

        foreach (var bookmark in _launcher.WebBookmarks)
            _bookmarkStrip.Children.Add(BuildBookmarkButton(bookmark));

        _bookmarkBar.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Whether this bookmark shows as its icon alone.
    /// </summary>
    /// <remarks>
    /// The launcher-wide setting wins: it is the blunter instrument, and a bookmark still showing
    /// its label under "icons only" would be that setting quietly failing. The per-bookmark flag
    /// only ever adds to what is collapsed, never subtracts.
    /// </remarks>
    private bool ShowsIconOnly(WebBookmark bookmark) =>
        _launcher.WebBookmarkIconsOnly || bookmark.IconsOnly;

    /// <summary>Empties the strip, detaching what its buttons were listening to first.</summary>
    private void ClearBookmarkButtons()
    {
        foreach (var (bookmark, handler) in _bookmarkHandlers)
            bookmark.PropertyChanged -= handler;

        _bookmarkHandlers.Clear();
        _bookmarkStrip.Children.Clear();
    }

    private Button BuildBookmarkButton(WebBookmark bookmark)
    {
        var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(bookmark.IconPath) && File.Exists(bookmark.IconPath))
            icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(bookmark.IconPath));

        // A page with no icon yet gets a globe rather than a hole in the row — U+E774, Segoe
        // Fluent's Globe.
        var fallback = new FontIcon
        {
            Glyph = "",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = icon.Source == null ? Visibility.Visible : Visibility.Collapsed,
        };

        string caption = string.IsNullOrWhiteSpace(bookmark.Name) ? bookmark.Url : bookmark.Name;

        var label = new TextBlock
        {
            Text = caption,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
            // Icons-only hides the label rather than omitting it, so the name is still there to
            // become a tooltip and the button rebuilds the same way either way.
            Visibility = ShowsIconOnly(bookmark) ? Visibility.Collapsed : Visibility.Visible,
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
        // With the labels hidden the name is the only thing identifying the button, so it leads the
        // tooltip; with them shown the name is already on screen and the address is the useful part.
        ToolTipService.SetToolTip(button,
            ShowsIconOnly(bookmark) ? $"{caption}\n{bookmark.Url}" : bookmark.Url);
        button.Click += (_, _) => OpenBookmark(bookmark, newTab: false);

        // Middle-click and Shift/Ctrl-click open a new tab, as they do on any link in any browser.
        // Handled on the press rather than in Click: marking it there is what stops the button
        // taking the plain-click path as well, and a middle button never raises Click at all.
        button.PointerPressed += (_, e) =>
        {
            var properties = e.GetCurrentPoint(button).Properties;
            bool modified = properties.IsLeftButtonPressed &&
                (e.KeyModifiers.HasFlag(global::Windows.System.VirtualKeyModifiers.Shift) ||
                 e.KeyModifiers.HasFlag(global::Windows.System.VirtualKeyModifiers.Control));

            if (!properties.IsMiddleButtonPressed && !modified) return;

            e.Handled = true;
            OpenBookmark(bookmark, newTab: true);
        };

        // Right-click carries everything that is not "open this", which is the same idiom the item
        // cards use — and the reason none of it needs a control of its own in a 34px strip.
        button.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowBookmarkMenu(bookmark, button);
        };

        WireBookmarkDrag(button, bookmark);

        // The icon arrives after the bookmark does — first from a favicon fetch, later replaced by
        // whatever the signed-in page declares — so the row keeps itself current.
        void OnBookmarkPropertyChanged(object? _, PropertyChangedEventArgs e)
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
        }

        bookmark.PropertyChanged += OnBookmarkPropertyChanged;
        _bookmarkHandlers.Add((bookmark, OnBookmarkPropertyChanged));

        return button;
    }

    /// <summary>
    /// Opens a bookmark, the way a browser's bookmarks bar does.
    /// </summary>
    /// <remarks>
    /// <para>Plainly, it loads in the tab in front — <em>whichever</em> tab that is, including one
    /// opened from a link, because that is the tab the user is looking at. It never closes anything
    /// and never toggles: clicking the bookmark for the page already showing loads it again, which
    /// is what a browser does and is a useful way to get back to the top of a dashboard.</para>
    /// <para>The one exception keeps the existing page rather than replacing it: a middle-click, a
    /// Shift- or Ctrl-click, or <b>Open in new tab</b> puts the bookmark in a tab of its own,
    /// <em>behind</em> the one in front. That
    /// is now the <em>only</em> way this launcher grows a second browser from the bar. A setting
    /// used to give every bookmark one automatically, which meant a plain click could not mean
    /// "here" — the gesture and the cost are the user's to choose, one page at a time.</para>
    /// </remarks>
    private void OpenBookmark(WebBookmark bookmark, bool newTab)
    {
        string url = NormalizeUrl(bookmark.Url);
        if (string.IsNullOrEmpty(url)) return;

        // Behind whatever is on screen, as every browser does for these three gestures: asking for
        // a second tab is not asking to leave the first.
        if (newTab)
        {
            _ = OpenLinkTabAsync(url, background: true);
            return;
        }

        // The launcher reopens where it was last sent, and this is one of the two gestures that
        // says so — see _rememberedUrl. Written here rather than inside Navigate, so that "the
        // user has not steered this launcher anywhere" stays a state the flyout can recognise.
        _rememberedUrl = url;

        // No browser at all — the flyout has been unloaded, or was never loaded. Building the
        // launcher's own tab on this address is the navigation.
        if (_activeTab == null)
        {
            _ = CreateTabAsync(PrimaryTabKey, url);
            return;
        }

        // Still starting: its own creation navigates it, and a second Navigate here would race it.
        if (_activeTab.View.CoreWebView2 == null) return;

        Navigate(url);
    }

    // ── The star ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the address bar's add/remove-bookmark star.
    /// </summary>
    /// <remarks>
    /// It acts on <em>what the address bar shows</em> rather than on the live page, and the two
    /// only differ while the box is being typed into — at which point what is on screen is no
    /// longer what the user means. Typing an address and starring it without visiting it first is
    /// then the same gesture as starring the page you are on, which is the reading that needs no
    /// rule to explain it.
    /// </remarks>
    private Button BuildBookmarkStar()
    {
        // U+E734 is Segoe Fluent's FavoriteStar and U+E735 its filled twin — the outline/solid
        // pair every browser uses for "not saved" and "saved".
        var star = BuildHeaderButton("", "Add to the bookmarks bar", (_, _) => ToggleBookmarkForAddress());
        star.Margin = new Thickness(4, 0, 0, 0);
        return star;
    }

    /// <summary>Shows the star's current answer: is this address in the bar, and can it be?</summary>
    private void UpdateBookmarkStar()
    {
        if (_bookmarkStar is not { Content: FontIcon glyph }) return;

        // Always offered. Every web launcher is a list of bookmarks now, so there is always
        // somewhere for this to write — a launcher showing "one address" is one with a single
        // bookmark, and starring a second page is how it stops being that.
        string url = CurrentAddressUrl();
        bool saved = FindBookmark(url) != null;

        _bookmarkStar.IsEnabled = !string.IsNullOrEmpty(url);
        glyph.Glyph = saved ? "" : "";

        // Filled *and* accented: at 12px the difference between the two glyphs is a few pixels of
        // interior, which is not enough on its own to answer "is this one already in the bar".
        //
        // ClearValue rather than assigning null on the way back. A null local value is not "no
        // local value" — it is a brush of none, which paints nothing, and the star would simply
        // vanish the moment its bookmark was removed.
        if (saved) glyph.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else glyph.ClearValue(IconElement.ForegroundProperty);

        ToolTipService.SetToolTip(_bookmarkStar,
            saved ? "Remove from the bookmarks bar" : "Add to the bookmarks bar");
    }

    /// <summary>The address the star acts on — whatever the box is showing.</summary>
    private string CurrentAddressUrl() => NormalizeUrl(_addressBox.Text);

    /// <summary>The bookmark for an address, or null when the bar does not hold it.</summary>
    private WebBookmark? FindBookmark(string url) =>
        string.IsNullOrEmpty(url)
            ? null
            : _launcher.WebBookmarks.FirstOrDefault(
                b => string.Equals(NormalizeUrl(b.Url), url, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the address the bar shows is already bookmarked. Read by the More menu.</summary>
    private bool IsCurrentAddressBookmarked() => FindBookmark(CurrentAddressUrl()) != null;

    /// <summary>Adds the current address to the bar, or takes it out again if it is already there.</summary>
    private void ToggleBookmarkForAddress()
    {
        string url = CurrentAddressUrl();
        if (string.IsNullOrEmpty(url)) return;

        if (FindBookmark(url) is { } existing)
        {
            RemoveBookmark(existing);
            return;
        }

        AddBookmark(url);
    }

    /// <summary>
    /// Adds one bookmark for an address, and gives it a name and an icon.
    /// </summary>
    /// <remarks>
    /// The page itself is asked for both when it is the page on screen: its title is what the user
    /// would call it, and its declared icon is the one a signed-in dashboard actually shows —
    /// unlike the unauthenticated favicon fetch, which is all a bookmark typed into settings can
    /// have. An address typed but not visited falls back to that fetch, and to its host for a name.
    /// </remarks>
    private void AddBookmark(string url)
    {
        var core = _webView?.CoreWebView2;
        bool onThisPage = core != null &&
            string.Equals(NormalizeUrl(core.Source), url, StringComparison.OrdinalIgnoreCase);

        string name = "";
        if (onThisPage)
        {
            try { name = core!.DocumentTitle ?? ""; }
            catch (Exception ex) { Logger.Debug(ex, "Reading the document title failed for launcher {Name}", _launcher.Name); }
        }

        if (string.IsNullOrWhiteSpace(name)) name = HostOf(url);

        var bookmark = new WebBookmark(name, url);
        _launcher.WebBookmarks.Add(bookmark);

        // Appended, never inserted. The first bookmark is the launcher's address, and starring the
        // page you happen to be on is not a request to change what the tray icon opens — dragging
        // it to the front is, which is a gesture with the consequence visible in it.
        PersistBookmarks();

        if (onThisPage) _ = AdoptBookmarkIconAsync(core!, bookmark);
        else _ = FetchBookmarkIconAsync(_launcher, bookmark);
    }

    /// <summary>
    /// Takes one bookmark out of the bar.
    /// </summary>
    /// <remarks>
    /// It does not touch what is on screen. Deleting a bookmark for the page you are reading is a
    /// change to the bar, not an instruction to close the page — which is both what a browser does
    /// and what falls out of the bar holding no state about which page is showing. Removing the
    /// <em>first</em> one does change where the launcher opens next time, because that is what
    /// first means.
    /// </remarks>
    private void RemoveBookmark(WebBookmark bookmark)
    {
        _launcher.WebBookmarks.Remove(bookmark);
        PersistBookmarks();
    }

    // ── Overflow ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the chevron that holds the bookmarks the bar could not fit.
    /// </summary>
    /// <remarks>
    /// U+E76C is Segoe Fluent's ChevronRight, which is the glyph every browser uses for this and
    /// reads as "there is more this way" rather than as a menu of its own.
    /// </remarks>
    private Button BuildBookmarkOverflowButton()
    {
        var button = BuildHeaderButton("", "More bookmarks", (_, _) => ShowBookmarkOverflowMenu());
        button.Visibility = Visibility.Collapsed;
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    /// <summary>Shows or hides the chevron for what the strip currently fits.</summary>
    /// <remarks>
    /// Called from the panel's layout pass, so it does no more than set a visibility, which WinUI
    /// folds into the next pass rather than running one from inside this one.
    /// </remarks>
    private void UpdateBookmarkOverflowButton()
    {
        if (_bookmarkOverflow == null) return;

        bool any = _bookmarkStrip.VisibleCount < _bookmarkStrip.Children.Count;
        _bookmarkOverflow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Drops the bookmarks that did not fit into a menu.
    /// </summary>
    /// <remarks>
    /// <para>Every gesture the bar answers, answered here too: a click loads the bookmark in the tab
    /// in front, a middle-click or a Shift/Ctrl-click puts it in a tab of its own, and a right-click
    /// opens its actions.</para>
    /// <para><b>Right-click expands in place rather than opening a second menu.</b> WinUI keeps only
    /// one menu up at a time, so a bookmark's own <c>MenuFlyout</c> light-dismissed this one and the
    /// list being read vanished at the moment the user asked to act on a row of it. The actions are
    /// inserted into this menu instead, under the row they belong to. See
    /// <see cref="FillBookmarkOverflowMenu"/>.</para>
    /// <para>Built fresh on each open, because which bookmarks are in here is a property of the
    /// window's current width rather than of the launcher.</para>
    /// </remarks>
    private void ShowBookmarkOverflowMenu()
    {
        if (_bookmarkOverflow == null) return;

        var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.Top,
            ShouldConstrainToRootBounds = false,
        };

        if (!FillBookmarkOverflowMenu(menu, expanded: null)) return;

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        menu.ShowAt(_bookmarkOverflow);
    }

    /// <summary>
    /// Puts the overflowed bookmarks into <paramref name="menu"/>, and wires the expansion.
    /// </summary>
    /// <returns>False when nothing overflowed, so there is no menu worth showing.</returns>
    /// <remarks>
    /// <para>Right-clicking a row inserts that bookmark's actions directly under it and
    /// right-clicking it again takes them out, so the gesture is its own undo and the list is never
    /// lost. A second <c>MenuFlyout</c> cannot be used for this: WinUI keeps only one menu up at a
    /// time, so the bookmark's own menu light-dismissed the overflow and the list being read
    /// vanished at the moment the user asked to act on a row of it.</para>
    /// <para><b>Insert and Remove, never Clear.</b> Emptying an open flyout's <c>Items</c> takes the
    /// presenter down with it: the menu closed and the right-click landed on the page underneath,
    /// which answered with WebView2's own Back / Refresh / Inspect menu. Mutating around the rows
    /// that stay leaves the popup alive and simply re-measures it.</para>
    /// </remarks>
    private bool FillBookmarkOverflowMenu(MenuFlyout menu, WebBookmark? expanded)
    {
        var inserted = new List<MenuFlyoutItemBase>();
        WebBookmark? open = expanded;

        void Collapse()
        {
            foreach (var item in inserted)
                menu.Items.Remove(item);

            inserted.Clear();
            open = null;
        }

        void Expand(WebBookmark bookmark, MenuFlyoutItem row)
        {
            Collapse();

            int at = menu.Items.IndexOf(row) + 1;
            if (at <= 0) return;

            // Bracketed and indented, so the block reads as belonging to the row above rather than
            // as more bookmarks. Both are needed: without the rules it is a wall of rows, and
            // without the indent the actions line up with the bookmark names exactly and the menu
            // looks like it simply grew eleven more entries.
            //
            // The dividers inside keep the grouping the bar's own menu has, because it is the same
            // menu and a user who knows where Remove sits should not have to look for it.
            void Insert(MenuFlyoutItemBase row)
            {
                menu.Items.Insert(at++, row);
                inserted.Add(row);
            }

            Insert(new MenuFlyoutSeparator());

            foreach (var action in BuildBookmarkMenuItems(bookmark, separators: true))
            {
                if (action is not MenuFlyoutSeparator)
                    action.Margin = new Thickness(NestedMenuIndentDips, 0, 0, 0);

                Insert(action);
            }

            Insert(new MenuFlyoutSeparator());

            open = bookmark;
        }

        for (int i = _bookmarkStrip.VisibleCount; i < _bookmarkStrip.Children.Count; i++)
        {
            if (_bookmarkStrip.Children[i] is not Button { Tag: WebBookmark bookmark }) continue;

            var captured = bookmark;

            var item = new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(captured.Name) ? captured.Url : captured.Name,
            };

            if (!string.IsNullOrEmpty(captured.IconPath) && File.Exists(captured.IconPath))
            {
                item.Icon = new ImageIcon
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(captured.IconPath)),
                };
            }

            item.Click += (_, _) => OpenBookmark(captured, newTab: false);

            // Middle-click and Shift/Ctrl-click open a new tab, as they do on the bar and on any
            // link in any browser. AddHandler with handledEventsToo, not a plain PointerPressed
            // subscription: a MenuFlyoutItem marks the press handled for its own visual states, so
            // an ordinary handler never runs and the gesture did nothing at all. Closing the menu
            // is part of the gesture: the tab is open, and the list has served its purpose.
            item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                var properties = e.GetCurrentPoint(item).Properties;
                bool modified = properties.IsLeftButtonPressed &&
                    (e.KeyModifiers.HasFlag(global::Windows.System.VirtualKeyModifiers.Shift) ||
                     e.KeyModifiers.HasFlag(global::Windows.System.VirtualKeyModifiers.Control));

                if (!properties.IsMiddleButtonPressed && !modified) return;

                e.Handled = true;
                menu.Hide();
                OpenBookmark(captured, newTab: true);
            }), handledEventsToo: true);

            item.ContextRequested += (_, e) =>
            {
                e.Handled = true;

                if (ReferenceEquals(open, captured)) Collapse();
                else Expand(captured, item);
            };

            menu.Items.Add(item);
        }

        return menu.Items.Count > 0;
    }

    // ── Context menu ────────────────────────────────────────────────

    /// <summary>
    /// Opens one bookmark's menu.
    /// </summary>
    /// <remarks>
    /// Same two traps as the header's More menu, and for the same reasons:
    /// <c>ShouldConstrainToRootBounds = false</c>, because the window can be a 34px strip and a menu
    /// constrained to that is clipped exactly as the <c>ContentDialog</c> the item editors had to
    /// stop using was; and <c>_isMenuOpen</c>, because an unconstrained menu is hosted in a popup of
    /// its own, which deactivates the flyout that raised it.
    /// </remarks>
    private void ShowBookmarkMenu(WebBookmark bookmark, FrameworkElement anchor)
    {
        var items = BuildBookmarkMenuItems(bookmark, separators: true);
        if (items.Count == 0) return;

        var menu = new MenuFlyout
        {
            // The bar sits at the foot of the window, which usually sits at the foot of the
            // screen, so a menu below it has nowhere to go.
            Placement = FlyoutPlacementMode.Top,
            ShouldConstrainToRootBounds = false,
        };

        foreach (var item in items)
            menu.Items.Add(item);

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        menu.ShowAt(anchor);
    }

    /// <summary>
    /// How far the overflow menu indents a bookmark's actions under its row.
    /// </summary>
    /// <remarks>
    /// <b>A margin, not padding.</b> A <c>MenuFlyoutPresenter</c> gives every row the same icon
    /// column as soon as one row has an icon, and the text column starts after it whatever the
    /// row's own padding says. So padding was swallowed and the actions came out on exactly the same
    /// left edge as the bookmark names above them, which is the layout that had no visible nesting
    /// at all. A margin moves the whole row, highlight included, and reads as a nested block.
    /// </remarks>
    private const double NestedMenuIndentDips = 20;

    /// <summary>
    /// Everything one bookmark can be asked to do, as menu rows.
    /// </summary>
    /// <param name="separators">
    /// False for the overflow menu's inline expansion, where the actions are already set apart by
    /// their indent and dividers would cut the list of bookmarks into pieces.
    /// </param>
    /// <remarks>
    /// One list, two menus: the bar's own right-click and the overflow menu's expanded row. They are
    /// the same question asked about the same bookmark, and the moment they were built separately
    /// one of them would start missing an action.
    /// </remarks>
    private List<MenuFlyoutItemBase> BuildBookmarkMenuItems(WebBookmark bookmark, bool separators)
    {
        var items = new List<MenuFlyoutItemBase>();

        int index = _launcher.WebBookmarks.IndexOf(bookmark);
        if (index < 0) return items;

        MenuFlyoutItem Item(string text, Action invoke, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => invoke();
            return item;
        }

        void Divide()
        {
            if (separators) items.Add(new MenuFlyoutSeparator());
        }

        // The two ways to open it lead, as they do on a link's own context menu: the menu is
        // reached by right-clicking the thing you wanted to open, so "open it" belongs at the top
        // rather than below five ways to edit it.
        items.Add(Item("Open", () => OpenBookmark(bookmark, newTab: false)));
        items.Add(Item("Open in new tab", () => OpenBookmark(bookmark, newTab: true)));

        Divide();

        items.Add(Item("Rename…", () => _ = RenameBookmarkAsync(bookmark)));
        items.Add(Item("Edit address…", () => _ = EditBookmarkAddressAsync(bookmark)));
        items.Add(Item("Copy address", () => CopyToClipboard(bookmark.Url)));

        // Per bookmark, so one long name can be collapsed without flattening the whole bar. Checked
        // rather than a one-way action, and disabled while still checked when the launcher-wide
        // setting is already collapsing everything, so it reads as "already the case, and not
        // because of this" rather than as an option that does nothing.
        var iconOnly = new ToggleMenuFlyoutItem
        {
            Text = "Icon only",
            IsChecked = ShowsIconOnly(bookmark),
            IsEnabled = !_launcher.WebBookmarkIconsOnly,
        };
        iconOnly.Click += (_, _) =>
        {
            bookmark.IconsOnly = iconOnly.IsChecked;
            PersistBookmarks();
        };
        items.Add(iconOnly);
        items.Add(Item("Open in browser", () => OpenExternally(bookmark.Url)));

        Divide();

        // First place is not decoration: it is the address the launcher opens at, so it is named
        // for what it does rather than for the move that implements it. "Open the launcher here"
        // was the move's own description and read as a third way to open the page, next to the two
        // that actually do.
        items.Add(Item("Set as default page", () => MoveBookmark(bookmark, -index), index > 0));

        // Kept beside the drag rather than replaced by it: a bookmark that does not fit on the bar
        // is in the chevron's menu, and there is nothing there to drag.
        items.Add(Item("Move left", () => MoveBookmark(bookmark, -1), index > 0));
        items.Add(Item("Move right", () => MoveBookmark(bookmark, 1), index < _launcher.WebBookmarks.Count - 1));

        Divide();
        items.Add(Item("Remove", () => RemoveBookmark(bookmark)));

        return items;
    }

    /// <summary>
    /// Opens the bar's own menu, for a right-click that did not land on a bookmark.
    /// </summary>
    /// <remarks>
    /// <para>The gap the star leaves. The star adds the page you are <em>on</em>, which is the
    /// common case and the reason it is a one-click gesture, but it is the only way in, so a
    /// bookmark for a page you are not looking at means going and loading it first, or walking to
    /// launcher settings. Both are the wrong shape for "and one for the wiki, while I am here".</para>
    /// <para>Right-clicking the empty part of a bar is where every browser puts this, so it needs no
    /// affordance of its own, which matters in a strip that is 34px tall and already full.</para>
    /// <para>Two ways in, because there are two ways a user knows an address: they can type it, or
    /// they already bookmarked it in a real browser years ago. The second is the one worth having:
    /// a dashboard URL is miserable to type from memory and is invariably already saved somewhere,
    /// which is the same argument that put the picker in launcher settings.</para>
    /// </remarks>
    private void ShowBookmarkBarMenu(ContextRequestedEventArgs e)
    {
        var menu = new MenuFlyout
        {
            // The bar sits at the foot of the window, which usually sits at the foot of the
            // screen, so a menu below it has nowhere to go.
            Placement = FlyoutPlacementMode.Top,
            ShouldConstrainToRootBounds = false,
        };

        MenuFlyoutItem Item(string text, Action invoke)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => invoke();
            return item;
        }

        menu.Items.Add(Item("Add bookmark…", () => _ = AddBookmarkByAddressAsync()));
        menu.Items.Add(Item("Add from browser…", () => _ = AddBookmarkFromBrowserAsync()));

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        // Where the pointer actually is, not the middle of the bar: the menu is an answer to a
        // right-click, so it belongs where the click was.
        if (e.TryGetPosition(_bookmarkBar, out var point))
            menu.ShowAt(_bookmarkBar, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(_bookmarkBar);
    }

    /// <summary>
    /// Adds a bookmark for a typed address.
    /// </summary>
    /// <remarks>
    /// One field, not two. <see cref="AddBookmark"/> already names a bookmark for its host, and the
    /// bar renames in place from the same menu the user is already in, so asking for a name up
    /// front would be a second field that is usually left to default anyway. It is also exactly
    /// what the star does, which keeps the two ways of adding one from behaving differently.
    /// </remarks>
    private async Task AddBookmarkByAddressAsync()
    {
        string? entered = await RunTextPromptAsync("Add bookmark", "https://…", "", "Add");
        if (string.IsNullOrWhiteSpace(entered)) return;

        string url = NormalizeUrl(entered);
        if (string.IsNullOrEmpty(url)) return;

        // Already in the bar: adding it twice is never what was meant, and silently doing nothing
        // reads as the prompt having failed.
        if (FindBookmark(url) != null)
        {
            ShowNotice("That page is already in the bookmarks bar.");
            return;
        }

        AddBookmark(url);
    }

    /// <summary>Adds a bookmark chosen out of an installed browser's own bookmarks.</summary>
    private async Task AddBookmarkFromBrowserAsync()
    {
        if (!Pages.BookmarkPicker.AnyBrowsersInstalled)
        {
            ShowNotice(Pages.BookmarkPicker.NoBrowsersMessage);
            return;
        }

        var picked = await RunBookmarkPickerAsync();
        if (picked == null) return;

        string url = NormalizeUrl(picked.Url);
        if (string.IsNullOrEmpty(url)) return;

        if (FindBookmark(url) != null)
        {
            ShowNotice("That page is already in the bookmarks bar.");
            return;
        }

        // The browser's own name for it wins over the host AddBookmark would fall back to: the user
        // chose it from a list showing that name, so anything else reads as the wrong bookmark.
        var bookmark = new WebBookmark(
            string.IsNullOrWhiteSpace(picked.Name) ? HostOf(url) : picked.Name, url);
        _launcher.WebBookmarks.Add(bookmark);
        PersistBookmarks();

        _ = FetchBookmarkIconAsync(_launcher, bookmark);
    }

    private async Task RenameBookmarkAsync(WebBookmark bookmark)
    {
        string? renamed = await RunTextPromptAsync("Rename bookmark", "Name", bookmark.Name, "Rename");
        if (renamed == null) return;

        bookmark.Name = string.IsNullOrWhiteSpace(renamed) ? HostOf(bookmark.Url) : renamed.Trim();
        PersistBookmarks();
    }

    /// <summary>
    /// Points one bookmark at a different page.
    /// </summary>
    /// <remarks>
    /// The cached icon is filed under the URL (<see cref="GetBookmarkIconPath"/>) and is now
    /// another site's logo, so it is dropped and re-fetched. <see cref="_rememberedUrl"/> follows
    /// only when it pointed at this bookmark, so a launcher left on this page still reopens on it.
    /// </remarks>
    private async Task EditBookmarkAddressAsync(WebBookmark bookmark)
    {
        string? entered = await RunTextPromptAsync("Edit address", "https://…", bookmark.Url, "Save");
        if (entered == null) return;

        string url = NormalizeUrl(entered);
        if (string.IsNullOrEmpty(url) || string.Equals(url, bookmark.Url, StringComparison.OrdinalIgnoreCase)) return;

        bool namedAfterItsHost = string.Equals(bookmark.Name, HostOf(bookmark.Url), StringComparison.OrdinalIgnoreCase);

        bool wasWhereItOpens = string.Equals(_rememberedUrl, bookmark.Url, StringComparison.OrdinalIgnoreCase);

        bookmark.Url = url;
        bookmark.IconPath = "";
        if (wasWhereItOpens) _rememberedUrl = url;

        // A name the user never chose follows the address; one they typed is theirs and stays.
        if (namedAfterItsHost) bookmark.Name = HostOf(url);

        PersistBookmarks();
        _ = FetchBookmarkIconAsync(_launcher, bookmark);

        // Deliberately nothing about the page on screen. Re-addressing a bookmark changes where it
        // goes next time it is clicked; it is not a request to navigate away from what is up, any
        // more than editing a bookmark in a browser is.
    }

    /// <param name="delta">Places to move it. Negative enough lands it at the front.</param>
    private void MoveBookmark(WebBookmark bookmark, int delta)
    {
        int from = _launcher.WebBookmarks.IndexOf(bookmark);
        if (from < 0) return;

        int to = Math.Clamp(from + delta, 0, _launcher.WebBookmarks.Count - 1);
        if (to == from) return;

        _launcher.WebBookmarks.Move(from, to);
        PersistBookmarks();
    }

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Copying {Text} to the clipboard failed", text);
        }
    }

    // ── Drag to reorder ─────────────────────────────────────────────

    /// <summary>
    /// Makes one bookmark button a drag source.
    /// </summary>
    /// <remarks>
    /// <see cref="UIElement.CanDrag"/> rather than pointer handlers of our own: a press that does
    /// not move still raises <c>Click</c>, so the button goes on opening its page, and the text
    /// payload means a bookmark can also be dragged out to anything that takes a URL.
    /// </remarks>
    private void WireBookmarkDrag(Button button, WebBookmark bookmark)
    {
        button.CanDrag = true;

        // CanDrag alone never fired: a Button captures the pointer for its own click handling, so
        // the drag gesture never reached WinUI. See WebFlyoutWindow.StripDrag.cs.
        WireStripDragSource(button);

        button.DragStarting += (_, e) =>
        {
            _draggingBookmark = bookmark;
            _isStripDragging = true;

            // A drag with no payload is refused outright, so the address is the payload — which is
            // also what makes dropping one into a browser or an editor do the obvious thing.
            e.Data.SetText(bookmark.Url);
            e.Data.RequestedOperation =
                global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move |
                global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        };

        button.DropCompleted += (_, _) =>
        {
            _draggingBookmark = null;
            _isStripDragging = false;
            HideDropCaret();
        };
    }

    /// <summary>Makes the strip the drop target. Called once, as the window is built.</summary>
    private void WireBookmarkStripDrop()
    {
        // On the strip, not on each button: DragOver bubbles, so hovering over a neighbour is
        // answered here — and the buttons are rebuilt whenever the bookmarks change, while this
        // is wired once.
        _bookmarkStrip.AllowDrop = true;
        _bookmarkStrip.DragOver += BookmarkStrip_DragOver;
        _bookmarkStrip.DragLeave += (_, _) => HideDropCaret();
        _bookmarkStrip.Drop += BookmarkStrip_Drop;

        // On the bar rather than the strip, and it is the bar that is hit-testable: the strip has
        // no background of its own, so a right-click beside the last bookmark lands here. A click
        // that did land on a bookmark never reaches this: those mark the event handled, which is
        // what stops a bookmark's own menu and this one both trying to open.
        _bookmarkBar.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowBookmarkBarMenu(e);
        };
    }

    /// <summary>
    /// Marks where the dragged bookmark would land.
    /// </summary>
    /// <remarks>
    /// A caret drawn in an overlay above the bar, not a live reshuffle of the strip. Moving the
    /// buttons as the pointer passes them is the more literal preview and it cannot be done here:
    /// the element that would move is the drag source, and taking it out of the panel and putting
    /// it back unloads it mid-gesture. Drawing the insertion point instead leaves the strip's
    /// layout untouched — which also means the measurements it is computed from never move under
    /// it, so the caret cannot oscillate between two slots.
    /// </remarks>
    private void BookmarkStrip_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingBookmark == null) return;

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.Handled = true;

        // The caret is the whole answer, so the drag's own caption and glyph are noise over a bar
        // this small.
        if (e.DragUIOverride is { } overrides)
        {
            overrides.IsCaptionVisible = false;
            overrides.IsGlyphVisible = false;
        }

        ShowDropCaretAt(DropIndexFor(e.GetPosition(_bookmarkStrip).X));
    }

    private void BookmarkStrip_Drop(object sender, DragEventArgs e)
    {
        var dragged = _draggingBookmark;
        HideDropCaret();
        if (dragged == null) return;

        e.Handled = true;
        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int from = _launcher.WebBookmarks.IndexOf(dragged);
        if (from < 0) return;

        // No adjustment for the gap the dragged item leaves behind: the index already counts only
        // the *others*, which is exactly the list that exists after it is removed — and both Move
        // and Remove-then-Insert take their destination in those post-removal terms. Subtracting one
        // as well double-counted the removal, so every drop to the right landed back where it
        // started and appeared to do nothing, while drops to the left (where the subtraction never
        // applied) worked.
        int to = DropIndexFor(e.GetPosition(_bookmarkStrip).X);
        if (to == from) return;

        _launcher.WebBookmarks.Move(from, to);
        PersistBookmarks();
    }

    /// <summary>Where in the row, counting only the bookmarks not being dragged, x falls.</summary>
    /// <remarks>
    /// Overflowed bookmarks are still children, arranged to nothing (see
    /// <c>Controls.OverflowStripPanel</c>), so they are skipped here by their width rather than
    /// removed from the panel, and a drop past the last visible one lands after it.
    /// </remarks>
    private int DropIndexFor(double x)
    {
        int index = 0;
        foreach (var child in _bookmarkStrip.Children)
        {
            if (child is not Button { Tag: WebBookmark bookmark } button) continue;
            if (ReferenceEquals(bookmark, _draggingBookmark)) continue;
            if (button.ActualWidth <= 0) break;

            var origin = button.TransformToVisual(_bookmarkStrip)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));

            // Laid out left to right, so the first button whose midpoint is past the pointer is
            // the one it lands in front of.
            if (x <= origin.X + button.ActualWidth / 2) break;
            index++;
        }

        return index;
    }

    private void ShowDropCaretAt(int index)
    {
        if (_barOverlay == null || _dropCaret == null) return;

        // On the row only: an overflowed button is arranged to a zero rect, so anchoring the caret
        // on one would draw it at the left edge of the bar with the pointer at the right.
        var others = _bookmarkStrip.Children
            .OfType<Button>()
            .Where(b => b.ActualWidth > 0
                     && b.Tag is WebBookmark bookmark
                     && !ReferenceEquals(bookmark, _draggingBookmark))
            .ToList();

        if (others.Count == 0)
        {
            HideDropCaret();
            return;
        }

        // In front of the button it would land before, or after the last one when it goes on the end.
        bool atEnd = index >= others.Count;
        var anchor = atEnd ? others[^1] : others[index];
        var origin = anchor.TransformToVisual(_barOverlay)
            .TransformPoint(new global::Windows.Foundation.Point(0, 0));

        Canvas.SetLeft(_dropCaret, Math.Max(0, (atEnd ? origin.X + anchor.ActualWidth : origin.X) - 1));
        Canvas.SetTop(_dropCaret, (BookmarkBarHeightDips - DropCaretHeightDips) / 2);
        _dropCaret.Visibility = Visibility.Visible;
    }

    private void HideDropCaret()
    {
        if (_dropCaret != null) _dropCaret.Visibility = Visibility.Collapsed;
    }

    // ── Plumbing ────────────────────────────────────────────────────

    /// <summary>
    /// Writes a bookmark change: settings, sync, and the bar it came from.
    /// </summary>
    /// <remarks>
    /// The save and the sync notification travel together for the reason the More menu's toggles
    /// give — a launcher change saved without telling the sync service is reverted by the next
    /// periodic download. The rebuild and the star follow because every edit that reaches here
    /// changes what one of them should say.
    /// </remarks>
    private void PersistBookmarks()
    {
        SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyLaunchersChanged();

        RebuildBookmarkBar();
        UpdateBookmarkStar();
    }

    /// <summary>
    /// Runs a one-field prompt over the flyout, pinned open for the duration.
    /// </summary>
    /// <remarks>
    /// Same contract as <c>OpenLauncherSettingsAsync</c>: the flyout dismisses on focus loss and
    /// opening a window takes focus away, and it has to drop always-on-top or the prompt opens
    /// behind the window that raised it.
    /// </remarks>
    private async Task<string?> RunTextPromptAsync(string title, string placeholder, string? initial, string accept)
    {
        if (_isModalOpen) return null;

        _isModalOpen = true;
        SetTopmost(false);
        try
        {
            return await TextPromptWindow.ShowAsync(title, placeholder, initial, accept, _hwnd, w => _openModal = w);
        }
        finally
        {
            _isModalOpen = false;
            _openModal = null;

            // Switching this launcher's kind elsewhere can dispose the flyout while a prompt is
            // up, so everything past here has to tolerate the window already being gone.
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                SetTopmost(true);
                RestoreActivation();
            }
        }
    }

    /// <summary>
    /// Runs the browser-bookmark chooser over the flyout, pinned open for the duration.
    /// </summary>
    /// <remarks>
    /// Same contract as <see cref="RunTextPromptAsync"/>, and for the same two reasons: the flyout
    /// dismisses on focus loss and opening a window takes focus away, and it has to drop
    /// always-on-top or the chooser opens behind the window that raised it.
    /// </remarks>
    private async Task<Services.FlatBookmark?> RunBookmarkPickerAsync()
    {
        if (_isModalOpen) return null;

        _isModalOpen = true;
        SetTopmost(false);
        try
        {
            return await BookmarkPickerWindow.ShowAsync(_hwnd, w => _openModal = w);
        }
        finally
        {
            _isModalOpen = false;
            _openModal = null;

            // Switching this launcher's kind elsewhere can dispose the flyout while the chooser is
            // up, so everything past here has to tolerate the window already being gone.
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                SetTopmost(true);
                RestoreActivation();
            }
        }
    }

    /// <summary>Host of a URL, used as a bookmark's name until the page offers a better one.</summary>
    internal static string HostOf(string url)
    {
        try { return new Uri(NormalizeUrl(url)).Host; }
        catch (UriFormatException) { return url; }
    }

    /// <summary>
    /// Fetches a provisional icon for a bookmark, the same unauthenticated way the launcher's own
    /// icon is fetched — good enough for public sites, and replaced by the page's declared icon
    /// once the bookmark has actually been opened with a signed-in browser.
    /// </summary>
    internal static async Task FetchBookmarkIconAsync(Launcher launcher, WebBookmark bookmark)
    {
        try
        {
            string? cached = await Services.FaviconService.FetchAndCacheAsync(bookmark.Url);
            if (string.IsNullOrEmpty(cached) || !File.Exists(cached)) return;

            string dest = GetBookmarkIconPath(launcher.Id, bookmark.Url);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(cached, dest, overwrite: true);

            bookmark.IconPath = dest;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            ApplyLauncherChanges(launcher.Id);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Bookmark icon fetch failed for {Url}", bookmark.Url);
        }
    }
}
