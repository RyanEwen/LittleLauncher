// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Text.Json.Serialization;

namespace LittleLauncher.Models;

/// <summary>
/// One browser extension this app knows about — enough to reproduce it on another machine.
/// </summary>
/// <remarks>
/// <para><b>The identity travels; the copy does not.</b> An unpacked extension is megabytes of
/// files, and the sync path exists to move one small JSON document — so what crosses is the store
/// id, which is all another machine needs to fetch its own copy from the same place this one did.
/// The name rides along so a machine can list an extension it has not fetched yet.</para>
/// <para><b>The synced list is authoritative, which is what makes removal work.</b> An extension
/// missing from the downloaded list is one that was uninstalled somewhere, so the machine reading it
/// uninstalls too. That is the same rule the bookmark collection follows.</para>
/// </remarks>
public class BrowserExtension
{
    /// <summary>
    /// The Chrome Web Store id, or empty for one added from a folder.
    /// </summary>
    /// <remarks>
    /// Empty means <b>local only</b>: there is no id to fetch by, so nothing on another machine can
    /// reproduce it, and it is deliberately left out of what is synced. An extension added from a
    /// zip stays on the machine it was added to, and says so in the settings list.
    /// </remarks>
    public string Id { get; set; } = "";

    /// <summary>What to call it in a list, resolved from the manifest at install time.</summary>
    /// <remarks>
    /// Stored rather than re-read, because a machine that has not fetched the extension yet has no
    /// manifest to read it from — see <c>BrowserExtensionService.ReadName</c> for the
    /// <c>__MSG_</c> indirection it had to go through to get this.
    /// </remarks>
    public string Name { get; set; } = "";

    /// <summary>Where the unpacked copy lives on <em>this</em> machine.</summary>
    /// <remarks>
    /// <para>Local truth, and it must be <b>written to settings.json</b> — it is the only record of
    /// where the unpacked copy is, so an extension without it cannot be loaded into a profile or
    /// shown in the header.</para>
    /// <para><b>Not <c>[JsonIgnore]</c>, though it must never sync.</b> It was, briefly, to keep it
    /// out of the payload — and since local settings use this same class, that dropped the folder
    /// from disk as well: every extension came back after a restart with an empty path, loaded into
    /// nothing and vanished from the header. What keeps it out of the payload is
    /// <c>BrowserExtensionService.Portable</c>, which projects id and name into fresh objects and
    /// never copies this. An attribute cannot express "persist here, not there"; a projection can.
    /// </para>
    /// </remarks>
    public string Folder { get; set; } = "";

    /// <summary>True when another machine could fetch this one for itself.</summary>
    [JsonIgnore]
    public bool IsPortable => !string.IsNullOrEmpty(Id);
}
