using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using System.Text.Json;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Services;

/// <summary>
/// What was read out of a shared launcher file.
/// </summary>
/// <param name="Items">The shared items.</param>
/// <param name="TwoWay">
/// Whether the owner published this as 2-way. <c>null</c> for a legacy file that predates the
/// envelope and therefore cannot say.
/// </param>
/// <param name="OwnerName">The owner's name for the launcher, or empty for a legacy file.</param>
internal sealed record SharedLauncherFile(List<LauncherItem> Items, bool? TwoWay, string OwnerName);

/// <summary>
/// The wire format of one shared launcher, and what happens when a copy arrives.
/// </summary>
/// <remarks>
/// <para>Shared with every sharing transport for the same reason <see cref="LauncherPayload"/> is
/// shared by the global ones: the file has to be byte-identical whichever way it travelled, or an
/// owner publishing over WebDAV and a subscriber pulling from a synced folder could not exchange
/// anything.</para>
/// <para><b>The envelope exists so direction travels with the share.</b> Whether a launcher is
/// 1-way or 2-way is the owner's decision about their launcher, but the subscriber used to be
/// asked to pick it when adding one — a question they have no way to answer and every reason to
/// get wrong. Now the owner writes it and the subscriber reads it.</para>
/// </remarks>
internal static class SharedLauncherPayload
{
    private static readonly JsonSerializerOptions JsonOptions = LauncherPayload.JsonOptions;

    /// <summary>
    /// The envelope written today. Older files are a bare <c>List&lt;LauncherItem&gt;</c>.
    /// </summary>
    private sealed class SharedEnvelope
    {
        public bool TwoWay { get; set; }
        public string Name { get; set; } = "";
        public DateTimeOffset LastModified { get; set; }
        public List<LauncherItem> Items { get; set; } = [];
    }

    /// <summary>Serialize a launcher's items and how it is being shared.</summary>
    public static byte[] Serialize(Launcher launcher) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new SharedEnvelope
            {
                TwoWay = launcher.SharedTwoWay,
                Name = launcher.Name,
                LastModified = DateTimeOffset.UtcNow,
                Items = new List<LauncherItem>(launcher.Items),
            },
            JsonOptions);

    /// <summary>
    /// Parse a shared launcher file, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Envelope first, then the legacy bare array. Both must keep working: files published by
    /// earlier versions are already sitting on servers and in shared folders, and a subscriber
    /// upgrading must not lose the launcher they were subscribed to. A JSON array cannot
    /// deserialize into the envelope object, so the fallback is unambiguous.
    /// </remarks>
    public static SharedLauncherFile? Deserialize(byte[] bytes)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SharedEnvelope>(bytes, JsonOptions);
            if (envelope?.Items != null)
                return new SharedLauncherFile(envelope.Items, envelope.TwoWay, envelope.Name);
        }
        catch (JsonException) { }

        try
        {
            var items = JsonSerializer.Deserialize<List<LauncherItem>>(bytes, JsonOptions);

            // Legacy: nothing recorded the direction, so say so rather than guessing. Callers
            // leave whatever the launcher already has, which is what it did before.
            return items == null ? null : new SharedLauncherFile(items, null, "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Apply a downloaded shared file to a launcher on the UI thread, fetch missing icons, save.
    /// </summary>
    /// <remarks>
    /// <para>Suppresses the auto-sync upload trigger. Without it, applying a pull marks the
    /// launchers dirty, which schedules an upload, which is seen as a local change — a feedback
    /// loop between two participants that never settles.</para>
    /// <para>Adopts the owner's direction when the file states one. That is what stops a
    /// subscriber having to know, and it means an owner switching a share to read-only actually
    /// takes effect on the other end rather than being a local preference each side keeps its own
    /// answer to.</para>
    /// </remarks>
    public static async Task ApplyAsync(Launcher launcher, SharedLauncherFile file)
    {
        AutoSyncService.SuppressNextChange = true;

        var tcs = new TaskCompletionSource();
        App.MainDispatcherQueue.TryEnqueue(() =>
        {
            if (file.TwoWay.HasValue && !launcher.IsSharedOwner)
                launcher.SharedTwoWay = file.TwoWay.Value;

            launcher.Items.Clear();
            foreach (var item in file.Items)
            {
                item.NormalizeGlyph();
                launcher.Items.Add(item);
            }
            tcs.SetResult();
        });
        await tcs.Task;

        await FaviconService.FetchMissingItemIconsAsync(launcher.Items);
        SettingsManager.SaveSettings();
    }
}
