// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleLauncher.Models;

/// <summary>String constants for <see cref="Launcher.TrayIconMode"/>.</summary>
public static class TrayIconModes
{
    public const string Blue = "Blue";
    public const string Green = "Green";
    public const string Teal = "Teal";
    public const string Red = "Red";
    public const string Orange = "Orange";
    public const string Purple = "Purple";
    public const string Pin = "Pin";
    public const string Star = "Star";
    public const string Heart = "Heart";
    public const string Lightning = "Lightning";
    public const string Search = "Search";
    public const string Globe = "Globe";
    public const string Custom = "Custom";
    public const string Composite = "Composite";

    /// <summary>Prefix for gallery-chosen glyph/emoji icons stored in TrayIconMode.</summary>
    public const string GlyphPrefix = "Glyph:";

    /// <summary>Returns true if the mode is a gallery-chosen glyph/emoji (starts with "Glyph:").</summary>
    public static bool IsGlyphMode(string? mode) => mode?.StartsWith(GlyphPrefix, StringComparison.Ordinal) == true;

    /// <summary>Extracts the glyph character(s) from a "Glyph:X" or "Glyph:#RRGGBB:X" mode string.</summary>
    public static string? GetGlyphCharacter(string? mode)
    {
        if (!IsGlyphMode(mode)) return null;
        string payload = mode![GlyphPrefix.Length..];
        // If payload starts with "#", color is encoded: "#RRGGBB:glyph"
        if (payload.StartsWith('#') && payload.Length > 8 && payload[7] == ':')
            return payload[8..];
        return payload;
    }

    /// <summary>Extracts the optional hex color from a "Glyph:#RRGGBB:X" mode string. Returns null if no color.</summary>
    public static string? GetGlyphColor(string? mode)
    {
        if (!IsGlyphMode(mode)) return null;
        string payload = mode![GlyphPrefix.Length..];
        if (payload.StartsWith('#') && payload.Length > 8 && payload[7] == ':')
            return payload[..7]; // "#RRGGBB"
        return null;
    }

    /// <summary>Creates a TrayIconMode string for an arbitrary glyph character with optional color.</summary>
    public static string ToGlyphMode(string glyph, string? color = null) =>
        string.IsNullOrEmpty(color)
            ? GlyphPrefix + glyph
            : GlyphPrefix + color + ":" + glyph;

    /// <summary>Maps legacy integer TrayIconMode values to string constants.</summary>
    internal static string FromLegacyInt(int mode) => mode switch
    {
        0 => Blue, 1 => Green, 2 => Teal, 3 => Red, 4 => Orange, 5 => Purple,
        6 => Pin, 7 => Star, 8 => Heart, 9 => Lightning, 10 => Search, 11 => Globe,
        12 => Custom, 13 => Composite,
        _ => Blue,
    };
}

/// <summary>Integer constants for <see cref="Launcher.Kind"/>.</summary>
public static class LauncherKinds
{
    /// <summary>The tray icon opens a flyout of launcher items. The original — and default — kind.</summary>
    public const int Items = 0;

    /// <summary>The tray icon opens a web flyout showing <see cref="Launcher.WebUrl"/>.</summary>
    public const int Web = 1;

    public static int Normalize(int value) => value == Web ? Web : Items;

    public static bool IsWeb(int value) => Normalize(value) == Web;
}

/// <summary>
/// Integer constants for <see cref="Launcher.WebHiddenPolicy"/> — what a web launcher's
/// browser does once its flyout is dismissed.
/// </summary>
/// <remarks>
/// The default is deliberately the cheapest option: a web flyout exists to be looked at for a
/// few seconds, and a Home Assistant dashboard full of camera cards keeps decoding video for
/// as long as its renderer lives. Ordered so that the value that costs the least when hidden
/// is <c>0</c> — <c>WhenWritingDefault</c> omits it from settings.json entirely.
/// </remarks>
public static class WebHiddenPolicies
{
    /// <summary>Suspend on dismiss, then tear the browser down after <see cref="Launcher.WebIdleUnloadMinutes"/>.</summary>
    public const int UnloadWhenIdle = 0;

    /// <summary>Suspend on dismiss but keep the browser alive, so the next open is instant.</summary>
    public const int Suspend = 1;

    /// <summary>Leave the page running while hidden. For pages that must not lose state or stream.</summary>
    public const int KeepRunning = 2;

