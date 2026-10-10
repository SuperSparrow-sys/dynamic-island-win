using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DynamicBay.Core;

/// <summary>
/// Small secret vault (passwords, refresh tokens) encrypted with Windows DPAPI for the current user.
/// Secrets never go into settings.json.
/// </summary>
public static class SecretStore
{
    private static string Folder => Path.Combine(AppSettings.Folder, "secrets");

    private static string FileFor(string key) =>
        Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".bin");

    public static void Set(string key, string? value)
    {
        try
        {
            if (string.IsNullOrEmpty(value)) { File.Delete(FileFor(key)); return; }
            Directory.CreateDirectory(Folder);
            // Written next to it and swapped in: a crash mid-write never leaves half a secret (e.g. a lost refresh token).
            string file = FileFor(key), tmp = $"{file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) { Log.Error("SecretStore", ex); }
    }

    public static string? Get(string key)
    {
        try
        {
            var f = FileFor(key);
            return File.Exists(f) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser)) : null;
        }
        catch { return null; }
    }
}
