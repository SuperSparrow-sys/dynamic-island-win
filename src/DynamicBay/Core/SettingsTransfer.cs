using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicBay.Core;

/// <summary>
/// Exports all settings into one file locked with a numeric code, and imports such a file on another PC.
/// Included: settings.json (apps, widgets, calendars, client IDs ...), Google client secrets and the script widgets.
/// Not included: passwords and sign-in tokens - after an import the user only signs in again - and the pairing with
/// other devices, which belongs to the PC (its key never leaves it).
///
/// File: "DYNBAY01" | iterations (int32) | salt (16) | nonce (12) | tag (16) | AES-256-GCM(gzip(JSON)).
/// The key comes from the code via PBKDF2-SHA256; the header is authenticated as associated data.
/// </summary>
public static class SettingsTransfer
{
    public const string Extension = ".dynamicbay";
    // 10 digits: with 600,000 PBKDF2 rounds a GPU needs days to try them all; 6 digits would take a minute.
    public const int MinPin = 10, MaxPin = 16;
    public const int DefaultIterations = 600_000;
    private const int MaxScriptBytes = 20 * 1024 * 1024;
    private static readonly byte[] Magic = "DYNBAY01"u8.ToArray();

    public sealed class Bundle
    {
        public int Format { get; set; } = 1;
        public string AppVersion { get; set; } = "";
        public DateTime Created { get; set; }
        /// <summary>settings.json as text.</summary>
        public string Settings { get; set; } = "";
        /// <summary>SecretStore entries that are configuration, not a sign-in (key → value).</summary>
        public Dictionary<string, string> Secrets { get; set; } = new();
        /// <summary>Files of the scripts folder: relative path with "/" → Base64 content.</summary>
        public Dictionary<string, string> Scripts { get; set; } = new();
    }

    public sealed class WrongPinException() : Exception("Wrong code or damaged file");

    public static bool IsValidPin(string? pin) => pin is { Length: >= MinPin and <= MaxPin } && pin.All(char.IsAsciiDigit);

    /// <summary>Which secrets travel along: the Google client secret (needed to sign in), never passwords or tokens.</summary>
    /// <summary>Settings that belong to this PC, not to the user: the pairing with other devices (the key stays here).</summary>
    internal static readonly string[] DeviceOnly = { nameof(AppSettings.PairedDevices), nameof(AppSettings.LinkEnabled), nameof(AppSettings.LinkDeviceName) };

    public static bool IsTransferableSecret(string key) => key.StartsWith("cal:", StringComparison.Ordinal) && key.EndsWith(":secret", StringComparison.Ordinal);

