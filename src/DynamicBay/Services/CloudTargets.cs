using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DynamicBay.Services;

public sealed record CloudTarget(string Name, string Root, string Kind)
{
    /// <summary>Files saved from DynamicBay land in a subfolder so they're easy to find.</summary>
    public string SaveFolder => Path.Combine(Root, "DynamicBay");
}

/// <summary>
/// Finds locally synced cloud folders: every OneDrive account (personal and work), iCloud Drive,
/// Google Drive for desktop and Dropbox. Saving into these folders uploads through the vendor's sync client.
/// </summary>
public static class CloudTargets
{
    private static List<CloudTarget>? _cached;
    private static DateTime _cachedAt;
    private static Task<List<CloudTarget>>? _refresh;

    /// <summary>
    /// The cloud folders, found off the UI thread (checking drives and the iCloud folder can take seconds) and kept
    /// for five minutes. The first call waits for the search, later calls return at once and refresh in the background.
    /// </summary>
    public static async Task<List<CloudTarget>> GetAsync()
    {
        if (_cached is not null)
        {
            if (DateTime.UtcNow - _cachedAt > TimeSpan.FromMinutes(5)) _ = RefreshAsync();
            return _cached;
        }
        return await RefreshAsync();
    }

    public static Task<List<CloudTarget>> RefreshAsync()
    {
        if (_refresh is { IsCompleted: false } running) return running;
        return _refresh = Task.Run(() =>
        {
            var list = Detect();
            _cached = list;
            _cachedAt = DateTime.UtcNow;
            return list;
        });
    }

    public static List<CloudTarget> Detect()
    {
        var list = new List<CloudTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string? root, string kind)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            root = Path.GetFullPath(root).TrimEnd('\\');
            if (seen.Add(root)) list.Add(new CloudTarget(name, root, kind));
        }

        // OneDrive: one registry key per signed-in account (Personal, Business1, Business2, ...)
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var sub in accounts?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var key = accounts!.OpenSubKey(sub);
                var folder = key?.GetValue("UserFolder") as string;
                if (folder is null) continue;
                string name = sub.Equals("Personal", StringComparison.OrdinalIgnoreCase)
                    ? (Core.Loc.German ? "OneDrive (privat)" : "OneDrive (personal)")
                    : "OneDrive – " + (key?.GetValue("DisplayName") as string ?? OrgFromFolder(folder));
                Add(name, folder, "onedrive");
            }
        }
        catch { }
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"), "onedrive");
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveCommercial"), "onedrive");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("iCloud Drive", Path.Combine(home, "iCloudDrive"), "icloud");

        // Google Drive for desktop mounts a virtual (fixed) drive with "My Drive" / "Meine Ablage".
        // Network, removable and optical drives are skipped: asking them can block for seconds.
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                foreach (var name in new[] { "My Drive", "Meine Ablage" })
                    Add("Google Drive", Path.Combine(drive.RootDirectory.FullName, name), "gdrive");
            }
            catch { }
        }

        // Dropbox publishes its folder in info.json.
        try
        {
            var info = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json");
            if (File.Exists(info))
            {
                var json = JsonNode.Parse(File.ReadAllText(info));
                Add("Dropbox", json?["personal"]?["path"]?.ToString(), "dropbox");
                Add("Dropbox Business", json?["business"]?["path"]?.ToString(), "dropbox");
            }
        }
        catch { }
        return list;
    }

    private static string OrgFromFolder(string folder)
    {
        var name = Path.GetFileName(folder);
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 ? name[(dash + 3)..] : name;
    }

    /// <summary>Copies a file into the target (unique name), returning the new path.</summary>
    public static string SaveFile(CloudTarget target, string sourcePath)
    {
        Directory.CreateDirectory(target.SaveFolder);
        string dest = Unique(Path.Combine(target.SaveFolder, Path.GetFileName(sourcePath)));
        File.Copy(sourcePath, dest);
        return dest;
    }

    public static string SaveText(CloudTarget target, string text, string fileName)
    {
        Directory.CreateDirectory(target.SaveFolder);
        string dest = Unique(Path.Combine(target.SaveFolder, fileName));
        File.WriteAllText(dest, text);
        return dest;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var c = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(c)) return c;
        }
    }
}
