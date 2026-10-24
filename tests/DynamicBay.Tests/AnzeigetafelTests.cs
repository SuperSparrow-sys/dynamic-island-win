using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>
/// The Anzeigetafel's device API: the answers and requests in Fixtures/Api/Anzeigetafel (the contract with
/// dashboard-zentrale), the commands of a focus change, and the service against a fake HTTP handler. No test talks to
/// the real Anzeigetafel or the panel.
/// </summary>
[Collection("Loc")]
public class AnzeigetafelTests
{
    private static string Fixture(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "Anzeigetafel", file));

    private static void SameJson(string file, JsonObject actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Fixture(file)), actual), $"{file}: {actual.ToJsonString()}");

    // ---------- answers ----------

    [Theory]
    [InlineData("panel_plan.json")]
    [InlineData("panel_fokus_schlafen.json")]
    [InlineData("panel_widget_hell40.json")]
    [InlineData("panel_nicht_erreichbar.json")]
    public void Every_answer_in_the_contract_reads(string file) => Assert.NotNull(PanelProtocol.Parse(Fixture(file)));

    [Fact]
    public void Answers_read_with_their_values_and_status_line()
    {
        var plan = PanelProtocol.Parse(Fixture("panel_plan.json"))!;
        Assert.True(plan.Reachable && plan.On == true && plan.Percent == 70 && plan.Steers);
        Assert.Null(plan.Override);
        Assert.Equal("Wochenplan · nächster Wechsel Sa 19:00", PanelProtocol.Describe(plan));

        var sleep = PanelProtocol.Parse(Fixture("panel_fokus_schlafen.json"))!;
        Assert.False(sleep.On);
        Assert.Equal(new PanelOverride("schwarz", 0, "Fokus Schlafen", "Desktop-PC", "So 13:30", "ende"), sleep.Override);
        Assert.Equal("Fokus Schlafen · bis So 13:30", PanelProtocol.Describe(sleep));

        var widget = PanelProtocol.Parse(Fixture("panel_widget_hell40.json"))!;
        Assert.Equal("Von Hand (Laptop) · bis Sa 19:00", PanelProtocol.Describe(widget));

        var gone = PanelProtocol.Parse(Fixture("panel_nicht_erreichbar.json"))!;
        Assert.False(gone.Reachable);
        Assert.Null(gone.On);
        Assert.Null(gone.Percent);
        Assert.False(gone.Steers);
        Assert.Equal("Panel antwortet nicht", PanelProtocol.Describe(gone));
    }

    [Theory]
    [InlineData("fehler_400.json")]
    [InlineData("fehler_401.json")]
    [InlineData("kein json")]
    public void Errors_and_other_text_are_no_state(string text) =>
        Assert.Null(PanelProtocol.Parse(text.EndsWith(".json") ? Fixture(text) : text));

    [Fact]
    public void Missing_fields_are_normal()
    {
        var s = PanelProtocol.Parse("""{"panel": {"erreichbar": true}}""")!;
        Assert.Null(s.On);
        Assert.True(s.Steers);
        Assert.Equal("", s.NextText);
        Assert.Equal("Wochenplan", PanelProtocol.Describe(s));
    }

    // ---------- requests ----------

    [Fact]
    public void Widget_requests_are_the_contract()
    {
        SameJson("anfrage_widget_helligkeit.json", PanelProtocol.Command("helligkeit", 40, "naechster", "Insel", "Laptop"));
        SameJson("anfrage_widget_an.json", PanelProtocol.Command("an", null, "naechster", "Insel", "Laptop"));
        SameJson("anfrage_widget_aus.json", PanelProtocol.Command("aus", null, "naechster", "Insel", "Laptop"));
        SameJson("anfrage_widget_plan.json", PanelProtocol.Command("plan", null, null, "Insel", "Laptop"));
    }

    [Fact]
    public void The_anzeigetafel_refuses_extra_fields_so_none_are_sent()
    {
        // prozent only with an/helligkeit, dauer never with plan, nur_wenn_grund only with plan (400 otherwise)
        Assert.Null(PanelProtocol.Command("aus", 40, "ende", "x", "y")["prozent"]);
        var plan = PanelProtocol.Command("plan", 40, "ende", "x", "y", "x");
        Assert.Null(plan["prozent"]);
        Assert.Null(plan["dauer"]);
        Assert.Null(PanelProtocol.Command("an", null, null, "x", "y", "x")["nur_wenn_grund"]);
        Assert.Equal(5, (int)PanelProtocol.Command("helligkeit", 1, null, "x", "y")["prozent"]!);
        Assert.Equal(60, ((string)PanelProtocol.Command("an", null, null, new string('a', 90), "y")["grund"]!).Length);
    }

    [Theory]
    [InlineData("https://homeserver.x.ts.net:20004", "https://homeserver.x.ts.net:20004/api/geraet/panel")]
    [InlineData("https://homeserver.x.ts.net:20004/", "https://homeserver.x.ts.net:20004/api/geraet/panel")]
    [InlineData("http://192.168.10.30:20004/api/geraet/panel", "http://192.168.10.30:20004/api/geraet/panel")]
    [InlineData("homeserver.x.ts.net:20004", "https://homeserver.x.ts.net:20004/api/geraet/panel")]
    [InlineData("", null)]
    [InlineData("ftp://x", null)]
    public void The_address_may_be_typed_short(string typed, string? endpoint) =>
        Assert.Equal(endpoint, PanelProtocol.Endpoint(typed)?.ToString());

    // ---------- service ----------

    private sealed class FakeTafel : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string? Auth, JsonObject? Body)> Calls = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Answer = Fixture("panel_plan.json");
        public bool Offline;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request.Method, request.Headers.Authorization?.ToString(), body is null ? null : JsonNode.Parse(body) as JsonObject));
            if (Offline) throw new HttpRequestException("offline");
            return new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? Answer : Fixture("fehler_401.json"), Encoding.UTF8, "application/json") };
        }
    }

    private static (AnzeigetafelService Service, FakeTafel Fake, AppSettings Settings) Service(string key = "geheim", bool enabled = true)
    {
        var s = new AppSettings { AnzeigetafelEnabled = enabled, AnzeigetafelUrl = "https://tafel.test:20004", LinkDeviceName = "Desktop-PC" };
        s.FocusProfiles.Add(new FocusProfile { Name = "Schlafen", Panel = "aus" });
        s.FocusProfiles.Add(new FocusProfile { Name = "Arbeit", Panel = "dimmen", PanelPercent = 15 });
        string stored = key;
        var fake = new FakeTafel();
        var service = new AnzeigetafelService(s, Dispatcher.CurrentDispatcher, fake, () => stored, v => stored = v ?? "");
        return (service, fake, s);
    }

    [Fact]
    public async Task Wrong_key_shows_a_short_sentence()
    {
        var (service, fake, _) = Service();
        fake.Status = HttpStatusCode.Unauthorized;
        await service.RefreshAsync();
        Assert.Equal("Schlüssel stimmt nicht", service.Error);
        Assert.Null(service.State);
    }

    [Fact]
    public async Task Widget_switches_and_dims_by_hand_until_the_next_plan_point()
    {
        var (service, fake, _) = Service();
        await service.RefreshAsync();
        Assert.True(service.IsOn);
        Assert.Equal(70, service.Level);

        await service.ToggleCommand.ExecuteAsync(null);
        var off = fake.Calls.Last().Body!;
        Assert.Equal(("aus", "naechster", "Insel", "Desktop-PC"), ((string)off["aktion"]!, (string)off["dauer"]!, (string)off["grund"]!, (string)off["quelle"]!));
        // The answer still says "on" (the panel follows a moment later): the switch keeps what was pressed.
        Assert.False(service.IsOn);

        service.Level = 40;
        await service.SendLevelAsync();
        Assert.Equal(40, (int)fake.Calls.Last().Body!["prozent"]!);

        await service.BackToPlanCommand.ExecuteAsync(null);
        Assert.Equal("plan", (string)fake.Calls.Last().Body!["aktion"]!);
        Assert.Null(fake.Calls.Last().Body!["nur_wenn_grund"]);
    }

    [Fact]
    public async Task Not_set_up_it_sends_nothing_even_with_address_and_key()
    {
        var (service, fake, s) = Service(enabled: false);
        Assert.False(service.IsConfigured);
        Assert.False(new AppSettings().AnzeigetafelEnabled); // off for everyone until set up
        await service.ReportAsync("Zuhause", false);
        await service.RefreshAsync();
        Assert.Empty(fake.Calls);
        s.AnzeigetafelEnabled = true;
        Assert.True(service.IsConfigured);
    }

    [Fact]
    public async Task Without_address_or_key_and_in_demo_nothing_is_sent()
    {
        var (service, fake, s) = Service(key: "");
        Assert.False(service.IsConfigured);
        s.InMeeting = true;
        await service.LocalChangedAsync();
        await service.RefreshAsync();
        Assert.Empty(fake.Calls);

        var (demo, demoFake, _) = Service();
        demo.LoadDemo();
        await demo.ToggleCommand.ExecuteAsync(null);
        await demo.RefreshAsync();
        Assert.Empty(demoFake.Calls);
        Assert.True(demo.HasOverride);
    }
}
