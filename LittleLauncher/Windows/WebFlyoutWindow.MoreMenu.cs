// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// The header's "…" menu: the per-launcher options worth changing while looking at the launcher,
/// with the full settings window one item below them.
/// </summary>
/// <remarks>
/// <para>These are the settings whose right value is a per-moment judgement — how this launcher
/// presents itself, and whether it gets out of the way when you click elsewhere. Everything that is
/// configured once and forgotten (address, size, zoom, profiles, browsing data) stays in launcher
/// settings; putting it all here would just be a second settings window in a worse place.</para>
/// <para>Each item writes the launcher and applies it immediately, because the menu is opened
/// <em>while looking at the thing it changes</em> — a toggle that only took effect on the next open
/// would be indistinguishable from one that did nothing.</para>
/// </remarks>
public sealed partial class WebFlyoutWindow
{
    /// <summary>
    /// Opens the More menu under the header button.
    /// </summary>
    /// <remarks>
    /// <para>Built fresh each time rather than kept: every item's checked state is read from the
    /// launcher, and which items exist depends on the launcher's mode. Rebuilding is a few objects
    /// and removes any question of the menu going stale.</para>
    /// <para>Two things make it usable from a window this small, and both are the same traps owned
    /// windows hit here:</para>
    /// <list type="bullet">
    ///   <item><c>ShouldConstrainToRootBounds = false</c> — a flyout can be 400px wide and 34px of
    ///   chrome tall, and a menu constrained to that gets clipped exactly like the
    ///   <c>ContentDialog</c> the item editors had to stop using.</item>
    ///   <item><c>_isMenuOpen</c> pins the flyout for as long as the menu is up. Once unconstrained
    ///   the menu is hosted in a popup of its own, so opening it deactivates the flyout — which
    ///   would dismiss it and take the menu down with it.</item>
    /// </list>
    /// </remarks>
    private void ShowMoreMenu()
    {
        var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
        };

        // ── How it presents itself ──────────────────────────────
        menu.Items.Add(Toggle("Regular window", _launcher.WebRegularWindow, on =>
        {
            _launcher.WebRegularWindow = on;
            ApplyWindowMode();
        }));

        // Only meaningful as a window: a flyout already dismisses on focus loss unless pinned, and
        // its pin is the control for that. Hidden rather than disabled — a greyed item still reads
        // as something you are missing out on.
        if (_launcher.WebRegularWindow)
        {
            menu.Items.Add(Toggle("Close when focus is lost", _launcher.WebWindowAutoHide, on =>
            {
                _launcher.WebWindowAutoHide = on;
            }));
        }

