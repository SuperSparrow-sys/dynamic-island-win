using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;

namespace DynamicBay.Services;

public enum SearchKind { App, Setting, Tool, File, Folder }

/// <summary>One line of the result list. The icon arrives later from its own thread.</summary>
public sealed partial class SearchItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    /// <summary>What opening starts: shell:AppsFolder\…, ms-settings:…, a file; "program|arguments" for a few tools.</summary>
    public required string Path { get; init; }
    public required string IconPath { get; init; }
    public SearchKind Kind { get; init; }
    public string Id => Path;
    /// <summary>Shown while the icon loads or when the shell has none.</summary>
    public Geometry? Glyph { get; set; }
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _selected;
}

/// <summary>
/// The search's sources without a window: installed apps, Windows settings pages and tools (ranked by
/// <see cref="SearchMath"/> with the task table in Assets\SearchSynonyms.txt), files from <see cref="FileSearch"/>,
/// and how often each hit was opened (search-usage.json in the data folder, never exported).
/// </summary>
public sealed class SearchService : IDisposable
{
    private const string SettingsApp = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
    private static readonly string UsageFile = System.IO.Path.Combine(AppSettings.Folder, "search-usage.json");

    private readonly FileSearch _files = new();
    private readonly StaWorker _icons = new("Search icons");
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _usage;
    private List<(SearchCandidate Candidate, SearchItem Template)> _catalog = new();

    public IReadOnlyList<SynonymRule> Rules { get; } = LoadRules();

    public SearchService()
    {
        _usage = LoadUsage();
        Rebuild();
        InstalledApps.Ready += Rebuild;
    }

    /// <summary>The task table shipped with the app (embedded, read once).</summary>
    public static List<SynonymRule> LoadRules()
    {
        try
        {
            using var stream = typeof(SearchService).Assembly.GetManifestResourceStream("DynamicBay.Assets.SearchSynonyms.txt");
            if (stream is null) return new();
            using var reader = new StreamReader(stream);
            return SearchMath.ParseSynonyms(reader.ReadToEnd());
        }
        catch (Exception ex) { Log.Error("Search synonyms", ex); return new(); }
    }

    private void Rebuild()
    {
        var list = new List<(SearchCandidate, SearchItem)>();
        string app = Loc.German ? "Programm" : "App";
        foreach (var a in (InstalledApps.Cached ?? new()).Where(a => !SearchMath.IsNoise(a.Name)))
            list.Add((SearchCandidate.Of(a.LaunchPath, a.Name), new SearchItem { Name = a.Name, Subtitle = app, Path = a.LaunchPath, IconPath = a.LaunchPath, Kind = SearchKind.App }));
        foreach (var s in SystemEntries)
        {
            string name = Loc.German ? s.De : s.En;
            var keys = new[] { s.De, s.En }.Concat(s.Words.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
            bool setting = s.Path.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase);
            string subtitle = setting ? (Loc.German ? "Einstellungen" : "Settings") : (Loc.German ? "Windows-Werkzeug" : "Windows tool");
            list.Add((SearchCandidate.Of(s.Path, name, keys), new SearchItem { Name = name, Subtitle = subtitle, Path = s.Path,
                IconPath = setting ? SettingsApp : s.Icon, Kind = setting ? SearchKind.Setting : SearchKind.Tool }));
        }
        _catalog = list;
    }

    /// <summary>Apps, settings pages and tools for a query, best first (fast: runs on the UI thread).</summary>
    public List<SearchItem> Find(string query, int max)
    {
        var catalog = _catalog;
        var byId = catalog.GroupBy(c => c.Candidate.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Template, StringComparer.OrdinalIgnoreCase);
        return SearchMath.Rank(query, catalog.Select(c => c.Candidate).ToList(), Rules, Launches, max)
            .Select(h => Copy(byId[h.Item.Id])).ToList();
    }

