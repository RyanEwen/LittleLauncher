using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// Small objects persisted outside settings.json, encrypted to the current Windows user.
/// </summary>
/// <remarks>
/// <para><b>Everything sync-related that is a credential belongs here, not in settings.json.</b>
/// That file is exported, imported, backed up and — the deciding reason — <i>uploaded by the sync
/// feature itself</i>. A refresh token or a WebDAV password in it would be copied to every machine
/// and into whatever server or folder is configured, which is the one place it must never go.</para>
/// <para><see cref="ProtectedData"/> (DPAPI) ties the ciphertext to the Windows user account, so a
/// copied file is useless on another machine or under another user. That is the appropriate bar
/// for a desktop app: it defends against the file being read or moved, not against malware already
/// running as this user, which nothing at this layer can.</para>
/// </remarks>
internal static class ProtectedStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>Extra entropy, so the blob cannot be decrypted by another app's DPAPI call alone.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LittleLauncher.CloudTokens.v1");

    private static string PathFor(string name) =>
        Path.Combine(MainWindow.GetPhysicalAppDataDir(), $"cloud-{name}.dat");

    /// <summary>Load a stored object, or null when absent or unreadable.</summary>
    public static T? Load<T>(string name) where T : class
    {
        string path = PathFor(name);
        try
        {
            if (!File.Exists(path)) return null;

            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(plain);
        }
        catch (Exception ex)
        {
            // A blob that will not decrypt is an absent-credential state, not an error to
            // propagate: it means a restored profile, a different Windows user, or a corrupted
            // file. Deleting it stops the app retrying a decrypt that can never succeed.
            Logger.Warn(ex, $"Could not read protected store '{name}'; treating as absent");
            Clear(name);
            return null;
        }
    }

    /// <summary>Persist an object, encrypted to the current user.</summary>
    public static void Save<T>(string name, T value)
    {
        string path = PathFor(name);
        try
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(value);
            byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, cipher);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Could not save protected store '{name}'");
        }
    }

    /// <summary>Forget a stored object.</summary>
    public static void Clear(string name)
    {
        string path = PathFor(name);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Could not delete {path}");
        }
    }
}
