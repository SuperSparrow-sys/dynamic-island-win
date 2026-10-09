using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicBay.Core;
using DynamicBay.Motion;
using DynamicBay.Services;

namespace DynamicBay.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+I")]
    [InlineData("Strg+Umschalt+5")]
    [InlineData("Win+Shift+F12")]
    public void Parses_valid_shortcuts(string text)
    {
        Assert.True(Hotkey.TryParse(text, out uint mods, out uint vk));
        Assert.NotEqual(0u, mods);
        Assert.NotEqual(0u, vk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I")]          // a modifier is required
    [InlineData("Ctrl+Banana")]
    public void Rejects_invalid_shortcuts(string text) => Assert.False(Hotkey.TryParse(text, out _, out _));

    [Fact]
    public void Ctrl_alt_i_maps_to_expected_codes()
    {
        Hotkey.TryParse("Ctrl+Alt+I", out uint mods, out uint vk);
        Assert.Equal(Native.MOD_CONTROL | Native.MOD_ALT, mods);
        Assert.Equal(0x49u, vk); // 'I'
    }
}

public class SpringTests
{
    [Fact]
    public void Spring_settles_on_target()
    {
        var s = new Spring(0, response: 0.4, damping: 0.8) { Target = 100 };
        for (int i = 0; i < 2000 && !s.IsSettled; i++) s.Step(1 / 480.0);
        Assert.True(s.IsSettled);
        Assert.Equal(100, s.Value);
    }

    [Fact]
    public void Underdamped_spring_overshoots_like_apples_bouncy_morph()
    {
        var s = new Spring(0, response: 0.5, damping: 0.6) { Target = 1 };
        double max = 0;
        for (int i = 0; i < 1000; i++) { s.Step(1 / 480.0); max = Math.Max(max, s.Value); }
        Assert.True(max > 1.01);
    }

    [Fact]
    public void Critically_damped_spring_never_overshoots()
    {
        var s = new Spring(0, response: 0.4, damping: 1.0) { Target = 1 };
        for (int i = 0; i < 1000; i++) { s.Step(1 / 480.0); Assert.True(s.Value <= 1.0005); }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.35)]
    public void Spring_ease_starts_at_0_and_ends_at_1(double bounce)
    {
        Assert.Equal(0, SpringEase.Evaluate(0, bounce), 6);
        Assert.Equal(1, SpringEase.Evaluate(1, bounce), 6);
    }
}

public class AppIconsTests
{
    [Theory]
    [InlineData("Discord")]
    [InlineData("WhatsApp")]
    [InlineData("Google Chrome")]
    [InlineData("Opera-Browser")]     // noise word stripped
    [InlineData("Spotify (Web-App)")]
    public void Known_apps_get_a_brand_glyph(string name) => Assert.NotNull(AppIcons.Find(name, ""));

    [Fact]
    public void Unknown_app_has_no_glyph() => Assert.Null(AppIcons.Find("ModbusMaster (Active)", @"C:\x\ModbusMaster.exe"));

    [Fact]
    public void Palette_contains_thousands_of_icons() => Assert.True(AppIcons.Count > 3000);

    [Fact]
    public void Normalize_strips_case_and_symbols() => Assert.Equal("visualstudiocode", AppIcons.Normalize("Visual Studio-Code"));
}