    /// <summary>Files and folders whose name fits, those with the better name first.</summary>
    public async Task<List<SearchItem>> FindFilesAsync(string query, int max)
    {
        var hits = await _files.FindAsync(query, max * 2);
        string q = SearchMath.Normalize(query);
        return hits.Select((h, i) => (Hit: h, Score: SearchMath.Score(q, SearchKey.Of(System.IO.Path.GetFileNameWithoutExtension(h.Path))) + Boost(h.Path), Order: i))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Order).Take(max)
            .Select(x => new SearchItem
            {
                Name = System.IO.Path.GetFileName(x.Hit.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : x.Hit.Path,
                Subtitle = System.IO.Path.GetDirectoryName(x.Hit.Path) ?? "",
                Path = x.Hit.Path, IconPath = x.Hit.Path, Kind = x.Hit.IsFolder ? SearchKind.Folder : SearchKind.File,
            }).ToList();
    }

    private static SearchItem Copy(SearchItem t) => new() { Name = t.Name, Subtitle = t.Subtitle, Path = t.Path, IconPath = t.IconPath, Kind = t.Kind };

    // ---------- icons ----------

    /// <summary>Fills the item's icon from the cache or the shell (own thread: shell icons can take a while).</summary>
    public async void LoadIcon(SearchItem item)
    {
        if (_iconCache.TryGetValue(item.IconPath, out var cached)) { item.Icon = cached; return; }
        try
        {
            string path = item.IconPath;
            // 48 px, not 64: icons that only come in 32 or 48 px (Advanced IP Scanner, Radmin) arrive larger than that
            // inside a light framed tile, as in Explorer's large icon view. 48 covers 28 DIP up to 150 % scale.
            var icon = await _icons.Run(() => ShellThumbnail.Get(path, 48, iconOnly: true));
            _iconCache[path] = icon;
            item.Icon = icon;
        }
        catch (Exception ex) { Log.Info($"Search: no icon for {item.Name} ({ex.Message})"); }
    }

    // ---------- open ----------