    public static int Normalize(int value) => value switch
    {
        Suspend => Suspend,
        KeepRunning => KeepRunning,
        _ => UnloadWhenIdle,
    };
}

/// <summary>
/// Where a web flyout opens when it has not been moved. See <see cref="Launcher.WebAnchor"/>.
/// </summary>
/// <remarks>
/// <see cref="Tray"/> is <c>0</c> so the tray-relative placement every web launcher shipped with
/// is what <c>WhenWritingDefault</c> leaves in the file — which is to say, nothing.
/// </remarks>
public static class WebAnchors
{
    /// <summary>Above (or below) the tray icon that was clicked, as a flyout normally behaves.</summary>
    public const int Tray = 0;

    public const int TopLeft = 1;
    public const int TopCenter = 2;
    public const int TopRight = 3;
    public const int Left = 4;
    public const int Center = 5;
    public const int Right = 6;
    public const int BottomLeft = 7;
    public const int BottomCenter = 8;
    public const int BottomRight = 9;

    public static int Normalize(int value) =>
        value >= Tray && value <= BottomRight ? value : Tray;

    public static bool IsTop(int a) => a is TopLeft or TopCenter or TopRight;
    public static bool IsBottom(int a) => a is BottomLeft or BottomCenter or BottomRight;
    public static bool IsLeft(int a) => a is TopLeft or Left or BottomLeft;
    public static bool IsRight(int a) => a is TopRight or Right or BottomRight;
}

/// <summary>Integer constants for <see cref="Launcher.ViewMode"/>.</summary>
public static class LauncherViewModes
{
    public const int Icon = 0;
    public const int List = 1;
    public const int SmallIcon = 2;

    public static int Normalize(int value) => value switch
    {
        List => List,
        SmallIcon => SmallIcon,
        _ => Icon,
    };

    public static bool IsIconMode(int value) => Normalize(value) != List;
}

/// <summary>
/// Reads TrayIconMode as a string. If the JSON token is a number (legacy format),
/// converts it via <see cref="TrayIconModes.FromLegacyInt"/>.
/// </summary>
public class TrayIconModeJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return TrayIconModes.FromLegacyInt(reader.GetInt32());
        return reader.GetString() ?? TrayIconModes.Blue;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Represents a named launcher: a set of launcher items with their own tray icon
/// appearance and a stable GUID-based identity for cross-process routing.
/// </summary>
public partial class Launcher : ObservableObject
{
    public const int MinIconModeIconsPerRow = 1;
    public const int DefaultIconModeIconsPerRow = 3;
    public const int MaxIconModeIconsPerRow = 12;

    // ── Web flyout geometry ──────────────────────────────────────────
    // Zero means "unset" for every one of these, because WhenWritingDefault omits a 0 from
    // settings.json — so the resolvers below, not the field initialisers, hold the defaults.

    public const int DefaultWebFlyoutWidth = 480;
    public const int DefaultWebFlyoutHeight = 720;
    public const int MinWebFlyoutWidth = 320;
    public const int MinWebFlyoutHeight = 240;
    public const int MaxWebFlyoutDimension = 4000;
    public const int DefaultWebIdleUnloadMinutes = 5;
    public const int MinWebZoomPercent = 25;
    public const int MaxWebZoomPercent = 400;

    public static int ClampIconModeIconsPerRow(int value) => Math.Clamp(value, MinIconModeIconsPerRow, MaxIconModeIconsPerRow);

    /// <summary>Resolved web flyout width in DIPs, with 0 meaning the default.</summary>
    [JsonIgnore]
    public int ResolvedWebFlyoutWidth => WebFlyoutWidth <= 0
        ? DefaultWebFlyoutWidth
        : Math.Clamp(WebFlyoutWidth, MinWebFlyoutWidth, MaxWebFlyoutDimension);

    /// <summary>Resolved web flyout height in DIPs, with 0 meaning the default.</summary>
    [JsonIgnore]
    public int ResolvedWebFlyoutHeight => WebFlyoutHeight <= 0
        ? DefaultWebFlyoutHeight
        : Math.Clamp(WebFlyoutHeight, MinWebFlyoutHeight, MaxWebFlyoutDimension);