public class SettingsTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var s = new AppSettings { Edge = IslandEdge.Left, Displays = DisplayMode.Mirror, AppIcons = AppIconStyle.Dark };
        s.HomeWidgets.Clear();
        s.HomeWidgets.Add(Widgets.Clock);
        s.CalendarAccounts.Add(new CalendarAccount { Kind = CalendarKind.ICloud, User = "a@b.c" });
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json), Json)!;
        Assert.Equal(IslandEdge.Left, back.Edge);
        Assert.Equal(DisplayMode.Mirror, back.Displays);
        Assert.Equal(new[] { Widgets.Clock }, back.HomeWidgets);
        Assert.Equal(CalendarKind.ICloud, back.CalendarAccounts.Single().Kind);
    }

    [Fact]
    public void Collection_changes_raise_property_changed_for_autosave()
    {
        var s = new AppSettings();
        var raised = new List<string?>();
        s.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        s.Shortcuts.Add("x");
        s.CompactItems.Remove(Widgets.Battery);
        Assert.Contains(nameof(AppSettings.Shortcuts), raised);
        Assert.Contains(nameof(AppSettings.CompactItems), raised);
    }

    [Fact]
    public void Old_single_ics_url_is_migrated_to_an_account()
    {
        var s = new AppSettings { CalendarIcsUrl = "https://example.com/cal.ics", MediaPeekOnTrackChange = true };
        s.Migrate();
        Assert.Equal("", s.CalendarIcsUrl);
        Assert.Equal("https://example.com/cal.ics", s.CalendarAccounts.Single().Url);
    }

    [Fact]
    public void Defaults_match_the_agreed_design()
    {
        var s = new AppSettings();
        Assert.Equal(IslandEdge.Top, s.Edge);
        Assert.Equal(IdleStyle.Bar, s.Idle);
        Assert.Equal(AppIconStyle.Mono, s.AppIcons);
        Assert.Equal(50, s.ClipboardMax);
        Assert.True(s.SuppressBanners);
        Assert.Empty(s.BannerExceptions);
    }
}

public class CalendarParseTests
{
    private const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//test//EN
        BEGIN:VEVENT
        UID:1
        DTSTART:20261009T120000Z
        DTEND:20261009T130000Z
        SUMMARY:Design Review
        END:VEVENT
        BEGIN:VEVENT
        UID:2
        DTSTART;VALUE=DATE:20261010
        DTEND;VALUE=DATE:20261011
        SUMMARY:Feiertag
        END:VEVENT
        BEGIN:VEVENT
        UID:3
        DTSTART:20261020T120000Z
        DTEND:20261020T130000Z
        SUMMARY:Später
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Parses_events_inside_the_window_only()
    {
        var events = CalendarService.Parse(Ics, new DateTime(2026, 10, 9), new DateTime(2026, 10, 11), "#FF0000");
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Title == "Design Review" && !e.AllDay);
        Assert.Contains(events, e => e.Title == "Feiertag" && e.AllDay);
        Assert.DoesNotContain(events, e => e.Title == "Später");
    }
}