    /// <summary>Opens the hit and counts it, so it climbs in the list next time.</summary>
    public void Open(SearchItem item)
    {
        try
        {
            string path = item.Path;
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) Process.Start("explorer.exe", path)?.Dispose(); // Store apps by AppUserModelId
            else if (path.Split('|', 2) is [var exe, var args]) Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            Count(item.Id);
        }
        catch (Exception ex) { Log.Info($"Search: {item.Name} did not open ({ex.Message})"); }
    }

    /// <summary>Strg+Enter: Explorer with the file selected. False when the hit has no folder (apps, settings).</summary>
    public bool OpenFolder(SearchItem item)
    {
        if (item.Kind is not (SearchKind.File or SearchKind.Folder)) return false;
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\"")?.Dispose(); return true; }
        catch (Exception ex) { Log.Info($"Search: folder of {item.Name} did not open ({ex.Message})"); return false; }
    }

    private int Launches(string id) => _usage.TryGetValue(id, out int n) ? n : 0;

    private int Boost(string id) => SearchMath.Boost(Launches(id));

    private void Count(string id)
    {
        _usage[id] = Launches(id) + 1;
        string json = JsonSerializer.Serialize(_usage);
        _ = Task.Run(() =>
        {
            try { AtomicFile.WriteAllText(UsageFile, json, backup: false); }
            catch (Exception ex) { Log.Info($"Search: launches not saved ({ex.Message})"); }
        });
    }

    private static Dictionary<string, int> LoadUsage()
    {
        try
        {
            if (File.Exists(UsageFile) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(UsageFile)) is { } d)
                return new Dictionary<string, int>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Info($"Search: launches not readable ({ex.Message})"); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        InstalledApps.Ready -= Rebuild;
        _files.Dispose();
        _icons.Dispose();
    }

    // ---------- Windows settings pages and tools ----------

    private sealed record SystemEntry(string De, string En, string Path, string Words, string Icon = "");

    private static string Sys(string file) => System.IO.Path.Combine(Environment.SystemDirectory, file);

    /// <summary>Settings pages (ms-settings:) and tools the Start menu does not list, found by their German and English names and a few words.</summary>
    private static readonly SystemEntry[] SystemEntries =
    {
        new("Bluetooth und Geräte", "Bluetooth & devices", "ms-settings:bluetooth", "bluetooth, geraete, koppeln, kopfhoerer, devices, pair, headphones"),
        new("Drucker und Scanner", "Printers & scanners", "ms-settings:printers", "drucker, scanner, drucken, printer, print"),
        new("Maus", "Mouse", "ms-settings:mousetouchpad", "maus, mauszeiger, mouse, cursor"),
        new("Touchpad", "Touchpad", "ms-settings:devices-touchpad", "touchpad, trackpad, gesten, gestures"),
        new("Eingabe und Tastatur", "Typing", "ms-settings:typing", "tastatur, eingabe, keyboard, typing, autokorrektur"),
        new("Anzeige", "Display", "ms-settings:display", "anzeige, bildschirm, monitor, aufloesung, skalierung, helligkeit, display, resolution, scale, brightness"),
        new("Nachtmodus", "Night light", "ms-settings:nightlight", "nachtmodus, nachtlicht, blaulicht, night light, blue light"),
        new("Grafik", "Graphics", "ms-settings:display-advancedgraphics", "grafik, grafikkarte, gpu, graphics"),
        new("Sound", "Sound", "ms-settings:sound", "sound, ton, lautsprecher, mikrofon, audio, lautstaerke, speaker, microphone, volume"),
        new("Benachrichtigungen", "Notifications", "ms-settings:notifications", "benachrichtigungen, mitteilungen, notifications"),
        new("Fokus", "Focus", "ms-settings:quiethours", "fokus, nicht stoeren, focus, do not disturb"),
        new("Energie und Akku", "Power & battery", "ms-settings:powersleep", "energie, akku, strom, standby, energiesparen, power, battery, sleep"),
        new("Speicher", "Storage", "ms-settings:storagesense", "speicher, festplatte, speicheroptimierung, storage, disk"),
        new("Multitasking", "Multitasking", "ms-settings:multitasking", "multitasking, andocken, fenster einrasten, snap"),
        new("Projizieren", "Projecting to this PC", "ms-settings:project", "projizieren, beamer, project"),
        new("Zwischenablage", "Clipboard", "ms-settings:clipboard", "zwischenablage, verlauf, clipboard"),
        new("Info", "About", "ms-settings:about", "info, pc name, computername, seriennummer, about, pc name, version, windows version"),
        new("Netzwerk und Internet", "Network & internet", "ms-settings:network", "netzwerk, internet, network"),
        new("WLAN", "Wi-Fi", "ms-settings:network-wifi", "wlan, wifi, wi fi, funknetz, wireless"),
        new("Ethernet", "Ethernet", "ms-settings:network-ethernet", "ethernet, lan, kabel"),
        new("VPN", "VPN", "ms-settings:network-vpn", "vpn"),
        new("Mobiler Hotspot", "Mobile hotspot", "ms-settings:network-mobilehotspot", "hotspot, tethering"),
        new("Proxy", "Proxy", "ms-settings:network-proxy", "proxy"),
        new("Hintergrund", "Background", "ms-settings:personalization-background", "hintergrund, hintergrundbild, wallpaper, background"),
        new("Farben", "Colors", "ms-settings:colors", "farben, dunkelmodus, hell, dunkel, akzentfarbe, colors, dark mode, light mode, accent"),
        new("Designs", "Themes", "ms-settings:themes", "designs, themes"),
        new("Sperrbildschirm", "Lock screen", "ms-settings:lockscreen", "sperrbildschirm, lock screen"),
        new("Taskleiste", "Taskbar", "ms-settings:taskbar", "taskleiste, taskbar"),
        new("Schriftarten", "Fonts", "ms-settings:fonts", "schriftarten, schriften, fonts"),
        new("Installierte Apps", "Installed apps", "ms-settings:appsfeatures", "apps, programme, deinstallieren, entfernen, uninstall, installed apps"),
        new("Standard-Apps", "Default apps", "ms-settings:defaultapps", "standard apps, standardprogramm, oeffnen mit, default apps, open with"),
        new("Autostart", "Startup", "ms-settings:startupapps", "autostart, systemstart, startup"),
        new("Konten", "Accounts", "ms-settings:yourinfo", "konto, konten, benutzer, account, accounts"),
        new("Anmeldeoptionen", "Sign-in options", "ms-settings:signinoptions", "anmelden, pin, passwort, windows hello, fingerabdruck, sign in, password"),
        new("Datum und Uhrzeit", "Date & time", "ms-settings:dateandtime", "datum, uhrzeit, zeitzone, date, time, time zone"),
        new("Sprache und Region", "Language & region", "ms-settings:regionlanguage", "sprache, region, language"),
        new("Spiele", "Gaming", "ms-settings:gaming-gamebar", "spiele, game bar, gaming"),
        new("Barrierefreiheit", "Accessibility", "ms-settings:easeofaccess", "barrierefreiheit, bedienungshilfen, accessibility"),
        new("Datenschutz und Sicherheit", "Privacy & security", "ms-settings:privacy", "datenschutz, privatsphaere, berechtigungen, privacy, permissions"),
        new("Mikrofon-Zugriff", "Microphone access", "ms-settings:privacy-microphone", "mikrofon zugriff, microphone access"),
        new("Kamera-Zugriff", "Camera access", "ms-settings:privacy-webcam", "kamera zugriff, webcam, camera access"),
        new("Für Entwickler", "For developers", "ms-settings:developers", "entwickler, entwicklermodus, developer mode"),
        new("Windows Update", "Windows Update", "ms-settings:windowsupdate", "update, updates, aktualisieren"),
        new("Wiederherstellung", "Recovery", "ms-settings:recovery", "wiederherstellung, zuruecksetzen, recovery, reset"),
        new("Mobilgeräte", "Mobile devices", "ms-settings:mobile-devices", "handy, smartphone, mobilgeraete, phone"),
        new("Geräte-Manager", "Device Manager", Sys("devmgmt.msc"), "geraete manager, treiber, hardware, device manager, drivers", Sys("devmgmt.msc")),
        new("Datenträgerverwaltung", "Disk Management", Sys("diskmgmt.msc"), "datentraeger, partition, festplatte formatieren, disk management, partition", Sys("diskmgmt.msc")),
        new("Netzwerkverbindungen", "Network Connections", Sys("ncpa.cpl"), "netzwerkadapter, adapter, ip adresse, network adapter, ncpa", Sys("ncpa.cpl")),
        new("Programme und Features", "Programs and Features", Sys("appwiz.cpl"), "programme, software deinstallieren, appwiz", Sys("appwiz.cpl")),
        new("Systemeigenschaften", "System Properties", Sys("sysdm.cpl"), "systemeigenschaften, computername aendern, remote einstellungen, sysdm", Sys("sysdm.cpl")),
        new("Umgebungsvariablen", "Environment Variables", Sys("rundll32.exe") + "|sysdm.cpl,EditEnvironmentVariables", "umgebungsvariablen, path variable, environment variables, env", Sys("sysdm.cpl")),
        new("Energieoptionen", "Power Options", Sys("powercfg.cpl"), "energieoptionen, energieplan, power plan", Sys("powercfg.cpl")),
        new("Sound (Systemsteuerung)", "Sound (Control Panel)", Sys("mmsys.cpl"), "wiedergabegeraete, aufnahmegeraete, mmsys, playback devices", Sys("mmsys.cpl")),
        new("Benutzerkonten", "User Accounts", Sys("netplwiz.exe"), "benutzerkonten, netplwiz, user accounts", Sys("netplwiz.exe")),
        new("Papierkorb", "Recycle Bin", "shell:RecycleBinFolder", "papierkorb, geloescht, recycle bin, trash", "shell:RecycleBinFolder"),
        new("Downloads", "Downloads", "shell:Downloads", "downloads, heruntergeladen", "shell:Downloads"),
    };
}
