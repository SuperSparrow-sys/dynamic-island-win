using System.IO;
using DynamicBay.Core;

namespace DynamicBay.Tests;

public class MenuBarMathTests
{
    private static readonly (string, bool)[] Displays = { (@"\\.\DISPLAY1", true), (@"\\.\DISPLAY2", false), (@"\\.\DISPLAY3", false) };

    [Theory]
    [InlineData(MenuBarScreens.Off, "")]
    [InlineData(MenuBarScreens.Island, @"\\.\DISPLAY2")]
    [InlineData(MenuBarScreens.Secondary, @"\\.\DISPLAY2|\\.\DISPLAY3")]
    [InlineData(MenuBarScreens.All, @"\\.\DISPLAY1|\\.\DISPLAY2|\\.\DISPLAY3")]
    public void Bars_go_to_the_chosen_displays(MenuBarScreens mode, string expected)
    {
        var screens = MenuBarMath.Screens(mode, Displays, @"\\.\display2");
        Assert.Equal(expected, string.Join("|", screens));
    }

    [Fact]
    public void Island_on_a_display_that_is_gone_gets_no_bar()
    {
        Assert.Empty(MenuBarMath.Screens(MenuBarScreens.Island, Displays, @"\\.\DISPLAY9"));
    }

    [Fact]
    public void Bar_is_lower_than_the_compact_notch()
    {
        Assert.True(MenuBarMath.HeightDip < 32);
    }

    [Theory]
    [InlineData(40, true, "Fluent.SpeakerMute")]
    [InlineData(0, false, "Fluent.Speaker0")]
    [InlineData(30, false, "Fluent.Speaker1")]
    [InlineData(80, false, "Fluent.Speaker2")]
    public void Volume_symbol(int volume, bool muted, string expected) =>
        Assert.Equal(expected, MenuBarMath.VolumeIcon(volume, muted));

    [Theory]
    [InlineData(false, true, 4, "Fluent.WifiOff")]
    [InlineData(true, false, 0, "Fluent.Ethernet")]
    [InlineData(true, true, 0, "Fluent.Wifi1")]
    [InlineData(true, true, 3, "Fluent.Wifi3")]
    [InlineData(true, true, 5, "Fluent.Wifi4")]
    public void Network_symbol(bool connected, bool wlan, int bars, string expected) =>
        Assert.Equal(expected, MenuBarMath.NetworkIcon(connected, wlan, bars));

    [Theory]
    [InlineData(96, false, "Fluent.Battery10")]
    [InlineData(45, false, "Fluent.Battery5")]
    [InlineData(3, true, "Fluent.BatteryCharge0")]
    [InlineData(150, true, "Fluent.BatteryCharge10")]
    public void Battery_symbol_in_tenths(int percent, bool charging, string expected) =>
        Assert.Equal(expected, MenuBarMath.BatteryIcon(percent, charging));

    [Fact]
    public void Date_like_the_mac_menu_bar()
    {
        var d = new DateTime(2026, 11, 18, 13, 57, 0);
        Assert.Equal("Mi. 18. Nov.", MenuBarMath.DateText(d, german: true));
        Assert.Equal("Wed 18 Nov", MenuBarMath.DateText(d, german: false));
        Assert.Equal("Mi. 6. Mai", MenuBarMath.DateText(new DateTime(2026, 5, 6), german: true));
        Assert.Equal("18. Nov.", MenuBarMath.DateText(d, german: true, weekday: false));
        Assert.Equal("Mi.", MenuBarMath.DateText(d, german: true, date: false));
        Assert.Equal("", MenuBarMath.DateText(d, german: false, weekday: false, date: false));
    }

    [Fact]
    public void Song_with_artist_or_alone()
    {
        Assert.Equal("Halo · Beyoncé", MenuBarMath.MediaText("Halo", "Beyoncé"));
        Assert.Equal("Podcast", MenuBarMath.MediaText("Podcast", ""));
        Assert.Equal("", MenuBarMath.MediaText("", "Beyoncé"));
    }

