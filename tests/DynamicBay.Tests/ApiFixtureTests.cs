using System.IO;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DynamicBay.Core;
using DynamicBay.Services;
using DynamicBay.Services.Calendars;
using DynamicBay.Services.M365;

namespace DynamicBay.Tests;

/// <summary>
/// The parsers fed with captured, documented-shape responses of each provider (Fixtures/Api/&lt;provider&gt;),
/// including the odd ones: ads without a track, local files, cancelled and all-day events, empty calendar-data,
/// releases without a setup. No test here talks to a real service.
/// </summary>
[Collection("Loc")]
public class ApiFixtureTests
{
    private static string Dir(string provider) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", provider);
    private static string Text(string provider, string file) => File.ReadAllText(Path.Combine(Dir(provider), file));
    private static JsonNode Json(string provider, string file) => JsonNode.Parse(Text(provider, file))!;
    private static XDocument Xml(string provider, string file) => XDocument.Parse(Text(provider, file));
    private static IEnumerable<string> Files(string provider, string pattern) => Directory.GetFiles(Dir(provider), pattern);

    // ---------- Spotify ----------

    [Fact]
    public void Spotify_player_with_a_track()
    {
        var p = SpotifyService.ParsePlayerState(Json("Spotify", "player_track.json"));
        Assert.True(p.Shuffle);
        Assert.Equal("context", p.Repeat);
        Assert.True(p.CanShuffle);
        Assert.True(p.CanRepeat);
        Assert.Equal("DESKTOP-JONAS", p.DeviceName);
        Assert.True(p.SupportsVolume);
        Assert.Equal(64, p.Volume);
        Assert.Equal("spotify:track:0VjIjW4GlUZAMYd2vXMi3b", p.TrackUri);
    }

    [Fact]
    public void Spotify_player_during_an_ad_has_no_track_and_allows_nothing()
    {
        var p = SpotifyService.ParsePlayerState(Json("Spotify", "player_ad.json"));
        Assert.Null(p.TrackUri);
        Assert.False(p.CanShuffle);
        Assert.False(p.CanRepeat);
        Assert.Equal("off", p.Repeat);
    }

    [Fact]
    public void Spotify_player_with_a_podcast_on_a_restricted_speaker()
    {
        var p = SpotifyService.ParsePlayerState(Json("Spotify", "player_episode.json"));
        Assert.Equal("spotify:episode:512ojhOuo1ktJprKbVcKyQ", p.TrackUri);
        Assert.Equal("Wohnzimmer", p.DeviceName);
        Assert.False(p.SupportsVolume);
        Assert.Null(p.Volume); // volume_percent: null keeps the last known volume
        Assert.False(p.CanShuffle);
    }

    [Fact]
    public void Spotify_player_with_a_local_file_and_odd_values()
    {
        var p = SpotifyService.ParsePlayerState(Json("Spotify", "player_local_file.json"));
        Assert.StartsWith("spotify:local:", p.TrackUri);
        Assert.Equal("track", p.Repeat);
        Assert.False(p.SupportsVolume); // missing
        Assert.Equal(30, p.Volume);
        Assert.True(p.CanRepeat);       // "yes" is no boolean: not read as a restriction
    }

    [Fact]
    public void Spotify_devices_skip_null_entries_and_keep_restricted_ones()
    {
        var devices = SpotifyService.ParseDevices(Json("Spotify", "devices.json"));
        Assert.Equal(3, devices.Count);
        Assert.True(devices[0].IsActive);
        Assert.Equal("", devices[1].Id);
        Assert.Equal("Wohnzimmer TV", devices[1].Name);
        Assert.False(devices[2].IsActive);
    }

