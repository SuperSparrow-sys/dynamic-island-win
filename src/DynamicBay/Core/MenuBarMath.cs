using System.Globalization;
using System.Windows.Media;

namespace DynamicBay.Core;

/// <summary>Pure rules of the menu bar (which displays, which symbol, the date text), testable without a window.</summary>
public enum ForegroundApp { None, Keep, Window, Owner }

public static class MenuBarMath
{
    /// <summary>Bar height in DIP: lower than the compact notch (32), so the notch hangs out below the bar like on a Mac.</summary>
    public const double HeightDip = 24;

    /// <summary>The displays that get a bar. <paramref name="islandDevice"/> is the display of the main island.</summary>
    public static List<string> Screens(MenuBarScreens mode, IEnumerable<(string Device, bool Primary)> monitors, string islandDevice,
                                       Func<string, bool>? chosen = null) =>
        mode switch
        {
            MenuBarScreens.Chosen => monitors.Where(m => chosen?.Invoke(m.Device) == true).Select(m => m.Device).ToList(),
            MenuBarScreens.All => monitors.Select(m => m.Device).ToList(),
            MenuBarScreens.Secondary => monitors.Where(m => !m.Primary).Select(m => m.Device).ToList(),
            MenuBarScreens.Island => monitors.Where(m => string.Equals(m.Device, islandDevice, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Device).ToList(),
            _ => new List<string>(),
        };

    /// <summary>
    /// A display as the choice remembers it: name and resolution, like the island remembers its place per display setup.
    /// </summary>
    public static string DisplayKey(string device, int width, int height) => $"{device}:{width}x{height}";

    public static string VolumeIcon(int volume, bool muted) =>
        muted ? "Fluent.SpeakerMute" : volume <= 0 ? "Fluent.Speaker0" : volume < 50 ? "Fluent.Speaker1" : "Fluent.Speaker2";

    /// <summary>Wi-Fi with Windows' signal bars (0-5), cable, or nothing.</summary>
    public static string NetworkIcon(bool connected, bool wlan, int bars) =>
        !connected ? "Fluent.WifiOff" : !wlan ? "Fluent.Ethernet" : $"Fluent.Wifi{Math.Clamp(bars, 1, 4)}";

    /// <summary>Battery in tenths, with the lightning bolt while it charges.</summary>
    public static string BatteryIcon(int percent, bool charging)
    {
        int tenths = (int)Math.Round(Math.Clamp(percent, 0, 100) / 10.0, MidpointRounding.AwayFromZero);
        return (charging ? "Fluent.BatteryCharge" : "Fluent.Battery") + tenths;
    }

    /// <summary>Colour (ARGB) before anything below the bar was sampled, after the Windows theme like the taskbar.</summary>
    public static uint Plain(bool light) => light ? 0xFFF2F2F2 : 0xFF1C1C1E;

    /// <summary>How many colours the gradient takes from below the bar, left to right.</summary>
    public const int ToneSlices = 12;

    /// <summary>
    /// Average colour of each of <paramref name="count"/> equal slices of a BGRA strip (rows x width). Columns in
    /// <paramref name="skip"/> (the island hanging over the strip) are left out; a slice with nothing left takes the
    /// colour between its neighbours. Null when everything was skipped.
    /// </summary>
    public static Color[]? Slices(byte[] bgra, int width, int rows, int count, IReadOnlyList<(int From, int To)>? skip = null)
    {
        var result = new Color?[count];
        for (int s = 0; s < count; s++)
        {
            int x0 = s * width / count, x1 = Math.Min(width, Math.Max(x0 + 1, (s + 1) * width / count));
            long r = 0, g = 0, b = 0, n = 0;
            for (int x = x0; x < x1; x++)
            {
                if (skip is not null && skip.Any(k => x >= k.From && x < k.To)) continue;
                for (int y = 0; y < rows; y++)
                {
                    int i = (y * width + x) * 4;
                    b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; n++;
                }
            }
            if (n > 0) result[s] = Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        if (result.All(c => c is null)) return null;
        var filled = new Color[count];
        for (int s = 0; s < count; s++)
        {
            if (result[s] is { } c) { filled[s] = c; continue; }
            int l = s - 1, rr = s + 1;
            while (l >= 0 && result[l] is null) l--;
            while (rr < count && result[rr] is null) rr++;
            if (l < 0) filled[s] = result[rr]!.Value;
            else if (rr >= count) filled[s] = result[l]!.Value;
            else
            {
                double f = (double)(s - l) / (rr - l);
                Color a = result[l]!.Value, z = result[rr]!.Value;
                filled[s] = Color.FromRgb((byte)(a.R + (z.R - a.R) * f), (byte)(a.G + (z.G - a.G) * f), (byte)(a.B + (z.B - a.B) * f));
            }
        }
        return filled;
    }

    /// <summary>
    /// Columns of the row below the bar (from the bar's left edge) that something lies over: the island shapes and
    /// windows kept on top such as picture in picture. Their colours are not the window below.
    /// </summary>
    public static List<(int From, int To)> Covered(Native.RECT bar, IEnumerable<Native.RECT> covers, int margin) =>
        covers.Where(c => c.Top <= bar.Bottom && c.Bottom > bar.Bottom && c.Right > bar.Left && c.Left < bar.Right)
              .Select(c => (c.Left - bar.Left - margin, c.Right - bar.Left + margin)).ToList();

    /// <summary>
    /// Room in pixels for the left side of the bar: up to the island that hangs out of it (the notch) minus a gap, like
    /// the Mac keeps its menus clear of the notch. Null when no island sits in the bar. An open panel (hanging far
    /// lower) does not count: it covers the bar anyway and would only make the left side jump.
    /// </summary>
    public static int? LeftRoom(Native.RECT bar, IEnumerable<Native.RECT> islands, int gap)
    {
        var inBar = islands.Where(i => i.Top <= bar.Bottom && i.Bottom > bar.Top && i.Left > bar.Left && i.Left < bar.Right
                                       && i.Bottom - bar.Bottom <= bar.Height * 2).ToList();
        return inBar.Count == 0 ? null : Math.Max(0, inBar.Min(i => i.Left) - bar.Left - gap);
    }

    /// <summary>
    /// A film or game in fullscreen: the window covers the whole display and has no caption. Maximized borderless apps
    /// (Spotify, VS Code) also cover it, but keep their resize frame.
    /// </summary>
    public static bool IsFullscreen(Native.RECT window, Native.RECT display, bool caption, bool zoomed, bool resizeFrame) =>
        !caption && !(zoomed && resizeFrame)
        && window.Left <= display.Left && window.Top <= display.Top && window.Right >= display.Right && window.Bottom >= display.Bottom;

    /// <summary>
    /// Which app name the bar shows for the window in front. Desktop: none (as the owner wants). Taskbar: the last app
    /// stays. Menus, dialogs and pop-ups count for the app that owns them; overlays nobody owns (a display tool's layer
    /// over the desktop, Start, search) show none.
    /// </summary>
    public static ForegroundApp ForegroundAppFor(string className, bool switchable, bool ownerSwitchable) =>
        className is "Progman" or "WorkerW" ? ForegroundApp.None
        : className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? ForegroundApp.Keep
        : switchable ? ForegroundApp.Window
        : ownerSwitchable ? ForegroundApp.Owner
        : ForegroundApp.None;

    /// <summary>
    /// Windows' own report counts only for games in exclusive fullscreen (3) and slide shows (4). "Busy" (2) also comes
    /// for any window in front that covers the screen, such as an input overlay while the mouse is on another PC.
    /// </summary>
    public static bool IsFullscreenState(int quns) => quns is 3 or 4;

    /// <summary>Tool windows, layered, click-through and never-active windows are overlays (the island, display and
    /// mouse-sharing tools), not films or games.</summary>
    public static bool IgnoredForFullscreen(long exStyle) => (exStyle & (0x80 | 0x80000 | 0x20 | 0x08000000)) != 0;

    /// <summary>Light enough for dark text (relative luminance of the average over 0.6), like the Mac menu bar on a bright wallpaper.</summary>
    public static bool IsLight(IReadOnlyList<Color> colors) =>
        colors.Count > 0 && colors.Average(c => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 > 0.6;

    /// <summary>"Mi. 18. Nov." / "Wed 18 Nov" like the Mac menu bar; weekday and date can each be left out.</summary>
    public static string DateText(DateTime now, bool german, bool weekday = true, bool date = true)
    {
        var culture = CultureInfo.GetCultureInfo(german ? "de-DE" : "en-US");
        string day = german ? now.ToString("ddd", culture) + "." : now.ToString("ddd", culture); // .NET writes "Mi" without the dot
        string dm = now.ToString(german ? "d. MMM" : "d MMM", culture);
        return weekday && date ? $"{day} {dm}" : weekday ? day : date ? dm : "";
    }

    /// <summary>Song shown in the bar: "Title · Artist", or just the title.</summary>
    public static string MediaText(string title, string artist) =>
        title.Length == 0 ? "" : artist.Length == 0 ? title : $"{title} · {artist}";

    /// <summary>
    /// Hover delay (ms) of an island at a display with a menu bar: its notch hangs into the windows below the bar, so
    /// passing by on the way to a tab must not open it; resting on it does.
    /// </summary>
    public static int HoverDelayMs(int setting, bool menuBarHere) => menuBarHere ? Math.Max(setting, 300) : Math.Max(0, setting);
}