        menu.Items.Add(Toggle(PinMenuLabel(), _launcher.WebPinFlyout, on =>
        {
            _launcher.WebPinFlyout = on;
            SetTopmost(true);
            UpdatePinButton();
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        // ── What it shows ───────────────────────────────────────
        menu.Items.Add(Toggle("Address bar", _launcher.WebShowAddressBar, on =>
        {
            _launcher.WebShowAddressBar = on;
            ApplyAddressBarVisibility();
        }));

        menu.Items.Add(Toggle("Reload when opened", _launcher.WebReloadOnShow, on =>
        {
            _launcher.WebReloadOnShow = on;
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        // ── Where and how big it opens ──────────────────────────
        menu.Items.Add(BuildAnchorSubmenu());

        // The pair a user reaches for straight after dragging a flyout and finding the change did
        // not stick — which is exactly a moment spent looking at the flyout, so the menu is the
        // right place for them rather than two windows away.
        // "…changes", not "…position": what is remembered is the *edit*, not the value. Without it
        // the label reads as "does this launcher have a position", which it always does — the
        // question is whether dragging it somewhere else outlives the visit.
        menu.Items.Add(Toggle("Remember position changes", _launcher.WebRememberPosition, on =>
        {
            _launcher.WebRememberPosition = on;

            // Dropping the remembered position on the way out, so turning this off actually
            // returns the flyout to its anchor rather than leaving it parked where it last was.
            if (!on) _launcher.WebFlyoutPosition = "";
        }));

        // WebLockSize is the inverse of what is shown, because this one is on by default and a
        // bool defaulting to true cannot be turned off under WhenWritingDefault. Inverted here, in
        // the line that builds the item — never in the model.
        menu.Items.Add(Toggle("Remember size changes", !_launcher.WebLockSize, on =>
        {
            _launcher.WebLockSize = !on;
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem { Text = "Launcher settings…" };
        settings.Click += (_, _) => _ = OpenLauncherSettingsAsync();
        menu.Items.Add(settings);

        // The flyout must not dismiss itself while the menu is up, and must be free to again the
        // moment it closes — including when the menu is dismissed by clicking away, which is the
        // common case and raises Closed just the same.
        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) => _isMenuOpen = false;

        menu.ShowAt(_moreButton);
    }

    /// <summary>
    /// The "Opens at" submenu — the one promoted geometry option that is not a toggle.
    /// </summary>
    /// <remarks>
    /// <para>A ten-way choice, so it is a submenu of radio items rather than something checkable.
    /// It belongs with Remember position for the reason its settings row's subtitle gives: with
    /// Remember position on, the anchor decides only the <em>first</em> open, and reading one
    /// without the other is how someone concludes the anchor does nothing.</para>
    /// <para>Picking one clears <c>WebFlyoutPosition</c>, exactly as the settings row does. A
    /// remembered position outranks the anchor, so leaving it in place would mean choosing a corner
    /// and watching the flyout open precisely where it did before.</para>
    /// </remarks>
    private MenuFlyoutSubItem BuildAnchorSubmenu()
    {
        var submenu = new MenuFlyoutSubItem { Text = "Opens at" };

        (string Label, int Value)[] anchors =
        [
            ("Near its tray icon", WebAnchors.Tray),
            ("Top left", WebAnchors.TopLeft),
            ("Top centre", WebAnchors.TopCenter),
            ("Top right", WebAnchors.TopRight),
            ("Left", WebAnchors.Left),
            ("Centre", WebAnchors.Center),
            ("Right", WebAnchors.Right),
            ("Bottom left", WebAnchors.BottomLeft),
            ("Bottom centre", WebAnchors.BottomCenter),
            ("Bottom right", WebAnchors.BottomRight),
        ];

        int current = WebAnchors.Normalize(_launcher.WebAnchor);
        foreach (var (label, value) in anchors)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = label,
                GroupName = "WebAnchor",
                IsChecked = value == current,
            };
            item.Click += (_, _) =>
            {
                if (WebAnchors.Normalize(_launcher.WebAnchor) == value) return;

                _launcher.WebAnchor = value;
                _launcher.WebFlyoutPosition = "";
                SettingsManager.SaveSettings();
                Services.AutoSyncService.NotifyLaunchersChanged();
            };
            submenu.Items.Add(item);
        }

        return submenu;
    }

    /// <summary>
    /// Builds a checkable item that writes the launcher and saves.
    /// </summary>
    /// <remarks>
    /// The save is here rather than in each callback so no item can forget it, and
    /// <c>NotifyLaunchersChanged</c> rides along with it — a launcher change that saves without
    /// telling the sync service is reverted by the next periodic download.
    /// </remarks>
    private static ToggleMenuFlyoutItem Toggle(string text, bool isChecked, Action<bool> apply)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked };
        item.Click += (_, _) =>
        {
            apply(item.IsChecked);
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        return item;
    }

    /// <summary>The pin's label, which names what it does in this launcher's current mode.</summary>
    /// <remarks>
    /// <see cref="Launcher.WebPinFlyout"/> is one flag with two readings — see
    /// <c>PinTooltip</c>. The menu spells them out where the header button only has a glyph.
    /// </remarks>
    private string PinMenuLabel() =>
        _launcher.WebRegularWindow ? "Always on top" : "Stay open when focus is lost";

    /// <summary>
    /// Applies a change of window mode to the live window.
    /// </summary>
    /// <remarks>
    /// Everything regular-window mode changes is window state rather than something re-read per
    /// open, so switching it from the menu has to push all of it now: the taskbar button and
    /// switcher eligibility, always-on-top (which the pin's meaning depends on), and whether the
    /// window may be minimized — without which its taskbar button's click does nothing at all.
    /// </remarks>
    private void ApplyWindowMode()
    {
        SetMinimizable(_launcher.WebRegularWindow);
        SetTopmost(true);
        UpdatePinButton();
        ApplyTaskbarButton(_isOpen);
    }
}