    /// <summary>Resolved page zoom factor, with 0 meaning 100%.</summary>
    [JsonIgnore]
    public double ResolvedWebZoomFactor => WebZoomPercent <= 0
        ? 1.0
        : Math.Clamp(WebZoomPercent, MinWebZoomPercent, MaxWebZoomPercent) / 100.0;

    /// <summary>Resolved idle-unload delay, with 0 meaning the default.</summary>
    [JsonIgnore]
    public int ResolvedWebIdleUnloadMinutes => WebIdleUnloadMinutes <= 0
        ? DefaultWebIdleUnloadMinutes
        : Math.Clamp(WebIdleUnloadMinutes, 1, 720);

    /// <summary>
    /// Stable GUID-based identifier.
    /// Used as the key for per-launcher tray icons, flyout windows, and companion shortcuts.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name shown in the tray icon tooltip and the settings Launchers page.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "Launcher";

    /// <summary>
    /// Tray icon style for this launcher.
    /// Use <see cref="TrayIconModes"/> constants: "Blue", "Green", "Teal", "Red", "Orange", "Purple",
    /// "Pin", "Star", "Heart", "Lightning", "Search", "Globe", "Custom", "Composite".
    /// </summary>
    [ObservableProperty]
    [JsonConverter(typeof(TrayIconModeJsonConverter))]
    public partial string TrayIconMode { get; set; } = TrayIconModes.Composite;

    /// <summary>Path to a custom tray icon file (.ico or .png) when TrayIconMode is "Custom".</summary>
    [ObservableProperty]
    public partial string CustomTrayIconPath { get; set; } = "";

    /// <summary>When true, this launcher's tray icon is hidden from the system tray.</summary>
    [ObservableProperty]
    public partial bool NIconHide { get; set; }

    /// <summary>
    /// Flyout display mode for this launcher.
    /// 0 = Icon (larger icon with name below, wrapping grid),
    /// 1 = List (icon + name side by side),
    /// 2 = Small Icon (tray-sized icon grid with no item labels).
    /// </summary>
    [ObservableProperty]
    public partial int ViewMode { get; set; }

    /// <summary>
    /// Number of icons shown across each icon-mode column.
    /// Defaults to 3 and is clamped to a sensible 1-12 range.
    /// </summary>
    [ObservableProperty]
    public partial int IconModeIconsPerRow { get; set; } = DefaultIconModeIconsPerRow;

    /// <summary>When true, the launcher name is shown at the top of the flyout popup.</summary>
    [ObservableProperty]
    public partial bool ShowTitle { get; set; }

    // ── Web launcher ────────────────────────────────────────────────

    /// <summary>
    /// What this launcher's tray icon opens.
    /// Use <see cref="LauncherKinds"/> constants: 0 = a flyout of items, 1 = a web panel.
    /// </summary>
    [ObservableProperty]
    public partial int Kind { get; set; }

    /// <summary>The page a web launcher shows. Ignored unless <see cref="Kind"/> is Web.</summary>
    [ObservableProperty]
    public partial string WebUrl { get; set; } = "";

    /// <summary>
    /// Web flyout width in DIPs. 0 means "not set" and resolves to
    /// <see cref="DefaultWebFlyoutWidth"/> — the property is omitted from settings.json while
    /// it holds the CLR default, so 0 has to mean the default rather than a real size.
    /// </summary>
    [ObservableProperty]
    public partial int WebFlyoutWidth { get; set; }

    /// <summary>Web flyout height in DIPs. 0 resolves to <see cref="DefaultWebFlyoutHeight"/>.</summary>
    [ObservableProperty]
    public partial int WebFlyoutHeight { get; set; }

    /// <summary>Page zoom as a percentage. 0 resolves to 100.</summary>
    [ObservableProperty]
    public partial int WebZoomPercent { get; set; }

    /// <summary>What the browser does when the panel is dismissed. See <see cref="WebHiddenPolicies"/>.</summary>
    [ObservableProperty]
    public partial int WebHiddenPolicy { get; set; }

    /// <summary>
    /// Minutes the flyout may sit dismissed before the browser is torn down, under
    /// <see cref="WebHiddenPolicies.UnloadWhenIdle"/>. 0 resolves to
    /// <see cref="DefaultWebIdleUnloadMinutes"/>.
    /// </summary>
    [ObservableProperty]
    public partial int WebIdleUnloadMinutes { get; set; }

    /// <summary>Reload the page every time the flyout is opened, rather than showing it as it was left.</summary>
    [ObservableProperty]
    public partial bool WebReloadOnShow { get; set; }

