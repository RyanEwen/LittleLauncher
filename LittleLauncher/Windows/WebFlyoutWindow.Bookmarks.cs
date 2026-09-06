// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        _launcher.WebBookmarkIconsOnly + "|" + SignatureOf(_launcher.WebBookmarks);

    /// <summary>
    /// What a run of bookmarks says, including whatever is inside any folders among them.
    /// </summary>
    /// <remarks>
    /// The contents count even though the bar does not draw them: a folder's menu is built from its
    /// button, so a bookmark added or renamed inside one changes what the bar can show while every
    /// top-level entry stays identical. Without this the rebuild is skipped and the folder keeps
    /// opening a menu of what it used to hold.
    /// </remarks>
    private static string SignatureOf(IEnumerable<WebBookmark> bookmarks) =>
        string.Join("", bookmarks.Select(b =>
            $"{b.Name}{b.Url}{b.IconPath}{b.IconsOnly}{b.IsFolder}" +
            (b.IsFolder ? "(" + SignatureOf(b.Children) + ")" : "")));

    /// <summary>
    /// Rebuilds the bar from the launcher's bookmarks, in the shape a browser uses: a small icon
    /// with its label beside it, centred while they fit and packed left once they do not, with
    /// whatever is left over reached through the chevron at the end.
    /// </summary>
    private void RebuildBookmarkBar(bool force = false)
    {
        if (!IsBarMode)
        {
            ApplyBookmarkBarVisibility();
            ClearBookmarkButtons();
            _barSignature = "";
            return;
        }

        // Icons-only is part of the signature: it changes what every button contains, so
        // toggling it has to rebuild rather than hand back the buttons built for the other mode.
        string signature = BookmarkBarSignature();
        if (!force && signature == _barSignature && _bookmarkStrip.Children.Count > 0 && BarHoldsLiveBookmarks())
        {
            // Same bookmarks as last time — the buttons are already built, laid out and decoded.
            ApplyBookmarkBarVisibility();
            return;
        }

        _barSignature = signature;
        ClearBookmarkButtons();

        foreach (var bookmark in _launcher.WebBookmarks)
            _bookmarkStrip.Children.Add(BuildBookmarkButton(bookmark));

        ApplyBookmarkBarVisibility();
    }

    /// <summary>
    /// Shows or hides the bar for what the launcher holds and what the page is doing.
    /// </summary>
    /// <remarks>
    /// <para>The header check is the same one the address bar and the tab strip make, and it is
    /// what the bar was missing: it is chrome, and a page showing something fullscreen means to
    /// have the chrome out of the way. <c>ApplyFullScreen</c> collapsed the bar directly, which
    /// held right up until anything called <see cref="RebuildBookmarkBar"/>, and that runs from
    /// <c>ApplyLauncherChanges</c>, so a background favicon fetch or a periodic sync put a row of
    /// bookmarks back over a fullscreen video with no user action at all. Both of those reach here
    /// even when they change nothing, because the early return above is a rebuild that was skipped
    /// rather than a decision not to show the bar.</para>
    /// <para>Every path that shows the bar goes through this, so there is one answer to "should the
    /// bar be visible" rather than one per caller.</para>
    /// </remarks>
    private void ApplyBookmarkBarVisibility() =>
        _bookmarkBar.Visibility = IsBarMode && _header.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// True when the buttons on the bar carry the very bookmarks the launcher holds now.
    /// </summary>
    /// <remarks>
    /// <para>A different question from the signature above, which compares what a bookmark
    /// <em>says</em>. A sync download empties <see cref="Launcher.WebBookmarks"/> and refills it
    /// with new objects carrying identical names, addresses and icons (see
    /// <c>LauncherPayload.Merge</c>), so the signature matches to the character while every button's
    /// <c>Tag</c> points at a bookmark the launcher no longer contains.</para>
    /// <para><b>Everything the bar does is then silently dead</b>, because it all starts by asking
    /// the launcher where this bookmark is: <c>IndexOf</c> returns -1, the actions list comes back
    /// empty, and the context menu, the moves and the removes do nothing at all, on the bar and in
    /// the overflow menu alike. Nothing throws and nothing logs, and it lasts until the app is
    /// restarted, which is why it read as "right-click stopped working".</para>
    /// </remarks>
    private bool BarHoldsLiveBookmarks()
    {
        if (_bookmarkStrip.Children.Count != _launcher.WebBookmarks.Count) return false;

        for (int i = 0; i < _launcher.WebBookmarks.Count; i++)
        {
            if (_bookmarkStrip.Children[i] is not Button { Tag: WebBookmark bookmark } ||
                !ReferenceEquals(bookmark, _launcher.WebBookmarks[i]))
                return false;
        }

        return true;
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
        if (!bookmark.IsFolder && !string.IsNullOrEmpty(bookmark.IconPath) && File.Exists(bookmark.IconPath))
            icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(bookmark.IconPath));

        // A page with no icon yet gets a globe rather than a hole in the row — U+E774, Segoe
        // Fluent's Globe. A folder gets U+E8B7, Segoe Fluent's Folder, and never a favicon: it
        // stands for a group of pages rather than any one of them.
        var fallback = new FontIcon
        {
            Glyph = bookmark.IsFolder ? "\uE8B7" : "",
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
        // A folder has no address, so the second line of the icons-only form is a blank one
        // under its name - and the label form is an empty tooltip entirely. Its name is the
        // whole of what there is to say about it.
        ToolTipService.SetToolTip(button,
            bookmark.IsFolder
                ? caption
                : ShowsIconOnly(bookmark) ? $"{caption}\n{bookmark.Url}" : bookmark.Url);
        // Shift/Ctrl-click opens a tab of its own, as it does on any link in any browser, and it is
        // answered in Click rather than on the press: a Button marks the left press handled for its
        // own press/click handling before any instance handler runs, so the plain PointerPressed
        // subscription this used to be never saw a modified click at all and the gesture did
        // nothing.
        // A folder opens its contents; everything else opens a page.
        if (bookmark.IsFolder)
            button.Click += (_, _) => ShowFolderPopup(bookmark, button);
        else
        {
            button.Click += (_, _) =>
            {
                CloseFolderPopups(0);
                OpenBookmark(bookmark, newTab: WantsNewTab());
            };
        }

        // Middle-click, which raises no Click at all and so has to be taken from the press. The
        // handler needs AddHandler with handledEventsToo for the same reason as above: a plain
        // subscription is skipped once the Button has claimed the press.
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsMiddleButtonPressed) return;

            e.Handled = true;

            // A folder has no page to put in a tab. Still handled, so the gesture does not fall
            // through to the strip's window-move handler.
            if (!bookmark.IsFolder) OpenBookmark(bookmark, newTab: true);
        }), handledEventsToo: true);

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
    /// True while Shift or Ctrl is held: every browser's "open this in a tab of its own".
    /// </summary>
    /// <remarks>
    /// <para><b>Read from the keyboard, not from a pointer event's <c>KeyModifiers</c>.</b> The
    /// gesture is answered in <c>Click</c>, because a <c>Button</c> and a <c>MenuFlyoutItem</c> both
    /// mark the left press handled for their own visual states before any instance handler runs, so
    /// the press that carries those modifiers is not reliably ours to read. Asking the keyboard for
    /// its state at the moment of the click is the same answer from a source that does not depend on
    /// which control claimed the press, and it is equally true of a trackpad tap, where the click
    /// and the modifier come from two different devices.</para>
    /// </remarks>
    private static bool WantsNewTab()
    {
        static bool Down(global::Windows.System.VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

        return Down(global::Windows.System.VirtualKey.Shift) ||
               Down(global::Windows.System.VirtualKey.Control);
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

        SendFrontTabTo(url);
    }

    /// <summary>
    /// Sends the tab in front to an address the user picked, and remembers it as where this
    /// launcher reopens.
    /// </summary>
    /// <remarks>
    /// <para>Shared by a bookmark click and the Home button, which are the same gesture aimed at
    /// different addresses. Both are the user saying where this launcher should be, so both write
    /// <see cref="_rememberedUrl"/> - written here rather than inside <c>Navigate</c>, so that "the
    /// user has not steered this launcher anywhere" stays a state the flyout can recognise.</para>
    /// </remarks>
    private void SendFrontTabTo(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        _rememberedUrl = url;

        // No browser at all - the flyout has been unloaded, or was never loaded. Building the
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

    /// <summary>
    /// Sends the launcher back to its home page.
    /// </summary>
    /// <remarks>
    /// <para><b>Not <see cref="ShowHomeContentAsync"/>, which is a different question.</b> That one
    /// asks "what should this launcher be showing right now", and the answer it gives is
    /// <see cref="CurrentTargetUrl"/> - which is <see cref="_rememberedUrl"/> when the user has
    /// steered the launcher somewhere. So wiring Home to it navigated to the page already on
    /// screen: click a bookmark, click Home, and nothing at all happens. It is the right answer for
    /// a show or an expansion and the wrong one for a button labelled Home.</para>
    /// <para>Going home is also a steer, so it overwrites the remembered address rather than
    /// leaving it pointing at wherever the user was: after this the launcher reopens at home, which
    /// is what "go home" means.</para>
    /// </remarks>
    private void GoHome() => SendFrontTabTo(NormalizeUrl(_launcher.WebAddress));

    /// <summary>
    /// Opens the bookmark a taskbar jump list task names, and reports whether it found one.
    /// </summary>
    /// <remarks>
    /// <para>The position is only a hint - where the bookmark sat when the list was published -
    /// and the token has the final say, because a published list can outlive an edit. Returning
    /// false rather than opening whatever now sits at that position is the point: see
    /// <see cref="LauncherPanels.LaunchFromJumpList"/> for what happens instead.</para>
    /// <para>Once found it is opened exactly as a click on the bar opens it, in the tab in front,
    /// because it is the same gesture reached from somewhere else. The remembered URL is written
    /// before the show so that a launcher with no browser yet builds its first tab straight on the
    /// bookmark, rather than loading its own page and replacing it a moment later.</para>
    /// </remarks>
    internal static bool OpenBookmarkFromJumpList(MainWindow owner, Launcher launcher, int index,
        int token, int screenX, int screenY)
    {
        var target = ResolveJumpListBookmark(launcher, index, token);
        if (target == null)
        {
            Logger.Info("Jump list task no longer matches a bookmark in {Name}; opening the launcher instead",
                launcher.Name);
            return false;
        }

        if (!Instances.TryGetValue(launcher.Id, out var panel) || panel._hwnd == IntPtr.Zero || !IsWindow(panel._hwnd))
        {
            panel = new WebFlyoutWindow(owner, launcher);
            Instances[launcher.Id] = panel;
        }

        panel._owner = owner;

        _ = panel.ShowAndOpenBookmarkAsync(target, screenX, screenY);
        return true;
    }

    /// <summary>
    /// The bookmark a jump list entry stands for, or null when the launcher no longer has it.
    /// </summary>
    /// <remarks>
    /// The position is only a hint at where it sat when the list was published; the token, hashed
    /// from the name and URL, has the final say. Opening - or deleting - whatever now sits at that
    /// position is the one outcome worse than doing nothing.
    /// </remarks>
    private static WebBookmark? ResolveJumpListBookmark(Launcher launcher, int index, int token)
    {
        var bookmarks = launcher.WebBookmarks;

        if (index >= 0 && index < bookmarks.Count && JumpListService.BookmarkToken(bookmarks[index]) == token)
            return bookmarks[index];

        return bookmarks.FirstOrDefault(b => JumpListService.BookmarkToken(b) == token);
    }

    /// <summary>
    /// Shows the flyout and puts a bookmark in front of the user, for a jump list task.
    /// </summary>
    /// <remarks>
    /// <para><b>A new tab, not the tab in front.</b> This is the one place a bookmark is reached
    /// without the launcher being on screen, so there is no "tab in front" the user was looking
    /// at and chose to replace - there is only whatever the launcher happened to be left on, quite
    /// possibly days ago, and taking it over is how a quick look at one page costs somebody the
    /// page they were on. In front rather than behind, because unlike a middle-click this gesture
    /// says nothing except "show me this".</para>
    /// <para>The exception is a launcher with nothing loaded at all, where the bookmark simply
    /// becomes its first tab. There is nothing to preserve, and the alternative is loading the
    /// launcher's own address and covering it over a moment later.</para>
    /// <para>Awaiting the show is what keeps the two apart. The show may be restoring the tabs the
    /// launcher had open last time, which builds tabs and picks one to activate, and a tab added
    /// alongside that would race it for which ends up in front.</para>
    /// </remarks>
    private async Task ShowAndOpenBookmarkAsync(WebBookmark bookmark, int screenX, int screenY)
    {
        string url = NormalizeUrl(bookmark.Url);
        bool empty = _tabs.Count == 0 && !HasSessionToRestore;

        if (empty && !string.IsNullOrEmpty(url))
            _rememberedUrl = url;

        if (!_isOpen)
            ShowFlyout(screenX, screenY);

        if (empty || string.IsNullOrEmpty(url)) return;

        if (_contentPreparation != null)
        {
            try { await _contentPreparation; }
            catch { /* whatever the show could not load is its own problem to report */ }
        }

        await OpenLinkTabAsync(url, background: false);
    }

    /// <summary>The rows for what a folder holds, with any folder among them as a submenu.</summary>
    private List<MenuFlyoutItemBase> BuildFolderContents(WebBookmark folder)
    {
        var rows = new List<MenuFlyoutItemBase>();

        if (folder.Children.Count == 0)
        {
            rows.Add(new MenuFlyoutItem { Text = "Empty", IsEnabled = false });
            return rows;
        }

        foreach (var child in folder.Children)
        {
            var captured = child;
            string caption = string.IsNullOrWhiteSpace(captured.Name) ? captured.Url : captured.Name;

            if (captured.IsFolder)
            {
                var sub = new MenuFlyoutSubItem { Text = caption };
                foreach (var nested in BuildFolderContents(captured))
                    sub.Items.Add(nested);
                rows.Add(sub);
                continue;
            }

            var row = new MenuFlyoutItem { Text = caption };
            if (!string.IsNullOrEmpty(captured.IconPath) && File.Exists(captured.IconPath))
            {
                row.Icon = new ImageIcon
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(captured.IconPath)),
                };
            }

            row.Click += (_, _) => OpenBookmark(captured, newTab: WantsNewTab());
            rows.Add(row);
        }

        return rows;
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
            : _launcher.WebBookmarks.SelectMany(b => b.Flatten()).FirstOrDefault(
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
    /// <summary>
    /// The right-click entries for a folder, which are about the folder rather than about a page.
    /// </summary>
    /// <remarks>
    /// A list of its own rather than the bookmark one with rows disabled: almost nothing on that
    /// menu means anything here - a folder has no address to copy, to open in a browser, or to make
    /// the home page - and a menu of greyed-out rows answers worse than a short menu.
    /// </remarks>
    private List<MenuFlyoutItemBase> BuildFolderMenuItems(WebBookmark folder,
        ObservableCollection<WebBookmark> owner, int index, List<MenuFlyoutItemBase> items)
    {
        MenuFlyoutItem Item(string text, Action invoke, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => invoke();
            return item;
        }

        items.Add(Item("Rename…", () => _ = RenameFolderAsync(folder)));
        items.Add(Item("Add bookmark…", () => _ = AddBookmarkToFolderAsync(folder)));
        items.Add(Item("Add folder…", () => _ = AddFolderAsync(folder)));

        items.Add(new MenuFlyoutSeparator());

        items.Add(Item("Move up", () => MoveBookmark(folder, -1), index > 0));
        items.Add(Item("Move down", () => MoveBookmark(folder, 1), index < owner.Count - 1));

        items.Add(new MenuFlyoutSeparator());

        items.Add(Item("Remove folder", () => RemoveFolder(folder)));

        return items;
    }

    /// <summary>Every folder in the bar, each with the path that names it.</summary>
    /// <remarks>
    /// The path rather than the bare name, because folders nest and two of them may be called the
    /// same thing at different depths. "Work / Dashboards" says which one is meant; "Dashboards"
    /// twice in a menu says nothing.
    /// </remarks>
    private IEnumerable<(WebBookmark Folder, string Path)> AllFolders(
        IEnumerable<WebBookmark>? within = null, string prefix = "")
    {
        foreach (var bookmark in within ?? _launcher.WebBookmarks)
        {
            if (!bookmark.IsFolder) continue;

            string name = string.IsNullOrWhiteSpace(bookmark.Name) ? "Folder" : bookmark.Name;
            string path = prefix.Length == 0 ? name : prefix + " / " + name;

            yield return (bookmark, path);

            foreach (var nested in AllFolders(bookmark.Children, path))
                yield return nested;
        }
    }

    /// <summary>Renames a folder. Unlike a bookmark, an empty name has nothing to fall back to.</summary>
    private async Task RenameFolderAsync(WebBookmark folder)
    {
        string? renamed = await RunTextPromptAsync("Rename folder", "Name", folder.Name, "Rename");
        if (string.IsNullOrWhiteSpace(renamed)) return;

        folder.Name = renamed.Trim();
        PersistBookmarks();
    }

    /// <summary>Adds a folder, either to the bar or inside another folder.</summary>
    private async Task AddFolderAsync(WebBookmark? parent = null)
    {
        string? name = await RunTextPromptAsync("Add folder", "Name", "", "Add");
        if (string.IsNullOrWhiteSpace(name)) return;

        var folder = WebBookmark.CreateFolder(name.Trim());

        if (parent != null) parent.Children.Add(folder);
        else _launcher.WebBookmarks.Add(folder);

        PersistBookmarks();
    }

    /// <summary>Adds a bookmark straight into a folder.</summary>
    private async Task AddBookmarkToFolderAsync(WebBookmark folder)
    {
        string? entered = await RunTextPromptAsync("Add bookmark", "https://…", "", "Add");
        if (string.IsNullOrWhiteSpace(entered)) return;

        string url = NormalizeUrl(entered);
        if (string.IsNullOrEmpty(url)) return;

        if (FindBookmark(url) != null)
        {
            ShowNotice("That page is already in the bookmarks bar.");
            return;
        }

        var bookmark = new WebBookmark(HostOf(url), url);
        folder.Children.Add(bookmark);
        PersistBookmarks();

        _ = FetchBookmarkIconAsync(_launcher, bookmark);
    }

    /// <summary>Deletes a folder and returns what was in it to the bar, where the folder was.</summary>
    /// <remarks>
    /// The folder goes; its contents do not. Deleting a folder to lose the bookmarks inside it is
    /// never what was meant by "remove folder", and there is no undo here to lean on.
    /// </remarks>
    private void RemoveFolder(WebBookmark folder)
    {
        // Whichever collection actually holds it, which for a folder inside another folder is that
        // one's children. Asking the launcher's top level returned -1 for those and gave up without
        // a word, so Remove folder did nothing at all - the third time a top-level-only lookup has
        // silently disabled an action in this file. OwnerOf is the answer to all of them.
        var owner = OwnerOf(folder);
        if (owner == null) return;

        int at = owner.IndexOf(folder);
        if (at < 0) return;

        owner.RemoveAt(at);

        // Its contents come out where it was, in the same collection: deleting a folder is not a
        // request to delete the bookmarks inside it, and there is no undo here.
        foreach (var child in folder.Children.ToList())
            owner.Insert(at++, child);

        folder.Children.Clear();
        PersistBookmarks();

        // The list this folder was drawn in is now describing something that no longer exists, and
        // a nested one is drawn from a folder that has just been emptied.
        CloseFolderPopups(0);
    }

    /// <summary>Files a bookmark into a folder, taking it out of wherever it was.</summary>
    private void MoveIntoFolder(WebBookmark bookmark, WebBookmark folder)
    {
        if (!folder.IsFolder || ReferenceEquals(bookmark, folder)) return;

        // A folder cannot be moved into itself or into anything it contains, which would take that
        // whole branch off the bar with no way back to it.
        if (bookmark.IsFolder && bookmark.Flatten().Contains(folder)) return;
        if (bookmark.IsFolder && Contains(bookmark, folder)) return;

        if (!DetachBookmark(bookmark)) return;

        folder.Children.Add(bookmark);
        PersistBookmarks();
    }

    /// <summary>True when <paramref name="branch"/> holds <paramref name="wanted"/> at any depth.</summary>
    private static bool Contains(WebBookmark branch, WebBookmark wanted) =>
        branch.Children.Any(c => ReferenceEquals(c, wanted) || (c.IsFolder && Contains(c, wanted)));

    /// <summary>Takes a bookmark out of whichever collection holds it, at any depth.</summary>
    private bool DetachBookmark(WebBookmark bookmark) => OwnerOf(bookmark)?.Remove(bookmark) == true;

    /// <summary>Points the launcher at a new home page.</summary>
    /// <remarks>
    /// Takes a URL, deliberately, rather than the bookmark it may have come from - nothing here
    /// records which bookmark that was, and nothing should. Applied through the same persist path
    /// as any other bar edit, so the flyout, the settings window and sync all hear about it the
    /// one way.
    /// </remarks>
    private void SetHomeUrl(string url)
    {
        _launcher.WebHomeUrl = NormalizeUrl(url);
        PersistBookmarks();
    }

    private void RemoveBookmark(WebBookmark bookmark)
    {
        // At any depth: the bar only shows the top level, but the overflow menu, launcher settings
        // and a folder's own menu all reach further in, and a remove that silently does nothing is
        // a failure this window has already been bitten by once.
        if (!DetachBookmark(bookmark)) return;

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
    /// opens that bookmark's actions.</para>
    /// <para><b>Aligned to the chevron's right edge, not centred on it.</b> The chevron is the last
    /// thing on the bar, so a menu centred over it straddles the window's edge and comes up half
    /// outside the flyout it belongs to, around the pointer rather than under the button. Aligned,
    /// it grows inwards from the button that opened it, which is where a browser puts its own
    /// overflow menu.</para>
    /// <para>Built fresh on each open, because which bookmarks are in here is a property of the
    /// window's current width rather than of the launcher.</para>
    /// </remarks>
    private void ShowBookmarkOverflowMenu()
    {
        if (_bookmarkOverflow == null) return;

        var menu = new MenuFlyout
        {
            // The bar sits at the foot of the window, so the menu goes up; its right edge on the
            // chevron's keeps it inside the flyout.
            Placement = OverflowPlacement,
            ShouldConstrainToRootBounds = false,
        };

        FillBookmarkOverflowMenu(menu);
        if (menu.Items.Count == 0) return;

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        menu.ShowAt(_bookmarkOverflow);
    }

    /// <summary>Where the chevron's menus go: up, and with their right edge on its.</summary>
    private const FlyoutPlacementMode OverflowPlacement = FlyoutPlacementMode.TopEdgeAlignedRight;

    /// <summary>
    /// Puts the overflowed bookmarks into <paramref name="menu"/>, and wires what each row answers.
    /// </summary>
    /// <remarks>
    /// <para><b>A right-click replaces this list with that bookmark's own menu, in the same place.</b>
    /// It reads as the list turning over to show one bookmark's actions, and it is the same menu the
    /// bar itself opens, so a bookmark answers a right-click the same way wherever it happens to be
    /// sitting. The chevron brings the list straight back.</para>
    /// <para><b>Keeping the list up underneath was tried twice and cannot be done.</b> WinUI keeps
    /// only one <c>MenuFlyout</c> up at a time, so a second one dismisses this one by definition, and
    /// the two ways around that both fail: <em>editing the open menu</em> (swapping a row for a
    /// <see cref="MenuFlyoutSubItem"/>, or inserting the actions under it) leaves a menu that never
    /// light-dismisses again, so every right-click stranded one on screen, and assigning over an
    /// entry empties a single-row menu for an instant, which closes it outright. <em>A submenu built
    /// in from the start</em> survives that, but nothing can open it: the automation peer's
    /// <c>Expand</c> is the only public way in and it goes around the framework's cascading-menu
    /// bookkeeping, putting the submenu in the corner of the window and breaking dismissal again. Left
    /// to open itself, it waits for the pointer to move, and a right-click that visibly does nothing
    /// is worse than one that answers with the wrong menu shape.</para>
    /// </remarks>
    private void FillBookmarkOverflowMenu(MenuFlyout menu)
    {
        for (int i = _bookmarkStrip.VisibleCount; i < _bookmarkStrip.Children.Count; i++)
        {
            if (_bookmarkStrip.Children[i] is not Button { Tag: WebBookmark bookmark }) continue;

            var captured = bookmark;
            string caption = string.IsNullOrWhiteSpace(captured.Name) ? captured.Url : captured.Name;

            // A folder that overflowed is still a folder: it becomes a submenu of the same rows its
            // button would have shown, rather than a row that opens an address it does not have.
            if (captured.IsFolder)
            {
                var sub = new MenuFlyoutSubItem { Text = caption };
                foreach (var row in BuildFolderContents(captured))
                    sub.Items.Add(row);

                menu.Items.Add(sub);
                continue;
            }

            var item = new MenuFlyoutItem
            {
                Text = caption,
            };

            if (!string.IsNullOrEmpty(captured.IconPath) && File.Exists(captured.IconPath))
            {
                item.Icon = new ImageIcon
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(captured.IconPath)),
                };
            }

            // The same three gestures the bar answers, answered the same way: the modified click in
            // Click, and middle-click on the press, since it raises no Click. The menu closes on a
            // click of its own accord, and Hide covers the middle-click that does not reach it.
            item.Click += (_, _) => OpenBookmark(captured, newTab: WantsNewTab());

            item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                if (!e.GetCurrentPoint(item).Properties.IsMiddleButtonPressed) return;

                e.Handled = true;
                menu.Hide();
                OpenBookmark(captured, newTab: true);
            }), handledEventsToo: true);

            // Anchored on the chevron rather than on the row, and shown without hiding this menu
            // first: the new menu light-dismisses this one as it opens, so there is never a moment
            // with no menu under the pointer. There was one when the list was hidden first, and the
            // cursor fell through to the flyout's resize edge and stayed a resize arrow, because
            // nothing re-asks for a cursor until the pointer moves.
            WireRowRightClick(item, () => ShowBookmarkMenu(captured, _bookmarkOverflow!, OverflowPlacement));

            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Wires what one row of a menu answers a right-click with.
    /// </summary>
    /// <remarks>
    /// <b>On the pointer press, not <c>ContextRequested</c>.</b> A <see cref="MenuFlyoutItem"/>
    /// marks <em>every</em> pointer press handled for its own visual states, whichever button it
    /// was, and a handled press never becomes the right-tap that raises <c>ContextRequested</c>. A
    /// <see cref="Button"/> takes only the left press, which is why the bar's own bookmarks can use
    /// that event and nothing inside a menu can: wired there, an overflowed bookmark's actions were
    /// simply unreachable. <c>handledEventsToo</c> is what lets this run after the row has marked
    /// the press. The <c>ContextRequested</c> handler is kept for the context-menu key, which raises
    /// it with no position where a pointer carries one, and the guard is what stops the two paths
    /// answering the same gesture twice.
    /// </remarks>
    private static void WireRowRightClick(FrameworkElement row, Action invoke)
    {
        row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsRightButtonPressed) return;

            e.Handled = true;
            invoke();
        }), handledEventsToo: true);

        row.ContextRequested += (_, e) =>
        {
            if (e.TryGetPosition(row, out var _)) return;

            e.Handled = true;
            invoke();
        };
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
    private void ShowBookmarkMenu(WebBookmark bookmark, FrameworkElement anchor,
        FlyoutPlacementMode placement = FlyoutPlacementMode.Top)
    {
        var items = BuildBookmarkMenuItems(bookmark);
        if (items.Count == 0) return;

        var menu = new MenuFlyout
        {
            // The bar sits at the foot of the window, which usually sits at the foot of the
            // screen, so a menu below it has nowhere to go. The overflow asks for its own
            // alignment, so its actions land where the list they came from was.
            Placement = placement,
            ShouldConstrainToRootBounds = false,
        };

        foreach (var item in items)
            menu.Items.Add(item);

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        menu.ShowAt(anchor);
    }

    /// <summary>
    /// Everything one bookmark can be asked to do, as menu rows.
    /// </summary>
    /// <remarks>
    /// One list, two menus: the bar's own right-click and the overflow menu's submenu. They are the
    /// same question asked about the same bookmark, and the moment they were built separately one of
    /// them would start missing an action.
    /// </remarks>
    private List<MenuFlyoutItemBase> BuildBookmarkMenuItems(WebBookmark bookmark)
    {
        var items = new List<MenuFlyoutItemBase>();

        // The collection that actually holds it, which for a row inside a folder popup is that
        // folder's children. Asking the launcher's top level returned -1 for those and handed back
        // an empty list, so a right-click inside a folder opened nothing at all.
        var owner = OwnerOf(bookmark);
        if (owner == null) return items;

        int index = owner.IndexOf(bookmark);
        if (index < 0) return items;

        if (bookmark.IsFolder) return BuildFolderMenuItems(bookmark, owner, index, items);

        MenuFlyoutItem Item(string text, Action invoke, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => invoke();
            return item;
        }

        void Divide() => items.Add(new MenuFlyoutSeparator());

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
        // Sets where the launcher opens, and moves nothing. It used to drag the bookmark to the
        // front, because the front *was* the address; now that the address is a setting of its own
        // the bar keeps the order the user gave it.
        //
        // The URL is copied, and that is all that happens. The bookmark is not remembered, marked
        // or linked in any way: renaming it later, re-addressing it or removing it leaves the home
        // page exactly where it was. This row is a convenient way to type an address the user
        // already has, not a way to make one bookmark special.
        items.Add(Item("Set as home page", () => SetHomeUrl(bookmark.Url),
            !string.Equals(NormalizeUrl(_launcher.WebAddress), NormalizeUrl(bookmark.Url),
                StringComparison.OrdinalIgnoreCase)));

        // Kept beside the drag rather than replaced by it: a bookmark that does not fit on the bar
        // is in the chevron's menu, and there is nothing there to drag.
        // "Up/down" inside a folder, "left/right" along the bar: the same move, and naming it for
        // the direction it actually travels is the difference between an instruction and a riddle.
        bool onBar = ReferenceEquals(owner, _launcher.WebBookmarks);
        items.Add(Item(onBar ? "Move left" : "Move up", () => MoveBookmark(bookmark, -1), index > 0));
        items.Add(Item(onBar ? "Move right" : "Move down", () => MoveBookmark(bookmark, 1),
            index < owner.Count - 1));

        var folders = AllFolders().ToList();
        if (folders.Count > 0)
        {
            Divide();

            var into = new MenuFlyoutSubItem { Text = "Move to folder" };
            foreach (var (folder, path) in folders)
            {
                var captured = folder;
                var row = new MenuFlyoutItem { Text = path };
                row.Click += (_, _) => MoveIntoFolder(bookmark, captured);
                into.Items.Add(row);
            }
            items.Add(into);
        }

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
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Add folder…", () => _ = AddFolderAsync()));

        menu.Items.Add(new MenuFlyoutSeparator());

        // The launcher-wide switch, on the bar it describes. It has lived in launcher settings
        // since it was written, and that is still where a launcher being set up meets it; but the
        // moment the labels start being in the way is a moment spent looking at the bar, and the
        // judgement is "do I still need to read these" about the row in front of you. Same argument
        // that put renaming and reordering here rather than only in the form.
        //
        // Plural against the per-bookmark row's singular "Icon only", because they are different
        // sizes of the same idea and the two menus can be one right-click apart.
        var iconsOnly = new ToggleMenuFlyoutItem
        {
            Text = "Icons only",
            IsChecked = _launcher.WebBookmarkIconsOnly,
        };
        iconsOnly.Click += (_, _) =>
        {
            _launcher.WebBookmarkIconsOnly = iconsOnly.IsChecked;

            // Not a bookmark edit, but the same three things have to happen: save, tell the sync
            // service, and rebuild the bar, which the signature already invalidates on this flag.
            PersistBookmarks();
        };
        menu.Items.Add(iconsOnly);

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
        var owner = OwnerOf(bookmark);
        if (owner == null) return;

        int from = owner.IndexOf(bookmark);
        if (from < 0) return;

        int to = Math.Clamp(from + delta, 0, owner.Count - 1);
        if (to == from) return;

        owner.Move(from, to);
        PersistBookmarks();
    }

    /// <summary>The collection holding a bookmark, at any depth, or null when nothing does.</summary>
    private ObservableCollection<WebBookmark>? OwnerOf(WebBookmark bookmark)
    {
        if (_launcher.WebBookmarks.Contains(bookmark)) return _launcher.WebBookmarks;

        foreach (var (folder, _) in AllFolders())
        {
            if (folder.Children.Contains(bookmark)) return folder.Children;
        }

        return null;
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
        _bookmarkStrip.DragLeave += (_, _) =>
        {
            HideDropCaret();
            ShowFolderDropTarget(null);
        };
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
    /// <summary>The folder button currently lit as a drop target, so it can be put back.</summary>
    private Button? _folderDropTarget;

    /// <summary>
    /// Lights the folder a drop would land in, and takes the caret down while it is lit.
    /// </summary>
    /// <remarks>
    /// The caret says "between these two", which is the wrong promise entirely when the drop is
    /// going to file the bookmark away inside something - it worked, and looked like it was about
    /// to do something else. A drop indicator is a promise, the same rule the flyout's external
    /// drops follow for the drop cursor.
    /// </remarks>
    private void ShowFolderDropTarget(Button? button)
    {
        if (ReferenceEquals(_folderDropTarget, button)) return;

        if (_folderDropTarget != null)
        {
            _folderDropTarget.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
            _folderDropTarget = null;
        }

        if (button == null) return;

        // Background only, never a border: a border insets the content and reflows the row, which
        // is the non-reflowing rule the flyout's edit-mode affordances follow for the same reason.
        button.Background = (Brush)Application.Current.Resources["AccentFillColorSelectedTextBackgroundBrush"];
        _folderDropTarget = button;
    }

    /// <summary>The button carrying a bookmark, if it is on the strip.</summary>
    private Button? ButtonFor(WebBookmark bookmark) =>
        _bookmarkStrip.Children.OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.Tag, bookmark));

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

        double overX = e.GetPosition(_bookmarkStrip).X;
        var overFolder = FolderUnder(overX);

        if (overFolder != null)
        {
            HideDropCaret();
            ShowFolderDropTarget(ButtonFor(overFolder));

            if (e.DragUIOverride != null)
            {
                e.DragUIOverride.Caption = $"Move into {overFolder.Name}";
                e.DragUIOverride.IsCaptionVisible = true;
            }

            return;
        }

        ShowFolderDropTarget(null);
        ShowDropCaretAt(DropIndexFor(overX));
    }

    private void BookmarkStrip_Drop(object sender, DragEventArgs e)
    {
        var dragged = _draggingBookmark;
        HideDropCaret();
        ShowFolderDropTarget(null);
        if (dragged == null) return;

        e.Handled = true;
        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        // Onto a folder rather than between two entries: the drop lands inside it, which is what a
        // folder on a bookmarks bar is for and what every browser does with the same gesture.
        // Checked before the reorder, because a folder occupies a position too and the reorder
        // would otherwise just shuffle the dragged bookmark past it.
        double dropX = e.GetPosition(_bookmarkStrip).X;
        var folder = FolderUnder(dropX);

        Logger.Info("Bar drop at x={X:F0} dragging {Dragged}: folder={Folder}, bands=[{Bands}]",
            dropX, dragged.Name, folder?.Name ?? "(none)", DescribeFolderBands());

        if (folder != null)
        {
            MoveIntoFolder(dragged, folder);
            return;
        }

        int from = _launcher.WebBookmarks.IndexOf(dragged);

        // Arriving from inside a folder rather than moving along the bar. Taken out of wherever it
        // was and inserted where it was dropped, which is what dragging one back out of a folder
        // has to mean - and the case CanReorderItems could never have handled.
        if (from < 0)
        {
            int at = DropIndexFor(e.GetPosition(_bookmarkStrip).X);
            if (!DetachBookmark(dragged)) return;

            _launcher.WebBookmarks.Insert(Math.Clamp(at, 0, _launcher.WebBookmarks.Count), dragged);
            PersistBookmarks();
            CloseFolderPopups(0);
            return;
        }

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

    /// <summary>
    /// The folder button <paramref name="x"/> is over, or null when the drop is between entries.
    /// </summary>
    /// <remarks>
    /// <para>The middle of a folder's button counts as "into it" and its edges do not, so a
    /// bookmark can still be dropped beside a folder rather than into it. Without the margin a
    /// folder would swallow every drop anywhere near it, and reordering the bar past one would
    /// become impossible.</para>
    /// <para>Skips whatever is being dragged, so dragging a folder does not find itself.</para>
    /// </remarks>
    /// <summary>Where each folder button actually is, for diagnosing a drop that missed.</summary>
    private string DescribeFolderBands()
    {
        var parts = new List<string>();

        foreach (var child in _bookmarkStrip.Children)
        {
            if (child is not Button { Tag: WebBookmark bookmark } button) continue;
            if (!bookmark.IsFolder) continue;

            var origin = button.TransformToVisual(_bookmarkStrip)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));
            parts.Add($"{bookmark.Name}@{origin.X:F0}+{button.ActualWidth:F0}");
        }

        return string.Join(", ", parts);
    }

    private WebBookmark? FolderUnder(double x)
    {
        foreach (var child in _bookmarkStrip.Children)
        {
            if (child is not Button { Tag: WebBookmark bookmark } button) continue;
            if (!bookmark.IsFolder || ReferenceEquals(bookmark, _draggingBookmark)) continue;

            double width = button.ActualWidth;
            if (width <= 0) continue;

            // The button's own arranged position, exactly as DropIndexFor reads it. Adding widths
            // up from zero instead was the bug: the row is centred while its bookmarks fit, so
            // every band was computed to the left of where the button actually sits and the drop
            // never landed on one.
            var origin = button.TransformToVisual(_bookmarkStrip)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));

            // Proportional, but capped: a quarter of a 32px icon-only folder is 8px, which is a
            // target the pointer skids across. Capping the inset makes a wide folder nearly all
            // target while a narrow one keeps just enough edge to drop *beside* it.
            double inset = Math.Min(width * 0.25, 12);
            if (x >= origin.X + inset && x <= origin.X + width - inset) return bookmark;
        }

        return null;
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