    [Fact]
    public void Spotify_queue_with_episode_local_file_and_holes()
    {
        var queue = SpotifyService.ParseQueue(Json("Spotify", "queue.json"));
        Assert.Equal(new[] { 0, 1, 3, 4 }, queue.Select(q => q.Index));
        Assert.Equal("Dua Lipa, DaBaby", queue[0].Artist);
        Assert.EndsWith("ab67616d00004851bd26ede1ae69327010d49946", queue[0].ImageUrl);
        Assert.Equal("Technik-Talk", queue[1].Artist);
        Assert.NotNull(queue[1].ImageUrl);
        Assert.Equal("Band aus dem Keller", queue[2].Artist);
        Assert.Null(queue[2].ImageUrl);
        Assert.Equal("", queue[3].Artist);
        Assert.Null(queue[3].ImageUrl);
    }

    [Fact]
    public void Spotify_playlists_with_missing_images_counts_and_owner()
    {
        var lists = SpotifyService.ParsePlaylists(Json("Spotify", "playlists.json"));
        Assert.Equal(3, lists.Count);
        Assert.Contains("57", lists[0].Detail);
        Assert.Contains("Jonas", lists[0].Detail);
        Assert.EndsWith("/60/ab67616d00001e02aaa", lists[0].ImageUrl);
        Assert.Null(lists[1].ImageUrl);
        Assert.StartsWith("0 ", lists[1].Detail);
        Assert.StartsWith("0 ", lists[2].Detail);
        Assert.Equal("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", lists[2].Uri);
    }

    [Theory]
    [InlineData("[true]", true)]
    [InlineData("[false]", false)]
    [InlineData("[]", false)]
    [InlineData("[null]", false)]
    [InlineData("""{"error":{"status":400,"message":"Unsupported URI"}}""", false)]
    public void Spotify_library_contains(string json, bool liked) =>
        Assert.Equal(liked, SpotifyService.ParseContains(JsonNode.Parse(json)));

    [Fact]
    public void Spotify_profile_without_display_name_uses_the_id()
    {
        Assert.Equal("jonas.example", SpotifyService.ParseUserName(Json("Spotify", "me.json")));
        Assert.Equal("Spotify", SpotifyService.ParseUserName(null));
    }