    [Theory]
    [InlineData(0, true, 300)]
    [InlineData(500, true, 500)]
    [InlineData(0, false, 0)]
    [InlineData(-5, false, 0)]
    public void Passing_by_the_notch_under_a_menu_bar_does_not_open_it(int setting, bool bar, int expected) =>
        Assert.Equal(expected, MenuBarMath.HoverDelayMs(setting, bar));

    [Fact]
    public void Gradient_takes_the_average_colour_of_each_slice_below_the_bar()
    {
        // 4 x 2 pixels BGRA: left half blue, right half white; the second row a bit darker
        byte[] px =
        {
            255, 0, 0, 255,  255, 0, 0, 255,  255, 255, 255, 255,  255, 255, 255, 255,
            205, 0, 0, 255,  205, 0, 0, 255,  205, 205, 205, 255,  205, 205, 205, 255,
        };
        var slices = MenuBarMath.Slices(px, width: 4, rows: 2, count: 2)!;
        Assert.Equal(System.Windows.Media.Color.FromRgb(0, 0, 230), slices[0]);
        Assert.Equal(System.Windows.Media.Color.FromRgb(230, 230, 230), slices[1]);
        // More slices than pixels: no empty slice, no exception
        Assert.Equal(6, MenuBarMath.Slices(px, width: 4, rows: 2, count: 6)!.Length);
    }

    [Fact]
    public void The_island_over_the_row_is_left_out_and_bridged_from_both_sides()
    {
        // 6 x 1 pixels: blue, blue, black island, black island, white, white
        byte[] px =
        {
            255, 0, 0, 255,  255, 0, 0, 255,  0, 0, 0, 255,  0, 0, 0, 255,  255, 255, 255, 255,  255, 255, 255, 255,
        };
        var slices = MenuBarMath.Slices(px, width: 6, rows: 1, count: 3, skip: new[] { (2, 4) })!;
        Assert.Equal(System.Windows.Media.Color.FromRgb(0, 0, 255), slices[0]);
        Assert.Equal(System.Windows.Media.Color.FromRgb(127, 127, 255), slices[1]); // between blue and white, no black
        Assert.Equal(System.Windows.Media.Color.FromRgb(255, 255, 255), slices[2]);
        Assert.Null(MenuBarMath.Slices(px, width: 6, rows: 1, count: 3, skip: new[] { (0, 6) }));
    }

    [Fact]
    public void Text_turns_dark_over_a_light_window_and_stays_light_over_a_dark_one()
    {
        var chrome = new[] { System.Windows.Media.Color.FromRgb(0xDE, 0xE1, 0xE6), System.Windows.Media.Color.FromRgb(0xF1, 0xF3, 0xF4) };
        var night = new[] { System.Windows.Media.Color.FromRgb(0x1D, 0x4E, 0x89), System.Windows.Media.Color.FromRgb(0xE0, 0x7A, 0x5F) };
        Assert.True(MenuBarMath.IsLight(chrome));
        Assert.False(MenuBarMath.IsLight(night));
        Assert.False(MenuBarMath.IsLight(Array.Empty<System.Windows.Media.Color>()));
    }

