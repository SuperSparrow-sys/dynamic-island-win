using System.IO;
using System.Net;
using System.Security.Cryptography;
using DynamicBay.Core;
using DynamicBay.Services;
using DynamicBay.Services.Link;

namespace DynamicBay.Tests;

/// <summary>
/// PC and laptop working together: timer, quiet and focus, questions on the main device, battery, locking together,
/// pictures in the clipboard both ways, and Tailscale discovery limited to the owner's own computers. Two link
/// endpoints in one process, loopback only.
/// </summary>
public class LinkTogetherTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dynbay-together-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    public void Dispose()
    {
        for (int i = _cleanup.Count - 1; i >= 0; i--) try { _cleanup[i].Dispose(); } catch { }
        try { Directory.Delete(_temp, true); } catch { }
    }

    private T Keep<T>(T d) where T : IDisposable { _cleanup.Add(d); return d; }

    private LinkOptions Options(int port, string downloads) => new()
    {
        Port = port, LoopbackOnly = true, LanDiscovery = false, TailscaleDiscovery = false, WatchSystem = false,
        MinBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(500),
        DownloadFolder = downloads, Log = _ => { },
    };

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var end = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }

    private static Task<T> Next<T>(Action<Action<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(v => tcs.TrySetResult(v));
        return tcs.Task.WaitAsync(Wait);
    }

    private static async Task<bool> Nothing<T>(Action<Action<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(v => tcs.TrySetResult(v));
        return await Task.WhenAny(tcs.Task, Task.Delay(Quiet)) != tcs.Task;
    }

    private sealed record Pair(LinkService A, LinkService B, DeviceIdentity IdA, DeviceIdentity IdB, AppSettings SA, AppSettings SB);

    /// <summary>"PC" (A) and "Laptop" (B), paired; features are created before both start (or later, with <paramref name="startB"/> false).</summary>
    private async Task<Pair> Connected(Action<Pair> features, bool startB = true)
    {
        var idA = Keep(DeviceIdentity.Create("PC"));
        var idB = Keep(DeviceIdentity.Create("Laptop"));
        int portA = FreePort();
        var a = Keep(new LinkService(idA, "PC", new[] { new PairedDevice { Id = idB.DeviceId, Name = "Laptop", Fingerprint = idB.Fingerprint } }, Options(portA, Path.Combine(_temp, "dl-a"))));
        var b = Keep(new LinkService(idB, "Laptop", new[] { new PairedDevice { Id = idA.DeviceId, Name = "PC", Fingerprint = idA.Fingerprint, LastAddresses = { $"127.0.0.1:{portA}" } } }, Options(0, Path.Combine(_temp, "dl-b"))));
        var pair = new Pair(a, b, idA, idB, new AppSettings(), new AppSettings());
        features(pair);
        a.Start();
        if (!startB) return pair;
        b.Start();
        await Until(() => a.IsOnline(idB.DeviceId) && b.IsOnline(idA.DeviceId), "connect");
        return pair;
    }

    // ---------- Tailscale: only the owner's own computers ----------

    [Fact]
    public void Tailscale_lists_only_the_owners_windows_and_mac_computers()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "Tailscale", "status-shared.json"));
        var (state, self, peers) = LinkDiscovery.ParseTailscale(json);
        Assert.Equal("Running", state);
        Assert.Equal(1111111111111111, self);
        Assert.Equal(9, peers.Count);
        var own = LinkDiscovery.OwnDesktops(self, peers);
        // The colleague's laptop and NAS, the tagged server, a node without user, the own phone and NAS: never probed.
        Assert.Equal(new[] { "example-laptop", "example-macbook" }, own.Select(p => p.HostName).OrderBy(n => n));
        Assert.Equal(IPAddress.Parse("100.64.10.20"), own.Single(p => p.HostName == "example-laptop").Ips[0]);
        // Without our own user id nobody is asked (a typed address still works).
        Assert.Empty(LinkDiscovery.OwnDesktops(null, peers));
        // The older fixture has no Self.UserID: nothing there either, but parsing still works.
        var old = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "Tailscale", "status.json"));
        var (_, oldSelf, oldPeers) = LinkDiscovery.ParseTailscale(old);
        Assert.Null(oldSelf);
        Assert.Empty(LinkDiscovery.OwnDesktops(oldSelf, oldPeers));
    }

    [Theory]
    [InlineData("windows", true)]
    [InlineData("Windows", true)]
    [InlineData("macOS", true)]
    [InlineData("linux", false)]
    [InlineData("iOS", false)]
    [InlineData("android", false)]
    [InlineData("", false)]
    public void Only_desktop_systems_are_probed(string os, bool desktop) => Assert.Equal(desktop, LinkDiscovery.IsDesktopOs(os));

    // ---------- timer ----------

    private LinkTimerState? _timerA;
    private readonly LinkTimerState? _timerB = null; // device B never owns a timer in these tests

    private async Task<(LinkTimerFeature A, LinkTimerFeature B, Pair P)> Timers(Action<Pair>? settings = null, bool startB = true)
    {
        LinkTimerFeature? ta = null, tb = null;
        var p = await Connected(x =>
        {
            settings?.Invoke(x);
            ta = Keep(new LinkTimerFeature(x.A, x.SA, () => _timerA));
            tb = Keep(new LinkTimerFeature(x.B, x.SB, () => _timerB));
        }, startB);
        return (ta!, tb!, p);
    }

    [Fact]
    public async Task Timer_start_pause_and_stop_show_on_the_other_device()
    {
        var (a, b, p) = await Timers();
        await Until(() => a.Peer.IsOn(p.IdB.DeviceId) && b.Peer.IsOn(p.IdA.DeviceId), "both know the other's switch");

        var got = Next<LinkTimerState>(h => b.Received += (_, _, s) => h(s));
        Assert.Equal(1, await a.PublishAsync(new LinkTimerState(true, true, "focus", 1500, 1499.4, p.IdA.DeviceId)));
        var s = await got;
        Assert.Equal((true, true, "focus", 1499.4, p.IdA.DeviceId), (s.Active, s.Running, s.Mode, s.RemainingSeconds, s.Owner));

        // Paused on the laptop: the PC pauses too.
        var paused = Next<LinkTimerState>(h => a.Received += (_, _, st) => h(st));
        Assert.Equal(1, await b.PublishAsync(new LinkTimerState(true, false, "focus", 1500, 1200, p.IdA.DeviceId)));
        Assert.False((await paused).Running);

        // Stopped on the PC: gone on the laptop.
        var stopped = Next<LinkTimerState>(h => b.Received += (_, _, st) => h(st));
        Assert.Equal(1, await a.PublishAsync(new LinkTimerState(false, false, "timer", 0, 0, p.IdA.DeviceId)));
        Assert.False((await stopped).Active);
    }

    [Fact]
    public async Task Timer_switch_is_checked_on_both_sides()
    {
        var (a, b, p) = await Timers(x => x.SA.LinkShareTimer = false);
        Assert.Equal(0, await a.PublishAsync(new LinkTimerState(true, true, "timer", 300, 300, p.IdA.DeviceId)));
        await Until(() => !b.Peer.IsOn(p.IdA.DeviceId) && a.Peer.IsOn(p.IdB.DeviceId), "B knows A's switch is off");
        // B sends, A drops it.
        Assert.True(await Nothing<LinkTimerState>(h => a.Received += (_, _, s) => h(s)));
        Assert.Equal(1, await b.PublishAsync(new LinkTimerState(true, true, "timer", 300, 300, p.IdB.DeviceId)));
        // Switched on at A: B learns it.
        p.SA.LinkShareTimer = true;
        await a.Peer.AnnounceAsync();
        await Until(() => b.Peer.IsOn(p.IdA.DeviceId), "B learns A's switch is on");
    }

    [Fact]
    public async Task A_running_timer_reaches_a_device_that_connects_later()
    {
        var (a, b, p) = await Timers(startB: false);
        _timerA = new LinkTimerState(true, true, "timer", 600, 420, p.IdA.DeviceId);
        var got = Next<LinkTimerState>(h => b.Received += (_, _, s) => h(s));
        p.B.Start();
        var s = await got;
        Assert.True(s.Active);
        Assert.Equal(p.IdA.DeviceId, s.Owner);
    }

    [Theory]
    [InlineData(5L, "B", 4L, "A", true)]
    [InlineData(4L, "B", 5L, "A", false)]
    [InlineData(5L, "B", 5L, "A", true)]   // same count: the higher device id wins - on both devices alike
    [InlineData(5L, "A", 5L, "B", false)]
    [InlineData(5L, "A", 5L, "A", false)]  // the same change again
    public void Newest_timer_change_wins(long seq, string from, long current, string currentFrom, bool newer) =>
        Assert.Equal(newer, LinkTimerFeature.Newer(seq, from, current, currentFrom));

    [Theory]
    [InlineData(false, false, true, false, true)]  // not shared: as always
    [InlineData(true, false, true, true, true)]    // no main device: where it belongs
    [InlineData(true, false, true, false, false)]  // ... not on the copy
    [InlineData(true, true, true, false, true)]    // main device: there, also as a copy
    [InlineData(true, true, false, true, false)]   // the quiet device stays silent, also when it is its own
    public void Timer_end_shows_once(bool shared, bool mainChosen, bool isMain, bool ownedHere, bool shows) =>
        Assert.Equal(shows, LinkTimerFeature.ShowsEnd(shared, mainChosen, isMain, ownedHere));

    [Theory]
    [InlineData("{\"active\":true,\"running\":true,\"mode\":\"pizza\",\"total\":60,\"remaining\":30}", true, "timer", 30d)]
    [InlineData("{\"active\":true,\"running\":true,\"remaining\":0}", false, "timer", 0d)]
    [InlineData("{\"active\":true,\"running\":false,\"mode\":\"break\",\"remaining\":999999}", true, "break", 86400d)]
    [InlineData("{}", false, "timer", 0d)]
    public void Timer_parses_tolerantly(string json, bool active, string mode, double remaining)
    {
        var s = LinkTimerFeature.Parse(System.Text.Json.Nodes.JsonNode.Parse(json))!;
        Assert.Equal((active, mode, remaining), (s.Active, s.Mode, s.RemainingSeconds));
    }

    [Fact]
    public void Applied_timer_is_a_copy_and_no_change_here()
    {
        var timer = new TimerService(new AppSettings());
        int changes = 0;
        timer.Changed += () => changes++;
        timer.Apply(true, false, TimerMode.Focus, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(12), "LAPTOPID");
        Assert.True(timer.IsActive);
        Assert.False(timer.IsRunning);
        Assert.Equal(TimerMode.Focus, timer.Mode);
        Assert.Equal("12:00", timer.RemainingText);
        Assert.Equal("LAPTOPID", timer.RemoteOwner);
        Assert.Equal(0, changes);
        // Stopped by the user here: a change; started here: this device's own again.
        timer.Stop();
        Assert.Equal(1, changes);
        timer.StartMinutes(5d);
        Assert.Null(timer.RemoteOwner);
        Assert.Equal(2, changes);
        timer.Apply(false, false, TimerMode.Timer, TimeSpan.Zero, TimeSpan.Zero, null);
        Assert.False(timer.IsActive);
        Assert.Equal(2, changes);
    }

    // ---------- quiet and focus ----------

    [Fact]
    public async Task Do_not_disturb_and_focus_go_both_ways_without_echo()
    {
        LinkQuietFeature? qa = null, qb = null;
        var p = await Connected(x =>
        {
            qa = Keep(new LinkQuietFeature(x.A, x.SA));
            qb = Keep(new LinkQuietFeature(x.B, x.SB));
        });
        // B writes the PC's choice while "adopting": the app does not send that back as a new choice.
        bool? adoptingWhenSet = null;
        p.SB.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.DoNotDisturb)) adoptingWhenSet ??= qb!.Adopting; };
        p.SA.DoNotDisturb = true;
        Assert.Equal(1, await qa!.DoNotDisturbChangedAsync());
        await Until(() => p.SB.DoNotDisturb, "B adopts do not disturb");
        Assert.Equal(p.SA.LinkQuietChanged, p.SB.LinkQuietChanged);
        Assert.True(adoptingWhenSet);

        // Switched off on the laptop: off on the PC.
        p.SB.DoNotDisturb = false;
        await qb!.DoNotDisturbChangedAsync();
        await Until(() => !p.SA.DoNotDisturb, "A follows");

        // An older choice does not win back.
        p.SA.LinkQuietChanged = DateTime.UtcNow.AddDays(-1);
        p.SA.DoNotDisturb = true;
        await qa.AnnounceAsync();
        await Task.Delay(Quiet);
        Assert.False(p.SB.DoNotDisturb);

        // A focus session on the PC makes the laptop quiet while it lasts; it never comes back as the PC's own.
        p.SA.FocusActive = true;
        await qa.AnnounceAsync();
        await Until(() => p.SB.PeerQuiet && p.SB.Quiet, "B quiet");
        Assert.False(p.SA.PeerQuiet);
        p.SA.FocusActive = false;
        p.SA.InMeeting = true; // a call counts the same
        await qa.AnnounceAsync();
        await Task.Delay(Quiet);
        Assert.True(p.SB.PeerQuiet);
        p.SA.InMeeting = false;
        await qa.AnnounceAsync();
        await Until(() => !p.SB.PeerQuiet, "B free again");

        // The PC goes away: nothing of it keeps the laptop quiet.
        p.SA.FocusActive = true;
        await qa.AnnounceAsync();
        await Until(() => p.SB.PeerQuiet, "B quiet again");
        p.A.Stop();
        await Until(() => !p.SB.PeerQuiet, "B free after the PC left");
    }

    [Fact]
    public async Task Quiet_switch_is_checked_on_both_sides()
    {
        LinkQuietFeature? qa = null;
        var p = await Connected(x =>
        {
            x.SB.LinkShareQuiet = false;
            qa = Keep(new LinkQuietFeature(x.A, x.SA));
            Keep(new LinkQuietFeature(x.B, x.SB));
        });
        p.SA.DoNotDisturb = true;
        p.SA.FocusActive = true;
        Assert.Equal(1, await qa!.DoNotDisturbChangedAsync());
        await Task.Delay(Quiet);
        Assert.False(p.SB.DoNotDisturb);
        Assert.False(p.SB.PeerQuiet);
        p.SA.LinkShareQuiet = false;
        Assert.Equal(0, await qa.AnnounceAsync());
    }

    // ---------- questions ----------

    private async Task<(LinkQuestionFeature A, LinkQuestionFeature B, Pair P)> Questions(Action<Pair>? settings = null)
    {
        LinkQuestionFeature? qa = null, qb = null;
        var p = await Connected(x =>
        {
            settings?.Invoke(x);
            qa = Keep(new LinkQuestionFeature(x.A, x.SA));
            qb = Keep(new LinkQuestionFeature(x.B, x.SB));
        });
        return (qa!, qb!, p);
    }

    private static readonly LinkQuestion Mic = new("Mikrofon aktiv", "Teams", "Icon.Mic", "#FF9F0A", "Stumm", "OK", 8, false, false);

    [Fact]
    public async Task Question_answer_yes_no_and_timeout()
    {
        var (pc, laptop, p) = await Questions();
        await Until(() => laptop.CanAsk(p.IdA.DeviceId), "the laptop knows the PC takes questions");

        // Yes on the PC: runs on the laptop.
        var asked = Next<LinkQuestionRequest>(h => pc.Asked += h);
        var answer = new TaskCompletionSource<LinkAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await laptop.AskAsync(p.IdA.DeviceId, Mic, a => answer.TrySetResult(a)));
        var q = await asked;
        Assert.Equal(("Laptop", "Mikrofon aktiv", "Stumm", "Icon.Mic", "#FF9F0A"), (q.PeerName, q.Question.Title, q.Question.ActionText, q.Question.IconKey, q.Question.Color));
        Assert.True(await pc.AnswerAsync(q.Id, true));
        Assert.Equal(LinkAnswer.Yes, await answer.Task.WaitAsync(Wait));
        Assert.False(await pc.AnswerAsync(q.Id, false)); // answered once only

        // The second button.
        var asked2 = Next<LinkQuestionRequest>(h => pc.Asked += h);
        var answer2 = new TaskCompletionSource<LinkAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await laptop.AskAsync(p.IdA.DeviceId, Mic, a => answer2.TrySetResult(a)));
        Assert.True(await pc.AnswerAsync((await asked2).Id, false));
        Assert.Equal(LinkAnswer.No, await answer2.Task.WaitAsync(Wait));

        // Nobody answers on the PC: when its time is up it goes back as a timeout and the peek goes away there.
        var asked3 = Next<LinkQuestionRequest>(h => pc.Asked += h);
        var answer3 = new TaskCompletionSource<LinkAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await laptop.AskAsync(p.IdA.DeviceId, Mic, a => answer3.TrySetResult(a)));
        var q3 = await asked3;
        var expired = Next<string>(h => pc.Expired += h);
        pc.Expire(DateTime.UtcNow.AddSeconds(5));
        Assert.True(await Nothing<string>(h => pc.Expired += h)); // not yet (8 s)
        pc.Expire(DateTime.UtcNow.AddSeconds(9));
        Assert.Equal(q3.Id, await expired);
        Assert.Equal(LinkAnswer.Timeout, await answer3.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task Question_without_any_answer_times_out_on_its_own_device()
    {
        var (pc, laptop, p) = await Questions();
        await Until(() => laptop.CanAsk(p.IdA.DeviceId), "switch known");
        var answers = new List<LinkAnswer>();
        Assert.True(await laptop.AskAsync(p.IdA.DeviceId, Mic, a => { lock (answers) answers.Add(a); }));
        laptop.Expire(DateTime.UtcNow.AddSeconds(Mic.Seconds + 1));
        Assert.Empty(answers); // the main device may have shown it late: there is a grace period
        laptop.Expire(DateTime.UtcNow + TimeSpan.FromSeconds(Mic.Seconds) + LinkQuestionFeature.Grace + TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { LinkAnswer.Timeout }, answers);
        laptop.Expire(DateTime.UtcNow.AddHours(1));
        Assert.Single(answers); // exactly once
    }

    [Fact]
    public async Task Question_comes_back_when_the_main_device_goes_away()
    {
        var (pc, laptop, p) = await Questions();
        await Until(() => laptop.CanAsk(p.IdA.DeviceId), "switch known");
        var asked = Next<LinkQuestionRequest>(h => pc.Asked += h);
        var answer = new TaskCompletionSource<LinkAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await laptop.AskAsync(p.IdA.DeviceId, Mic, a => answer.TrySetResult(a)));
        await asked;
        p.A.Stop();
        Assert.Equal(LinkAnswer.Lost, await answer.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task Questions_switch_off_on_the_main_device_keeps_them_here()
    {
        var (pc, laptop, p) = await Questions(x => x.SA.LinkShareQuestions = false);
        await Until(() => pc.Peer.IsOn(p.IdB.DeviceId), "switches exchanged");
        Assert.False(laptop.CanAsk(p.IdA.DeviceId));
        Assert.False(await laptop.AskAsync(p.IdA.DeviceId, Mic, _ => throw new InvalidOperationException("must not run")));
        // Off on the asking device: nothing goes either.
        p.SB.LinkShareQuestions = false;
        p.SA.LinkShareQuestions = true;
        await pc.Peer.AnnounceAsync();
        await Until(() => laptop.Peer.IsOn(p.IdA.DeviceId), "PC switch on");
        Assert.False(laptop.CanAsk(p.IdA.DeviceId));
    }

    [Fact]
    public async Task Only_the_asked_device_can_answer()
    {
        var (pc, laptop, p) = await Questions();
        await Until(() => laptop.CanAsk(p.IdA.DeviceId) && pc.CanAsk(p.IdB.DeviceId), "switches known");
        var asked = Next<LinkQuestionRequest>(h => pc.Asked += h);
        var answers = new List<LinkAnswer>();
        Assert.True(await laptop.AskAsync(p.IdA.DeviceId, Mic, a => { lock (answers) answers.Add(a); }));
        var q = await asked;
        // The laptop answering its own question id (as if the PC had) is ignored: answers are matched to the device asked.
        await p.B.SendAsync(p.IdA.DeviceId, LinkQuestionFeature.AnswerType, new System.Text.Json.Nodes.JsonObject { ["id"] = q.Id, ["answer"] = "yes" });
        await Task.Delay(Quiet);
        Assert.Empty(answers);
    }

    // ---------- battery ----------

    [Fact]
    public async Task Battery_events_reach_the_other_device()
    {
        LinkBatteryFeature? ba = null, bb = null;
        var p = await Connected(x =>
        {
            ba = Keep(new LinkBatteryFeature(x.A, x.SA));
            bb = Keep(new LinkBatteryFeature(x.B, x.SB));
        });
        await Until(() => bb!.Peer.IsOn(p.IdA.DeviceId), "switch known");
        var got = Next<LinkBatteryEvent>(h => ba!.Received += h);
        Assert.Equal(1, await bb!.PublishAsync("low", 15));
        var e = await got;
        Assert.Equal(("Laptop", "low", 15), (e.PeerName, e.Kind, e.Percent));
        Assert.Equal(0, await bb.PublishAsync("explode", 15));

        p.SA.LinkShareBattery = false;
        Assert.True(await Nothing<LinkBatteryEvent>(h => ba!.Received += h));
        Assert.Equal(1, await bb.PublishAsync("plugged", 16));
        p.SB.LinkShareBattery = false;
        Assert.Equal(0, await bb.PublishAsync("plugged", 16));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]   // the main device does not take it: shown here as always
    [InlineData(false, true, false)]   // the main device shows it, with this device's name
    public void Own_battery_peek_shows_once(bool isMain, bool sharedWithMain, bool shows) =>
        Assert.Equal(shows, LinkBatteryFeature.ShowsOwnPeek(isMain, sharedWithMain));

    // ---------- lock together ----------

    [Fact]
    public async Task Locking_follows_only_when_the_other_device_is_idle()
    {
        double idleB = 120;
        int locksA = 0, locksB = 0;
        LinkLockFeature? la = null, lb = null;
        var p = await Connected(x =>
        {
            x.SA.LinkLockTogether = x.SB.LinkLockTogether = true;
            la = Keep(new LinkLockFeature(x.A, x.SA, () => 0, () => Interlocked.Increment(ref locksA)));
            lb = Keep(new LinkLockFeature(x.B, x.SB, () => idleB, () => Interlocked.Increment(ref locksB)));
        });
        var followed = Next<string>(h => lb!.LockedByPeer += h);
        Assert.Equal(1, await la!.LockedHereAsync());
        Assert.Equal("PC", await followed);
        Assert.Equal(1, locksB);
        // The laptop locked because of the PC: its own lock event does not go back.
        Assert.Equal(0, await lb!.LockedHereAsync());
        Assert.Equal(0, locksA);

        // Someone works on the laptop: it stays unlocked.
        idleB = 10;
        Assert.Equal(1, await la.LockedHereAsync(DateTime.UtcNow.AddMinutes(1)));
        await Task.Delay(Quiet);
        Assert.Equal(1, locksB);
    }

    [Fact]
    public async Task Locking_together_is_off_by_default()
    {
        Assert.False(new AppSettings().LinkLockTogether);
        int locks = 0;
        LinkLockFeature? la = null;
        var p = await Connected(x =>
        {
            x.SA.LinkLockTogether = true; // only one side on: the other ignores it
            la = Keep(new LinkLockFeature(x.A, x.SA, () => 999, () => { }));
            Keep(new LinkLockFeature(x.B, x.SB, () => 999, () => Interlocked.Increment(ref locks)));
        });
        Assert.Equal(1, await la!.LockedHereAsync());
        await Task.Delay(Quiet);
        Assert.Equal(0, locks);
        p.SA.LinkLockTogether = false;
        Assert.Equal(0, await la.LockedHereAsync(DateTime.UtcNow.AddMinutes(5)));
    }

    [Theory]
    [InlineData(0d, false)]
    [InlineData(59.5d, false)]
    [InlineData(60d, true)]
    [InlineData(3600d, true)]
    public void Never_locks_under_someones_hands(double idle, bool locks) => Assert.Equal(locks, LinkLockFeature.ShouldLock(idle));

    // ---------- clipboard pictures both ways ----------

    [Fact]
    public async Task Clipboard_pictures_go_both_ways_and_not_back()
    {
        LinkClipboardFeature? ca = null, cb = null;
        await Connected(x =>
        {
            var fa = Keep(new LinkFilesFeature(x.A, x.SA));
            var fb = Keep(new LinkFilesFeature(x.B, x.SB));
            ca = Keep(new LinkClipboardFeature(x.A, x.SA, fa, Path.Combine(_temp, "clip-a")));
            cb = Keep(new LinkClipboardFeature(x.B, x.SB, fb, Path.Combine(_temp, "clip-b")));
        });
        static byte[] Png(int size)
        {
            var bytes = RandomNumberGenerator.GetBytes(size);
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
            return bytes;
        }
        // Laptop → PC, small and large.
        var small = Png(5000);
        var got = Next<LinkClip>(h => ca!.Received += h);
        Assert.Equal(1, await cb!.ShareImageAsync(small));
        var clip = await got;
        Assert.Equal("Laptop", clip.PeerName);
        Assert.Equal(small, clip.Png);
        var big = Png(LinkClipboardFeature.InlineImageBytes + 10_000);
        var got2 = Next<LinkClip>(h => ca!.Received += h);
        Assert.Equal(1, await cb.ShareImageAsync(big).WaitAsync(Wait));
        Assert.Equal(big, (await got2).Png);
        // PC → laptop.
        var other = Png(3000);
        var got3 = Next<LinkClip>(h => cb.Received += h);
        Assert.Equal(1, await ca!.ShareImageAsync(other));
        Assert.Equal(other, (await got3).Png);
        // What came from the other device does not go back.
        Assert.Equal(0, await ca.ShareImageAsync(small));
        Assert.Equal(0, await cb.ShareImageAsync(other));
    }

    // ---------- settings ----------

    [Fact]
    public void Working_together_switches_are_plain_settings_without_device_ids()
    {
        var s = new AppSettings();
        Assert.True(s.LinkShareTimer && s.LinkShareQuiet && s.LinkShareQuestions && s.LinkShareBattery);
        Assert.False(s.LinkLockTogether);
        s.PeerQuiet = true;
        Assert.True(s.Quiet);
        var json = s.ToJson();
        foreach (var name in new[] { "LinkShareTimer", "LinkShareQuiet", "LinkShareQuestions", "LinkShareBattery", "LinkLockTogether", "LinkQuietChanged" })
            Assert.Contains(name, json);
        Assert.DoesNotContain("PeerQuiet", json); // the other device's state is never stored
    }
}