    /// <summary>
    /// Keep the flyout on screen when it loses focus, instead of dismissing like a flyout.
    /// </summary>
    /// <remarks>
    /// Phrased as "keep open" rather than "auto-hide" so the default behaviour is <c>false</c>:
    /// a bool that defaults to <c>true</c> cannot be turned off in this settings file, because
    /// <c>WhenWritingDefault</c> drops <c>false</c> on write and the field initialiser puts it
    /// back on load.
    /// </remarks>
    [ObservableProperty]
    public partial bool WebPinFlyout { get; set; }

    /// <summary>
    /// Grant camera, microphone, location, notifications and the rest to this launcher's pages
    /// without asking.
    /// </summary>
    /// <remarks>
    /// Phrased so <c>false</c> — ask — is the default, both because that is the safe direction and
    /// because <c>WhenWritingDefault</c> would otherwise make it impossible to turn off. The
    /// decision is the toggle itself, so a request answered by it is deliberately **not** saved
    /// into the WebView2 profile: turning it back off must return the launcher to asking rather
    /// than leave a profile full of silent grants behind.
    /// </remarks>
    [ObservableProperty]
    public partial bool WebAllowAllPermissions { get; set; }

    /// <summary>
    /// Put this launcher's cookies, logins and cache in one profile shared with every other
    /// launcher that sets it, instead of its own private one.
    /// </summary>
    /// <remarks>
    /// Isolation is the default because it is the one that cannot surprise anyone — two launchers
    /// can be two accounts on the same site. Sharing is for the opposite case: several launchers
    /// onto the same system, where a private profile means signing in to it once per launcher and
    /// again whenever a session expires.
    /// <para>
    /// Off by default, so the CLR default under <c>WhenWritingDefault</c> is the isolated
    /// behaviour that shipped first — an existing launcher cannot be moved onto a profile it was
    /// never signed in to by an upgrade.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial bool WebSharedProfile { get; set; }

    /// <summary>
    /// When true this web launcher shows a bookmark bar; when false it opens
    /// <see cref="WebUrl"/> directly.
    /// </summary>
    /// <remarks>
    /// An explicit choice rather than one inferred from how many bookmarks exist. Inferring it
    /// meant adding a second bookmark silently changed what the tray icon did, and removing one
    /// changed it back.
    /// </remarks>
    [ObservableProperty]
    public partial bool WebUseBookmarks { get; set; }

    /// <summary>
    /// The bookmark opened as soon as the flyout is shown. Empty means none — the flyout opens
    /// as just the bar, and nothing loads until a bookmark is picked.
    /// </summary>
    /// <remarks>
    /// Held as a URL rather than an index so reordering the bar cannot silently change which
    /// page opens, and so the CLR default (empty) means "none" under
    /// <c>WhenWritingDefault</c> rather than accidentally meaning "the first one".
    /// </remarks>
    [ObservableProperty]
    public partial string WebDefaultBookmarkUrl { get; set; } = "";

    /// <summary>
    /// Where this web flyout opens when it has not been moved. See <see cref="WebAnchors"/>.
    /// </summary>
    /// <remarks>
    /// Ranks below <see cref="WebFlyoutPosition"/>: a flyout the user has dragged somewhere opens
    /// where they left it, so with <see cref="WebRememberPosition"/> on this decides the *first*
    /// open and nothing after it. Changing it clears the remembered position, or picking a corner
    /// would appear to do nothing at all.
    /// </remarks>
    [ObservableProperty]
    public partial int WebAnchor { get; set; }

    /// <summary>
    /// Keep a moved flyout where it was put, across dismissals and restarts.
    /// </summary>
    /// <remarks>
    /// Off by default, which makes a move temporary: it holds while the flyout stays open, and
    /// the next one anchors to the tray again. Some moves are "shove it aside for a minute" and
    /// some are "this lives here now", and only the user knows which.
    /// </remarks>
    [ObservableProperty]
    public partial bool WebRememberPosition { get; set; }

    /// <summary>
    /// Screen position a web flyout was last moved to, as "x,y" in physical pixels.
    /// Empty means it has never been moved and should anchor to the tray as usual.
    /// </summary>
    /// <remarks>
    /// A string rather than two ints because empty is the only honest "unset" under
    /// <c>WhenWritingDefault</c> — 0,0 is a real screen position, so a numeric pair could not
    /// tell "top-left corner" from "never moved".
    /// </remarks>
    [ObservableProperty]
    public partial string WebFlyoutPosition { get; set; } = "";

