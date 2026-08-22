> **Scope:** Use when editing XAML files for WinUI 3 controls, Fluent Design, NavigationView pages, or resource dictionaries. Covers WinUI 3 control conventions, resource localization, and Mica/Acrylic backdrop patterns.
> **Governs:** `**/*.xaml` (all XAML across the solution).

# WinUI 3 XAML Conventions

## Namespaces

Standard WinUI 3 pages use these default namespaces:
```xml
xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
```

## Localization

- String resources live in `Resources/Localization/Dictionary-en-US.xaml`
- Reference via `{StaticResource KeyName}` in XAML (resource dictionary is merged in App.xaml)
- In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`
- Always add new string keys to the dictionary when adding UI text

## Pages

- Pages are `Page` objects (not `UserControl`)
- Navigation via WinUI 3 `NavigationView` with `TargetPageType` in XAML
- No MVVM routing framework — direct page type references

## Controls

- Use `Border` with `{ThemeResource CardBackgroundFillColorDefaultBrush}` and `{ThemeResource CardStrokeColorDefaultBrush}` for settings cards
- Use `ToggleSwitch` for boolean settings
- Use `NumberBox` for numeric inputs
- Use `FontIcon` with Segoe Fluent Icons glyphs

## Data Binding

- Bind to `SettingsManager.Current.<Property>` for settings
- Use `Mode=TwoWay` for editable settings
- Use `UpdateSourceTrigger=PropertyChanged` when immediate feedback needed

## Backdrops

- **SettingsWindow** uses `MicaBackdrop`
- **FlyoutWindow** uses a transparent backdrop

## Code-Built Dialogs

The add/edit item form in `ItemEditorWindow` is built entirely in C# (not XAML). Conventions:

- All input controls (`TextBox`, `ComboBox`, `ToggleSwitch`) use `HorizontalAlignment = HorizontalAlignment.Stretch` to fill the dialog width uniformly.
- The form container is a `StackPanel` with `MinWidth = 460`, wrapped in a `ScrollViewer` so a tall form never overflows off-screen.
- When a row needs a stretch input + a fixed button (e.g. path + Browse), use a `Grid` with `Star` + `Auto` column definitions instead of a horizontal `StackPanel`.
- Labels are created via a `Label(string)` helper that returns a styled `TextBlock`.

### Unified app/PWA picker

There is **no type dropdown**. The target is chosen via a two-item `SelectorBar` tab strip — modelled on the picker in the sibling `CopilotRekey` project:

- **"Apps & web apps"** tab — a single searchable list (`AutoSuggestBox` + `ListView`, fixed `Height = 260`) showing installed applications and PWAs together, each with an icon.
- **"File or link"** tab — the manual path/link `TextBox` + Browse, the arguments box, and the website "Open as app window" toggle + browser/profile pickers.

`Name` and `Icon` live below the tab content (shared across both tabs). `ShowTabPanel(tag)` toggles the two panels' visibility, keeps `tabBar.SelectedItem` in sync, and re-derives the target; the `SelectorBar.SelectionChanged` handler routes through it (a `populating` flag guards re-entrancy, same pattern as `_initializing` in `UserSettings`).

- The list is fed by `AppPickerEntry` rows (display name, `LaunchPath`, `IsPwa`, icon target). The catalog combines `GetInstalledApplications()` + `GetInstalledPwas()` and is built on a **background STA thread** via `AppPickerService.RunStaAsync` (the shell enumeration is expensive and apartment-threaded), so the dialog opens instantly and the list fills in.
- Icons stream in asynchronously via `AppPickerService.LoadIcons` → `ShellIcons.Extract` (an `IShellItemImageFactory` 32px BGRA extraction). The `ListView` `DataTemplate` is built from a XAML string with `XamlReader.Load` and binds `{Binding Icon}` (`ObservableObject` change notification), since the dialog is code-built.
- **Target resolution:** `ResolveTarget()` keys off the active tab — `custom` → the typed path/link (classified by `LooksLikeWebUrl` / `LooksLikeFilePath` into website vs application); `list` → the selected `AppPickerEntry`. It returns `(path, isPwa, isWebsite)`; `SyncDerived()` pushes that into the derived flags and the app-window options' visibility.
- Stored values are unchanged from before (PWA → AUMID in `Path` + `IsPwa`; Store app → `shell:AppsFolder\…` path; exe → file path; website URL → `IsWebsite`), so launch behaviour in `FlyoutWindow` is preserved.

## Settings rows (code-built)

`LauncherSettingsWindow.BuildRow` lays out a label/subtitle in a `Star` column with the control in
an `Auto` column. Two rules keep it from collapsing:

- **The control is centred vertically, never stretched.** A control with the default alignment
  grows to the row's height, and the row is as tall as its label — so a subtitle that wraps to two
  lines silently inflates the input beside it. `BuildRow` sets `VerticalAlignment.Center` on every
  control it lays out, so this cannot come back per-row.
- **Anything with long, open-ended content uses `BuildStackedRow` instead** (label above,
  full-width control below). An `Auto` column sizes to its content, so a control holding a URL
  claims the width the text wants and starves the `Star` label column — a long web address once
  squeezed "Web Address" into a three-character ribbon of wrapped text and stretched the box down
  the whole height of it.

## NumberBox: inline spin buttons eat the value

`SpinButtonPlacementMode="Inline"` spends about 64px on the two buttons, and the clear "✕" the
TextBox template adds **on focus** takes ~30 more. At `Width="130"` that left nothing for the digits:
the value was visible until you clicked into the field and then vanished behind its own chrome.

Use `Compact` (the placement every other NumberBox in this app uses — the buttons appear over the
box rather than reserving width) and leave at least ~140px. If `Inline` is genuinely wanted, budget
the buttons *and* the focus-only clear button, not just the digits.

## Owned windows, not ContentDialog

UI opened from the flyout (`ItemEditorWindow`, `TextPromptWindow`, `LauncherSettingsWindow`, `BookmarkPickerWindow`, `ExtensionPopupWindow`) uses standalone `Window`s. A `ContentDialog` renders inside its host window's content area and **cannot overflow the HWND** — hosted in a flyout that is often ~175px wide and ~130 dips tall, even a one-field dialog gets its input and buttons clipped.

Conventions for these windows:

- `ExtendsContentIntoTitleBar = true` with `WindowChrome.BuildTitleBar(...)`. A default WinUI title bar does **not** follow the app's `RequestedTheme`, so it renders light chrome over dark content.
- `WindowChrome.ApplyIcon(hwnd)` for the app icon. Both paths are needed: `WM_SETICON` drives taskbar/Alt-Tab, `AppWindow.SetIcon` drives the title bar.
- Set the owner via `SetWindowLongPtr(GWLP_HWNDPARENT)`, **and** have the flyout drop its `IsAlwaysOnTop` flag while the window is open — ownership alone does not beat a topmost owner.
- Size to content. Surplus height shows as a large empty gap, because the form is top-aligned above a bottom-anchored button row.
- Don't leak the window: the flyout tracks the open editor and closes it when edit mode ends, so an orphan can't commit into a launcher the user has navigated away from.
- **A dialog that both a settings window and the flyout need is split from its host.** `BookmarkPicker` was a `ContentDialog`; it is now `BookmarkPickerView` (the chooser) plus two hosts: the dialog for full-size windows, `BookmarkPickerWindow` for the flyout. Copying the chooser into a second window would have been the third place browser bookmarks are read.

## Flyouts and menus inside the flyout window

A `MenuFlyout` opened from the web flyout's header hits the same wall as `ContentDialog`, and needs
two things the default does not give:

- **`ShouldConstrainToRootBounds = false`.** The window is often 400px wide with 34px of chrome, so
  a constrained menu is clipped exactly like a `ContentDialog` would be.
- **Pin the flyout while it is open.** Once unconstrained the menu lives in a popup of its own, so
  opening it *deactivates* the flyout — which dismisses it and takes the menu down in the same
  motion. `WebFlyoutWindow` sets `_isMenuOpen` from `Opened`/`Closed` and tests it in both
  dismissal guards, alongside `_isModalOpen`. Clear it on `Closed`, which also covers the
  dismissed-by-clicking-away case.

This is the same failure mode as the owned-window rule above: anything that takes focus away from a
window that dismisses on focus loss has to say so first.

Two more, for menus whose rows answer more than a plain click:

- **A right-click on a `MenuFlyoutItem` has to be answered on the pointer press**, added with
  `AddHandler(UIElement.PointerPressedEvent, ..., handledEventsToo: true)`. A `MenuFlyoutItem` marks
  *every* pointer press handled for its own visual states, whichever button it was, and a handled
  press never becomes the right-tap that raises `ContextRequested`, so that event is never raised
  inside a menu at all. A `Button` takes only the left press, which is why the same wiring works on
  ordinary controls and silently does nothing on a menu row. Middle-click needs the same treatment.
  `ContextRequested` is still worth keeping for the context-menu key, guarded on `TryGetPosition`
  returning no position so the two paths cannot answer one gesture twice.
- **Never mutate an open `MenuFlyout`'s `Items`.** Every way of doing it breaks something, and none
  of them break at the moment of the change: swapping a row (for a `MenuFlyoutSubItem`, say) leaves
  a menu that never light-dismisses again, so it sits on screen through every click outside it;
  assigning over an entry (`Items[at] = ...`) removes before it adds, which empties a single-row
  menu for an instant and closes it outright; and `Clear` takes the presenter down mid-gesture.
  Build the menu you want and show it instead, letting the new one light-dismiss the old, which is
  also the only way to keep a menu under the pointer at all times: the moment there is none, the
  cursor falls through to whatever is beneath and does not change back until the pointer moves.
- **A `MenuFlyoutSubItem` cannot be opened from code.** `MenuFlyoutSubItemAutomationPeer`'s `Expand`
  is the only public way in and it goes around the framework's cascading-menu bookkeeping: the
  submenu comes up against bounds the row does not have yet, in the corner of the window, and
  afterwards neither menu can be light-dismissed. A submenu only opens on hover or a click, so a
  gesture that has to *show* something immediately cannot be built out of one.

## A popup cannot receive a drag

Menus and flyouts escape a small window because they are hosted outside it (`ShouldConstrainToRootBounds
= false`, above). That hosting is exactly why **a `Popup` can never be a drop target**: XAML registers
the *window* for drag-and-drop, so a popup renders, hit-tests and clicks normally while its `DragOver`
never fires once. Measured twice on the web flyout's bookmark folders, with light dismiss on and off;
dragging *out* of the popup worked throughout, because the receiving element was in the window's tree.

A `MenuFlyoutItem` cannot be a drag *source* either - it is not an element `CanDrag` applies to, and
menu rows mark every pointer press handled.

So anything that must be dragged into lives in the window's own tree, as an overlay spanning the root
grid, and is clamped to the window in exchange. Anything that merely has to be clicked can stay a
popup and overflow. `WebFlyoutWindow.FolderPopup.cs` records the whole chain.

## Expander rows for on/off + settings

`SyncPage` renders each sync destination as an `Expander`: icon, name and a live status line in
the header, a `ToggleSwitch` on the right, and that destination's settings as the content.

Two rules, both learned the hard way:

- **Enabled and expanded must be separate, visible affordances.** An earlier version used a
  selected-card highlight to mean "its settings are shown below", and it was not discoverable — a
  border colour reads as *state*, not as *"click me, there is more"*. The chevron says it outright,
  and the settings appear inside the thing you clicked rather than detached at the bottom.
- **Setting `ToggleSwitch.IsOn` from code raises `Toggled` exactly as a click does.** Any refresh
  that syncs toggles from settings must be wrapped in an `_initializing` guard, or it writes the
  settings back over themselves.

Give the expanders `HorizontalAlignment="Stretch"` *and* `HorizontalContentAlignment="Stretch"` (a
shared `Style` is easiest) or they size to their content and the list looks ragged.

### An expanding section must scroll its own start into view

`Classes/ExpanderReveal.Attach(expander)` — call it on every `Expander` that lives in a scrolling
form. A section near the bottom of a scroller reveals its content **below the fold**: the header
stays put, everything the click produced is off screen, and it reads as having done nothing.
Launcher settings' **Advanced** is the worst case by construction, since it is deliberately last in
the dialog.

Two details are load-bearing, and both are in the helper so no call site has to know them:

- **`VerticalAlignmentRatio = 0`, not a bare `StartBringIntoView()`.** The default scrolls the
  least amount that makes the target visible, which for a section taller than the viewport shows
  its *end* — exactly the sections that most need the start.
- **It runs twice**, the second pass at `DispatcherQueuePriority.Low`. The section's rows are still
  being realised while `Expanding` runs, so the scroller's extent is not yet large enough to put a
  bottom-most header at the top.

Note this is *not* in tension with "the window does not resize when Advanced expands" (see
[web-launchers.md](web-launchers.md)) — that rejects growing the **window**, which jolts. Scrolling
the existing viewport is what that decision assumed would happen, and it now actually does.

## Drag-and-Drop (FlyoutWindow)

ListViews use `CanDragItems="True"` with custom handlers — **never `CanReorderItems`**, which cannot be overridden for cross-list drops. Dragging is gated behind flyout edit mode. See [drag-drop.md](drag-drop.md) for full details.

## Groups

Groups render a heading plus a nested child `ListView`. In icon mode, consecutive ungrouped items are wrapped into ephemeral **synthetic groups** so loose icons pack into a wrapping grid; these are never persisted and never editable. See [drag-drop.md](drag-drop.md).
