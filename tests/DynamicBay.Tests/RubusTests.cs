using System.IO;
using System.Text.Json.Nodes;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>
/// Rubus protocol v1 (POST /rubus/): the message shapes in Fixtures/Api/Rubus, limits, channels, expiry after 90 s,
/// clicks only once and only to their channel, the transitions that become peeks, and the listener rules.
/// No test talks to a real Rubus or opens a socket.
/// </summary>
[Collection("Loc")]
public class RubusTests
{
    private const string K = "0123456789abcdef0123456789abcdef";
    private const string K2 = "fedcba9876543210fedcba9876543210";
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static RubusNachricht? Fixture(string file) =>
        RubusProtocol.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "Rubus", file))));

    private static RubusNachricht Msg(string file) => Fixture(file) ?? throw new InvalidOperationException(file);

    private static RubusSitzung S(string id, RubusZustand z, string? grund = null, string text = "") => new(id, "T", "host", "p", z, grund, text, null);
    private static RubusAnfrage A(string id, bool fenster = false) => new(id, "rubus-17", "Titel", "p", "host", fenster);
    private static RubusStand Stand(RubusSitzung[]? s = null, RubusAnfrage[]? a = null) => new(s ?? Array.Empty<RubusSitzung>(), a ?? Array.Empty<RubusAnfrage>());
    private static RubusNachricht StandMsg(string kanal, RubusStand stand) => new(kanal, RubusArt.Stand, stand);
    private static RubusNachricht Abholen(string kanal) => new(kanal, RubusArt.Abholen, null);

    // ---------- shapes ----------

    [Fact]
    public void Stand_with_an_approval()
    {
        var m = Msg("stand_freigabe.json");
        Assert.Equal(K, m.Kanal);
        Assert.Equal(RubusArt.Stand, m.Art);
        var s = Assert.Single(m.Stand!.Sitzungen);
        Assert.Equal(("rubus-17", "Umbau", "frank-raspi", "automation"), (s.Id, s.Titel, s.Host, s.Projekt));
        Assert.Equal(RubusZustand.Wartet, s.Zustand);
        Assert.Equal("freigabe", s.Grund);
        Assert.Null(s.Seit); // 0 = unknown
        var a = Assert.Single(m.Stand.Anfragen);
        Assert.Equal(("0123456789abcdef", "rubus-17", "Claude möchte auf test deployen"), (a.Id, a.Sitzung, a.Titel));
        Assert.False(a.NurImFenster);
    }

    [Fact]
    public void Stand_with_every_state()
    {
        var m = Msg("stand_mehrere.json");
        Assert.Equal(new[] { RubusZustand.Arbeitet, RubusZustand.Wartet, RubusZustand.Bereit, RubusZustand.Unbekannt },
            m.Stand!.Sitzungen.Select(s => s.Zustand));
        Assert.Equal("erlaubnis", m.Stand.Sitzungen[1].Grund);
        Assert.Equal("Claude möchte Bash ausführen", m.Stand.Sitzungen[1].Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792224000).LocalDateTime, m.Stand.Sitzungen[0].Seit);
        Assert.Null(m.Stand.Sitzungen[2].Seit);
        Assert.True(Assert.Single(m.Stand.Anfragen).NurImFenster);
    }

    [Fact]
    public void Stand_with_error_interrupt_and_last_event()
    {
        var st = Msg("stand_gruende.json").Stand!;
        Assert.Equal(new string?[] { null, "fehler", "unterbrochen", "eingabe" }, st.Sitzungen.Select(s => s.Grund));
        var working = st.Sitzungen[0];
        Assert.Equal("Bash: pytest -q", working.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760800000).LocalDateTime, working.Seit); // start of the state
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760800042).LocalDateTime, working.Aktualisiert); // last event
        Assert.Equal("API Error: 529 overloaded", st.Sitzungen[1].Text);
        Assert.Null(st.Sitzungen[3].Aktualisiert); // missing
    }

    [Fact]
    public void Empty_stand()
    {
        var m = Msg("stand_leer.json");
        Assert.Empty(m.Stand!.Sitzungen);
        Assert.Empty(m.Stand.Anfragen);
    }

    [Fact]
    public void Unknown_fields_and_bad_entries_are_skipped()
    {
        var st = Msg("stand_tolerant.json").Stand!;
        Assert.Equal(new[] { "rubus-17", "rubus-21" }, st.Sitzungen.Select(s => s.Id)); // bad ids, non-objects and the duplicate left out
        Assert.Empty(st.Anfragen); // missing list
        var first = st.Sitzungen[0];
        Assert.Equal((RubusZustand.Unbekannt, (string?)null, (DateTime?)null, ""), (first.Zustand, first.Grund, first.Seit, first.Titel));
        var second = st.Sitzungen[1];
        Assert.Equal("ZeilemitSteuerzeichen", second.Titel); // control characters removed
        Assert.Equal(("", ""), (second.Host, second.Projekt)); // null and a number
        Assert.Equal("eingabe", second.Grund);
    }

    [Fact]
    public void Pickup_end_and_unknown_kind()
    {
        Assert.Equal(RubusArt.Abholen, Msg("abholen.json").Art);
        Assert.Equal(RubusArt.Ende, Msg("ende.json").Art);
        Assert.Equal(RubusArt.Unbekannt, Msg("unbekannte_art.json").Art);
        Assert.Null(Msg("abholen.json").Stand);
    }

    [Fact]
    public void Other_versions_and_bad_channels_are_refused()
    {
        Assert.Null(Fixture("version_2.json"));
        Assert.Null(Fixture("kanal_falsch.json"));
        Assert.Null(RubusProtocol.Parse(JsonNode.Parse("[]")));
        Assert.Null(RubusProtocol.Parse(JsonNode.Parse("""{"kanal":"0123456789abcdef0123456789abcdef","art":"abholen"}""")));
        // Upper case hex is the same channel.
        Assert.Equal(K, RubusProtocol.Parse(JsonNode.Parse("""{"v":1,"kanal":"0123456789ABCDEF0123456789ABCDEF","art":"abholen"}"""))!.Kanal);
    }

    [Fact]
    public void Lists_and_texts_are_bounded()
    {
        var o = new JsonObject
        {
            ["v"] = 1, ["kanal"] = K, ["art"] = "stand",
            ["sitzungen"] = new JsonArray(Enumerable.Range(0, 80).Select(i => (JsonNode)new JsonObject { ["id"] = $"s{i}", ["titel"] = new string('x', 1000) }).ToArray()),
            ["anfragen"] = new JsonArray(Enumerable.Range(0, 80).Select(i => (JsonNode)new JsonObject { ["id"] = $"a{i}" }).ToArray()),
        };
        var st = RubusProtocol.Parse(o)!.Stand!;
        Assert.Equal(RubusProtocol.MaxSitzungen, st.Sitzungen.Count);
        Assert.Equal(RubusProtocol.MaxAnfragen, st.Anfragen.Count);
        Assert.Equal(200, st.Sitzungen[0].Titel.Length);
    }

    // ---------- channels and clicks ----------

    [Fact]
    public void Stand_without_clicks_answers_204_and_clicks_come_once()
    {
        var b = new RubusBridge();
        var (status, body, _) = b.Handle(Msg("stand_freigabe.json"), T0);
        Assert.Equal((204, (string?)null), (status, body));

        Assert.True(b.Enqueue(K, new RubusAktion("erlauben", Anfrage: "0123456789abcdef")));
        Assert.False(b.Enqueue(K, new RubusAktion("ablehnen", Anfrage: "0123456789abcdef"))); // answered already
        Assert.True(b.Enqueue(K, new RubusAktion("sitzung_oeffnen", Sitzung: "rubus-17")));

        (status, body, _) = b.Handle(Abholen(K), T0.AddSeconds(2));
        Assert.Equal(200, status);
        var arr = JsonNode.Parse(body!)!["aktionen"]!.AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal(("erlauben", "0123456789abcdef"), (arr[0]!["art"]!.ToString(), arr[0]!["anfrage"]!.ToString()));
        Assert.Equal(("sitzung_oeffnen", "rubus-17"), (arr[1]!["art"]!.ToString(), arr[1]!["sitzung"]!.ToString()));
        Assert.Null(arr[1]!["anfrage"]);

        // Picked up: the next answer is empty, also while the request is still in the state.
        Assert.Equal(204, b.Handle(Abholen(K), T0.AddSeconds(4)).Status);
        Assert.Equal(204, b.Handle(Msg("stand_freigabe.json"), T0.AddSeconds(5)).Status);
        Assert.False(b.Enqueue(K, new RubusAktion("erlauben", Anfrage: "0123456789abcdef")));
    }

    [Fact]
    public void Clicks_only_go_to_the_channel_that_reported_the_request()
    {
        var b = new RubusBridge();
        b.Handle(StandMsg(K, Stand(new[] { S("rubus-17", RubusZustand.Wartet, "freigabe") }, new[] { A("aaaa") })), T0);
        b.Handle(StandMsg(K2, Stand()), T0);
        Assert.False(b.Enqueue(K2, new RubusAktion("erlauben", Anfrage: "aaaa"))); // not K2's request
        Assert.False(b.Enqueue(K2, new RubusAktion("sitzung_oeffnen", Sitzung: "rubus-17")));
        Assert.True(b.Enqueue(K, new RubusAktion("ablehnen", Anfrage: "aaaa")));
        Assert.Equal(204, b.Handle(Abholen(K2), T0.AddSeconds(1)).Status);
        Assert.Equal(200, b.Handle(Abholen(K), T0.AddSeconds(1)).Status);
        // Unknown channel, unknown request, unknown kind of click.
        Assert.False(b.Enqueue("ffffffffffffffffffffffffffffffff", new RubusAktion("erlauben", Anfrage: "aaaa")));
        Assert.False(b.Enqueue(K, new RubusAktion("erlauben", Anfrage: "bbbb")));
        Assert.False(b.Enqueue(K, new RubusAktion("loeschen", Anfrage: "aaaa")));
    }

    [Fact]
    public void Pickup_from_an_unknown_channel_creates_nothing()
    {
        var b = new RubusBridge();
        Assert.Equal(204, b.Handle(Abholen(K), T0).Status);
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void Stand_replaces_the_previous_one_of_its_channel_only()
    {
        var b = new RubusBridge();
        b.Handle(StandMsg(K, Stand(new[] { S("a", RubusZustand.Arbeitet), S("b", RubusZustand.Arbeitet) })), T0);
        b.Handle(StandMsg(K2, Stand(new[] { S("c", RubusZustand.Arbeitet) })), T0);
        b.Handle(StandMsg(K, Stand(new[] { S("b", RubusZustand.Bereit) })), T0.AddSeconds(1));
        var snap = b.Snapshot().ToDictionary(x => x.Kanal, x => x.Stand);
        Assert.Equal(new[] { "b" }, snap[K].Sitzungen.Select(s => s.Id));
        Assert.Equal(new[] { "c" }, snap[K2].Sitzungen.Select(s => s.Id));
    }

    [Fact]
    public void A_silent_channel_goes_after_90_seconds()
    {
        var b = new RubusBridge();
        b.Handle(StandMsg(K, Stand(new[] { S("rubus-17", RubusZustand.Wartet, "freigabe") }, new[] { A("aaaa") })), T0);
        b.Handle(StandMsg(K2, Stand(new[] { S("x", RubusZustand.Arbeitet) })), T0);
        b.Handle(Abholen(K2), T0.AddSeconds(60)); // any message counts as a sign of life
        Assert.Empty(b.Expire(T0.AddSeconds(89)));
        var gone = b.Expire(T0.AddSeconds(90));
        Assert.Equal(new RubusAnfrageWeg(K, "aaaa"), Assert.Single(gone));
        Assert.Equal(new[] { K2 }, b.Snapshot().Select(x => x.Kanal));
        Assert.False(b.Enqueue(K, new RubusAktion("erlauben", Anfrage: "aaaa")));
        Assert.Empty(b.Expire(T0.AddSeconds(150))); // K2 goes too; it had no requests
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void End_removes_the_channel_and_its_requests()
    {
        var b = new RubusBridge();
        b.Handle(StandMsg(K, Stand(new[] { S("rubus-17", RubusZustand.Wartet, "freigabe") }, new[] { A("aaaa"), A("bbbb") })), T0);
        var (status, _, events) = b.Handle(new RubusNachricht(K, RubusArt.Ende, null), T0.AddSeconds(1));
        Assert.Equal(204, status);
        Assert.Equal(new RubusEreignis[] { new RubusAnfrageWeg(K, "aaaa"), new RubusAnfrageWeg(K, "bbbb") }, events);
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void Switching_off_clears_every_channel()
    {
        var b = new RubusBridge();
        b.Handle(StandMsg(K, Stand(a: new[] { A("aaaa") })), T0);
        b.Handle(StandMsg(K2, Stand(a: new[] { A("bbbb") })), T0);
        Assert.Equal(2, b.Clear().Count);
        Assert.Empty(b.Snapshot());
    }

    [Fact]
    public void Channels_and_pending_clicks_are_bounded()
    {
        var b = new RubusBridge();
        for (int i = 0; i < RubusBridge.MaxKanaele; i++) Assert.Equal(204, b.Handle(StandMsg(i.ToString("x32"), Stand()), T0).Status);
        Assert.Equal(429, b.Handle(StandMsg(K, Stand()), T0).Status);

        var c = new RubusBridge();
        c.Handle(StandMsg(K, Stand(a: Enumerable.Range(0, 40).Select(i => A($"a{i}")).ToArray())), T0);
        int queued = Enumerable.Range(0, 40).Count(i => c.Enqueue(K, new RubusAktion("erlauben", Anfrage: $"a{i}")));
        Assert.Equal(RubusBridge.MaxAktionen, queued);
    }

    // ---------- transitions → peeks ----------

    [Fact]
    public void A_new_request_asks_and_a_vanished_one_closes()
    {
        var a = A("aaaa");
        Assert.Equal(new RubusEreignis[] { new RubusNeueAnfrage(K, a) }, RubusProtocol.Between(K, Stand(), Stand(a: new[] { a })));
        Assert.Empty(RubusProtocol.Between(K, Stand(a: new[] { a }), Stand(a: new[] { a }))); // the same request asks once
        Assert.Equal(new RubusEreignis[] { new RubusAnfrageWeg(K, "aaaa") }, RubusProtocol.Between(K, Stand(a: new[] { a }), Stand()));
    }

    [Theory]
    [InlineData("erlaubnis")]
    [InlineData("eingabe")]
    public void Waiting_for_permission_or_input_needs_you(string grund)
    {
        var waiting = S("s", RubusZustand.Wartet, grund, "Darf ich?");
        var e = Assert.Single(RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Arbeitet) }), Stand(new[] { waiting })));
        Assert.Equal(new RubusBrauchtDich(K, waiting), e);
        // Already waiting for the same reason: no second peek.
        Assert.Empty(RubusProtocol.Between(K, Stand(new[] { waiting }), Stand(new[] { waiting })));
        // A session that is already waiting when it first shows up counts too.
        Assert.Single(RubusProtocol.Between(K, Stand(), Stand(new[] { waiting })));
    }

    [Fact]
    public void The_next_question_of_the_same_kind_needs_you_again()
    {
        var first = S("s", RubusZustand.Wartet, "eingabe", "Soll ich deployen?") with { Seit = T0 };
        var next = first with { Text = "Passt das so?", Seit = T0.AddMinutes(2) };
        Assert.Equal(new RubusEreignis[] { new RubusBrauchtDich(K, next) },
            RubusProtocol.Between(K, Stand(new[] { first }), Stand(new[] { next })));
        Assert.Empty(RubusProtocol.Between(K, Stand(new[] { next }), Stand(new[] { next })));
    }

    [Fact]
    public void A_question_in_the_text_from_rubus_0_2_1_needs_you()
    {
        // Claude ended its answer with a question; Rubus reports it as waiting for input, source "claude" or "hook".
        Loc.Init("de");
        var b = new RubusBridge();
        b.Handle(StandMsg(K, Stand(new[] { S("rubus-36", RubusZustand.Arbeitet) })), T0);
        var e = b.Handle(Msg("stand_frage_im_text.json"), T0.AddSeconds(2)).Ereignisse.Cast<RubusBrauchtDich>().ToList();
        Assert.Equal(new[] { "rubus-36", "rubus-37" }, e.Select(x => x.Sitzung.Id));
        Assert.Equal(("eingabe", "Soll ich die Auffälligkeit in control_data nachverfolgen?"), (e[0].Sitzung.Grund, e[0].Sitzung.Text));
        Assert.Equal("Wartet auf dich", ClaudeService.StatusText(e[1].Sitzung));
    }

    [Fact]
    public void Waiting_for_an_approval_has_no_own_peek()
    {
        Assert.Empty(RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Arbeitet) }), Stand(new[] { S("s", RubusZustand.Wartet, "freigabe") })));
        // From approval to permission is a new reason.
        Assert.Single(RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Wartet, "freigabe") }), Stand(new[] { S("s", RubusZustand.Wartet, "erlaubnis") })));
    }

    [Fact]
    public void Working_to_ready_is_done()
    {
        var ready = S("s", RubusZustand.Bereit);
        Assert.Equal(new RubusEreignis[] { new RubusFertig(K, ready) },
            RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Arbeitet) }), Stand(new[] { ready })));
        Assert.Empty(RubusProtocol.Between(K, Stand(), Stand(new[] { ready }))); // never seen working
        Assert.Empty(RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Wartet, "eingabe") }), Stand(new[] { ready })));
        Assert.Empty(RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Arbeitet) }), Stand())); // gone is not done
    }

    [Fact]
    public void Interrupted_is_not_done()
    {
        Assert.Empty(RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Arbeitet) }), Stand(new[] { S("s", RubusZustand.Bereit, "unterbrochen") })));
    }

    [Fact]
    public void Stopping_with_an_error_has_its_own_peek()
    {
        var failed = S("s", RubusZustand.Bereit, "fehler", "API Error: 529 overloaded");
        Assert.Equal(new RubusEreignis[] { new RubusFehler(K, failed) },
            RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Arbeitet) }), Stand(new[] { failed }))); // and no "done"
        Assert.Empty(RubusProtocol.Between(K, Stand(new[] { failed }), Stand(new[] { failed }))); // once
        Assert.Empty(RubusProtocol.Between(K, Stand(), Stand(new[] { failed }))); // an old error of a new channel
        Assert.Single(RubusProtocol.Between(K, Stand(new[] { S("s", RubusZustand.Bereit) }), Stand(new[] { failed })));
    }

    [Fact]
    public void Bridge_reports_the_transitions_of_each_stand()
    {
        var b = new RubusBridge();
        var first = b.Handle(Msg("stand_freigabe.json"), T0).Ereignisse;
        Assert.IsType<RubusNeueAnfrage>(Assert.Single(first));
        Assert.Empty(b.Handle(Msg("stand_freigabe.json"), T0.AddSeconds(30)).Ereignisse);
        Assert.IsType<RubusAnfrageWeg>(Assert.Single(b.Handle(Msg("stand_leer.json"), T0.AddSeconds(31)).Ereignisse));
    }

    // ---------- question peek ----------

    [Fact]
    public void Question_peek_allows_or_denies()
    {
        Loc.Init("de");
        var answers = new List<string>();
        var p = App.RubusQuestion(A("aaaa") with { Titel = "Claude möchte auf test deployen", Projekt = "automation", Host = "frank-raspi" },
            null, System.Windows.Media.Brushes.White, System.Windows.Media.Brushes.Black, answers.Add);
        Assert.Equal(("Claude möchte auf test deployen", "automation · frank-raspi"), (p.Title, p.Subtitle));
        Assert.Equal(("Erlauben", "Ablehnen"), (p.ActionText, p.DismissText));
        Assert.True(p.ShowInDnd);
        Assert.Equal(Island.PeekPriority.High, p.Priority);
        Assert.Equal(Island.PeekKind.Local, p.Kind);
        Assert.True(p.Seconds >= 3600); // stays until the request leaves the state
        p.Action!();
        p.OnDismiss!();
        Assert.Equal(new[] { "erlauben", "ablehnen" }, answers);
    }

    [Fact]
    public void Question_peek_for_the_window_only_opens_rubus()
    {
        Loc.Init("en");
        try
        {
            var answers = new List<string>();
            var p = App.RubusQuestion(A("aaaa", fenster: true), null, System.Windows.Media.Brushes.White, System.Windows.Media.Brushes.Black, answers.Add);
            Assert.Equal("Open in Rubus", p.ActionText);
            Assert.Null(p.DismissText);
            Assert.False(p.HasDismiss);
            Assert.True(p.HasActions);
            Assert.Null(p.OnDismiss);
            p.Action!();
            Assert.Equal(new[] { "oeffnen" }, answers);
        }
        finally { Loc.Init("de"); }
    }

    [Fact]
    public void Waiting_reason_texts()
    {
        Loc.Init("de");
        Assert.Equal("Freigabe", ClaudeService.WaitText("freigabe"));
        Assert.Equal("Erlaubnis", ClaudeService.WaitText("erlaubnis"));
        Assert.Equal("Wartet auf dich", ClaudeService.WaitText("eingabe"));
        Assert.Equal("Fehler", ClaudeService.WaitText("fehler"));
        Assert.Equal("Unterbrochen", ClaudeService.WaitText("unterbrochen"));
    }

    [Fact]
    public void Second_line_shows_the_reason_while_waiting_or_after_an_error_or_interrupt()
    {
        Loc.Init("de");
        Assert.Equal("Erlaubnis", ClaudeService.StatusText(S("s", RubusZustand.Wartet, "erlaubnis")));
        Assert.Equal("Fehler", ClaudeService.StatusText(S("s", RubusZustand.Bereit, "fehler")));
        Assert.Equal("Unterbrochen", ClaudeService.StatusText(S("s", RubusZustand.Bereit, "unterbrochen")));
        Assert.Equal("", ClaudeService.StatusText(S("s", RubusZustand.Bereit))); // the age instead
        Assert.Equal("", ClaudeService.StatusText(S("s", RubusZustand.Arbeitet, "fehler")));
    }

    // ---------- listener rules ----------

    [Theory]
    [InlineData("/rubus/", ListenerGate.Route.Rubus)]
    [InlineData("/claude/", ListenerGate.Route.Claude)]
    [InlineData("/claude/x", ListenerGate.Route.Claude)]
    [InlineData("/", ListenerGate.Route.None)]
    [InlineData("/rubusx", ListenerGate.Route.None)]
    public void Routes(string path, ListenerGate.Route route) => Assert.Equal(route, ListenerGate.RouteOf(path));

    [Fact]
    public void Listener_rules()
    {
        var R = ListenerGate.Route.Rubus;
        var C = ListenerGate.Route.Claude;
        Assert.Null(ListenerGate.Refuse(R, "POST", false, false, 1000, hooksOn: false, rubusOn: true));
        // Switch off: 404, whatever else is wrong; the hooks path is not affected.
        Assert.Equal(404, ListenerGate.Refuse(R, "POST", false, false, 1000, hooksOn: true, rubusOn: false));
        Assert.Equal(404, ListenerGate.Refuse(R, "GET", true, false, 1000, hooksOn: true, rubusOn: false));
        Assert.Null(ListenerGate.Refuse(C, "POST", false, false, 1000, hooksOn: true, rubusOn: false));
        Assert.Equal(404, ListenerGate.Refuse(C, "POST", false, false, 1000, hooksOn: false, rubusOn: true));
        // Browsers: Origin or Sec-Fetch-Site.
        Assert.Equal(403, ListenerGate.Refuse(R, "POST", true, false, 10, false, true));
        Assert.Equal(403, ListenerGate.Refuse(R, "POST", false, true, 10, false, true));
        Assert.Equal(405, ListenerGate.Refuse(R, "GET", false, false, 0, false, true));
        Assert.Equal(405, ListenerGate.Refuse(R, "PUT", false, false, 0, false, true));
        // 64 KB for Rubus, 1 MB for hooks; unknown length (-1, chunked) is checked while reading.
        Assert.Null(ListenerGate.Refuse(R, "POST", false, false, 64 * 1024, false, true));
        Assert.Equal(413, ListenerGate.Refuse(R, "POST", false, false, 64 * 1024 + 1, false, true));
        Assert.Null(ListenerGate.Refuse(R, "POST", false, false, -1, false, true));
        Assert.Null(ListenerGate.Refuse(C, "POST", false, false, 64 * 1024 + 1, true, false));
        Assert.Equal(413, ListenerGate.Refuse(C, "POST", false, false, 1024 * 1024 + 1, true, false));
        Assert.Equal(64 * 1024, ListenerGate.MaxBody(R));
    }
}
