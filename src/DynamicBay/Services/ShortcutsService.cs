using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>A launcher entry rendered as a uniform tile (brand glyph on a squircle, or the original icon inside one).</summary>
public sealed class ShortcutItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public Geometry? Glyph { get; init; }
    public bool GlyphStroked { get; init; }
    public Brush TileBackground { get; init; } = Brushes.Transparent;
    public Brush GlyphBrush { get; init; } = Brushes.White;
    public bool GlyphFilled => !GlyphStroked;
    public bool ShowGlyph => Glyph is not null;
    public bool ShowIcon => Glyph is null;
}

/// <summary>Pinned apps, files and folders for the launcher widget (NotchNook's "Open Apps").</summary>
public sealed partial class ShortcutsService
{
    public const int MaxItems = 12;
    private readonly AppSettings _settings;
    public ObservableCollection<ShortcutItem> Items { get; } = new();

    public ShortcutsService(AppSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.Shortcuts) or nameof(AppSettings.AppIcons)) Rebuild();
        };
        // Built after startup (App calls Rebuild once the icon palette and app list are warm) and again when
        // the installed-apps list is ready, so names of Store apps resolve.
        InstalledApps.Ready += Rebuild;
    }

    private static readonly Brush NeutralTile = Frozen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    // "Schlicht": dark graphite tile + muted grey mark (still ~6:1 contrast), brightening on hover in the template.
    private static readonly Brush MonoTile = Frozen(new LinearGradientBrush(Color.FromRgb(0x2E, 0x2E, 0x31), Color.FromRgb(0x24, 0x24, 0x26), 90));
    private static readonly Brush MonoGlyph = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xAE)));

    public void Rebuild()
    {
        Items.Clear();
        foreach (var p in _settings.Shortcuts)
        {
            bool shellApp = p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            if (!shellApp && !File.Exists(p) && !Directory.Exists(p)) continue;
            Items.Add(Create(p, _settings.AppIcons));
        }
    }

    public static ShortcutItem Create(string path, AppIconStyle style)
    {
        string name = InstalledApps.DisplayName(path);
        var icon = ShellThumbnail.Get(path, 64, iconOnly: true);
        var glyph = style == AppIconStyle.Original ? null : AppIcons.Find(name, path);
        if (glyph is null)
        {
            return style switch
            {
                AppIconStyle.Original => new ShortcutItem { Path = path, Name = name, Icon = icon, TileBackground = Brushes.Transparent },
                // Unknown apps in the clean style: their own icon, but monochrome, on the same glass tile.
                AppIconStyle.Mono => new ShortcutItem { Path = path, Name = name, Icon = icon is BitmapSource bs ? Monochrome(bs) : icon, TileBackground = MonoTile },
                _ => new ShortcutItem { Path = path, Name = name, Icon = icon, TileBackground = NeutralTile },
            };
        }

        Brush tile, fg;
        if (style == AppIconStyle.Mono)
        {
            tile = MonoTile;
            fg = MonoGlyph;
        }
        else if (style == AppIconStyle.Color)
        {
            // iOS-like: brand colour with a soft top highlight, white mark.
            var c = glyph.Brand;
            var top = Lighten(c, 0.18);
            tile = Frozen(new LinearGradientBrush(top, c, 90));
            fg = IsVeryLight(c) ? Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E))) : Brushes.White;
        }
        else
        {
            // Dark: matches the black island, mark in (readable) brand colour.
            tile = NeutralTile;
            fg = Frozen(new SolidColorBrush(Readable(glyph.Brand)));
        }
        return new ShortcutItem { Path = path, Name = name, Icon = icon, Glyph = glyph.Data, GlyphStroked = glyph.Stroked, TileBackground = tile, GlyphBrush = fg };
    }

    private static Color Lighten(Color c, double f) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

    private static bool IsVeryLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 200;

    /// <summary>Lift dark brand colours (e.g. SQLite navy) so they stay visible on the dark tile.</summary>
    private static Color Readable(Color c)
    {
        double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return lum < 90 ? Lighten(c, 0.55) : c;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Turns a colourful icon into a light grey silhouette with tonal detail (keeps alpha).</summary>
    private static ImageSource Monochrome(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            double lum = (0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2]) / 255.0;
            byte g = (byte)(120 + 70 * lum); // muted grey range, matching the glyph tiles
            px[i] = px[i + 1] = px[i + 2] = g;
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!_settings.Shortcuts.Contains(p, StringComparer.OrdinalIgnoreCase) && _settings.Shortcuts.Count < MaxItems)
                _settings.Shortcuts.Add(p);
    }

    [RelayCommand]
    public void Launch(ShortcutItem? item)
    {
        if (item is null) return;
        try
        {
            if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start("explorer.exe", item.Path); // Store apps (WhatsApp, Discord, ...) by AppUserModelId
            else
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Launch", ex); }
    }

    [RelayCommand]
    public void Remove(ShortcutItem? item)
    {
        if (item is null) return;
        var match = _settings.Shortcuts.FirstOrDefault(s => string.Equals(s, item.Path, StringComparison.OrdinalIgnoreCase));
        if (match is not null) _settings.Shortcuts.Remove(match);
    }
}