    /// <summary>
    /// Hold the flyout at its configured size, making any drag-resize temporary.
    /// </summary>
    /// <remarks>
    /// <para>Surfaced as <b>Remember Size</b>, which is <b>on</b> by default — so this property is
    /// its inverse, and off means locked. A bool that defaults to <c>true</c> cannot be turned off
    /// in this settings file: <c>WhenWritingDefault</c> drops <c>false</c> on save and the field
    /// initialiser puts <c>true</c> back on load. See
    /// <c>.claude/docs/user-settings.md</c>.</para>
    /// <para>Locking is how a size is pinned down: set the width and height you want, turn Remember
    /// Size off, and dragging the edges then only lasts as long as the flyout is open. It is the
    /// same bargain the header's maximize makes, one step less drastic — and unlike maximize it is
    /// a property of the launcher, because "this one is always this big" is a decision about the
    /// launcher rather than about one viewing.</para>
    /// </remarks>
    [ObservableProperty]
    public partial bool WebLockSize { get; set; }

    /// <summary>
    /// Show only icons in the bookmark bar, without their labels.
    /// </summary>
    /// <remarks>
    /// For a bar of familiar sites, the labels are the widest part of every button and say the
    /// least — the favicon already identifies the page. Names still exist and become tooltips, so
    /// nothing is lost, and a bar that scrolled now usually does not.
    /// </remarks>
    [ObservableProperty]
    public partial bool WebBookmarkIconsOnly { get; set; }

    /// <summary>Bookmarks shown as a bar along the bottom of the web flyout.</summary>
    public ObservableCollection<WebBookmark> WebBookmarks { get; set; } = [];

    /// <summary>True when this launcher opens a web flyout rather than an item flyout.</summary>
    [JsonIgnore]
    public bool IsWebLauncher => LauncherKinds.IsWeb(Kind);

    /// <summary>
    /// True when the flyout should open as a bookmark bar rather than straight into a page.
    /// </summary>
    [JsonIgnore]
    public bool HasWebBookmarkBar => IsWebLauncher && WebUseBookmarks && WebBookmarks.Count > 0;

    /// <summary>Parses <see cref="WebFlyoutPosition"/>, or null when it has never been moved.</summary>
    public (int X, int Y)? GetWebFlyoutPosition()
    {
        string[] parts = (WebFlyoutPosition ?? "").Split(',');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return null;
        return (x, y);
    }

    /// <summary>The bookmark to open on show, or null when the flyout should open as just the bar.</summary>
    [JsonIgnore]
    public WebBookmark? DefaultWebBookmark =>
        string.IsNullOrWhiteSpace(WebDefaultBookmarkUrl)
            ? null
            : WebBookmarks.FirstOrDefault(b => string.Equals(b.Url, WebDefaultBookmarkUrl, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The address a web launcher opens when it is not showing a bar: its single bookmark if it
    /// has exactly one, otherwise <see cref="WebUrl"/>.
    /// </summary>
    [JsonIgnore]
    public string ResolvedSingleWebUrl => WebUrl;

    /// <summary>
    /// The launcher items (shortcuts, groups, headings, column breaks) in this launcher.
    /// Serialized as a JSON array.
    /// </summary>
    public ObservableCollection<LauncherItem> Items { get; set; } = [];

    // ── Sharing ─────────────────────────────────────────────────────

    /// <summary>Whether this launcher participates in per-launcher sharing.</summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// true = this user publishes (owner); false = subscribed (consumer, items read-only).
    /// Only meaningful when <see cref="IsShared"/> is true and <see cref="SharedTwoWay"/> is false.
    /// </summary>
    public bool IsSharedOwner { get; set; }

    /// <summary>
    /// When true, all participants both push and pull (last save wins).
    /// When false, sharing is 1-way: owners push, subscribers pull.
    /// </summary>
    public bool SharedTwoWay { get; set; }

    /// <summary>
    /// 0 = File (local or network path), 1 = SFTP.
    /// Determines how the shared launcher items are read/written.
    /// </summary>
    public int SharedSyncMode { get; set; }

    /// <summary>
    /// The file path (local/network) or SFTP remote path for the shared launcher JSON.
    /// For File mode: a local or UNC path (e.g. C:\shared\launcher.json or \\server\share\launcher.json).
    /// For SFTP mode: a remote path (supports ~ for home directory).
    /// </summary>
    public string SharedPath { get; set; } = "";

    /// <summary>SFTP host for the shared launcher file (SFTP mode only).</summary>
    public string SharedSftpHost { get; set; } = "";

    /// <summary>SFTP port for the shared launcher file (SFTP mode only).</summary>
    public int SharedSftpPort { get; set; } = 22;

    /// <summary>SFTP username — defaults to Windows username if empty (SFTP mode only).</summary>
    public string SharedSftpUsername { get; set; } = "";

    /// <summary>Path to a private key for the shared SFTP connection — auto-detected if empty (SFTP mode only).</summary>
    public string SharedSftpPrivateKeyPath { get; set; } = "";

    /// <summary>
    /// Legacy property for backward compatibility with settings saved before SharedPath was introduced.
    /// On deserialization, migrates its value into <see cref="SharedPath"/> and sets SFTP mode.
    /// </summary>
    public string SharedSftpRemotePath
    {
        get => "";
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(SharedPath))
            {
                SharedPath = value;
                SharedSyncMode = 1; // SFTP
            }
        }
    }

