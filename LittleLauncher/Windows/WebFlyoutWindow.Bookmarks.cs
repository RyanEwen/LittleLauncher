// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    /// True while a bookmark is being dragged, which pins the flyout open.
    /// </summary>
    /// <remarks>
    /// The drag carries the bookmark's address as text, so it can end in another application —
    /// and that deactivates this window. Without this the flyout would dismiss itself mid-gesture
    /// and take the bar being reordered with it, exactly as a resize or a window move would.
    /// </remarks>
    private bool _isBookmarkDragging;

    private Button? _bookmarkStar;
    private Border? _dropCaret;
    private Canvas? _barOverlay;

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
    /// with its label beside it, packed left, scrolling horizontally when there are too many.
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
        int index = _launcher.WebBookmarks.IndexOf(bookmark);
        if (index < 0) return;

        var menu = new MenuFlyout
        {
            // The bar sits at the foot of the window, which usually sits at the foot of the
            // screen, so a menu below it has nowhere to go.
            Placement = FlyoutPlacementMode.Top,
            ShouldConstrainToRootBounds = false,
        };

        MenuFlyoutItem Item(string text, Action invoke, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => invoke();
            return item;
        }

        // The two ways to open it lead, as they do on a link's own context menu — the menu is
        // reached by right-clicking the thing you wanted to open, so "open it" belongs at the top
        // rather than below five ways to edit it.
        menu.Items.Add(Item("Open", () => OpenBookmark(bookmark, newTab: false)));
        menu.Items.Add(Item("Open in new tab", () => OpenBookmark(bookmark, newTab: true)));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item("Rename…", () => _ = RenameBookmarkAsync(bookmark)));
        menu.Items.Add(Item("Edit address…", () => _ = EditBookmarkAddressAsync(bookmark)));
        menu.Items.Add(Item("Copy address", () => CopyToClipboard(bookmark.Url)));

        // Per bookmark, so one long name can be collapsed without flattening the whole bar. Checked
        // rather than a one-way action, and disabled — still checked — while the launcher-wide
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
        menu.Items.Add(iconOnly);
        menu.Items.Add(Item("Open in browser", () => OpenExternally(bookmark.Url)));

        menu.Items.Add(new MenuFlyoutSeparator());

        // Kept beside the drag rather than replaced by it: a bar with more bookmarks than fit
        // scrolls, and dragging one past the edge of a scroller is the gesture these two avoid.
        // First place is not decoration — it is the address the launcher opens at — so there is
        // also a way to claim it that does not depend on dragging accurately.
        menu.Items.Add(Item("Open the launcher here", () => MoveBookmark(bookmark, -index), index > 0));
        menu.Items.Add(Item("Move left", () => MoveBookmark(bookmark, -1), index > 0));
        menu.Items.Add(Item("Move right", () => MoveBookmark(bookmark, 1), index < _launcher.WebBookmarks.Count - 1));

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Remove", () => RemoveBookmark(bookmark)));

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        menu.ShowAt(anchor);
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

        button.DragStarting += (_, e) =>
        {
            _draggingBookmark = bookmark;
            _isBookmarkDragging = true;

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
            _isBookmarkDragging = false;
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

        // The index counts only the other bookmarks, so a drop to the right of its own slot has
        // already skipped the gap it is about to leave behind.
        int to = DropIndexFor(e.GetPosition(_bookmarkStrip).X);
        if (to > from) to--;
        if (to == from) return;

        _launcher.WebBookmarks.Move(from, to);
        PersistBookmarks();
    }

    /// <summary>Where in the row, counting only the bookmarks not being dragged, x falls.</summary>
    private int DropIndexFor(double x)
    {
        int index = 0;
        foreach (var child in _bookmarkStrip.Children)
        {
            if (child is not Button { Tag: WebBookmark bookmark } button) continue;
            if (ReferenceEquals(bookmark, _draggingBookmark)) continue;

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

        var others = _bookmarkStrip.Children
            .OfType<Button>()
            .Where(b => b.Tag is WebBookmark bookmark && !ReferenceEquals(bookmark, _draggingBookmark))
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
