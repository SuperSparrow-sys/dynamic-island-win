using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Services;
using DynamicBay.Services.Server;

namespace DynamicBay.Tests;

/// <summary>
/// The DynamicBay server: the messages in Fixtures/Api/Server (the contract with dynamicbay-server), the shared
/// settings, and the client against a local WebSocket server on localhost. No test talks to the real server.
/// </summary>
[Collection("Loc")]
public class ServerTests
{
    private static string Fixture(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "Server", file));

    private static JsonObject Obj(string file) => (JsonObject)JsonNode.Parse(Fixture(file))!;

    /// <summary>Same JSON, leaving out what only the server adds when it passes a message on (<c>von</c>).</summary>
    private static void SameJson(string file, JsonObject actual, params string[] ignore)
    {
        var expected = Obj(file);
        foreach (var k in ignore) expected.Remove(k);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{file}: {actual.ToJsonString()}");
    }

    /// <summary>12:00 on 24.10.2026, the day of the examples.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2));

    // ---------- messages ----------

    [Fact]
    public void Requests_of_the_device_are_the_contract()
    {
        SameJson("koppeln_anfrage.json", ServerProtocol.PairRequest("483920", "7f3c9a2e5b1d4f60a8e2c4b6d8f0a1c3", "Desktop-PC", "pc", "1.1.14"));
        var open = Obj("ws_hallo.json")["offen"]![0]!;
        SameJson("ws_hallo.json", ServerProtocol.Hello("7f3c9a2e5b1d4f60a8e2c4b6d8f0a1c3", "Desktop-PC", "pc", "1.1.14",
            new[] { ("stumme_apps", new SharedValue(open["wert"], (long)open["geaendert"]!, "")) }));
        var ownApps = Obj("ws_einstellung_nur_hier.json");
        SameJson("ws_einstellung_nur_hier.json", ServerProtocol.Setting("stumme_apps", ownApps["wert"], (long)ownApps["geaendert"]!, onlyHere: true), "von");
        SameJson("ws_einstellung_entfernen.json", ServerProtocol.Setting("stumme_apps", null, (long)Obj("ws_einstellung_entfernen.json")["geaendert"]!, onlyHere: true, remove: true), "von");
        SameJson("ws_nicht_stoeren.json", ServerProtocol.Quiet(true, (long)Obj("ws_nicht_stoeren.json")["geaendert"]!));
        SameJson("ws_fokus_beenden.json", ServerProtocol.EndFocus("Arbeit"));
        // The widget command without grund and quelle: the server sets "Insel" and the device's name itself.
        SameJson("ws_panel.json", ServerProtocol.Panel(PanelProtocol.Command("helligkeit", 40, "naechster", "Insel", "Laptop")));
    }

    [Fact]
    public void Answers_of_the_server_read()
    {
        Assert.Equal("q8Zr3VbN1xY7mT0kLs2Hf9Wd4Pc6Ja5E", ServerProtocol.PairKey(Fixture("koppeln_antwort.json")));
        Assert.Null(ServerProtocol.PairKey(Fixture("fehler_code.json")));

        var w = ServerProtocol.Welcome(Obj("ws_willkommen.json"))!;
        Assert.Equal(new[] { "anzeigetafel.fokus", "stumme_apps" }, w.Shared.Keys.OrderBy(k => k));
        Assert.Equal("Discord", (string)w.Overrides["stumme_apps"].Value![0]!);
        Assert.Equal("Arbeit", w.State.Focus!.Name);
        Assert.Equal(15, w.State.Panel!.Percent);
        Assert.Equal(2, w.Devices.Count);
        Assert.False(w.Devices.Single(d => d.Kind == "laptop").Online);
        Assert.Equal("abcd-efgh-ijkm-npqr-stuv-wxyz", w.IphoneKey);
        Assert.EndsWith("/api/v1/iphone/fokus", w.IphoneAddress);

        var z = ServerProtocol.State(Obj("ws_zustand.json"));
        Assert.False(z.QuietOn);
        Assert.True(z.QuietChanged > 0);
        Assert.Equal("Fokus Arbeit", z.Panel!.Override!.Reason);

        Assert.Equal(("483920", 1792836300L), ServerProtocol.CodeOf(Obj("ws_code.json")));
        Assert.Equal("wert größer als 64 KB", ServerProtocol.ErrorOf(Obj("ws_fehler.json")));
        Assert.Equal("Anzeigetafel nicht erreichbar", ServerProtocol.ErrorOf(Obj("ws_panel_fehler.json")));
        var s = ServerProtocol.SettingOf(Obj("ws_einstellung_entfernen.json"))!.Value;
        Assert.True(s.OnlyHere && s.Remove);
    }

    [Fact]
    public void A_state_without_parts_means_none()
    {
        var s = ServerProtocol.State(new JsonObject());
        Assert.Null(s.Focus);
        Assert.Null(s.Panel);
        Assert.False(s.QuietOn);
        Assert.Equal(0, s.QuietChanged);
        Assert.Null(ServerProtocol.Welcome(new JsonObject { ["art"] = "zustand" }));
    }

    [Theory]
    [InlineData("homeserver.x.ts.net:20009", "https://homeserver.x.ts.net:20009/", "wss://homeserver.x.ts.net:20009/api/v1/verbindung")]
    [InlineData("https://homeserver.x.ts.net:20009/irgendwas", "https://homeserver.x.ts.net:20009/", "wss://homeserver.x.ts.net:20009/api/v1/verbindung")]
    [InlineData("http://localhost:5000", "http://localhost:5000/", "ws://localhost:5000/api/v1/verbindung")]
    public void The_address_may_be_typed_short(string typed, string b, string socket)
    {
        var u = ServerProtocol.Base(typed)!;
        Assert.Equal(b, u.ToString());
        Assert.Equal(socket, ServerProtocol.SocketUri(u).ToString());
        Assert.Equal(b + "api/v1/koppeln", ServerProtocol.PairUri(u).ToString());
    }

    [Theory]
    [InlineData("483920", "483920")]
    [InlineData("483 920", "483920")]
    [InlineData("48-39-20", "483920")]
    [InlineData("48392", null)]
    [InlineData("abc", null)]
    public void The_code_is_six_digits(string typed, string? code) => Assert.Equal(code, ServerProtocol.CleanCode(typed));

    // ---------- shared settings ----------

    [Fact]
    public void The_rule_table_goes_to_the_server_as_automatik_and_never_comes_back()
    {
        var s = new AppSettings();
        foreach (var f in FocusProfile.Defaults()) s.FocusProfiles.Add(f);
        Assert.True(JsonNode.DeepEquals(Obj("ws_einstellung_automatik_warten.json")["wert"], SharedSettings.Read(s, SharedSettings.Automatik)));
        Assert.Contains(SharedSettings.Automatik, SharedSettings.Keys);
        Assert.False(SharedSettings.Write(s, SharedSettings.Automatik, new JsonObject()));
    }

    [Fact]
    public void A_command_of_this_pc_goes_out_as_regel()
    {
        var meeting = FocusProfile.Defaults().Single(p => p.Trigger == Triggers.Camera);
        meeting.Name = "Besprechung"; // as in the contract example
        SameJson("ws_regel.json", ServerProtocol.Rule(PanelRules.While(meeting, "LAPTOPJONATHAN")!));
    }

    [Fact]
    public void Where_the_iphone_is_comes_from_the_reports_without_a_time_limit()
    {
        var s = new AppSettings();
        var server = Offline(s);
        server.Handle(Obj("ws_zustand_berichte.json"));
        Assert.True(s.PresenceHome);
        var z = Obj("ws_zustand_berichte.json");
        z["berichte"]!["Zuhause"]!["an"] = false;
        server.Handle(z);
        Assert.False(s.PresenceHome);
        // A state without the report leaves it as it is.
        server.Handle(Obj("ws_zustand.json"));
        Assert.False(s.PresenceHome);
        Assert.True(ServerProtocol.State(Obj("ws_zustand.json")).Reports["Arbeit"]);
    }

    [Fact]
    public void Wrong_shapes_from_the_server_change_nothing()
    {
        var s = new AppSettings();
        s.MutedApps.Add("WhatsApp");
        Assert.False(SharedSettings.Write(s, SharedSettings.MutedApps, JsonValue.Create("kein array")));
        Assert.False(SharedSettings.Write(s, "unbekannt", new JsonArray()));
        Assert.False(SharedSettings.Write(s, SharedSettings.Automatik, new JsonObject()));
        Assert.Equal(new[] { "WhatsApp" }, s.MutedApps);
        // Profiles: empty and doubled names are dropped.
        Assert.True(SharedSettings.Write(s, SharedSettings.FocusProfiles, JsonNode.Parse("""[{"Name":"Arbeit"},{"Name":"arbeit"},{"Name":""}]""")));
        Assert.Equal("Arbeit", Assert.Single(s.FocusProfiles).Name);
    }

    // ---------- the service, without network ----------

    private static ServerService Offline(AppSettings s, DateTimeOffset? now = null)
    {
        string key = "k";
        return new ServerService(s, Dispatcher.CurrentDispatcher, () => key, v => key = v ?? "", now: () => now ?? Noon);
    }

    [Fact]
    public void Welcome_brings_the_settings_the_focus_and_this_devices_own_list()
    {
        var s = new AppSettings { ServerAddress = "https://srv.test:20009", ServerDeviceId = "7f3c9a2e5b1d4f60a8e2c4b6d8f0a1c3" };
        foreach (var f in FocusProfile.Defaults()) s.FocusProfiles.Add(f);
        var server = Offline(s);
        server.Handle(Obj("ws_willkommen.json"));

        Assert.True(server.IsConnected);
        // The shared list is WhatsApp and Discord; this PC keeps only Discord for itself.
        Assert.Equal(new[] { "Discord" }, s.MutedApps);
        Assert.Contains(SharedSettings.MutedApps, s.OverriddenKeys);
        Assert.Equal("Arbeit", s.PhoneFocusServer);
        Assert.Equal("Arbeit", s.ActiveFocus?.Name);
        Assert.Equal("Busy", s.FocusTeamsStatus); // main device (no paired one)
        Assert.Equal("Desktop-PC", server.Devices[0].Name); // this device first
        Assert.Equal("abcd-efgh-ijkm-npqr-stuv-wxyz", server.IphoneKey);

        // The focus ends on the server; a reported one here still wins over it.
        server.Handle(new JsonObject { ["art"] = "zustand" });
        Assert.Equal("", s.PhoneFocusServer);
        Assert.Null(s.ActiveFocus);
    }

    [Fact]
    public void After_an_update_the_automations_and_the_rule_table_go_to_the_server()
    {
        // Settings of 1.1.15: the update turns them into automations and counts that as a change.
        var s = new AppSettings { SettingsVersion = 12 };
        s.FocusProfiles.Add(new FocusProfile { Name = "Zuhause", PanelEnd = "aus" });
        s.Migrate(cursorHopInstalled: false);
        var server = Offline(s);
        var old = Obj("ws_willkommen.json"); // the server still has the old list and no table
        old["zustand"]!["fokus"] = null; // no unknown focus: only the update counts here
        server.Handle(old);
        Assert.Contains(SharedSettings.FocusProfiles, server.Dirty);
        Assert.Contains(SharedSettings.Automatik, server.Dirty);

        // Nothing newer here and the same table: nothing goes out.
        var same = new AppSettings();
        var quiet = Offline(same);
        var welcome = Obj("ws_willkommen.json");
        welcome["einstellungen"]!["automatik"] = new JsonObject { ["wert"] = SharedSettings.Read(same, SharedSettings.Automatik), ["geaendert"] = 1, ["von"] = "x" };
        welcome["zustand"]!["fokus"] = null; // a focus unknown here would add a profile, and that goes out too
        quiet.Handle(welcome);
        Assert.Empty(quiet.Dirty);
    }

    [Fact]
    public void The_servers_old_focus_zuhause_is_where_the_iphone_is_not_a_new_profile()
    {
        var s = new AppSettings();
        foreach (var f in FocusProfile.Defaults()) s.FocusProfiles.Add(f);
        var server = Offline(s);
        var z = Obj("ws_zustand_berichte.json");
        z["fokus"] = new JsonObject { ["name"] = "Zuhause", ["seit"] = 1792866602, ["bis"] = 1792924202 };
        server.Handle(z);
        Assert.Equal("", s.PhoneFocusServer);
        Assert.Null(s.ProfileFor("Zuhause"));
        Assert.True(s.PresenceHome);

        // The empty profile 1.1.17 made of it goes on the next start; one the user filled in stays.
        var back = AppSettings.FromJson(s.ToJson());
        back.FocusProfiles.Add(new FocusProfile { Name = "Zuhause" });
        back.Migrate(cursorHopInstalled: false);
        Assert.Null(back.ProfileFor("Zuhause"));
    }

    [Fact]
    public void Teams_follows_a_focus_from_the_server_on_one_device_only()
    {
        var s = new AppSettings();
        foreach (var f in FocusProfile.Defaults()) s.FocusProfiles.Add(f);
        var server = Offline(s);
        server.TeamsHere = () => false; // the laptop, not the main device
        server.Handle(Obj("ws_zustand.json"));
        Assert.Equal("Arbeit", s.ActiveFocus?.Name);
        Assert.Equal("", s.FocusTeamsStatus);
    }

    [Fact]
    public void An_older_value_from_the_server_does_not_overwrite_a_newer_one_here()
    {
        var s = new AppSettings();
        s.MutedApps.Add("Teams");
        s.SharedChanged[SharedSettings.MutedApps] = Noon.AddHours(1).ToUnixTimeMilliseconds();
        var server = Offline(s);
        server.Handle(new JsonObject
        {
            ["art"] = "einstellung", ["schluessel"] = "stumme_apps", ["wert"] = new JsonArray("WhatsApp"),
            ["geaendert"] = Noon.ToUnixTimeMilliseconds(), ["nur_hier"] = false,
        });
        Assert.Equal(new[] { "Teams" }, s.MutedApps);
        server.Handle(new JsonObject
        {
            ["art"] = "einstellung", ["schluessel"] = "stumme_apps", ["wert"] = new JsonArray("WhatsApp"),
            ["geaendert"] = Noon.AddHours(2).ToUnixTimeMilliseconds(), ["nur_hier"] = false,
        });
        Assert.Equal(new[] { "WhatsApp" }, s.MutedApps);
    }

    [Fact]
    public void A_focus_past_its_end_and_an_older_do_not_disturb_are_ignored()
    {
        var s = new AppSettings { DoNotDisturb = true, LinkQuietChanged = Noon.AddMinutes(5).UtcDateTime };
        var server = Offline(s, now: Noon.AddDays(2));
        server.Handle(Obj("ws_zustand.json")); // focus until 25.10. 01:12, quiet off at 08:00
        Assert.Equal("", s.PhoneFocusServer);
        Assert.True(s.DoNotDisturb);

        var later = Offline(s);
        var z = Obj("ws_zustand.json");
        z["nicht_stoeren"] = new JsonObject { ["an"] = false, ["geaendert"] = Noon.AddMinutes(10).ToUnixTimeMilliseconds(), ["von"] = "x" };
        later.Handle(z);
        Assert.False(s.DoNotDisturb);
    }

    [Fact]
    public void The_panel_goes_through_the_server_while_connected()
    {
        var s = new AppSettings { AnzeigetafelEnabled = true };
        var tafel = new AnzeigetafelService(s, Dispatcher.CurrentDispatcher, readKey: () => "", writeKey: _ => { });
        Assert.False(tafel.IsConfigured);
        var sent = new List<JsonObject>();
        tafel.UseServer(c => { sent.Add(c); return Task.FromResult(true); });
        Assert.True(tafel.IsConfigured);
        tafel.ApplyServer(ServerProtocol.State(Obj("ws_zustand.json")).Panel);
        Assert.Equal("Fokus Arbeit · bis So 01:12", tafel.StatusText);
        tafel.BackToPlanCommand.Execute(null);
        Assert.Equal("plan", (string)Assert.Single(sent)["aktion"]!);
        // The focus command is the server's job: nothing from here.
        s.PhoneFocusHere = "Schlafen";
        Assert.Single(sent);
        tafel.UseServer(null);
        Assert.False(tafel.IsConfigured);
        Assert.Null(tafel.State);
    }

    // ---------- the client against a local server ----------

    /// <summary>A WebSocket server on localhost (HttpListener): answers the pairing, records what arrives.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public readonly int Port;
        public readonly List<JsonObject> Received = new();
        public string? Authorization;
        public WebSocket? Socket;

        public FakeServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                if (ctx.Request.Url!.AbsolutePath == "/api/v1/koppeln")
                {
                    using var r = new StreamReader(ctx.Request.InputStream);
                    var pair = (JsonObject)JsonNode.Parse(await r.ReadToEndAsync())!;
                    lock (Received) Received.Add(pair);
                    var bytes = Encoding.UTF8.GetBytes(Fixture("koppeln_antwort.json"));
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                else if (ctx.Request.IsWebSocketRequest)
                {
                    Authorization = ctx.Request.Headers["Authorization"];
                    var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                    Socket = ws;
                    _ = Task.Run(() => ReadAsync(ws));
                }
                else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            }
        }

        private async Task ReadAsync(WebSocket ws)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    var o = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count))!;
                    lock (Received) Received.Add(o);
                }
            }
            catch (WebSocketException) { }
        }

        public Task SendAsync(string json) => Socket!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        public Task CloseAsync(int code) => Socket!.CloseOutputAsync((WebSocketCloseStatus)code, "", CancellationToken.None);

        public bool Got(string art) { lock (Received) return Received.Any(o => ServerProtocol.Kind(o) == art); }
        public JsonObject Last(string art) { lock (Received) return Received.Last(o => ServerProtocol.Kind(o) == art); }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    /// <summary>The service lives on its own dispatcher thread, as on the UI thread in the app.</summary>
    private static (Dispatcher Ui, Thread Thread) UiThread()
    {
        Dispatcher? ui = null;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() => { ui = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        return (ui!, t);
    }

    private static async Task Until(Func<bool> done, string what)
    {
        for (int i = 0; i < 750 && !done(); i++) await Task.Delay(20); // up to 15 s: the full run is busy
        Assert.True(done(), what);
    }

    [Fact]
    public async Task Pair_connect_take_the_welcome_send_a_change_and_stop_when_the_key_is_gone()
    {
        using var fake = new FakeServer();
        var (ui, _) = UiThread();
        string key = "";
        var s = new AppSettings { LinkDeviceName = "Desktop-PC" };
        var server = ui.Invoke(() => new ServerService(s, ui, () => key, v => key = v ?? "", now: () => Noon));
        try
        {
            Assert.True(await ui.Invoke(() => server.PairAsync($"http://localhost:{fake.Port}", "483 920")));
            Assert.Equal("q8Zr3VbN1xY7mT0kLs2Hf9Wd4Pc6Ja5E", key);
            JsonObject pairing;
            lock (fake.Received) pairing = fake.Received.Single(o => o.ContainsKey("code"));
            Assert.Equal("483920", (string)pairing["code"]!);
            Assert.Contains((string)pairing["art"]!, new[] { "pc", "laptop" });

            await Until(() => fake.Got("hallo"), "hallo");
            Assert.Equal("Bearer q8Zr3VbN1xY7mT0kLs2Hf9Wd4Pc6Ja5E", fake.Authorization);
            Assert.Equal("Desktop-PC", (string)fake.Last("hallo")["name"]!);

            await fake.SendAsync(Fixture("ws_willkommen.json"));
            await Until(() => server.IsConnected, "connected");
            Assert.Equal("Arbeit", s.PhoneFocusServer);

            // A change here goes to the server; stumme_apps is this device's own list after the welcome.
            ui.Invoke(() => s.MutedApps.Add("Signal"));
            await ui.Invoke(server.FlushAsync);
            // The welcome's focus "Arbeit" is new here: its empty profile goes out too (fokus.profile, anzeigetafel.fokus),
            // in any order. Look for the muted apps, not for the last message.
            JsonObject? Apps() { lock (fake.Received) return fake.Received.LastOrDefault(o => ServerProtocol.Kind(o) == "einstellung" && (string?)o["schluessel"] == "stumme_apps"); }
            await Until(() => Apps() is not null, "einstellung stumme_apps");
            var sent = Apps()!;
            Assert.True((bool)sent["nur_hier"]!);

            // Removed on the server: the key goes, no new attempts.
            await fake.CloseAsync(ServerProtocol.CloseUnpaired);
            await Until(() => !server.IsPaired, "unpaired");
            Assert.Equal("", key);
            Assert.Equal("", s.PhoneFocusServer);
        }
        finally
        {
            ui.Invoke(server.Dispose);
            ui.InvokeShutdown();
        }
    }
}