    /// <summary>WebDAV collection URL holding the shared file (WebDAV mode only).</summary>
    /// <remarks>
    /// Separate from the global <c>UserSettings.WebDavUrl</c> on purpose: the server you sync your
    /// own launchers to and the server a colleague shares one from are routinely different, and
    /// each subscriber points at their own account on the sharing server anyway.
    /// </remarks>
    public string SharedWebDavUrl { get; set; } = "";

    /// <summary>WebDAV username for the shared file. The password lives in <c>ProtectedStore</c>.</summary>
    public string SharedWebDavUsername { get; set; } = "";

    /// <summary>
    /// The share link for a launcher shared through a cloud account.
    /// </summary>
    /// <remarks>
    /// This is what a subscriber is given and all they need — the file's location in the owner's
    /// drive is the owner's business, and may not even be readable to them. The owner keeps a copy
    /// here too so the link can be shown again without re-minting it.
    /// </remarks>
    public string SharedLinkUrl { get; set; } = "";

    /// <summary>
    /// The resolved drive and item ids behind <see cref="SharedLinkUrl"/>, cached after the first
    /// lookup so every sync does not re-resolve the link.
    /// </summary>
    public string SharedItemId { get; set; } = "";

    /// <inheritdoc cref="SharedItemId"/>
    public string SharedDriveId { get; set; } = "";

    /// <summary>Convenience: true when SharedSyncMode is OneDrive (3).</summary>
    [JsonIgnore]
    public bool IsOneDriveSync => SharedSyncMode == SharedSyncModes.OneDrive;

    /// <summary>Convenience: true when SharedSyncMode is File (0).</summary>
    [JsonIgnore]
    public bool IsFileSync => SharedSyncMode == SharedSyncModes.File;

    /// <summary>Convenience: true when SharedSyncMode is SFTP (1).</summary>
    [JsonIgnore]
    public bool IsSftpSync => SharedSyncMode == SharedSyncModes.Sftp;

    /// <summary>Convenience: true when SharedSyncMode is WebDAV (2).</summary>
    [JsonIgnore]
    public bool IsWebDavSync => SharedSyncMode == SharedSyncModes.WebDav;

    // ── Constructor (defaults for JsonSerializer) ────────────────────

    public Launcher()
    {
        Name = "Launcher";
        TrayIconMode = TrayIconModes.Composite;
        CustomTrayIconPath = "";
        NIconHide = false;
        Items = [];
    }

    // ── Factory ─────────────────────────────────────────────────────

    /// <summary>Creates a new launcher with sample starter items.</summary>
    internal static Launcher CreateDefault()
    {
        var launcher = new Launcher { Id = Guid.NewGuid().ToString(), Name = "Default" };
        launcher.Items.Add(new LauncherItem("Google", "https://www.google.com", "\uE774", isWebsite: true));
        launcher.Items.Add(new LauncherItem("Explorer", "explorer.exe", "Folder24"));
        launcher.Items.Add(new LauncherItem("Notepad", "notepad.exe", "Notepad24"));
        return launcher;
    }
}
