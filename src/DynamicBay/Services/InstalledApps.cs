using System.IO;

namespace DynamicBay.Services;

public sealed record InstalledApp(string Name, string LaunchPath);

/// <summary>
/// Lists installed apps the way the Start menu does (shell:AppsFolder): classic programs and Store apps
/// such as WhatsApp or Discord. Launch paths look like "shell:AppsFolder\{AppUserModelId}".
/// </summary>
public static class InstalledApps
{
    private static List<InstalledApp>? _cache;

    public static List<InstalledApp> All(bool refresh = false)
    {
        if (_cache is not null && !refresh) return _cache;
        var list = new List<InstalledApp>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic? shell = shellType is null ? null : Activator.CreateInstance(shellType);
            dynamic? folder = shell?.NameSpace("shell:AppsFolder");
            if (folder is not null)
            {
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string id = item.Path; // AppUserModelID or a file path
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    // Skip uninstallers, help files and documentation links.
                    if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("deinstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (id.EndsWith(".chm", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new InstalledApp(name, @"shell:AppsFolder\" + id));
                }
            }
        }
        catch (Exception ex) { Core.Log.Error("InstalledApps", ex); }
        // Same name twice (e.g. Spotify desktop + Spotify web app): label the browser-installed one.
        var dupes = list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        list = list.Select(a => dupes.Contains(a.Name) && a.LaunchPath.Contains("_crx_", StringComparison.OrdinalIgnoreCase)
            ? a with { Name = a.Name + " (Web-App)" } : a).ToList();
        _cache = list.GroupBy(a => a.LaunchPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                     .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return _cache;
    }

    /// <summary>Shortcuts and programs inside a folder (e.g. a personal "Apps" folder full of .lnk files).</summary>
    public static List<InstalledApp> FromFolder(string folder)
    {
        var exts = new[] { ".lnk", ".exe", ".url", ".appref-ms" };
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => new InstalledApp(Path.GetFileNameWithoutExtension(f), f))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch { return new(); }
    }

    public static string DisplayName(string launchPath)
    {
        if (launchPath.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            return All().FirstOrDefault(a => string.Equals(a.LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase))?.Name
                   ?? launchPath.Split('\\').Last().Split('!').Last();
        var name = Path.GetFileNameWithoutExtension(launchPath.TrimEnd('\\'));
        return name.Length > 0 ? name : launchPath;
    }
}