    private static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact]
    public void Island_and_picture_in_picture_over_the_row_are_left_out_others_not()
    {
        var bar = R(1920, 0, 5760, 36);
        var covers = new[]
        {
            R(3600, 0, 3900, 48),    // compact notch hanging over the row
            R(5200, 20, 5700, 300),  // picture in picture at the top right
            R(2000, 400, 2600, 800), // window further down: not over the row
            R(0, 0, 1900, 1200),     // other display
        };
        Assert.Equal(new[] { (1668, 1992), (3268, 3792) }, MenuBarMath.Covered(bar, covers, margin: 12));
    }

    [Fact]
    public void Left_side_ends_before_the_notch_but_not_before_an_open_panel()
    {
        var bar = R(1920, 0, 5760, 36);
        Assert.Equal(3600 - 1920 - 18, MenuBarMath.LeftRoom(bar, new[] { R(3600, 0, 3900, 48) }, gap: 18));
        Assert.Null(MenuBarMath.LeftRoom(bar, new[] { R(3200, 0, 4400, 700) }, gap: 18)); // open panel
        Assert.Null(MenuBarMath.LeftRoom(bar, new[] { R(600, 0, 900, 48) }, gap: 18));    // island on another display
        Assert.Null(MenuBarMath.LeftRoom(bar, Array.Empty<Native.RECT>(), gap: 18));
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", @"C:\Program Files\WindowsApps\Spotify\Spotify.exe", null, true)]
    [InlineData(@"shell:AppsFolder\Chrome", "Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", null, true)]
    [InlineData(@"shell:AppsFolder\{6D809377-6AF0-444B-8957-A3773F02200E}\Obsidian\Obsidian.exe", null, @"C:\Program Files\Obsidian\Obsidian.exe", null, true)]
    [InlineData(@"shell:AppsFolder\{6D809377-6AF0-444B-8957-A3773F02200E}\Obsidian\Obsidian.exe", null, @"C:\Program Files\Other\Other.exe", null, false)]
    [InlineData(@"shell:AppsFolder\Chrome", null, @"C:\Program Files\Mozilla Firefox\firefox.exe", null, false)]
    [InlineData(@"C:\Tools\notepad++.exe", null, @"c:\tools\NOTEPAD++.EXE", null, true)]
    [InlineData(@"C:\Users\me\Desktop\Zotero.lnk", null, @"C:\Program Files\Zotero\zotero.exe", @"C:\Program Files\Zotero\zotero.exe", true)]
    [InlineData(@"C:\Users\me\Documents", null, @"C:\Windows\explorer.exe", null, false)]
    public void Pinned_app_finds_its_open_window(string launch, string? appId, string? exe, string? lnkTarget, bool expected)
    {
        string? Folder(Guid g) => g == new Guid("6D809377-6AF0-444B-8957-A3773F02200E") ? @"C:\Program Files" : null;
        Assert.Equal(expected, AppWindows.Matches(launch, appId, exe, lnkTarget, Folder));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, false, false, false, true)]   // borderless fullscreen film or game
    [InlineData(-8, -8, 1928, 1088, true, true, true, false)]  // maximized window with caption
    [InlineData(0, 0, 1920, 1080, false, true, true, false)]   // maximized borderless app (Spotify, VS Code)
    [InlineData(0, 0, 1920, 1000, false, false, false, false)] // not the whole display
    public void Fullscreen_is_a_window_over_the_whole_display_without_caption(int l, int t, int r, int b, bool caption, bool zoomed, bool frame, bool expected) =>
        Assert.Equal(expected, MenuBarMath.IsFullscreen(R(l, t, r, b), R(0, 0, 1920, 1080), caption, zoomed, frame));

    [Fact]
    public void A_killed_run_gets_its_strips_back_only_for_windows_that_are_gone()
    {
        string file = Path.Combine(Path.GetTempPath(), $"appbars-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(file, new[] { "1001", "1002", "junk", "0" });
        var removed = new List<long>();
        int n = AppBar.RemoveStale(file, h => h.ToInt64() == 1002, h => removed.Add(h.ToInt64())); // 1002 still lives
        Assert.Equal(1, n);
        Assert.Equal(new long[] { 1001 }, removed);
        Assert.False(File.Exists(file));
        Assert.Equal(0, AppBar.RemoveStale(file, _ => false, _ => throw new InvalidOperationException())); // no file: nothing
    }

    [Fact]
    public void Every_symbol_the_bar_asks_for_exists_in_the_fluent_icons()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DynamicBay.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "DynamicBay", "Themes", "Icons.Fluent.xaml"));
        var keys = new List<string> { "Fluent.WifiOff", "Fluent.Ethernet", "Fluent.SpeakerMute", "Fluent.Speaker0", "Fluent.Speaker1", "Fluent.Speaker2", "Fluent.Bluetooth" };
        for (int i = 1; i <= 4; i++) keys.Add($"Fluent.Wifi{i}");
        for (int i = 0; i <= 10; i++) { keys.Add($"Fluent.Battery{i}"); keys.Add($"Fluent.BatteryCharge{i}"); }
        foreach (var k in keys) Assert.Contains($"x:Key=\"{k}\"", xaml);
    }
}