    public static Bundle Collect(AppSettings settings, string appVersion, string scriptsFolder, Func<string, string?> getSecret)
    {
        var bundle = new Bundle { AppVersion = appVersion, Created = DateTime.UtcNow, Settings = WithoutDeviceOnly(settings.ToJson()) };
        foreach (var account in settings.CalendarAccounts)
        {
            string key = $"cal:{account.Id}:secret";
            if (getSecret(key) is { Length: > 0 } value) bundle.Secrets[key] = value;
        }
        if (Directory.Exists(scriptsFolder))
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(scriptsFolder, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if ((total += info.Length) > MaxScriptBytes) break; // widget data caches are not worth a huge file
                string rel = Path.GetRelativePath(scriptsFolder, file).Replace('\\', '/');
                bundle.Scripts[rel] = Convert.ToBase64String(File.ReadAllBytes(file));
            }
        }
        return bundle;
    }

    public static byte[] Encrypt(Bundle bundle, string pin, int iterations = DefaultIterations)
    {
        if (!IsValidPin(pin)) throw new ArgumentException("The code must have 6 to 12 digits.", nameof(pin));
        byte[] plain;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                JsonSerializer.Serialize(gz, bundle);
            plain = ms.ToArray();
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var header = Header(iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key(pin, salt, iterations), 16))
            aes.Encrypt(nonce, plain, cipher, tag, header);
        CryptographicOperations.ZeroMemory(plain);

        using var outStream = new MemoryStream();
        outStream.Write(header);
        outStream.Write(salt);
        outStream.Write(nonce);
        outStream.Write(tag);
        outStream.Write(cipher);
        return outStream.ToArray();
    }

    /// <exception cref="WrongPinException">Wrong code, or the file was changed.</exception>
    /// <exception cref="InvalidDataException">Not a DynamicBay export.</exception>
    public static Bundle Decrypt(byte[] file, string pin)
    {
        const int fixedLength = 8 + 4 + 16 + 12 + 16;
        if (file.Length < fixedLength || !file.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Not a DynamicBay export");
        int iterations = BitConverter.ToInt32(file, 8);
        // Upper bound: a crafted file must not freeze the window for long with a huge round count
        if (iterations is < 10_000 or > 2_000_000) throw new InvalidDataException("Not a DynamicBay export");
        if (!IsValidPin(pin)) throw new WrongPinException();
        var salt = file.AsSpan(12, 16);
        var nonce = file.AsSpan(28, 12);
        var tag = file.AsSpan(40, 16);
        var cipher = file.AsSpan(fixedLength);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Key(pin, salt.ToArray(), iterations), 16);
            aes.Decrypt(nonce, cipher, tag, plain, file.AsSpan(0, 12));
        }
        catch (AuthenticationTagMismatchException) { throw new WrongPinException(); }
        try
        {
            using var gz = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
            return JsonSerializer.Deserialize<Bundle>(gz) ?? throw new InvalidDataException("Empty export");
        }
        catch (JsonException ex) { throw new InvalidDataException("Damaged export", ex); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>
    /// Writes the bundle into the data folder: settings.json (the current one stays as settings.before-import.json),
    /// the secrets and the script files. Checks everything first, so a bad file changes nothing.
    /// </summary>
    public static void Apply(Bundle bundle, string dataFolder, Action<string, string?> setSecret)
    {
        AppSettings.FromJson(bundle.Settings); // throws on broken settings before anything is written
        string scripts = Path.GetFullPath(Path.Combine(dataFolder, "scripts"));
        var files = new List<(string Path, byte[] Bytes)>();
        foreach (var (rel, content) in bundle.Scripts)
        {
            string target = Path.GetFullPath(Path.Combine(scripts, rel.Replace('/', Path.DirectorySeparatorChar)));
            // Only inside the scripts folder: a crafted file must not write anywhere else
            if (!target.StartsWith(scripts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Bad script path: {rel}");
            files.Add((target, Convert.FromBase64String(content)));
        }

        // Scripts and secrets first, settings.json last: if writing fails on the way, the settings stay as they were.
        foreach (var (path, bytes) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        foreach (var (key, value) in bundle.Secrets)
            if (IsTransferableSecret(key)) setSecret(key, value);
        string settingsFile = Path.Combine(dataFolder, "settings.json");
        string settings = WithoutDeviceOnly(bundle.Settings);
        if (File.Exists(settingsFile))
        {
            File.Copy(settingsFile, Path.Combine(dataFolder, "settings.before-import.json"), overwrite: true);
            settings = KeepDeviceOnly(settings, File.ReadAllText(settingsFile)); // this PC stays paired as it was
        }
        AtomicFile.WriteAllText(settingsFile, settings);
    }

    internal static string WithoutDeviceOnly(string settingsJson)
    {
        var node = JsonNode.Parse(settingsJson)!.AsObject();
        foreach (var key in DeviceOnly) node.Remove(key);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The imported settings with this PC's own pairing values (from its current settings.json).</summary>
    internal static string KeepDeviceOnly(string importedJson, string currentJson)
    {
        var imported = JsonNode.Parse(importedJson)!.AsObject();
        JsonObject? current = null;
        try { current = JsonNode.Parse(currentJson) as JsonObject; } catch (JsonException) { } // broken file: defaults
        foreach (var key in DeviceOnly)
        {
            imported.Remove(key);
            if (current?[key] is { } value) imported[key] = value.DeepClone();
        }
        return imported.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static byte[] Header(int iterations)
    {
        var header = new byte[12];
        Magic.CopyTo(header, 0);
        BitConverter.GetBytes(iterations).CopyTo(header, 8);
        return header;
    }

    private static byte[] Key(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
}
