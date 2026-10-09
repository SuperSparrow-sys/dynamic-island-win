using System.IO;
using DynamicBay.Services;

namespace DynamicBay.Core;

/// <summary>
/// Entries of "hide while these apps are active": either a process name ("chrome.exe") or, for apps without a
/// unique exe (Store apps, apps with their own id), the app's AppUserModelID ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App").
/// </summary>
public static class ExcludedApps
{
    private const string AppsFolder = @"shell:AppsFolder\";

    /// <summary>The entry to store for an app picked from the installed-apps list.</summary>
    public static string TokenFor(InstalledApp app)
    {
        var id = app.LaunchPath.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) ? app.LaunchPath[AppsFolder.Length..] : app.LaunchPath;
        return id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(id) : id;
    }

    public static bool IsAppId(string entry) => !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Readable name for the list in the settings.</summary>
    public static string Label(string entry) =>
        IsAppId(entry) ? InstalledApps.DisplayName(AppsFolder + entry) : Path.GetFileNameWithoutExtension(entry);

    /// <summary>Does the foreground window belong to one of the entries?</summary>
    public static bool Matches(IEnumerable<string> entries, string? processName, Func<string?> windowAppId)
    {
        string? appId = null;
        bool appIdRead = false;
        foreach (var e in entries)
        {
            if (!IsAppId(e))
            {
                if (processName is not null && string.Equals(Path.GetFileNameWithoutExtension(e), processName, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (!appIdRead) { appId = windowAppId(); appIdRead = true; }
            if (appId is not null && string.Equals(e, appId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
