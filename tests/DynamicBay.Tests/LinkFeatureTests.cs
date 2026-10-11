using System.IO;
using System.Net;
using System.Security.Cryptography;
using DynamicBay.Core;
using DynamicBay.Island;
using DynamicBay.Services;
using DynamicBay.Services.Link;

namespace DynamicBay.Tests;

/// <summary>
/// The mirroring features on two link endpoints in one process (loopback only). Each feature: sent only with its
/// switch on at the sender, taken only with the switch on at the receiver, nothing echoed back.
/// </summary>
public class LinkFeatureTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dynbay-feat-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    /// <summary>How long a message that must not arrive is waited for.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    public void Dispose()
    {
        for (int i = _cleanup.Count - 1; i >= 0; i--) try { _cleanup[i].Dispose(); } catch { }
        try { Directory.Delete(_temp, true); } catch { }
    }

    private T Keep<T>(T d) where T : IDisposable { _cleanup.Add(d); return d; }

    private LinkOptions Options(int port = 0, string? downloads = null) => new()
    {
        Port = port, LoopbackOnly = true, LanDiscovery = false, TailscaleDiscovery = false, WatchSystem = false,
        MinBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(500),
        DownloadFolder = downloads ?? Path.Combine(_temp, "downloads"), Log = _ => { },
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

    /// <summary>True when nothing came within <see cref="Quiet"/>.</summary>
    private static async Task<bool> Nothing<T>(Action<Action<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(v => tcs.TrySetResult(v));
        return await Task.WhenAny(tcs.Task, Task.Delay(Quiet)) != tcs.Task;
    }

    private sealed record Pair(LinkService A, LinkService B, DeviceIdentity IdA, DeviceIdentity IdB, AppSettings SA, AppSettings SB);

    /// <summary>"PC" (A) and "Laptop" (B), paired; features are created in <paramref name="features"/> before both start.</summary>
    private async Task<Pair> Connected(Action<Pair> features)
    {
        var idA = Keep(DeviceIdentity.Create("PC"));
        var idB = Keep(DeviceIdentity.Create("Laptop"));
        int portA = FreePort();
        var a = Keep(new LinkService(idA, "PC", new[] { new PairedDevice { Id = idB.DeviceId, Name = "Laptop", Fingerprint = idB.Fingerprint } }, Options(portA, Path.Combine(_temp, "dl-a"))));
        var b = Keep(new LinkService(idB, "Laptop", new[] { new PairedDevice { Id = idA.DeviceId, Name = "PC", Fingerprint = idA.Fingerprint, LastAddresses = { $"127.0.0.1:{portA}" } } }, Options(0, Path.Combine(_temp, "dl-b"))));
        var pair = new Pair(a, b, idA, idB, new AppSettings(), new AppSettings());
        features(pair);
        a.Start();
        b.Start();
        await Until(() => a.IsOnline(idB.DeviceId) && b.IsOnline(idA.DeviceId), "connect");
        return pair;
    }

    // ---------- status ----------

    [Fact]
    public async Task Status_arrives_on_connect_and_when_it_changes()
    {
        int battery = 80;
        LinkStatusFeature? sa = null, sb = null;
        var onConnect = new TaskCompletionSource<LinkStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = await Connected(x =>
        {
            sa = Keep(new LinkStatusFeature(x.A, x.SA, () => new LinkStatus("PC", battery, true, "1.0.19")));
            sb = Keep(new LinkStatusFeature(x.B, x.SB, () => new LinkStatus("Laptop", null, false, "1.0.19")));
            sb.Received += (_, s) => onConnect.TrySetResult(s);
        });
        var first = await onConnect.Task.WaitAsync(Wait);
        Assert.Equal(("PC", 80, true, "1.0.19"), (first.Name, first.Battery, first.Charging, first.Version));
        Assert.Equal(80, sb!.StatusOf(p.IdA.DeviceId)!.Battery);

        battery = 55;
        var changed = Next<LinkStatus>(h => sb.Received += (_, s) => { if (s.Battery == 55) h(s); });
        Assert.Equal(1, await sa!.PublishAsync());
        Assert.Equal(55, (await changed).Battery);
        Assert.Equal(0, await sa.PublishAsync()); // the same again is not sent
    }

    [Fact]
    public async Task Status_switch_is_checked_on_both_sides()
    {
        LinkStatusFeature? sa = null, sb = null;
        var p = await Connected(x =>
        {
            x.SA.LinkShareStatus = false; // sender off: not even on connect
            sa = Keep(new LinkStatusFeature(x.A, x.SA, () => new LinkStatus("PC", 50, false, "1")));
            sb = Keep(new LinkStatusFeature(x.B, x.SB, () => new LinkStatus("Laptop", 70, false, "1")));
        });
        Assert.True(await Nothing<LinkStatus>(h => sb!.Received += (_, s) => h(s)));
        Assert.Equal(0, await sa!.PublishAsync(force: true));
        Assert.Null(sb!.StatusOf(p.IdA.DeviceId));

        // Receiver off: what B sends is dropped at A.
        Assert.True(await Nothing<LinkStatus>(h => sa.Received += (_, s) => h(s)));
        Assert.Equal(1, await sb.PublishAsync(force: true));
        Assert.Null(sa.StatusOf(p.IdB.DeviceId)); // A's switch is off: it keeps nothing
    }

    [Theory]
    [InlineData("{\"name\":\"Laptop\",\"battery\":140,\"charging\":true}", null, false)]
    [InlineData("{\"battery\":12,\"charging\":true,\"extra\":[1]}", 12, true)]
    [InlineData("{\"battery\":\"x\"}", null, false)]
    public void Status_parses_tolerantly(string json, int? battery, bool charging)
    {
        var s = LinkStatusFeature.Parse(System.Text.Json.Nodes.JsonNode.Parse(json), "Fallback")!;
        Assert.Equal((battery, charging), (s.Battery, s.Charging));
        Assert.False(string.IsNullOrEmpty(s.Name));
    }

    // ---------- files ----------

    [Fact]
    public async Task Received_file_ends_up_in_the_shelf()
    {
        LinkFilesFeature? fa = null, fb = null;
        var shelf = new ShelfService(new AppSettings(), Path.Combine(_temp, "shelf-b"));
        var p = await Connected(x =>
        {
            fa = Keep(new LinkFilesFeature(x.A, x.SA));
            fb = Keep(new LinkFilesFeature(x.B, x.SB) { Shelf = shelf });
        });
        Directory.CreateDirectory(_temp);
        var src = Path.Combine(_temp, "Angebot.pdf");
        File.WriteAllBytes(src, RandomNumberGenerator.GetBytes(300_000));
        var received = Next<(IncomingFileOffer, string)>(h => fb!.Received += (o, path) => h((o, path)));

        Assert.Equal(FileTransferResult.Done, await fa!.SendAsync(p.IdB.DeviceId, src).WaitAsync(Wait));
        var (offer, path) = await received;
        Assert.Equal("PC", offer.PeerName);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(path));
        Assert.Contains(shelf.Items, i => i.Path == path);
        Assert.True(File.Exists(Path.Combine(_temp, "shelf-b", "shelf.json"))); // the test shelf, never the user's
    }

    [Fact]
    public async Task Files_switch_off_declines_and_sends_nothing()
    {
        LinkFilesFeature? fa = null, fb = null;
        var shelf = new ShelfService(new AppSettings(), Path.Combine(_temp, "shelf-b"));
        var p = await Connected(x =>
        {
            x.SB.LinkShareFiles = false;
            fa = Keep(new LinkFilesFeature(x.A, x.SA));
            fb = Keep(new LinkFilesFeature(x.B, x.SB) { Shelf = shelf });
        });
        Directory.CreateDirectory(_temp);
        var src = Path.Combine(_temp, "a.txt");
        File.WriteAllText(src, "hallo");
        Assert.Equal(FileTransferResult.Declined, await fa!.SendAsync(p.IdB.DeviceId, src).WaitAsync(Wait));
        Assert.Empty(shelf.Items);
        Assert.False(Directory.Exists(Path.Combine(_temp, "dl-b")) && Directory.EnumerateFiles(Path.Combine(_temp, "dl-b")).Any());

        // Sender off: declined right away, nothing goes over the wire.
        p.SA.LinkShareFiles = false;
        p.SB.LinkShareFiles = true;
        bool offered = false;
        p.B.FileOffered += _ => offered = true;
        Assert.Equal(FileTransferResult.Declined, await fa.SendAsync(p.IdB.DeviceId, src));
        await Task.Delay(Quiet);
        Assert.False(offered);
    }

    [Fact]
    public async Task Files_go_both_ways_at_once_without_blocking()
    {
        // Both read loops answer while chunks pour in from the other side: no answer may wait on a full buffer.
        LinkFilesFeature? fa = null, fb = null;
        var p = await Connected(x =>
        {
            fa = Keep(new LinkFilesFeature(x.A, x.SA));
            fb = Keep(new LinkFilesFeature(x.B, x.SB));
        });
        Directory.CreateDirectory(_temp);
        var sends = new List<Task<FileTransferResult>>();
        for (int i = 0; i < 3; i++)
        {
            var fromA = Path.Combine(_temp, $"a{i}.bin");
            var fromB = Path.Combine(_temp, $"b{i}.bin");
            File.WriteAllBytes(fromA, RandomNumberGenerator.GetBytes(3 * 1024 * 1024));
            File.WriteAllBytes(fromB, RandomNumberGenerator.GetBytes(3 * 1024 * 1024));
            sends.Add(fa!.SendAsync(p.IdB.DeviceId, fromA));
            sends.Add(fb!.SendAsync(p.IdA.DeviceId, fromB));
        }
        var results = await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.All(results, r => Assert.Equal(FileTransferResult.Done, r));
    }

    // ---------- clipboard ----------

    private async Task<(LinkClipboardFeature A, LinkClipboardFeature B, Pair P)> Clipboards(Action<Pair>? settings = null)
    {
        LinkClipboardFeature? ca = null, cb = null;
        var p = await Connected(x =>
        {
            settings?.Invoke(x);
            var filesA = Keep(new LinkFilesFeature(x.A, x.SA));
            var filesB = Keep(new LinkFilesFeature(x.B, x.SB));
            ca = Keep(new LinkClipboardFeature(x.A, x.SA, filesA, Path.Combine(_temp, "clip-a")));
            cb = Keep(new LinkClipboardFeature(x.B, x.SB, filesB, Path.Combine(_temp, "clip-b")));
        });
        return (ca!, cb!, p);
    }

    [Fact]
    public async Task Copied_text_arrives_with_the_device_name()
    {
        var (a, b, _) = await Clipboards();
        var got = Next<LinkClip>(h => b.Received += h);
        Assert.Equal(1, await a.ShareTextAsync("Treffen um 14 Uhr"));
        var clip = await got;
        Assert.Equal(("PC", "Treffen um 14 Uhr"), (clip.PeerName, clip.Text));
        Assert.Null(clip.Png);
    }

    [Fact]
    public async Task Clipboard_switch_is_checked_on_both_sides()
    {
        var (a, b, p) = await Clipboards(x => x.SA.LinkShareClipboard = false);
        Assert.Equal(0, await a.ShareTextAsync("geheim"));
        p.SA.LinkShareClipboard = true;
        p.SB.LinkShareClipboard = false;
        Assert.True(await Nothing<LinkClip>(h => b.Received += h));
        Assert.Equal(1, await a.ShareTextAsync("auch nicht")); // sent, but B drops it
    }

    [Fact]
    public async Task Clipboard_content_is_not_sent_back()
    {
        var (a, b, _) = await Clipboards();
        var got = Next<LinkClip>(h => b.Received += h);
        await a.ShareTextAsync("Hin");
        await got;
        // B's clipboard history gets the entry; when it shows up as copied there again, it does not go back.
        Assert.Equal(0, await b.ShareTextAsync("Hin"));
        // CursorHop brought it (the entry has a source device): not sent either.
        Assert.Equal(0, await b.ShareTextAsync("Von CursorHop", sourceDevice: "PC"));
        Assert.True(await Nothing<LinkClip>(h => a.Received += h));
    }

    [Fact]
    public async Task Pictures_go_inline_or_as_a_claimed_file()
    {
        var (a, b, p) = await Clipboards();
        byte[] Png(int size)
        {
            var bytes = RandomNumberGenerator.GetBytes(size);
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
            return bytes;
        }
        var small = Png(2000);
        var got = Next<LinkClip>(h => b.Received += h);
        Assert.Equal(1, await a.ShareImageAsync(small));
        Assert.Equal(small, (await got).Png);

        // Over the message limit: as a file the clipboard takes itself - not into the downloads or the shelf.
        var big = Png(LinkClipboardFeature.InlineImageBytes + 50_000);
        var got2 = Next<LinkClip>(h => b.Received += h);
        Assert.Equal(1, await a.ShareImageAsync(big).WaitAsync(Wait));
        Assert.Equal(big, (await got2).Png);
        Assert.False(Directory.Exists(Path.Combine(_temp, "dl-b")) && Directory.EnumerateFiles(Path.Combine(_temp, "dl-b")).Any());
        Assert.Empty(Directory.Exists(Path.Combine(_temp, "clip-b")) ? Directory.GetFiles(Path.Combine(_temp, "clip-b")) : Array.Empty<string>());
        Assert.Equal(0, await a.ShareImageAsync(Png(LinkClipboardFeature.MaxImageBytes + 1)));
    }

    // ---------- notifications ----------

    [Fact]
    public async Task Notifications_arrive_marked_with_the_device_and_never_go_back()
    {
        LinkNotificationsFeature? na = null, nb = null;
        var p = await Connected(x =>
        {
            na = Keep(new LinkNotificationsFeature(x.A, x.SA));
            nb = Keep(new LinkNotificationsFeature(x.B, x.SB));
        });
        var icon = new byte[] { 1, 2, 3 };
        var got = Next<LinkNotification>(h => nb!.Received += h);
        Assert.Equal(1, await na!.ForwardAsync("WhatsApp", "Lena", "Bist du dabei?", icon));
        var n = await got;
        Assert.Equal(("PC", "WhatsApp", "Lena", "Bist du dabei?"), (n.PeerName, n.App, n.Title, n.Body));
        Assert.Equal(icon, n.Icon);

        // On B it is a notification from a device: never forwarded, also not when it shows up again unmarked.
        Assert.Equal(0, await nb!.ForwardAsync(n.App, n.Title, n.Body, null, fromDevice: n.PeerName));
        Assert.Equal(0, await nb.ForwardAsync(n.App, n.Title, n.Body, null));
        Assert.True(await Nothing<LinkNotification>(h => na.Received += h));

        // Switches: sender off sends nothing, receiver off drops it.
        p.SA.LinkShareNotifications = false;
        Assert.Equal(0, await na.ForwardAsync("Outlook", "Review", "", null));
        p.SA.LinkShareNotifications = true;
        p.SB.LinkShareNotifications = false;
        Assert.True(await Nothing<LinkNotification>(h => nb.Received += h));
        Assert.Equal(1, await na.ForwardAsync("Outlook", "Review 2", "", null));
    }

    [Fact]
    public void The_same_notification_from_both_devices_shows_once()
    {
        var settings = new AppSettings();
        var service = new NotificationService(settings);
        Assert.True(service.AddRemote("Laptop", "Teams", "Anna", "Kurz Zeit?", null));
        Assert.False(service.AddRemote("Laptop", "Teams", "Anna", "Kurz Zeit?", null));
        Assert.True(service.IsRepeat("Teams", "Anna", "Kurz Zeit?"));
        Assert.Single(service.Items);
        Assert.Equal("Laptop", service.Items[0].Device);
        Assert.True(service.ShowList); // without access to Windows notifications the list still shows them
        settings.MutedApps.Add("Slack");
        Assert.False(service.AddRemote("Laptop", "Slack", "x", "y", null));
    }

    // ---------- music ----------

    [Fact]
    public async Task Music_shows_on_the_other_device_and_commands_run_only_where_it_plays()
    {
        LinkMediaFeature? ma = null, mb = null;
        var p = await Connected(x =>
        {
            ma = Keep(new LinkMediaFeature(x.A, x.SA));
            mb = Keep(new LinkMediaFeature(x.B, x.SB));
        });
        var shown = Next<LinkNowPlaying?>(h => mb!.RemoteChanged += (_, _, np) => h(np));
        Assert.Equal(1, await ma!.PublishAsync(new LinkNowPlaying(true, "Midnight Drive", "Neon Harbor", "Spotify", true, 81, 214, new byte[] { 9 })));
        var np = await shown;
        Assert.Equal(("Midnight Drive", "Neon Harbor", true, 214d), (np!.Title, np.Artist, np.Playing, np.Duration));

        var commandsA = new List<string>();
        var commandsB = new List<string>();
        ma.CommandReceived += (_, c) => { lock (commandsA) commandsA.Add(c); };
        mb!.CommandReceived += (_, c) => { lock (commandsB) commandsB.Add(c); };
        var onA = Next<string>(h => ma.CommandReceived += (_, c) => h(c));
        Assert.True(await mb.SendCommandAsync(p.IdA.DeviceId, "next"));
        Assert.Equal("next", await onA);
        await Task.Delay(300);
        Assert.Equal(new[] { "next" }, commandsA);
        Assert.Empty(commandsB); // only the device that plays does it
        Assert.False(await mb.SendCommandAsync(p.IdA.DeviceId, "play-something"));

        // A's switch off: commands from B are not done there.
        p.SA.LinkShareMedia = false;
        Assert.True(await mb.SendCommandAsync(p.IdA.DeviceId, "playpause"));
        await Task.Delay(Quiet);
        Assert.Equal(new[] { "next" }, commandsA);
        Assert.Equal(0, await ma.PublishAsync(LinkNowPlaying.None, force: true));
    }

    // ---------- main device ----------

    [Theory]
    [InlineData("", "A", "B", 100d, 0d, null, null, true)]          // no choice: both show everything
    [InlineData("A", "A", "B", 100d, 0d, null, null, true)]
    [InlineData("B", "A", "B", 0d, 100d, null, null, false)]
    [InlineData("auto", "A", null, 100d, null, null, null, true)]  // nothing connected
    [InlineData("auto", "A", "B", 0d, 100d, false, null, false)]    // CursorHop: the mouse is on B
    [InlineData("auto", "A", "B", 100d, 0d, null, false, true)]     // B says the mouse is not there
    [InlineData("auto", "A", "B", 30d, 1d, null, null, false)]      // B used clearly more recently
    [InlineData("auto", "A", "B", 2d, 1d, null, null, true)]        // about the same: stays main (never both quiet)
    [InlineData("C", "A", "B", 100d, 0d, null, null, true)]         // a device that is not connected
    public void Main_device_rule(string mode, string local, string? peer, double localIdle, double? peerIdle, bool? localCursor, bool? peerCursor, bool main) =>
        Assert.Equal(main, LinkRoleFeature.IsMainDevice(mode, local, peer, localIdle, peerIdle, localCursor, peerCursor));

    [Fact]
    public void Auto_never_makes_both_devices_quiet()
    {
        foreach (var (a, b) in new[] { (0.0, 0.0), (1.0, 3.5), (10.0, 9.0), (0.5, 100.0) })
            Assert.True(LinkRoleFeature.IsMainDevice("auto", "A", "B", a, b, null, null) || LinkRoleFeature.IsMainDevice("auto", "B", "A", b, a, null, null));
    }

    [Fact]
    public async Task Main_device_choice_is_shared_and_peeks_go_there()
    {
        LinkRoleFeature? ra = null, rb = null;
        var p = await Connected(x =>
        {
            ra = Keep(new LinkRoleFeature(x.A, x.SA, () => 5, () => null));
            rb = Keep(new LinkRoleFeature(x.B, x.SB, () => 5, () => null));
        });
        Assert.True(ra!.IsMain && rb!.IsMain);

        // Chosen on A: the laptop (B) is main. B adopts it, A goes quiet.
        await ra.ChooseAsync(LinkRoleFeature.Other);
        await Until(() => p.SB.LinkMain == LinkRoleFeature.This, "B adopts the choice (as \"this device\")");
        Assert.False(ra.IsMain);
        Assert.True(rb!.IsMain);
        Assert.Equal(p.IdB.DeviceId, ra.MainPeer);

        var peek = Next<LinkPeek>(h => rb.PeekReceived += h);
        Assert.True(await ra.ForwardPeekAsync("Termin beginnt", "Design Review · 14:00", "Icon.Calendar"));
        var got = await peek;
        Assert.Equal(("Termin beginnt", "Icon.Calendar", "PC"), (got.Title, got.IconKey, got.PeerName));
        Assert.False(await rb.ForwardPeekAsync("x", "y", null)); // the main device keeps its own

        // An older choice does not win back.
        p.SA.LinkMainChanged = DateTime.UtcNow.AddDays(-1);
        p.SA.LinkMain = "";
        await ra.AnnounceAsync();
        await Task.Delay(Quiet);
        Assert.Equal(LinkRoleFeature.This, p.SB.LinkMain);
        // The choice is stored relative ("this"/"other"): no device id in the settings, so they can be exported.
        foreach (var json in new[] { p.SA.ToJson(), p.SB.ToJson() })
        {
            Assert.DoesNotContain(p.IdA.DeviceId, json);
            Assert.DoesNotContain(p.IdB.DeviceId, json);
        }

        // B goes away: A shows everything itself again.
        p.B.Stop();
        await Until(() => ra.IsMain, "A main again");
    }

    [Fact]
    public void Repeated_peeks_show_once_within_ten_seconds()
    {
        var recent = new Dictionary<string, DateTime>();
        var t = DateTime.UtcNow;
        var peek = new PeekItem { Title = "Design Review", Subtitle = "Beginnt bald" };
        Assert.False(IslandManager.IsRepeat(recent, peek, t));
        Assert.True(IslandManager.IsRepeat(recent, new PeekItem { Title = "Design Review", Subtitle = "Beginnt bald" }, t.AddSeconds(3)));
        Assert.False(IslandManager.IsRepeat(recent, new PeekItem { Title = "Design Review", Subtitle = "Beginnt bald" }, t.AddSeconds(14)));
        // Key feedback and questions always show.
        Assert.False(IslandManager.IsRepeat(recent, new PeekItem { Title = "Mikrofon stumm", Kind = PeekKind.Local }, t));
        Assert.False(IslandManager.IsRepeat(recent, new PeekItem { Title = "Mikrofon stumm", Kind = PeekKind.Local }, t));
    }

    [Fact]
    public void A_device_announced_by_CursorHop_is_not_announced_again_by_the_link()
    {
        DeviceAnnouncements.Clear();
        var t = DateTime.UtcNow;
        Assert.True(DeviceAnnouncements.TryAnnounce("LaptopJonathan", true, t));
        Assert.False(DeviceAnnouncements.TryAnnounce("laptopjonathan", true, t.AddSeconds(4)));
        Assert.True(DeviceAnnouncements.TryAnnounce("LaptopJonathan", false, t.AddSeconds(5))); // going away is news
        Assert.True(DeviceAnnouncements.TryAnnounce("LaptopJonathan", true, t.AddSeconds(30)));
    }

    [Fact]
    public void Link_announces_a_drop_after_thirty_seconds()
    {
        var a = new CursorHopAnnouncer(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
        var t = DateTime.UtcNow;
        a.Connected("PC", t);
        Assert.Empty(a.Due(t.AddSeconds(2)));
        Assert.Equal(new[] { ("PC", true) }, a.Due(t.AddSeconds(3)));
        a.Disconnected("PC", t.AddSeconds(10));
        Assert.Empty(a.Due(t.AddSeconds(39)));
        Assert.Equal(new[] { ("PC", false) }, a.Due(t.AddSeconds(40)));
    }

    [Fact]
    public void Pairing_code_reads_in_two_groups() => Assert.Equal("482 913", PairingCodeText.Format("482913"));
}

