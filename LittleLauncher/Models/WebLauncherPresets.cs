// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace LittleLauncher.Models;

/// <summary>A creation template, not a lasting link that can overwrite user customizations.</summary>
internal sealed record WebLauncherPreset(string Name, string Address, int Width, int Height)
{
    /// <summary>
    /// Creates an independent launcher with messaging-friendly dimensions and background
    /// activity. Notification permissions still belong to the browser's normal consent flow.
    /// Existing names are used only to choose a distinguishable name for another instance.
    /// </summary>
    public Launcher Create(IEnumerable<string> existingNames)
    {
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = Name;
        for (int suffix = 2; names.Contains(name); suffix++)
            name = $"{Name} ({suffix})";

        var launcher = new Launcher
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Kind = LauncherKinds.Web,
            ShowTitle = true,
            WebSharedProfile = true,
            WebHomeUrl = Address,
            WebFlyoutWidth = Width,
            WebFlyoutHeight = Height,
            WebHiddenPolicy = WebHiddenPolicies.KeepRunning,
        };
        launcher.WebBookmarks.Add(new WebBookmark(Name, Address));
        return launcher;
    }
}

/// <summary>Official web entry points offered by the Add Launcher menu.</summary>
internal static class WebLauncherPresets
{
    public static IReadOnlyList<WebLauncherPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new WebLauncherPreset("WhatsApp", "https://web.whatsapp.com/", 1000, 760),
        new WebLauncherPreset("Google Messages", "https://messages.google.com/web/", 1000, 760),
        new WebLauncherPreset("Messenger", "https://www.messenger.com/", 1000, 760),
        new WebLauncherPreset("Discord", "https://discord.com/app", 1100, 800),
        new WebLauncherPreset("Teams", "https://teams.microsoft.com/", 1200, 800),
    });
}
