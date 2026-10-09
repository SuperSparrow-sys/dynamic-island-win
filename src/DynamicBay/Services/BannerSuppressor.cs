using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// While DynamicBay is active, turns off the Windows toast *banner* for chosen apps (e.g. Snipping Tool),
/// so the message only appears in the island. Notifications still reach the notification center and the island.
/// The user's original per-app setting is backed up and restored on exit (and by the uninstaller).
/// </summary>
public static class BannerSuppressor
{
    public const string SnippingTool = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App";
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private static string BackupFile => Path.Combine(AppSettings.Folder, "banner-backup.json");

    /// <summary>Applies or lifts suppression for exactly the configured apps.</summary>
    public static void Apply(AppSettings s)
    {
        bool active = s.SuppressBanners && !s.Hidden;
        var wanted = active ? s.SuppressBannerApps.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        var backup = LoadBackup();

        // Restore apps that are no longer suppressed.
        foreach (var app in backup.Keys.Where(a => !wanted.Contains(a)).ToList())
        {
            Restore(app, backup[app]);
            backup.Remove(app);
        }
        // Suppress new ones (remember the original value first; null = Windows default "on").
        foreach (var app in wanted.Where(a => !backup.ContainsKey(a)))
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\{app}");
            backup[app] = key.GetValue("ShowBanner") is int v ? v : null;
            key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
        }
        SaveBackup(backup);
    }

    /// <summary>Puts every app back the way it was (app exit, uninstall: DynamicBay.exe --restore-banners).</summary>
    public static void RestoreAll()
    {
        var backup = LoadBackup();
        foreach (var (app, original) in backup) Restore(app, original);
        try { File.Delete(BackupFile); } catch { }
    }

    private static void Restore(string app, int? original)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\{app}");
            if (original is int v) key.SetValue("ShowBanner", v, RegistryValueKind.DWord);
            else key.DeleteValue("ShowBanner", false);
        }
        catch (Exception ex) { Log.Error("BannerRestore", ex); }
    }

    private static Dictionary<string, int?> LoadBackup()
    {
        try
        {
            if (File.Exists(BackupFile))
                return JsonSerializer.Deserialize<Dictionary<string, int?>>(File.ReadAllText(BackupFile)) ?? new();
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveBackup(Dictionary<string, int?> backup)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            if (backup.Count == 0) File.Delete(BackupFile);
            else File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup));
        }
        catch { }
    }

    /// <summary>Friendly name for an app id in the settings list.</summary>
    public static string NameOf(string appId) => appId switch
    {
        SnippingTool => "Snipping Tool",
        _ => InstalledApps.DisplayName(@"shell:AppsFolder\" + appId),
    };
}