public class CloudTargetTests
{
    [Fact]
    public void Saving_twice_creates_unique_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "dynamicbay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = new CloudTarget("Test", root, "test");
            var a = CloudTargets.SaveText(target, "one", "note.txt");
            var b = CloudTargets.SaveText(target, "two", "note.txt");
            Assert.NotEqual(a, b);
            Assert.Equal("one", File.ReadAllText(a));
            Assert.EndsWith("note (2).txt", b);
            Assert.StartsWith(Path.Combine(root, "DynamicBay"), a);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class LocalizationTests
{
    [Fact]
    public void Every_key_exists_in_both_languages()
    {
        var field = typeof(Loc).GetField("Table", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var table = (Dictionary<string, (string de, string en)>)field.GetValue(null)!;
        foreach (var (key, (de, en)) in table)
        {
            Assert.False(string.IsNullOrWhiteSpace(de), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
        }
    }
}

public class UpdateTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0-beta.1", "1.0.0", false)]
    [InlineData("v2.0.0", "1.9.9", true)]
    [InlineData("v1.0.0", "1.0.1", false)]
    public void Detects_newer_versions(string tag, string current, bool newer)
    {
        Assert.True(UpdateCheck.TryParse(tag, out var v));
        Assert.Equal(newer, UpdateCheck.IsNewer(v, current));
    }

    [Fact]
    public void Rejects_garbage_tags() => Assert.False(UpdateCheck.TryParse("latest", out _));
}

public class MigrationTests
{
    [Fact]
    public void Old_settings_turn_the_track_change_peek_off_once()
    {
        var s = new AppSettings { MediaPeekOnTrackChange = true, SettingsVersion = 0 };
        s.Migrate();
        Assert.False(s.MediaPeekOnTrackChange);
        Assert.True(s.SettingsVersion >= 2);
        s.MediaPeekOnTrackChange = true; // the user's own choice afterwards is kept
        s.Migrate();
        Assert.True(s.MediaPeekOnTrackChange);
    }
}

public class ExcludedAppsTests
{
    [Theory]
    [InlineData(@"shell:AppsFolder\{6D809377-6AF0-444B-8957-A3773F02200E}\Google\Chrome\Application\chrome.exe", "chrome.exe")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"C:\Tools\obs64.exe", "obs64.exe")]
    public void Picked_apps_become_exe_names_or_app_ids(string launchPath, string expected) =>
        Assert.Equal(expected, ExcludedApps.TokenFor(new InstalledApp("x", launchPath)));

    [Fact]
    public void Matches_by_process_name_or_window_app_id()
    {
        var entries = new[] { "obs64.exe", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" };
        Assert.True(ExcludedApps.Matches(entries, "OBS64", () => null));
        Assert.True(ExcludedApps.Matches(entries, "CalculatorApp", () => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
        Assert.False(ExcludedApps.Matches(entries, "notepad", () => "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"));
    }

    [Fact]
    public void Window_app_id_is_only_read_when_an_app_id_entry_exists()
    {
        bool read = false;
        ExcludedApps.Matches(new[] { "obs64.exe" }, "notepad", () => { read = true; return null; });
        Assert.False(read);
    }
}

public class CalendarDayTests
{
    private static readonly DateTime Day = new(2026, 10, 9);

    [Fact]
    public void All_day_event_belongs_to_its_day_only()
    {
        var ev = new CalendarEvent { Start = Day, End = Day.AddDays(1), AllDay = true };
        Assert.True(ev.IsOn(Day));
        Assert.False(ev.IsOn(Day.AddDays(1)));
        Assert.False(ev.IsOn(Day.AddDays(-1)));
    }

    [Fact]
    public void Multi_day_and_overnight_events_show_on_every_day_they_touch()
    {
        var trip = new CalendarEvent { Start = Day, End = Day.AddDays(3), AllDay = true };
        Assert.True(trip.IsOn(Day.AddDays(2)));
        var party = new CalendarEvent { Start = Day.AddHours(22), End = Day.AddDays(1).AddHours(2) };
        Assert.True(party.IsOn(Day));
        Assert.True(party.IsOn(Day.AddDays(1)));
    }
}

public class ClaudeNotificationTests
{
    [Theory]
    [InlineData("Claude", "Claude_pzs8sxrjxfjjc!Claude", "Claude braucht deine Eingabe", "Darf ich die Datei ändern?", true)]
    [InlineData("Google Chrome", "Chrome", "Claude hat eine Frage", "Remote Control · DynamicBay", true)]
    [InlineData("Claude", null, "Aufgabe fertig", "Claude ist fertig", false)]
    [InlineData("Claude", null, "Task completed", "", false)]
    [InlineData("WhatsApp", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "Lena", "Kommst du?", false)]
    public void Recognises_questions_from_claude(string app, string? appId, string title, string body, bool expected) =>
        Assert.Equal(expected, ClaudeService.NeedsAnswer(app, appId, title, body));

    [Fact]
    public void Remote_question_shows_waiting_until_answered()
    {
        var claude = new ClaudeService(new AppSettings());
        claude.RemoteAsked(42);
        Assert.True(claude.AnyWaiting);
        claude.RemoteAnswered(42);
        Assert.False(claude.AnyWaiting);
    }
}

public class DevicesWidgetTests
{
    [Fact]
    public void Migration_adds_the_devices_widget_once()
    {
        var s = new AppSettings { SettingsVersion = 4 };
        s.Migrate();
        Assert.Contains(Widgets.Devices, s.HomeWidgets);
        s.HomeWidgets.Remove(Widgets.Devices);   // user switched it off
        s.Migrate();
        Assert.DoesNotContain(Widgets.Devices, s.HomeWidgets);
    }

    [Theory]
    [InlineData(null, "–")]
    [InlineData(5, "5 %")]
    public void Battery_label(int? pct, string expected) =>
        Assert.Equal(expected, new DynamicBay.Controls.BatteryRing { Mode = "text" }.Convert(pct!, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
}