/// <summary>Fixes from the review of the link transport.</summary>
public class LinkHardeningTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dynbay-hard-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        for (int i = _cleanup.Count - 1; i >= 0; i--) try { _cleanup[i].Dispose(); } catch { }
        try { Directory.Delete(_temp, true); } catch { }
    }

    private LinkOptions Options() => new()
    {
        // Loopback only: a listener on all addresses makes Windows Firewall ask about the test host.
        Port = 0, LoopbackOnly = true, LanDiscovery = false, TailscaleDiscovery = false, WatchSystem = false,
        MinBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(300),
        DownloadFolder = Path.Combine(_temp, "downloads"), Log = _ => { },
    };

    [Theory]
    [InlineData(true, false, "auth")]
    [InlineData(false, true, "wrong-device")]
    [InlineData(false, false, "unreachable")]
    public void Only_a_refusal_by_the_pinned_device_counts_as_auth(bool pinMatched, bool weRefused, string reason) =>
        Assert.Equal(reason, LinkService.FailureReason(pinMatched, weRefused));

    [Fact]
    public async Task Another_device_at_the_old_address_does_not_make_the_pairing_rejected()
    {
        // B was paired with A; A's old address now belongs to another DynamicBay (C). B must keep A, just offline.
        var idA = DeviceIdentity.Create("PC"); _cleanup.Add(idA);
        var idB = DeviceIdentity.Create("Laptop"); _cleanup.Add(idB);
        var idC = DeviceIdentity.Create("Fremd"); _cleanup.Add(idC);
        var c = new LinkService(idC, "Fremd", null, Options()); _cleanup.Add(c);
        c.Start();
        var b = new LinkService(idB, "Laptop", new[] { new PairedDevice { Id = idA.DeviceId, Name = "PC", Fingerprint = idA.Fingerprint, LastAddresses = { $"127.0.0.1:{c.ListeningPort}" } } }, Options());
        _cleanup.Add(b);
        bool rejected = false;
        b.PeerStateChanged += (_, s) => { if (s == LinkPeerState.Rejected) rejected = true; };
        b.Start();
        await Task.Delay(3000); // with 100-300 ms backoff: well over three attempts
        Assert.False(rejected);
        Assert.NotEqual(LinkPeerState.Rejected, b.GetState(idA.DeviceId));
        Assert.False(Assert.Single(b.PairedDevices).Rejected);
        Assert.False(b.IsOnline(idA.DeviceId));
    }

    [Fact]
    public void Discovery_list_stays_small_and_old_entries_go()
    {
        var d = new LinkDiscovery(Options(), () => "0000000000000000", () => "me", () => 1, () => { });
        var t = DateTime.UtcNow;
        // A flood of made-up ids from "Tailscale" addresses: capped.
        for (int i = 0; i < 300; i++)
            d.Upsert(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), "x", IPAddress.Parse($"100.64.{i / 250}.{i % 250 + 1}"), 43823, LinkVia.Lan, t);
        Assert.Equal(LinkDiscovery.MaxEntries, d.Count);
        // Nothing heard for longer than the age (Tailscale-flagged ones too): gone.
        d.Prune(t + LinkDiscovery.OtherAge + TimeSpan.FromSeconds(1));
        Assert.Equal(0, d.Count);
        d.Upsert("0123456789ABCDEF", "Laptop", IPAddress.Parse("192.168.1.20"), 43823, LinkVia.Lan, t);
        d.Prune(t + TimeSpan.FromSeconds(10));
        Assert.Equal(1, d.Count);
        d.Prune(t + LinkDiscovery.LanAge + TimeSpan.FromSeconds(1));
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void Format_characters_are_removed_from_file_names()
    {
        Assert.Equal("rechnung_fdp.exe", LinkProtocol.SanitizeFileName("rechnung‮fdp.exe"));
        Assert.Equal("a_b.txt", LinkProtocol.SanitizeFileName("a​b.txt"));
    }

    [Fact]
    public void Pairing_limit_counts_per_address_with_a_total_cap()
    {
        var limiter = new PairingLimiter(2, TimeSpan.FromMinutes(10));
        var t = DateTime.UtcNow;
        var noisy = IPAddress.Parse("192.168.1.66");
        Assert.True(limiter.TryAttempt(noisy, t));
        Assert.True(limiter.TryAttempt(noisy, t));
        Assert.False(limiter.TryAttempt(noisy, t));
        Assert.True(limiter.TryAttempt(IPAddress.Parse("192.168.1.20"), t)); // the real laptop still gets through
        Assert.True(limiter.TryAttempt(IPAddress.Parse("::ffff:192.168.1.21"), t));
        // Total cap: 4 x 2 attempts, then nobody.
        for (int i = 30; i < 40; i++) limiter.TryAttempt(IPAddress.Parse($"10.0.0.{i}"), t);
        Assert.False(limiter.TryAttempt(IPAddress.Parse("10.0.1.1"), t));
        // Ten minutes later it is open again.
        Assert.True(limiter.TryAttempt(noisy, t.AddMinutes(11)));
    }

    [Fact]
    public void Key_left_behind_by_a_hard_kill_is_removed_at_the_next_start()
    {
        var path = Path.Combine(_temp, "identity.bin");
        var first = DeviceIdentity.LoadOrCreate(path);
        var name = DeviceIdentity.KeyNameOf(first.Certificate);
        Assert.False(string.IsNullOrEmpty(name));
        Assert.True(System.Security.Cryptography.CngKey.Exists(name!, System.Security.Cryptography.CngProvider.MicrosoftSoftwareKeyStorageProvider));
        // No Dispose (the process was killed): the next start removes the old key and loads the same identity again.
        using var second = DeviceIdentity.LoadOrCreate(path);
        Assert.False(System.Security.Cryptography.CngKey.Exists(name!, System.Security.Cryptography.CngProvider.MicrosoftSoftwareKeyStorageProvider));
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.True(second.Certificate.HasPrivateKey);
        try { first.Dispose(); } catch { }
    }
}
