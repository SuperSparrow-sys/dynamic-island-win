using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// While DynamicBay is active, Windows shows no toast banners and plays no notification sound: every app's banner is
/// turned off (ShowBanner=0) and the global notification sound too, so messages only appear in the island.
/// Notifications still reach the notification center, which is where the island reads them from.
/// The user's original values are backed up and restored on exit (and by the uninstaller).
/// </summary>
public static class BannerSuppressor
{
    public const string SnippingTool = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App";
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string SoundValue = "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND";
    /// <summary>Backup key of the global sound switch (app ids never start with an asterisk).</summary>
    private const string SoundKey = "*sound";
    private static string BackupFile => Path.Combine(AppSettings.Folder, "banner-backup.json");

    private static Dictionary<string, int?>? _backup;
    private static bool _active;
    private static HashSet<string> _except = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Silences Windows (or puts it back). Only while the island can actually show the notifications:
    /// without notification access the banners stay, otherwise messages would get lost.
    /// </summary>
    public static void Apply(AppSettings s, bool islandShowsNotifications)
    {
        try
        {
            _active = s.SuppressBanners && islandShowsNotifications && s.NotificationsEnabled && !s.Hidden;
            _except = s.BannerExceptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var backup = _backup ??= LoadBackup();

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_active)
            {
                using var root = Registry.CurrentUser.CreateSubKey(Root);
                foreach (var app in root.GetSubKeyNames()) if (!_except.Contains(app)) wanted.Add(app);
                wanted.Add(SoundKey);
            }
            foreach (var app in backup.Keys.Where(a => !wanted.Contains(a)).ToList())
            {
                Restore(app, backup[app]);
                backup.Remove(app);
            }
            foreach (var app in wanted.Where(a => !backup.ContainsKey(a)).ToList()) Silence(app, backup);
            SaveBackup(backup);
            Log.Info(_active ? $"Windows banners off for {backup.Count - 1} apps" : "Windows banners as configured by the user");
        }
        catch (Exception ex) { Log.Error("BannerSuppressor", ex); }
    }

    /// <summary>
    /// An app sent a notification: if it is new to Windows (first message ever), its banner was still shown once;
    /// from now on it is off as well.
    /// </summary>
    public static void Seen(string appId)
    {
        if (!_active || appId.Length == 0 || _except.Contains(appId)) return;
        var backup = _backup ??= LoadBackup();
        if (backup.ContainsKey(appId)) return;
        try { Silence(appId, backup); SaveBackup(backup); }
        catch (Exception ex) { Log.Error("BannerSuppressor", ex); }
    }

    /// <summary>Puts every app back the way it was (app exit, uninstall: DynamicBay.exe --restore-banners).</summary>
    public static void RestoreAll()
    {
        var backup = _backup ?? LoadBackup();
        foreach (var (app, original) in backup) Restore(app, original);
        backup.Clear();
        _active = false;
        try { File.Delete(BackupFile); } catch { }
    }

    /// <summary>Remembers the original value first (null = Windows default "on"), then switches it off.</summary>
    private static void Silence(string app, Dictionary<string, int?> backup)
    {
        if (app == SoundKey)
        {
            using var root = Registry.CurrentUser.CreateSubKey(Root);
            backup[app] = root.GetValue(SoundValue) is int s ? s : null;
            root.SetValue(SoundValue, 0, RegistryValueKind.DWord);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\{app}");
        backup[app] = key.GetValue("ShowBanner") is int v ? v : null;
        key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
    }

    private static void Restore(string app, int? original)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(app == SoundKey ? Root : $@"{Root}\{app}");
            string name = app == SoundKey ? SoundValue : "ShowBanner";
            if (original is int v) key.SetValue(name, v, RegistryValueKind.DWord);
            else key.DeleteValue(name, false);
        }
        catch (Exception ex) { Log.Error("BannerRestore", ex); }
    }

    private static Dictionary<string, int?> LoadBackup()
    {
        try
        {
            if (File.Exists(BackupFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, int?>>(File.ReadAllText(BackupFile));
                if (loaded is not null) return new(loaded, StringComparer.OrdinalIgnoreCase);
            }
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
