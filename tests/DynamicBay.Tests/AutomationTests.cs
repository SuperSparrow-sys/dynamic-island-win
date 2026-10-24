using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>
/// Automations end to end, as flows: each step (a report from the iPhone, a meeting beginning …) is followed by what
/// the panel shows. The panel is a fake that keeps one override like the Anzeigetafel does (a new command replaces
/// it, "plan" with nur_wenn_grund only gives back its own). The table the server runs is the contract in
/// Fixtures/Api/Server; here it runs on this PC (no server). No test talks to the real Anzeigetafel.
/// </summary>
[Collection("Loc")]
public class AutomationTests
{
    private static string Fixture(string dir, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", dir, file));

    private static JsonObject Obj(string dir, string file) => (JsonObject)JsonNode.Parse(Fixture(dir, file))!;

    /// <summary>Same command, leaving out who sent it (the server sets "DynamicBay-Server", a PC its own name).</summary>
    private static void SameCommand(string file, JsonObject actual)
    {
        var expected = Obj("Anzeigetafel", file);
        expected.Remove("quelle");
        var copy = (JsonObject)actual.DeepClone();
        copy.Remove("quelle");
        Assert.True(JsonNode.DeepEquals(expected, copy), $"{file}: {actual.ToJsonString()}");
    }

    /// <summary>The panel as the Anzeigetafel keeps it: one override or the weekly plan (here: on).</summary>
    private sealed class FakePanel : HttpMessageHandler
    {
        public readonly List<JsonObject> Commands = new();
        public string? Reason;
        public string Action = "";

        public bool On => Reason is null || Action != "aus";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null && JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) is JsonObject c)
            {
                lock (Commands) Commands.Add(c);
                string action = (string)c["aktion"]!;
                if (action == "plan")
                {
                    string? only = (string?)c["nur_wenn_grund"];
                    if (only is null || only == Reason) Reason = null;
                }
                else { Reason = (string)c["grund"]!; Action = action; }
            }
            var answer = new JsonObject
            {
                ["v"] = 1, ["panel"] = new JsonObject { ["erreichbar"] = true, ["an"] = On, ["prozent"] = 70 },
                ["soll"] = new JsonObject { ["zustand"] = On ? "hell" : "schwarz", ["prozent"] = On ? 70 : 0, ["grund"] = Reason is null ? "Wochenplan" : "Vorrang" },
                ["vorrang"] = Reason is null ? null : new JsonObject { ["zustand"] = On ? "hell" : "schwarz", ["grund"] = Reason, ["dauer"] = "ende" },
                ["steuern"] = true,
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Flow
    {
        public readonly AppSettings S = new() { AnzeigetafelEnabled = true, AnzeigetafelUrl = "https://tafel.test:20004", LinkDeviceName = "LAPTOPJONATHAN" };
        public readonly FakePanel Panel = new();
        public readonly AnzeigetafelService Tafel;

        public Flow()
        {
            foreach (var p in FocusProfile.Defaults()) S.FocusProfiles.Add(p);
            Tafel = new AnzeigetafelService(S, Dispatcher.CurrentDispatcher, Panel, () => "geheim", _ => { });
        }

        /// <summary>A report from the iPhone, as PhoneFocusService passes it on ("Zuhause" sets where it is).</summary>
        public async Task Report(string name, bool on)
        {
            if (S.IsPresenceName(name)) S.PresenceHome = on;
            await Tafel.ReportAsync(name, on);
            await Settle();
        }

        public async Task Camera(bool on)
        {
            S.CameraOn = on;
            await Tafel.LocalChangedAsync();
            await Settle();
        }

        public async Task Waited(string when)
        {
            await Tafel.WaitedAsync(when);
            await Settle();
        }

        /// <summary>The settings event sends too (fire and forget): let it finish.</summary>
        private static Task Settle() => Task.Delay(30);

        public JsonObject Last { get { lock (Panel.Commands) return Panel.Commands[^1]; } }
        public int Count { get { lock (Panel.Commands) return Panel.Commands.Count; } }
    }

    // ---------- the table and the commands are the contract ----------

    [Fact]
    public void The_table_for_the_server_is_the_contract()
    {
        var defaults = FocusProfile.Defaults().ToList();
        var table = PanelRules.ToJson(PanelRules.Table(defaults, "Zuhause"));
        var expected = Obj("Server", "ws_einstellung_automatik_warten.json")["wert"]!;
        Assert.True(JsonNode.DeepEquals(expected, table), table.ToJsonString());
        // The camera is not in the table: it is on at a PC, which sends its own command.
        Assert.DoesNotContain("Kamera", table.ToJsonString());
    }

    [Fact]
    public void The_camera_sends_the_contract_and_gives_the_panel_back_quickly()
    {
        // The contract example is named "Besprechung"; the trigger is the camera.
        var meeting = FocusProfile.Defaults().Single(p => p.Trigger == Triggers.Camera);
        meeting.Name = "Besprechung";
        SameCommand("anfrage_besprechung_aus.json", PanelRules.While(meeting, "LAPTOPJONATHAN")!);
        SameCommand("anfrage_besprechung_ende.json", PanelRules.Back(meeting, "LAPTOPJONATHAN"));
    }

    // ---------- flows ----------

    [Fact]
    public async Task Leaving_switches_off_after_a_minute_and_coming_home_back_on()
    {
        var f = new Flow();
        await f.Report("Zuhause", true);
        SameCommand("anfrage_zuhause_an.json", f.Last);
        Assert.True(f.Panel.On);

        await f.Report("Zuhause", false);
        Assert.True(f.Panel.On); // waits a minute
        await f.Waited("Zuhause|aus");
        SameCommand("anfrage_zuhause_aus.json", f.Last);
        Assert.False(f.Panel.On);

        await f.Report("Zuhause", true);
        Assert.True(f.Panel.On);
    }

    [Fact]
    public async Task A_short_wifi_drop_at_home_changes_nothing()
    {
        var f = new Flow();
        await f.Report("Zuhause", true);
        int before = f.Count;
        await f.Report("Zuhause", false);
        await f.Report("Zuhause", true); // back within the minute
        await f.Waited("Zuhause|aus"); // its time would be up now: it was cancelled
        Assert.True(f.Panel.On);
        Assert.DoesNotContain(f.Panel.Commands.Skip(before), c => (string)c["aktion"]! == "aus");
        // The same report again is no change.
        int now = f.Count;
        await f.Report("Zuhause", true);
        Assert.Equal(now, f.Count);
    }

    [Fact]
    public async Task The_camera_at_home_switches_off_quickly_and_back_afterwards()
    {
        var f = new Flow();
        await f.Report("Zuhause", true);
        await f.Camera(true);
        Assert.False(f.Panel.On);
        Assert.Equal(10, (int)f.Last["uebergang_s"]!);
        await f.Camera(false);
        Assert.True(f.Panel.On);
        Assert.Equal("Automation Kamera", (string)f.Last["nur_wenn_grund"]!);
    }

    [Fact]
    public async Task Away_wins_the_camera_on_the_laptop_does_not_touch_the_panel()
    {
        var f = new Flow();
        await f.Report("Zuhause", false);
        await f.Waited("Zuhause|aus");
        int before = f.Count;
        await f.Camera(true);
        await f.Camera(false);
        Assert.Equal(before, f.Count);
        Assert.False(f.Panel.On);
        await f.Report("Zuhause", true);
        Assert.True(f.Panel.On);
    }

    [Fact]
    public async Task Leaving_with_the_camera_on_keeps_the_panel_off_until_coming_home()
    {
        var f = new Flow();
        await f.Report("Zuhause", true);
        await f.Camera(true);
        await f.Report("Zuhause", false);
        await f.Waited("Zuhause|aus");
        Assert.Equal("Weg", f.Panel.Reason);
        // The camera goes off on the way: its "plan" only gives back its own override, "Weg" stays.
        await f.Camera(false);
        Assert.False(f.Panel.On);
        await f.Report("Zuhause", true);
        Assert.True(f.Panel.On);
    }

    [Fact]
    public async Task Do_not_disturb_on_the_iphone_changes_nothing()
    {
        var f = new Flow();
        await f.Report("Zuhause", true);
        int before = f.Count;
        await f.Report("Nicht stören", true);
        await f.Report("Nicht stören", false);
        Assert.Equal(before, f.Count);
        Assert.True(f.S.PresenceHome);
        Assert.True(f.Panel.On);
    }

    [Fact]
    public async Task Sleeping_at_home_switches_off_and_waking_up_back_on_but_not_while_away()
    {
        var f = new Flow();
        await f.Report("Zuhause", true);
        await f.Report("Schlafen", true);
        Assert.False(f.Panel.On);
        await f.Report("Schlafen", false);
        Assert.True(f.Panel.On);

        await f.Report("Zuhause", false);
        await f.Waited("Zuhause|aus");
        int before = f.Count;
        await f.Report("Schlafen", true); // in a hotel: the panel at home is off anyway, nothing is sent
        Assert.Equal(before, f.Count);
    }

    [Fact]
    public async Task By_hand_the_widget_switches_the_panel_on_also_while_away()
    {
        var f = new Flow();
        await f.Report("Zuhause", false);
        await f.Waited("Zuhause|aus");
        await f.Tafel.ToggleCommand.ExecuteAsync(null); // shows "off": switches on
        Assert.Equal(("an", "Insel"), ((string)f.Last["aktion"]!, (string)f.Last["grund"]!));
        Assert.True(f.Panel.On);
    }

    [Fact]
    public async Task An_automation_switched_off_does_nothing()
    {
        var f = new Flow();
        f.S.FocusProfiles.Single(p => p.Trigger == Triggers.Away).Enabled = false;
        await f.Report("Zuhause", true);
        await f.Report("Zuhause", false);
        await f.Waited("Zuhause|aus");
        Assert.True(f.Panel.On);
    }

    // ---------- Teams: grey only instead of green, while away ----------

    [Fact]
    public void Away_turns_teams_grey_only_when_it_is_green_and_never_during_a_call_or_the_camera()
    {
        var s = new AppSettings();
        foreach (var p in FocusProfile.Defaults()) s.FocusProfiles.Add(p);
        s.TeamsNatural = "Available";
        s.PresenceHome = true;
        Assert.Equal("", s.FocusTeamsStatus); // at home Teams shows what it shows by itself
        s.PresenceHome = false;
        Assert.Equal("Offline", s.FocusTeamsStatus);
        // Set by us: Teams' own status is hidden now; it stays grey.
        s.TeamsAuto = "Offline";
        s.TeamsNatural = "Offline";
        Assert.Equal("Offline", s.FocusTeamsStatus);
        // A call or the camera on the laptop: Teams would be red, the grey goes.
        s.InMeeting = true;
        Assert.Equal("", s.FocusTeamsStatus);
        s.InMeeting = false;
        s.CameraOn = true;
        Assert.Equal("", s.FocusTeamsStatus);
        s.CameraOn = false;
        // Yellow or red by itself: left as it is.
        s.TeamsAuto = "";
        foreach (var own in new[] { "Away", "BeRightBack", "Busy", "DoNotDisturb" })
        {
            s.TeamsNatural = own;
            Assert.Equal("", s.FocusTeamsStatus);
        }
        // Without the condition the status counts always.
        s.FocusProfiles.Single(p => p.Trigger == Triggers.Away).TeamsOnlyIfAvailable = false;
        s.FocusProfilesChanged(save: false);
        Assert.Equal("Offline", s.FocusTeamsStatus);
    }

    // ---------- the settings of 1.1.12 to 1.1.16 become automations ----------

    [Fact]
    public void Old_focus_profiles_become_automations()
    {
        var s = new AppSettings { AnzeigetafelEnabled = true, SettingsVersion = 12 };
        s.FocusProfiles.Add(new FocusProfile { Name = "Arbeit", TeamsStatus = "Busy" });
        s.FocusProfiles.Add(new FocusProfile { Name = "Schule", PanelEnd = "aus" });
        s.FocusProfiles.Add(new FocusProfile { Name = "Schlafen", Panel = "aus" });
        s.FocusProfiles.Add(new FocusProfile { Name = "Zuhause", Quiet = true, PanelEnd = "aus" });
        s.Migrate(cursorHopInstalled: false);

        var home = s.FocusProfiles.Single(p => p.Trigger == Triggers.Home);
        Assert.Equal(("Zu Hause", "plan", true), (home.Name, home.Panel, home.Quiet));
        var away = s.FocusProfiles.Single(p => p.Trigger == Triggers.Away);
        Assert.Equal(("aus", "Offline", true), (away.Panel, away.TeamsStatus, away.TeamsOnlyIfAvailable));
        var meeting = s.FocusProfiles.Single(p => p.Trigger == Triggers.Camera);
        Assert.Equal(("aus", 10), (meeting.Panel, meeting.PanelFade));
        Assert.Equal(new[] { "Arbeit", "Schule", "Schlafen" }, s.FocusProfiles.Where(p => p.Trigger == Triggers.Focus).Select(p => p.Name));
        Assert.Null(s.ProfileFor("Zuhause")); // the report "Zuhause" is where the iPhone is, not a focus
        Assert.Equal(13, s.SettingsVersion);
    }

    [Fact]
    public void A_new_installation_starts_with_home_away_camera_and_three_focus_modes()
    {
        var s = new AppSettings { SettingsVersion = 11 };
        s.Migrate(cursorHopInstalled: false);
        Assert.Equal(new[] { Triggers.Home, Triggers.Away, Triggers.Camera, Triggers.Focus, Triggers.Focus, Triggers.Focus }, s.FocusProfiles.Select(p => p.Trigger));
        Assert.Single(s.FocusProfiles, p => p.Trigger == Triggers.Camera);
    }

    // ---------- what holds and what it switches ----------

    [Fact]
    public void Automations_hold_with_their_trigger_and_the_upper_one_wins()
    {
        var s = new AppSettings();
        foreach (var p in FocusProfile.Defaults()) s.FocusProfiles.Add(p);
        s.FocusProfiles.Single(p => p.Trigger == Triggers.Camera).TeamsStatus = "DoNotDisturb";
        s.FocusProfiles.Single(p => p.Trigger == Triggers.Home).TeamsStatus = "Available";
        s.PresenceHome = true;
        Assert.Equal("Available", s.FocusTeamsStatus);
        s.CameraOn = true;
        Assert.Equal(new[] { "Zu Hause", "Kamera" }, s.ActiveRules.Select(p => p.Name));
        Assert.Equal("Available", s.FocusTeamsStatus); // "Zu Hause" is above "Kamera"
        s.FocusProfiles.Move(2, 0);
        s.FocusProfilesChanged(save: false);
        Assert.Equal("DoNotDisturb", s.FocusTeamsStatus);

        s.PhoneFocusHere = "Schlafen";
        Assert.True(s.FocusHidesIsland);
        Assert.True(s.Quiet);
        s.PhoneFocusHere = "";
        s.CameraOn = false;
        Assert.False(s.FocusHidesIsland);
    }
}