    [Fact]
    public void Spotify_parsers_never_throw_on_any_spotify_fixture()
    {
        foreach (var file in Files("Spotify", "*.json"))
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            var ex = Record.Exception(() =>
            {
                SpotifyService.ParsePlayerState(json);
                SpotifyService.ParseDevices(json);
                SpotifyService.ParseQueue(json);
                SpotifyService.ParsePlaylists(json);
                SpotifyService.ParseContains(json);
                SpotifyService.ParseUserName(json);
            });
            Assert.True(ex is null, $"{Path.GetFileName(file)}: {ex}");
        }
    }

    // ---------- Microsoft Graph ----------

    [Fact]
    public void Graph_calendars_skip_ones_without_id_and_auto_colour_is_null()
    {
        var cals = OutlookCalendar.ParseCalendars(Json("Graph", "calendars.json"));
        Assert.Equal(3, cals.Count);
        Assert.Equal("#0078d4", cals[0].Color);
        Assert.Null(cals[1].Color);
        Assert.Equal("", cals[2].Name);
    }

    [Fact]
    public void Graph_events_with_teams_without_location_all_day_cancelled_and_foreign_zones()
    {
        var events = OutlookCalendar.ParseEvents(Json("Graph", "calendarView.json"), "#0078d4");
        Assert.Equal(5, events.Count); // cancelled and null entries are left out
        Assert.DoesNotContain(events, e => e.Title.StartsWith("Abgesagt"));

        var sync = events.Single(e => e.Title == "Weekly Sync");
        Assert.Equal("Teams", sync.Meeting?.Service);
        Assert.True(sync.End > sync.Start);
        Assert.Equal("#0078d4", sync.Color);

        var dentist = events.Single(e => e.Title == "Zahnarzt");
        Assert.Null(dentist.Location);
        Assert.Null(dentist.Meeting);

        var holiday = events.Single(e => e.AllDay);
        Assert.Equal(new DateTime(2026, 10, 14), holiday.Start);
        Assert.Equal(new DateTime(2026, 10, 15), holiday.End);

        var seattle = events.Single(e => e.Title.Contains("Seattle"));
        Assert.Equal("Zoom", seattle.Meeting?.Service);
        Assert.Equal(TimeSpan.FromMinutes(30), seattle.End - seattle.Start);

        var broken = events.Single(e => e.Title == "");
        Assert.False(broken.AllDay);
    }

    [Fact]
    public void Graph_todo_lists_get_their_names_and_lists_without_id_are_skipped()
    {
        Loc.Init("de");
        var lists = TodoService.ParseLists(Json("Graph", "todo_lists.json"));
        Assert.Equal(new[] { "Aufgaben", "Gekennzeichnete E-Mails", "Einkaufen" }, lists.Select(l => l.Name));
    }

    [Fact]
    public void Graph_todo_tasks_leave_out_completed_and_broken_ones()
    {
        var tasks = TodoService.ParseTasks("list1", ((JsonArray)Json("Graph", "todo_tasks.json")["value"]!).ToList());
        Assert.Equal(4, tasks.Count);
        Assert.All(tasks, t => Assert.Equal("list1", t.ListId));
        Assert.True(tasks[0].Important);
        Assert.Equal(new DateTime(2026, 10, 12), tasks[0].Due); // midnight UTC = that day, never the day before
        Assert.NotNull(tasks[1].Due);
        Assert.Equal("", tasks[2].Title);
        Assert.Null(tasks[2].Due);
        Assert.Equal(new DateTime(2026, 10, 14), tasks[3].Due); // unknown zone: the date as written
    }

    [Fact]
    public void Graph_presence()
    {
        Assert.Equal("BusyIdle", PresenceService.ParseAvailability(Json("Graph", "presence.json")));
        Assert.Equal("PresenceUnknown", PresenceService.ParseAvailability(JsonNode.Parse("{}")));
        Assert.Equal("PresenceUnknown", PresenceService.ParseAvailability(null));
    }

    [Fact]
    public void Graph_people_are_persons_only_with_the_best_phone()
    {
        var people = ContactsService.ParsePeople(Json("Graph", "people.json"));
        Assert.Equal(new[] { "Anna Schmidt", "Lukas Weber", "Mia Fischer" }, people.Select(p => p.Name));
        Assert.Equal("+49 151 2345678", people[0].Phone); // mobile before business
        Assert.Equal("", people[1].Email);
        Assert.Equal("", people[1].Phone);
        Assert.Equal("+49 40 555", people[2].Phone);
    }

    [Fact]
    public void Graph_contacts_fall_back_to_business_and_home_numbers()
    {
        var contacts = ContactsService.ParseContacts(Json("Graph", "contacts.json"));
        Assert.Equal(3, contacts.Count);
        Assert.Equal("+49 89 9876543", contacts[0].Phone);
        Assert.Equal("jonas.becker@example.com", contacts[0].Email);
        Assert.Equal("+49 621 12345", contacts[1].Phone);
        Assert.Equal("", contacts[1].Email);
        Assert.Equal("", contacts[2].Name); // dropped later by the service (no name)
    }

    [Fact]
    public void Graph_parsers_never_throw_on_any_graph_fixture()
    {
        foreach (var file in Files("Graph", "*.json"))
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            var ex = Record.Exception(() =>
            {
                OutlookCalendar.ParseCalendars(json);
                OutlookCalendar.ParseEvents(json, null);
                TodoService.ParseLists(json);
                TodoService.ParseTasks("x", (json["value"] as JsonArray)?.ToList() ?? new());
                PresenceService.ParseAvailability(json);
                ContactsService.ParsePeople(json);
                ContactsService.ParseContacts(json);
            });
            Assert.True(ex is null, $"{Path.GetFileName(file)}: {ex}");
        }
    }

    // ---------- Google Calendar ----------

    [Fact]
    public void Google_calendar_list_skips_switched_off_and_broken_entries()
    {
        var cals = GoogleCalendarClient.ParseCalendarList(Json("Google", "calendarList.json"));
        Assert.Equal(2, cals.Count);
        Assert.Equal(("jonas.example@gmail.com", "jonas.example@gmail.com", "#9fe1e7"), cals[0]);
        Assert.Equal("Feiertage", cals[1].name); // summaryOverride wins
    }

    [Fact]
    public void Google_events_with_date_and_dateTime_cancelled_and_missing_fields()
    {
        var events = GoogleCalendarClient.ParseEvents(Json("Google", "events.json"), "#9fe1e7");
        Assert.Equal(5, events.Count); // cancelled, start-less and null entries are left out

        var review = events.Single(e => e.Title == "Design Review");
        Assert.False(review.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero).LocalDateTime, review.Start);
        Assert.Equal(TimeSpan.FromHours(1), review.End - review.Start);
        Assert.Equal("Büro, Raum 4", review.Location);

        var birthday = events.Single(e => e.Title == "Geburtstag Mia");
        Assert.True(birthday.AllDay);
        Assert.Equal(new DateTime(2026, 10, 14), birthday.Start);
        Assert.Equal(new DateTime(2026, 10, 15), birthday.End);

        var teams = events.Single(e => e.Meeting?.Service == "Teams");
        Assert.Equal("(ohne Titel)", teams.Title);

        var noEnd = events.Single(e => e.Title == "Ohne Ende");
        Assert.Equal(TimeSpan.FromHours(1), noEnd.End - noEnd.Start);

        var fair = events.Single(e => e.Title == "Messe");
        Assert.Equal(4, (fair.End - fair.Start).Days);
        Assert.All(events, e => Assert.Equal("#9fe1e7", e.Color));
    }

    [Fact]
    public void Google_parsers_never_throw_on_any_google_fixture()
    {
        foreach (var file in Files("Google", "*.json"))
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            var ex = Record.Exception(() =>
            {
                GoogleCalendarClient.ParseCalendarList(json);
                GoogleCalendarClient.ParseEvents(json, null);
            });
            Assert.True(ex is null, $"{Path.GetFileName(file)}: {ex}");
        }
    }

    // ---------- CalDAV (iCloud) ----------

    [Fact]
    public void CalDav_discovery_hrefs()
    {
        Assert.Equal("/1234567890/principal/", CalDavClient.Href(Xml("CalDav", "principal.xml"), CalDavClient.PrincipalProp));
        Assert.Equal("https://p42-caldav.icloud.com:443/1234567890/calendars/", CalDavClient.Href(Xml("CalDav", "home.xml"), CalDavClient.HomeSetProp));
        Assert.Null(CalDavClient.Href(Xml("CalDav", "principal_missing.xml"), CalDavClient.PrincipalProp));
    }

    [Fact]
    public void CalDav_calendar_list_keeps_event_calendars_only()
    {
        var home = new Uri("https://p42-caldav.icloud.com/1234567890/calendars/");
        var cals = CalDavClient.ParseCalendars(Xml("CalDav", "calendars.xml"), home);
        Assert.Equal(3, cals.Count); // not the home itself, reminders (VTODO only), the inbox or an entry without href
        Assert.Equal("Privat", cals[0].Name);
        Assert.Equal("#1BADF8FF", cals[0].Color);
        Assert.Equal("https://p42-caldav.icloud.com/1234567890/calendars/home/", cals[0].Url.ToString());
        Assert.Equal("Arbeit & Projekte", cals[1].Name);
        Assert.Null(cals[1].Color);
        Assert.Equal("Kalender", cals[2].Name); // empty display name
    }

    [Fact]
    public void CalDav_report_skips_empty_calendar_data_and_events_expand()
    {
        var blobs = CalDavClient.ParseCalendarData(Xml("CalDav", "report.xml"));
        Assert.Equal(3, blobs.Count);

        DateTime from = new(2026, 10, 10), to = new(2026, 10, 17);
        var events = blobs.SelectMany(b => CalendarService.Parse(b, from, to, "#1BADF8FF")).ToList();

        var doctor = events.Single(e => e.Title == "Arzttermin");
        Assert.Equal("Praxis Dr. Weber, Hauptstraße 5", doctor.Location);
        Assert.Equal(TimeSpan.FromMinutes(30), doctor.End - doctor.Start);

        // Weekly Mon/Wed/Fri, Wednesday excluded: Monday 12th and Friday 16th.
        var standups = events.Where(e => e.Title == "Stand-up").OrderBy(e => e.Start).ToList();
        Assert.Equal(2, standups.Count);
        Assert.Equal(DayOfWeek.Monday, standups[0].Start.DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, standups[1].Start.DayOfWeek);
        Assert.All(standups, s => Assert.Equal("Teams", s.Meeting?.Service));

        var off = events.Single(e => e.Title == "Urlaubstag");
        Assert.True(off.AllDay);
        Assert.Equal(new DateTime(2026, 10, 14), off.Start.Date);
    }

    [Fact]
    public void Ics_without_end_or_summary_and_with_a_todo()
    {
        var events = CalendarService.Parse(Text("CalDav", "no_end_no_summary.ics"), new DateTime(2026, 10, 10), new DateTime(2026, 10, 17), null);
        Assert.Equal(2, events.Count); // the VTODO is not an event
        Assert.Contains(events, e => e.Title == "" && e.End >= e.Start);
        var withDuration = events.Single(e => e.Title == "Mit Dauer statt Ende");
        Assert.Equal(TimeSpan.FromMinutes(45), withDuration.End - withDuration.Start);
        Assert.Equal("Zoom", withDuration.Meeting?.Service);
    }

    [Fact]
    public void CalDav_parsers_never_throw_on_any_xml_fixture()
    {
        var home = new Uri("https://caldav.icloud.com/1/calendars/");
        foreach (var file in Files("CalDav", "*.xml"))
        {
            var doc = XDocument.Parse(File.ReadAllText(file));
            var ex = Record.Exception(() =>
            {
                CalDavClient.Href(doc, CalDavClient.PrincipalProp);
                CalDavClient.Href(doc, CalDavClient.HomeSetProp);
                CalDavClient.ParseCalendars(doc, home);
                CalDavClient.ParseCalendarData(doc);
            });
            Assert.True(ex is null, $"{Path.GetFileName(file)}: {ex}");
        }
    }

    // ---------- GitHub releases ----------

    [Fact]
    public void GitHub_release_with_setup_and_digest()
    {
        var r = UpdateCheck.ParseRelease(Json("GitHub", "release_latest.json"))!;
        Assert.Equal(new Version(1, 0, 18), r.Version);
        Assert.EndsWith("/DynamicBay-Setup-1.0.18.exe", r.SetupUrl); // not the portable zip listed first
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", r.Sha256);
        Assert.Equal(5242880, r.Size);
        Assert.Equal("https://github.com/SuperSparrow-sys/dynamic-island-win/releases/tag/v1.0.18", r.PageUrl);
    }

    [Fact]
    public void GitHub_release_without_setup_opens_the_page_instead()
    {
        var r = UpdateCheck.ParseRelease(Json("GitHub", "release_no_setup.json"))!;
        Assert.Null(r.SetupUrl);
        Assert.Null(r.Sha256);
        Assert.Equal(0, r.Size);
    }

    [Fact]
    public void GitHub_prerelease_without_digest_is_never_installable()
    {
        var r = UpdateCheck.ParseRelease(Json("GitHub", "release_prerelease.json"))!;
        Assert.Equal(new Version(1, 1, 0), r.Version);
        Assert.NotNull(r.SetupUrl);
        Assert.Null(r.Sha256); // InstallAsync refuses a setup without checksum
        Assert.True(UpdateCheck.IsNewer(r.Tag, "1.0.17"));
        Assert.False(UpdateCheck.IsNewer(r.Tag, "1.1.0"));
    }

    [Fact]
    public void GitHub_draft_and_error_answers_give_no_release()
    {
        Assert.Null(UpdateCheck.ParseRelease(Json("GitHub", "release_draft.json")));
        Assert.Null(UpdateCheck.ParseRelease(Json("GitHub", "error_rate_limit.json")));
        Assert.Null(UpdateCheck.ParseRelease(null));
    }
}
