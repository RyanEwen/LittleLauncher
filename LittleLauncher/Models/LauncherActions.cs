// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace LittleLauncher.Models;

/// <summary>
/// The launcher-level commands a taskbar jump list offers below the launcher's own entries.
/// </summary>
/// <remarks>
/// <para><b>Numbers, not names, because the companion exe carries them.</b> A jump list task can
/// only be a command line, so the action travels as <c>--action {n}</c> through
/// <c>LittleLauncherFlyout.exe</c>, which forwards it in a window message without knowing what any
/// of it means. Keeping the meaning on this side is what stops the two projects owning two copies
/// of the same table and drifting apart.</para>
/// <para><b>Never renumber one.</b> The numbers are baked into shell links sitting in the user's
/// profile, and a published list outlives the version that wrote it - so a renumbered action would
/// have an old pin quietly invoking the wrong command. Append instead, and leave retired numbers
/// unused.</para>
/// </remarks>
internal static class LauncherActions
{
    /// <summary>Not an action. What an unparsable or absent <c>--action</c> resolves to.</summary>
    public const int None = 0;

    /// <summary>Opens this launcher's settings window.</summary>
    public const int LauncherSettings = 1;

    /// <summary>Opens the launcher's flyout straight into edit mode. Item launchers only.</summary>
    public const int EditItems = 2;

    /// <summary>Opens the launcher's address in the real browser. Web launchers only.</summary>
    public const int OpenInBrowser = 3;

    /// <summary>Opens the app's own settings window.</summary>
    public const int AppSettings = 4;
}
