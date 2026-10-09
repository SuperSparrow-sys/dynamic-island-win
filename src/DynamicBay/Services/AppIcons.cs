using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace DynamicBay.Services;

public sealed record AppGlyph(Geometry Data, Color Brand, bool Stroked);

/// <summary>
/// Matches launcher apps to a consistent glyph: Simple Icons brand marks (CC0, ~3500 apps, loaded on first use)
/// or Lucide glyphs for built-in Windows apps. Returns null when nothing fits (the original icon is used then).
/// </summary>
public static partial class AppIcons
{
    private sealed record Entry(string Title, string Hex, string Path, string[] Aliases);

    private static readonly Lazy<Dictionary<string, Entry>> Index = new(LoadIndex);
    private static readonly Dictionary<string, AppGlyph?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Built-in Windows apps -> (Lucide glyph key, tint). Keys are normalized names or exe names.
    private static readonly Dictionary<string, (string icon, uint color)> Builtins = new()
    {
        ["rechner"] = ("Icon.Calculator", 0xFFFF9F0A), ["calculator"] = ("Icon.Calculator", 0xFFFF9F0A), ["calc"] = ("Icon.Calculator", 0xFFFF9F0A),
        ["editor"] = ("Icon.Notepad", 0xFF0A84FF), ["notepad"] = ("Icon.Notepad", 0xFF0A84FF),
        ["explorer"] = ("Icon.Folder", 0xFFFFCC00), ["dateiexplorer"] = ("Icon.Folder", 0xFFFFCC00), ["fileexplorer"] = ("Icon.Folder", 0xFFFFCC00),
        ["terminal"] = ("Icon.Terminal", 0xFF8E8E93), ["windowsterminal"] = ("Icon.Terminal", 0xFF8E8E93), ["eingabeaufforderung"] = ("Icon.Terminal", 0xFF8E8E93),
        ["commandprompt"] = ("Icon.Terminal", 0xFF8E8E93), ["windowspowershell"] = ("Icon.Terminal", 0xFF5E5CE6), ["powershell"] = ("Icon.Terminal", 0xFF5E5CE6),
        ["einstellungen"] = ("Icon.Settings", 0xFF8E8E93), ["settings"] = ("Icon.Settings", 0xFF8E8E93), ["systemsteuerung"] = ("Icon.Cog", 0xFF8E8E93),
        ["kamera"] = ("Icon.Camera", 0xFF30D158), ["camera"] = ("Icon.Camera", 0xFF30D158),
        ["fotos"] = ("Icon.Photos", 0xFFFF375F), ["photos"] = ("Icon.Photos", 0xFFFF375F),
        ["paint"] = ("Icon.Paint", 0xFFFF9F0A), ["snippingtool"] = ("Icon.Scissors", 0xFFBF5AF2), ["ausschneidetool"] = ("Icon.Scissors", 0xFFBF5AF2),
        ["kalender"] = ("Icon.Calendar", 0xFFFF453A), ["calendar"] = ("Icon.Calendar", 0xFFFF453A),
        ["uhr"] = ("Icon.Clock", 0xFF8E8E93), ["clock"] = ("Icon.Clock", 0xFF8E8E93), ["alarmsclock"] = ("Icon.Clock", 0xFF8E8E93),
        ["karten"] = ("Icon.Map", 0xFF30D158), ["maps"] = ("Icon.Map", 0xFF30D158),
        ["microsoftstore"] = ("Icon.Store", 0xFF0A84FF), ["xbox"] = ("Icon.Game", 0xFF30D158),
        ["windowssicherheit"] = ("Icon.Shield", 0xFF0A84FF), ["windowssecurity"] = ("Icon.Shield", 0xFF0A84FF),
        ["medienwiedergabe"] = ("Icon.Music", 0xFFFF375F), ["mediaplayer"] = ("Icon.Music", 0xFFFF375F),
    };

    private static Dictionary<string, Entry> LoadIndex()
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var res = typeof(AppIcons).Assembly.GetManifestResourceStream("DynamicBay.Assets.AppIcons.json.gz");
            if (res is null) return map;
            using var gz = new GZipStream(res, CompressionMode.Decompress);
            using var doc = JsonDocument.Parse(gz);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var aliases = el.TryGetProperty("a", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
                var e = new Entry(el.GetProperty("t").GetString()!, el.GetProperty("h").GetString()!, el.GetProperty("p").GetString()!, aliases);
                map.TryAdd(Normalize(e.Title), e);
                foreach (var al in aliases) map.TryAdd(Normalize(al), e);
            }
        }
        catch (Exception ex) { Core.Log.Error("AppIcons", ex); }
        return map;
    }

    [GeneratedRegex(@"\((web-app|active|beta|preview|x64|x86)\)|\b(\d+(\.\d+)*)\b|\b(for windows|für windows|desktop|app|client|browser|software|deutsch|english)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    public static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static AppGlyph? Find(string name, string path)
    {
        string key = name + "|" + path;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var g = Resolve(name, path);
            Cache[key] = g;
            return g;
        }
    }

    private static AppGlyph? Resolve(string name, string path)
    {
        string n = Normalize(name);
        string exe = Normalize(Path.GetFileNameWithoutExtension(path.Split('!')[0].Split('\\').Last()));
        foreach (var k in new[] { n, exe })
            if (k.Length > 0 && Builtins.TryGetValue(k, out var b) && Application.Current.TryFindResource(b.icon) is Geometry bg)
                return new AppGlyph(bg, FromArgb(b.color), true);

        var index = Index.Value;
        // 1) exact title/alias  2) without noise words ("Opera-Browser" -> "opera")  3) progressively shorter prefixes
        var candidates = new List<string> { n, Normalize(Noise().Replace(name, " ")) };
        var words = Noise().Replace(name.Replace('-', ' ').Replace('_', ' '), " ")
                           .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int len = words.Length - 1; len >= 1; len--)
            candidates.Add(Normalize(string.Join("", words.Take(len))));
        foreach (var c in candidates.Where(c => c.Length >= 3).Distinct())
            if (index.TryGetValue(c, out var e))
                return new AppGlyph(Freeze(Geometry.Parse(e.Path)), Color.FromRgb(Hex(e.Hex, 0), Hex(e.Hex, 2), Hex(e.Hex, 4)), false);
        return null;
    }

    private static byte Hex(string h, int i) => Convert.ToByte(h.Substring(i, 2), 16);
    private static Color FromArgb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

    public static int Count => Index.Value.Values.Distinct().Count();
}
