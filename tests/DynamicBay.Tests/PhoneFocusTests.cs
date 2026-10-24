using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Services;
using DynamicBay.Services.M365;

namespace DynamicBay.Tests;

/// <summary>
/// iPhone focus: the request shapes in Fixtures/Api/PhoneFocus, the rules of the endpoint (address, key, browser,
/// size), the focus after reports in either order, the profiles in the settings and the listener on loopback.
/// No test talks to an iPhone, Teams or another machine.
/// </summary>
[Collection("Loc")]
public class PhoneFocusTests
{
    private const string Key = "abcd-efgh-ijkm-npqr-stuv-wxyz";

    private static FocusReport? Fixture(string file) =>
        PhoneFocusProtocol.Parse("", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "PhoneFocus", file)));

    private static HttpHead Head(string method = "POST", string path = "/focus", params (string, string)[] headers)
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + Key, ["Content-Length"] = "30" };
        foreach (var (k, v) in headers) all[k] = v;
        return new HttpHead(method, path, "", all);
    }

    private static readonly IPAddress Lan = IPAddress.Parse("192.168.178.24");

    // ---------- shapes ----------

    [Fact]
    public void Shortcut_with_a_boolean()
    {
        Assert.Equal(new FocusReport("Arbeit", true), Fixture("kurzbefehl_bool.json"));
    }

    [Fact]
    public void State_as_german_text()
    {
        Assert.Equal(new FocusReport("Schule", false), Fixture("kurzbefehl_text.json"));
    }

    [Fact]
    public void Name_and_state_with_unknown_fields()
    {
        Assert.Equal(new FocusReport("Schlafen", true), Fixture("name_state.json"));
    }

    [Fact]
    public void Without_a_state_nothing_happens()
    {
        Assert.Null(Fixture("ohne_zustand.json"));
        Assert.Null(PhoneFocusProtocol.Parse("", "{\"on\": true}"));
        Assert.Null(PhoneFocusProtocol.Parse("", "{\"focus\": 3, \"on\": true}"));
        Assert.Null(PhoneFocusProtocol.Parse("", "{broken"));
        Assert.Null(PhoneFocusProtocol.Parse("", "[\"Arbeit\", true]"));
        Assert.Null(PhoneFocusProtocol.Parse("", ""));
    }

    [Fact]
    public void Query_works_without_a_body()
    {
        Assert.Equal(new FocusReport("Nicht stören", true), PhoneFocusProtocol.Parse("focus=Nicht%20st%C3%B6ren&on=1", ""));
        Assert.Equal(new FocusReport("Lesen", false), PhoneFocusProtocol.Parse("name=Lesen&state=off", ""));
        // The body wins over the query.
        Assert.Equal(new FocusReport("Arbeit", true), PhoneFocusProtocol.Parse("focus=Schule&on=0", "{\"focus\":\"Arbeit\",\"on\":true}"));
    }

    [Fact]
    public void State_words_and_numbers()
    {
        foreach (var w in new[] { "on", "true", "1", "an", "ein", "Aktiv", " JA " }) Assert.True(PhoneFocusProtocol.OnOf(w));
        foreach (var w in new[] { "off", "false", "0", "aus", "inaktiv", "nein" }) Assert.False(PhoneFocusProtocol.OnOf(w));
        Assert.Null(PhoneFocusProtocol.OnOf("vielleicht"));
        Assert.Equal(new FocusReport("Arbeit", false), PhoneFocusProtocol.Parse("", "{\"focus\":\"Arbeit\",\"on\":0}"));
    }

    [Fact]
    public void Names_lose_control_characters_and_are_cut()
    {
        Assert.Equal("Arbeit", PhoneFocusProtocol.Clean("  Ar‮beit\n "));
        Assert.Equal(PhoneFocusProtocol.MaxName, PhoneFocusProtocol.Clean(new string('x', 200)).Length);
        Assert.Equal("", PhoneFocusProtocol.Clean(null));
    }

    // ---------- focus after reports ----------

    [Fact]
    public void Off_ends_only_the_same_focus()
    {
        // iOS: Schule turns on, then Arbeit turns off - or the other way round. Both orders end in Schule.
        string a = PhoneFocusProtocol.Next("Arbeit", new("Schule", true));
        a = PhoneFocusProtocol.Next(a, new("Arbeit", false));
        string b = PhoneFocusProtocol.Next("Arbeit", new("Arbeit", false));
        b = PhoneFocusProtocol.Next(b, new("Schule", true));
        Assert.Equal("Schule", a);
        Assert.Equal("Schule", b);
        Assert.Equal("", PhoneFocusProtocol.Next("arbeit", new("Arbeit", false)));
    }

    // ---------- rules of the endpoint ----------

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.5", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.178.24", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2a00:1450:4001::1", false)]
    public void Only_lan_and_tailscale(string ip, bool allowed) =>
        Assert.Equal(allowed, PhoneFocusProtocol.IsAllowedRemote(IPAddress.Parse(ip)));

    [Fact]
    public void Key_ignores_case_spaces_and_dashes()
    {
        Assert.True(PhoneFocusProtocol.KeyMatches("Bearer " + Key, Key));
        Assert.True(PhoneFocusProtocol.KeyMatches("bearer ABCDEFGHIJKMNPQRSTUVWXYZ", Key));
        Assert.True(PhoneFocusProtocol.KeyMatches("Bearer abcd efgh ijkm npqr stuv wxyz", Key));
        Assert.False(PhoneFocusProtocol.KeyMatches("Bearer abcd-efgh-ijkm-npqr-stuv-wxya", Key));
        Assert.False(PhoneFocusProtocol.KeyMatches(Key, Key));
        Assert.False(PhoneFocusProtocol.KeyMatches(null, Key));
        Assert.False(PhoneFocusProtocol.KeyMatches("Bearer ", ""));
    }

    [Fact]
    public void New_keys_are_typeable_and_differ()
    {
        var k = PhoneFocusProtocol.NewKey();
        Assert.Matches("^[a-km-np-z2-9]{4}(-[a-km-np-z2-9]{4}){5}$", k);
        Assert.NotEqual(k, PhoneFocusProtocol.NewKey());
    }

    [Fact]
    public void Refusals()
    {
        Assert.Null(PhoneFocusProtocol.Refuse(Head(), Lan, Key));
        Assert.Equal(403, PhoneFocusProtocol.Refuse(Head(), IPAddress.Parse("8.8.8.8"), Key));
        Assert.Equal(403, PhoneFocusProtocol.Refuse(Head(headers: ("Origin", "https://evil.example")), Lan, Key));
        Assert.Equal(403, PhoneFocusProtocol.Refuse(Head(headers: ("Sec-Fetch-Site", "cross-site")), Lan, Key));
        Assert.Equal(404, PhoneFocusProtocol.Refuse(Head(path: "/claude/"), Lan, Key));
        Assert.Equal(405, PhoneFocusProtocol.Refuse(Head(method: "GET"), Lan, Key));
        Assert.Equal(401, PhoneFocusProtocol.Refuse(Head(headers: ("Authorization", "Bearer falsch")), Lan, Key));
        Assert.Equal(401, PhoneFocusProtocol.Refuse(Head(), Lan, ""));
        Assert.Equal(411, PhoneFocusProtocol.Refuse(Head(headers: ("Transfer-Encoding", "chunked")), Lan, Key));
        Assert.Equal(413, PhoneFocusProtocol.Refuse(Head(headers: ("Content-Length", "5000")), Lan, Key));
    }

    [Fact]
    public void Request_head()
    {
        var h = PhoneFocusProtocol.ParseHead("POST /focus?on=1 HTTP/1.1\r\nHost: pc:43825\r\ncontent-length: 12\r\nAuthorization: Bearer x");
        Assert.NotNull(h);
        Assert.Equal(("POST", "/focus", "on=1"), (h!.Method, h.Path, h.Query));
        Assert.Equal(12, PhoneFocusProtocol.ContentLength(h));
        Assert.Equal("Bearer x", h.Header("authorization"));
        Assert.Null(PhoneFocusProtocol.ParseHead("hello"));
        Assert.Null(PhoneFocusProtocol.ParseHead("POST /focus HTTP/1.1\r\nkaputt"));
    }

    // ---------- profiles in the settings ----------

    private static AppSettings WithDefaults()
    {
        var s = new AppSettings { SettingsVersion = 11 };
        s.Migrate(cursorHopInstalled: false);
        return s;
    }

    [Fact]
    public void Migration_brings_the_automations_once()
    {
        var s = WithDefaults();
        Assert.Equal(new[] { "Zu Hause", "Unterwegs", "Kamera", "Arbeit", "Schule", "Schlafen" }, s.FocusProfiles.Select(p => p.Name));
        s.FocusProfiles.RemoveAt(5);
        s.Migrate(cursorHopInstalled: false);
        Assert.Equal(5, s.FocusProfiles.Count);
        // Someone who already has profiles (an import) keeps them.
        var own = new AppSettings { SettingsVersion = 11 };
        own.FocusProfiles.Add(new FocusProfile { Name = "Fitness" });
        own.Migrate(cursorHopInstalled: false);
        Assert.Equal("Fitness", Assert.Single(own.FocusProfiles).Name);
    }

    [Fact]
    public void Profile_switches_what_it_names()
    {
        var s = WithDefaults();
        var changed = new List<string?>();
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(s.Quiet);
        Assert.True(s.ShowNotificationText);

        s.PhoneFocusHere = "schule"; // case does not matter
        Assert.Contains(nameof(AppSettings.Quiet), changed);
        Assert.Contains(nameof(AppSettings.ActiveFocus), changed);
        Assert.Equal("Schule", s.ActiveFocus!.Name);
        Assert.True(s.Quiet);
        Assert.False(s.ShowNotificationText);
        Assert.True(s.FocusHidesWidget(Widgets.Media));
        Assert.False(s.FocusHidesIsland);

        s.PhoneFocusHere = "Schlafen";
        Assert.True(s.FocusHidesIsland);
        Assert.False(s.FocusHidesWidget(Widgets.Media));

        s.PhoneFocusHere = "Arbeit";
        Assert.False(s.Quiet);
        Assert.Equal("Busy", s.FocusTeamsStatus);
        s.ActiveFocus!.MutedApps.Add("WhatsApp");
        Assert.True(s.IsMuted("whatsapp"));
        s.PhoneFocusHere = "";
        Assert.False(s.IsMuted("WhatsApp"));
        Assert.Null(s.ActiveFocus);
    }

    [Fact]
    public void Own_focus_wins_over_the_paired_one_and_teams_follows_only_the_own()
    {
        var s = WithDefaults();
        s.PhoneFocusPeer = "Arbeit";
        Assert.Equal("Arbeit", s.ActiveFocus!.Name);
        Assert.Equal("", s.FocusTeamsStatus); // the PC that got it sets Teams, not the laptop as well
        s.PhoneFocusHere = "Schule";
        Assert.Equal("Schule", s.ActiveFocus!.Name);
    }

    [Fact]
    public void Edited_profile_raises_quiet()
    {
        var s = WithDefaults();
        s.PhoneFocusHere = "Arbeit";
        Assert.False(s.Quiet);
        var changed = new List<string?>();
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        s.ActiveFocus!.Quiet = true;
        s.FocusProfilesChanged(save: false);
        Assert.Contains(nameof(AppSettings.Quiet), changed);
        Assert.True(s.Quiet);
    }

    [Fact]
    public void Profiles_survive_the_settings_file_and_the_focus_does_not()
    {
        var s = WithDefaults();
        s.PhoneFocusEnabled = true;
        s.PhoneFocusHere = "Arbeit";
        s.ProfileFor("Schule")!.MutedApps.Add("Outlook");
        var back = AppSettings.FromJson(s.ToJson());
        Assert.True(back.PhoneFocusEnabled);
        Assert.Equal("", back.PhoneFocusHere);
        Assert.Equal(new[] { "Outlook" }, back.ProfileFor("Schule")!.MutedApps);
        Assert.Equal(new[] { Widgets.Media }, back.ProfileFor("Schule")!.HiddenWidgets);
        Assert.Equal(Triggers.Away, back.FocusProfiles[1].Trigger);
        // Older or hand-edited files: missing lists are empty, not null.
        var odd = AppSettings.FromJson("{\"FocusProfiles\":[{\"Name\":\"Lesen\",\"MutedApps\":null}]}");
        Assert.Empty(odd.FocusProfiles[0].MutedApps);
        Assert.Empty(odd.FocusProfiles[0].HiddenWidgets);
        Assert.DoesNotContain("PhoneFocusHere", s.ToJson());
    }

    [Fact]
    public void Teams_target()
    {
        Assert.Equal("DoNotDisturb", PresenceService.AutoTargetOf(quiet: true, "Busy"));
        Assert.Equal("Busy", PresenceService.AutoTargetOf(quiet: false, "Busy"));
        Assert.Null(PresenceService.AutoTargetOf(quiet: false, ""));
        Assert.Null(PresenceService.AutoTargetOf(quiet: false, "Kaffee"));
    }

    // ---------- service ----------

    [Fact]
    public void Service_adds_unknown_focus_and_ends_after_16_hours()
    {
        var s = WithDefaults();
        var service = new PhoneFocusService(s, Dispatcher.CurrentDispatcher);
        var t0 = new DateTime(2026, 10, 24, 9, 0, 0);
        service.Receive(new("Fitness", true), "192.168.178.24", t0);
        Assert.Equal("Fitness", s.PhoneFocusHere);
        Assert.Contains(s.FocusProfiles, p => p.Name == "Fitness");
        Assert.False(s.Quiet); // a new profile switches nothing yet

        service.Receive(new("Arbeit", false), "192.168.178.24", t0.AddMinutes(1)); // an old focus ending late
        Assert.Equal("Fitness", s.PhoneFocusHere);
        Assert.StartsWith("Arbeit aus", service.LastEvent);

        service.CheckExpiry(t0.AddHours(15));
        Assert.Equal("Fitness", s.PhoneFocusHere);
        service.CheckExpiry(t0.AddHours(16).AddMinutes(1));
        Assert.Equal("", s.PhoneFocusHere);

        // An "off" of a focus nobody knows creates nothing.
        service.Receive(new("Gaming", false), "192.168.178.24", t0);
        Assert.DoesNotContain(s.FocusProfiles, p => p.Name == "Gaming");
    }

    [Fact]
    public void Service_creates_at_most_twelve_profiles()
    {
        var s = WithDefaults();
        var service = new PhoneFocusService(s, Dispatcher.CurrentDispatcher);
        for (int i = 0; i < 20; i++) service.Receive(new($"Fokus {i}", true), "10.0.0.2", DateTime.Now);
        Assert.Equal(PhoneFocusProtocol.MaxProfiles, s.FocusProfiles.Count(p => p.Trigger == Triggers.Focus));
    }

    [Fact]
    public void Service_names_the_refusal()
    {
        var service = new PhoneFocusService(WithDefaults(), Dispatcher.CurrentDispatcher);
        service.Refused(401, "192.168.178.24", new DateTime(2026, 10, 24, 9, 30, 0));
        Assert.Equal("Abgelehnt: falscher Schlüssel, 09:30", service.LastEvent);
    }

    // ---------- listener on loopback ----------

    private static async Task<string> Send(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))) ?? "";
    }

    private static string Post(string body, string key = Key, string extra = "") =>
        $"POST /focus HTTP/1.1\r\nHost: pc\r\nAuthorization: Bearer {key}\r\nContent-Type: application/json\r\n{extra}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    [Fact]
    public async Task Listener_takes_the_report_and_refuses_the_rest()
    {
        var reports = new List<FocusReport>();
        var refused = new List<int>();
        using var l = new PhoneFocusListener(0, () => Key, (r, _) => { lock (reports) reports.Add(r); }, (st, _) => { lock (refused) refused.Add(st); }, loopbackOnly: true);

        Assert.Equal("HTTP/1.1 204 No Content", await Send(l.Port, Post("{\"focus\":\"Schlafen\",\"on\":true}")));
        lock (reports) Assert.Equal(new FocusReport("Schlafen", true), Assert.Single(reports));

        Assert.StartsWith("HTTP/1.1 401", await Send(l.Port, Post("{\"focus\":\"Arbeit\",\"on\":true}", key: "falsch")));
        Assert.StartsWith("HTTP/1.1 403", await Send(l.Port, Post("{\"focus\":\"Arbeit\",\"on\":true}", extra: "Origin: http://evil\r\n")));
        Assert.StartsWith("HTTP/1.1 405", await Send(l.Port, "GET /focus HTTP/1.1\r\nHost: pc\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 413", await Send(l.Port, Post(new string(' ', 5000))));
        Assert.StartsWith("HTTP/1.1 400", await Send(l.Port, Post("{\"focus\":\"Arbeit\"}")));
        Assert.StartsWith("HTTP/1.1 400", await Send(l.Port, "nonsense\r\n\r\n"));
        // Query form, as in a shortcut without a JSON body.
        Assert.Equal("HTTP/1.1 204 No Content", await Send(l.Port, $"POST /focus?focus=Schlafen&on=aus HTTP/1.1\r\nAuthorization: Bearer {Key}\r\nContent-Length: 0\r\n\r\n"));
        lock (reports) Assert.Equal(new FocusReport("Schlafen", false), reports[^1]);
        lock (refused) Assert.Equal(new[] { 401, 403, 405, 413, 400, 400 }, refused);
    }

    [Fact]
    public async Task Listener_reads_a_body_that_comes_in_pieces()
    {
        var got = new TaskCompletionSource<FocusReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var l = new PhoneFocusListener(0, () => Key, (r, _) => got.TrySetResult(r), (_, _) => { }, loopbackOnly: true);
        string body = "{\"focus\":\"Arbeit\",\"on\":true}";
        string request = Post(body);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, l.Port);
        var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(request);
        int split = request.IndexOf("\r\n\r\n", StringComparison.Ordinal) - 5;
        await stream.WriteAsync(bytes.AsMemory(0, split));
        await stream.FlushAsync();
        await Task.Delay(100);
        await stream.WriteAsync(bytes.AsMemory(split));
        Assert.Equal(new FocusReport("Arbeit", true), await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
