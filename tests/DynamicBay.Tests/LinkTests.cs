using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicBay.Core;
using DynamicBay.Services.Link;

namespace DynamicBay.Tests;

/// <summary>Wire format and the pure rules of the link: no sockets.</summary>
public class LinkProtocolTests
{
    [Fact]
    public async Task Frames_round_trip_and_clean_end_is_null()
    {
        var ms = new MemoryStream();
        await LinkProtocol.WriteFrameAsync(ms, "hallo"u8.ToArray());
        await LinkProtocol.WriteFrameAsync(ms, new byte[LinkProtocol.MaxFrame]);
        ms.Position = 0;
        Assert.Equal("hallo"u8.ToArray(), await LinkProtocol.ReadFrameAsync(ms));
        Assert.Equal(LinkProtocol.MaxFrame, (await LinkProtocol.ReadFrameAsync(ms))!.Length);
        Assert.Null(await LinkProtocol.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task Oversize_frames_are_refused_both_ways()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => LinkProtocol.WriteFrameAsync(new MemoryStream(), new byte[LinkProtocol.MaxFrame + 1]));
        // A header that claims more than 1 MiB is refused before anything is allocated.
        var ms = new MemoryStream(new byte[] { 0x00, 0x10, 0x00, 0x01, 1, 2, 3 });
        await Assert.ThrowsAsync<InvalidDataException>(() => LinkProtocol.ReadFrameAsync(ms));
        await Assert.ThrowsAsync<InvalidDataException>(() => LinkProtocol.ReadFrameAsync(new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })));
    }

    [Fact]
    public async Task Truncated_frames_throw()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => LinkProtocol.ReadFrameAsync(new MemoryStream(new byte[] { 0, 0 })));
        await Assert.ThrowsAsync<EndOfStreamException>(() => LinkProtocol.ReadFrameAsync(new MemoryStream(new byte[] { 0, 0, 0, 5, 1, 2 })));
    }

    [Theory]
    [InlineData("{\"t\":\"x\"}", true)]
    [InlineData("{\"t\":5}", false)]
    [InlineData("[1,2]", false)]
    [InlineData("kein json", false)]
    [InlineData("{}", false)]
    public void Envelopes_need_a_type(string json, bool ok) =>
        Assert.Equal(ok, LinkProtocol.Decode(System.Text.Encoding.UTF8.GetBytes(json)) is not null);

    private static readonly string FpA = new('A', 64), FpB = new('B', 64), FpM = Convert.ToHexString(SHA256.HashData("mallory"u8));
    private static readonly byte[] Na = Enumerable.Repeat((byte)1, 32).ToArray(), Nb = Enumerable.Repeat((byte)2, 32).ToArray();

    [Fact]
    public void Pairing_code_is_six_digits_and_independent_of_fingerprint_order()
    {
        var code = LinkProtocol.PairingCode(FpA, FpB, Na, Nb);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(code, LinkProtocol.PairingCode(FpB, FpA, Na, Nb));
    }

    [Fact]
    public void Device_in_the_middle_gets_other_codes()
    {
        // A talks to M (thinking it is B) and M to B: each side mixes in the certificate it really saw.
        string honest = LinkProtocol.PairingCode(FpA, FpB, Na, Nb);
        Assert.NotEqual(honest, LinkProtocol.PairingCode(FpA, FpM, Na, Nb));
        Assert.NotEqual(honest, LinkProtocol.PairingCode(FpM, FpB, Na, Nb));
        Assert.NotEqual(honest, LinkProtocol.PairingCode(FpA, FpB, Na, Na)); // another nonce, another code
    }

    [Fact]
    public void Commitment_binds_nonce_and_both_certificates()
    {
        var c = LinkProtocol.Commitment(FpB, FpA, Nb);
        Assert.Equal(c, LinkProtocol.Commitment(FpB, FpA, Nb));
        Assert.NotEqual(c, LinkProtocol.Commitment(FpB, FpA, Na));
        Assert.NotEqual(c, LinkProtocol.Commitment(FpM, FpA, Nb));
        Assert.NotEqual(c, LinkProtocol.Commitment(FpB, FpM, Nb));
    }

    [Fact]
    public void Tie_break_keeps_the_same_connection_on_both_sides()
    {
        const string a = "0000000000000001", b = "FFFFFFFFFFFFFFFF";
        // Both dialed. Each side holds the other's connection first, then sees its own (or the reverse).
        foreach (var (firstOutbound, secondOutbound) in new[] { (true, false), (false, true) })
        {
            bool keepNewOnA = LinkProtocol.KeepNew(a, b, firstOutbound, "boot", secondOutbound, "boot");
            bool aKeepsOutbound = keepNewOnA ? secondOutbound : firstOutbound;
            // B sees the same two connections with the direction flipped.
            bool keepNewOnB = LinkProtocol.KeepNew(b, a, !firstOutbound, "boot", !secondOutbound, "boot");
            bool bKeepsOutbound = keepNewOnB ? !secondOutbound : !firstOutbound;
            Assert.True(aKeepsOutbound);   // the one A (smaller id) dialed
            Assert.False(bKeepsOutbound);  // B keeps the same one, inbound for it
        }
        Assert.True(LinkProtocol.KeepNew(a, b, true, "old", false, "new")); // the peer restarted: its new connection wins
        Assert.True(LinkProtocol.KeepNew(a, b, true, "x", true, "x"));      // same direction: the newer one
    }

    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", 43823)]
    [InlineData("192.168.1.5:5000", "192.168.1.5", 5000)]
    [InlineData("laptop", "laptop", 43823)]
    [InlineData("laptop.tail0000.ts.net:1234", "laptop.tail0000.ts.net", 1234)]
    [InlineData("[::1]:99", "::1", 99)]
    [InlineData("fd7a:115c:a1e0::1", "fd7a:115c:a1e0::1", 43823)]
    [InlineData(" 100.64.1.2 ", "100.64.1.2", 43823)]
    public void Addresses_parse(string text, string host, int port)
    {
        Assert.True(LinkProtocol.TryParseAddress(text, LinkProtocol.Port, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:abc")]
    [InlineData("[::1")]
    [InlineData("bad host")]
    public void Bad_addresses_are_refused(string text) => Assert.False(LinkProtocol.TryParseAddress(text, LinkProtocol.Port, out _, out _));

    [Fact]
    public void Address_format_reads_back()
    {
        foreach (var (ip, port) in new[] { (IPAddress.Parse("10.0.0.2"), 43823), (IPAddress.Parse("10.0.0.2"), 5), (IPAddress.IPv6Loopback, 7), (IPAddress.Parse("::ffff:127.0.0.1"), 9) })
        {
            var text = LinkProtocol.FormatAddress(ip, port);
            Assert.True(LinkProtocol.TryParseAddress(text, LinkProtocol.Port, out var h, out var p), text);
            Assert.Equal(port, p);
            Assert.Equal(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, IPAddress.Parse(h));
        }
    }

    [Theory]
    [InlineData("Bericht.pdf", "Bericht.pdf")]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\a.txt", "a.txt")]
    [InlineData("datei.txt:stream", "datei.txt_stream")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1.log", "_LPT1.log")]
    [InlineData("COM9", "_COM9")]
    [InlineData("a<b>c|d?.txt", "a_b_c_d_.txt")]
    [InlineData("name. . .", "name")]
    [InlineData("..", "Datei")]
    [InlineData("", "Datei")]
    [InlineData(null, "Datei")]
    [InlineData("tab\there", "tab_here")]
    public void File_names_are_sanitized(string? name, string expected) => Assert.Equal(expected, LinkProtocol.SanitizeFileName(name));

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        var name = LinkProtocol.SanitizeFileName(new string('x', 400) + ".docx");
        Assert.True(name.Length <= 160);
        Assert.EndsWith(".docx", name);
    }

    [Fact]
    public void Reserved_types_cannot_be_used_by_features()
    {
        foreach (var t in new[] { "hello", "ping", "pair.confirm", "file.chunk", "unpair", "reject" }) Assert.True(LinkProtocol.IsReserved(t), t);
        foreach (var t in new[] { "clipboard", "notification", "media", "status" }) Assert.False(LinkProtocol.IsReserved(t), t);
    }
}

public class LinkDiscoveryTests
{
    [Fact]
    public void Announcements_parse_tolerantly()
    {
        var ok = LinkDiscovery.ParseAnnounce("{\"t\":\"announce\",\"id\":\"0123456789abcdef\",\"name\":\"Laptop\",\"port\":43823,\"proto\":1,\"extra\":[1]}"u8.ToArray());
        Assert.NotNull(ok);
        Assert.Equal(("announce", "0123456789ABCDEF", "Laptop", 43823, 1), ok!.Value);
        var noPort = LinkDiscovery.ParseAnnounce("{\"t\":\"query\",\"id\":\"0123456789abcdef\",\"name\":null}"u8.ToArray());
        Assert.Equal(LinkProtocol.Port, noPort!.Value.Port);
        Assert.Equal(Environment.MachineName, noPort.Value.Name); // no name: falls back, never empty
        Assert.Null(LinkDiscovery.ParseAnnounce("{\"t\":\"announce\",\"id\":\"short\"}"u8.ToArray()));
        Assert.Null(LinkDiscovery.ParseAnnounce("{\"t\":\"other\",\"id\":\"0123456789abcdef\"}"u8.ToArray()));
        Assert.Null(LinkDiscovery.ParseAnnounce("{\"t\":\"announce\",\"id\":\"0123456789abcdef\",\"port\":99999}"u8.ToArray()));
        Assert.Null(LinkDiscovery.ParseAnnounce("nonsense"u8.ToArray()));
        Assert.Null(LinkDiscovery.ParseAnnounce(Array.Empty<byte>()));
    }

    [Fact]
    public void Tailscale_status_fixture()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "Tailscale", "status.json"));
        var (state, peers) = LinkDiscovery.ParseTailscaleStatus(json);
        Assert.Equal("Running", state);
        Assert.Equal(2, peers.Count); // the node without addresses is skipped
        var laptop = peers.Single(p => p.HostName == "example-laptop");
        Assert.True(laptop.Online);
        Assert.Equal(IPAddress.Parse("100.64.10.20"), laptop.Ips[0]); // IPv4 first
        Assert.Equal("example-laptop.tail0000.ts.net", laptop.DnsName);
        Assert.False(peers.Single(p => p.HostName == "example-phone").Online);
        Assert.Empty(LinkDiscovery.ParseTailscaleStatus("{\"BackendState\":\"Stopped\",\"Peer\":null}").Peers);
        Assert.Null(LinkDiscovery.ParseTailscaleStatus("not json").BackendState);
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    public void Tailscale_range(string ip, bool expected) => Assert.Equal(expected, LinkDiscovery.IsTailscale(IPAddress.Parse(ip)));
}

public class DeviceIdentityTests
{
    [Fact]
    public void Identity_is_created_once_and_stored_protected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dynbay-id-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "identity.bin");
        try
        {
            string id, fp;
            using (var first = DeviceIdentity.LoadOrCreate(path))
            {
                id = first.DeviceId; fp = first.Fingerprint;
                Assert.Matches("^[0-9A-F]{16}$", id);
                Assert.Matches("^[0-9A-F]{64}$", fp);
                Assert.True(first.Certificate.HasPrivateKey);
                Assert.StartsWith("CN=DynamicBay", first.Certificate.Subject);
            }
            using (var again = DeviceIdentity.LoadOrCreate(path))
            {
                Assert.Equal(id, again.DeviceId);
                Assert.Equal(fp, again.Fingerprint);
            }
            // DPAPI-wrapped: the file itself is no readable certificate.
            Assert.ThrowsAny<CryptographicException>(() => System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), null));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Broken_identity_file_is_set_aside_and_replaced()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dynbay-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "identity.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        try
        {
            using var id = DeviceIdentity.LoadOrCreate(path);
            Assert.True(id.Certificate.HasPrivateKey);
            Assert.Contains(Directory.GetFiles(dir), f => f.Contains(".corrupt-"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Paired_devices_survive_settings_json()
    {
        var s = new AppSettings { LinkEnabled = true, LinkDeviceName = "Laptop" };
        s.PairedDevices.Add(new PairedDevice { Id = "0123456789ABCDEF", Name = "PC", Fingerprint = new string('A', 64), LastAddresses = { "100.64.1.2" }, PairedAt = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc) });
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, opts), opts)!;
        Assert.True(back.LinkEnabled);
        Assert.Equal("Laptop", back.LinkDeviceName);
        var d = Assert.Single(back.PairedDevices);
        Assert.Equal(("0123456789ABCDEF", "PC", "100.64.1.2", false), (d.Id, d.Name, d.LastAddresses[0], d.Rejected));
        // Older settings without the link fields load with the link off.
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{}", opts)!.LinkEnabled);
    }
}

/// <summary>Two link endpoints in one process, talking over loopback (no other network).</summary>
public class LinkServiceTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dynbay-link-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public void Dispose()
    {
        for (int i = _cleanup.Count - 1; i >= 0; i--) try { _cleanup[i].Dispose(); } catch { }
        try { Directory.Delete(_temp, true); } catch { }
    }

    private DeviceIdentity NewIdentity(string machine)
    {
        var id = DeviceIdentity.Create(machine);
        _cleanup.Add(id);
        return id;
    }

    private LinkOptions Options(long maxFile = 1L << 30, int rateLimit = 5, int port = 0, double confirmSeconds = 120) => new()
    {
        ConfirmTimeout = TimeSpan.FromSeconds(confirmSeconds),
        Port = port, LoopbackOnly = true, LanDiscovery = false, TailscaleDiscovery = false, WatchSystem = false,
        MinBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(500),
        DownloadFolder = Path.Combine(_temp, "downloads"), MaxFileBytes = maxFile, PairingRateLimit = rateLimit,
        Log = _ => { },
    };

    private LinkService Start(DeviceIdentity id, string name, IEnumerable<PairedDevice>? paired = null, LinkOptions? options = null)
    {
        var s = new LinkService(id, name, paired, options ?? Options());
        _cleanup.Add(s);
        s.Start();
        return s;
    }

    private static PairedDevice Know(DeviceIdentity id, LinkService? at = null) => new()
    {
        Id = id.DeviceId, Name = "peer", Fingerprint = id.Fingerprint, PairedAt = DateTime.UtcNow,
        LastAddresses = at is null ? new() : new() { $"127.0.0.1:{at.ListeningPort}" },
    };

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

    /// <summary>A and B already paired (as after a pairing); B knows where A listens and dials it.</summary>
    private async Task<(LinkService A, LinkService B, DeviceIdentity IdA, DeviceIdentity IdB)> Connected(LinkOptions? optsA = null, LinkOptions? optsB = null)
    {
        var idA = NewIdentity("PC");
        var idB = NewIdentity("Laptop");
        var a = Start(idA, "PC", new[] { Know(idB) }, optsA);
        var b = Start(idB, "Laptop", new[] { Know(idA, a) }, optsB);
        await Until(() => a.IsOnline(idB.DeviceId) && b.IsOnline(idA.DeviceId), "connect");
        return (a, b, idA, idB);
    }

    [Fact]
    public async Task Pairing_shows_the_same_code_and_stores_only_after_both_confirm()
    {
        var idA = NewIdentity("PC");
        var idB = NewIdentity("Laptop");
        var a = Start(idA, "PC");
        var b = Start(idB, "Laptop");
        int changedA = 0, changedB = 0;
        a.PairedDevicesChanged += () => Interlocked.Increment(ref changedA);
        b.PairedDevicesChanged += () => Interlocked.Increment(ref changedB);
        var requestedOnA = Next<PairingSession>(h => a.PairingRequested += h);

        a.AllowPairing();
        var sessionB = await b.PairAsync($"127.0.0.1:{a.ListeningPort}");
        var sessionA = await requestedOnA;

        Assert.Matches("^[0-9]{6}$", sessionA.Code);
        Assert.Equal(sessionA.Code, sessionB.Code);
        Assert.Equal(idB.DeviceId, sessionA.PeerId);
        Assert.Equal(idA.DeviceId, sessionB.PeerId);
        Assert.Equal(idB.Fingerprint, sessionA.PeerFingerprint);
        Assert.Equal("Laptop", sessionA.PeerName);
        Assert.True(sessionB.Initiator);
        Assert.False(sessionA.Initiator);

        // Only one side confirmed: nothing is stored yet.
        sessionB.Confirm(true);
        await Task.Delay(500);
        Assert.False(sessionA.Completion.IsCompleted);
        Assert.False(sessionB.Completion.IsCompleted);
        Assert.Empty(a.PairedDevices);
        Assert.Empty(b.PairedDevices);
        Assert.Equal(0, changedA + changedB);

        a.ConfirmPairing(true);
        Assert.Equal(PairingResult.Paired, await sessionA.Completion.WaitAsync(Wait));
        Assert.Equal(PairingResult.Paired, await sessionB.Completion.WaitAsync(Wait));
        Assert.Equal(idB.Fingerprint, Assert.Single(a.PairedDevices).Fingerprint);
        Assert.Equal(idA.Fingerprint, Assert.Single(b.PairedDevices).Fingerprint);
        Assert.False(a.PairingWindowOpen); // one pairing per "Koppeln erlauben"
        await Until(() => a.IsOnline(idB.DeviceId) && b.IsOnline(idA.DeviceId), "online after pairing");

        // The pairing connection carries messages right away.
        var got = Next<LinkMessage>(h => a.MessageReceived += h);
        Assert.True(await b.SendAsync(idA.DeviceId, "status", new { battery = 80 }));
        var msg = await got;
        Assert.Equal("status", msg.Type);
        Assert.Equal(80, msg.Payload!["battery"]!.GetValue<int>());
    }

    [Fact]
    public async Task Declined_pairing_stores_nothing()
    {
        var idA = NewIdentity("PC");
        var idB = NewIdentity("Laptop");
        var a = Start(idA, "PC");
        var b = Start(idB, "Laptop");
        var requestedOnA = Next<PairingSession>(h => a.PairingRequested += h);
        a.AllowPairing();
        var sessionB = await b.PairAsync($"127.0.0.1:{a.ListeningPort}");
        var sessionA = await requestedOnA;
        sessionB.Confirm(true);
        sessionA.Confirm(false);
        Assert.Equal(PairingResult.Declined, await sessionA.Completion.WaitAsync(Wait));
        Assert.Equal(PairingResult.Declined, await sessionB.Completion.WaitAsync(Wait));
        Assert.Empty(a.PairedDevices);
        Assert.Empty(b.PairedDevices);
    }

    [Fact]
    public async Task Unconfirmed_pairing_times_out()
    {
        var a = Start(NewIdentity("PC"), "PC", options: Options(confirmSeconds: 1));
        var b = Start(NewIdentity("Laptop"), "Laptop", options: Options(confirmSeconds: 1));
        var onA = Next<PairingSession>(h => a.PairingRequested += h);
        a.AllowPairing();
        var sessionB = await b.PairAsync($"127.0.0.1:{a.ListeningPort}");
        sessionB.Confirm(true); // only one side
        Assert.Equal(PairingResult.TimedOut, await (await onA).Completion.WaitAsync(Wait));
        Assert.NotEqual(PairingResult.Paired, await sessionB.Completion.WaitAsync(Wait));
        Assert.Empty(a.PairedDevices);
        Assert.Empty(b.PairedDevices);
    }

    [Fact]
    public async Task Pairing_is_refused_without_open_window()
    {
        var a = Start(NewIdentity("PC"), "PC");
        var b = Start(NewIdentity("Laptop"), "Laptop");
        bool asked = false;
        a.PairingRequested += _ => asked = true;
        var ex = await Assert.ThrowsAsync<LinkException>(() => b.PairAsync($"127.0.0.1:{a.ListeningPort}"));
        Assert.Contains(ex.Reason, new[] { "auth", "pair-closed" }); // the unknown certificate is turned away in TLS already
        Assert.False(asked);
        Assert.Empty(a.PairedDevices);
    }

    [Fact]
    public async Task Pairing_attempts_are_rate_limited()
    {
        var a = Start(NewIdentity("PC"), "PC", options: Options(rateLimit: 2));
        var b = Start(NewIdentity("Laptop"), "Laptop");
        a.AllowPairing();
        for (int i = 0; i < 2; i++)
        {
            var onA = Next<PairingSession>(h => a.PairingRequested += h);
            var s = await b.PairAsync($"127.0.0.1:{a.ListeningPort}");
            s.Confirm(false);
            Assert.Equal(PairingResult.Declined, await (await onA).Completion.WaitAsync(Wait));
        }
        var ex = await Assert.ThrowsAsync<LinkException>(() => b.PairAsync($"127.0.0.1:{a.ListeningPort}"));
        Assert.Equal("rate-limited", ex.Reason);
    }

    [Fact]
    public async Task Unpaired_certificate_is_rejected_outside_the_pairing_window()
    {
        var idA = NewIdentity("PC");
        var idC = NewIdentity("Fremd");
        var a = Start(idA, "PC");
        bool online = false;
        a.PeerOnline += _ => online = true;
        // C claims to be paired with A (it knows A's certificate) - A never paired with it.
        var c = Start(idC, "Fremd", new[] { Know(idA, a) });
        await Until(() => c.GetState(idA.DeviceId) == LinkPeerState.Rejected, "C gives up");
        Assert.False(online);
        Assert.Null(a.ConnectionInfo(idC.DeviceId));
        Assert.True(Assert.Single(c.PairedDevices).Rejected);
    }

    [Fact]
    public async Task Pinned_certificate_must_match()
    {
        // B knows A's id but another certificate: B refuses A's server certificate and never connects. That is B's own
        // refusal (another device at the address), so the pairing is not marked rejected - B just keeps trying.
        var idA = NewIdentity("PC");
        var idOld = NewIdentity("PC-alt");
        var idB = NewIdentity("Laptop");
        var a = Start(idA, "PC", new[] { Know(idB) });
        var stale = Know(idA, a);
        stale.Fingerprint = idOld.Fingerprint;
        var b = Start(idB, "Laptop", new[] { stale });
        await Task.Delay(2500);
        Assert.False(a.IsOnline(idB.DeviceId));
        Assert.False(b.IsOnline(idA.DeviceId));
        Assert.NotEqual(LinkPeerState.Rejected, b.GetState(idA.DeviceId));
    }

    [Fact]
    public async Task Messages_round_trip_through_handlers()
    {
        var (a, b, idA, idB) = await Connected();
        var viaHandler = new TaskCompletionSource<LinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (b.RegisterHandler("clipboard", m => viaHandler.TrySetResult(m)))
        {
            Assert.True(await a.SendAsync(idB.DeviceId, "clipboard", new JsonObject { ["text"] = "Grüße" }));
            var m = await viaHandler.Task.WaitAsync(Wait);
            Assert.Equal(idA.DeviceId, m.PeerId);
            Assert.Equal("PC", m.PeerName);
            Assert.Equal("Grüße", m.Payload!["text"]!.GetValue<string>());
        }
        Assert.Throws<ArgumentException>(() => a.RegisterHandler("file.chunk", _ => { }));
        await Assert.ThrowsAsync<ArgumentException>(() => a.SendAsync(idB.DeviceId, "ping"));
        await Assert.ThrowsAsync<ArgumentException>(() => a.SendAsync(idB.DeviceId, "big", new { s = new string('x', LinkProtocol.MaxFrame) }));
        Assert.False(await a.SendAsync("0000000000000000", "status"));
        // The connection still works after the refused ones.
        var back = Next<LinkMessage>(h => a.MessageReceived += h);
        Assert.True(await b.SendAsync(idA.DeviceId, "status", null));
        Assert.Null((await back).Payload);
    }

    [Fact]
    public async Task File_arrives_intact_under_a_sanitized_name()
    {
        var (a, b, _, idB) = await Connected();
        var data = RandomNumberGenerator.GetBytes(LinkProtocol.ChunkSize * 3 + 1234);
        Directory.CreateDirectory(_temp);
        var src = Path.Combine(_temp, "quelle.bin");
        File.WriteAllBytes(src, data);
        var target = Path.Combine(_temp, "ziel");

        var offered = Next<IncomingFileOffer>(h => b.FileOffered += h);
        var received = Next<(IncomingFileOffer, string)>(h => b.FileReceived += (o, p) => h((o, p)));
        double lastProgress = 0;
        var send = a.SendFileAsync(idB.DeviceId, src, "..\\..\\Windows\\CON.txt", new Progress<double>(p => lastProgress = p), CancellationToken.None);
        var offer = await offered;
        Assert.Equal("_CON.txt", offer.FileName);
        Assert.Equal(data.Length, offer.Size);
        Assert.True(await offer.Accept(target));

        Assert.Equal(FileTransferResult.Done, await send.WaitAsync(Wait));
        var (_, path) = await received;
        Assert.Equal(Path.Combine(target, "_CON.txt"), path);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(target, "*.part"));
        await Until(() => lastProgress >= 1, "progress");

        // Same name again: the first file stays, the second gets a number.
        var offered2 = Next<IncomingFileOffer>(h => b.FileOffered += h);
        var send2 = a.SendFileAsync(idB.DeviceId, src, "..\\..\\Windows\\CON.txt", null, CancellationToken.None);
        Assert.True(await (await offered2).Accept(target));
        Assert.Equal(FileTransferResult.Done, await send2.WaitAsync(Wait));
        Assert.True(File.Exists(Path.Combine(target, "_CON (2).txt")));
    }

    [Fact]
    public async Task Files_over_the_limit_are_declined()
    {
        var (a, b, _, idB) = await Connected(optsB: Options(maxFile: 1000));
        Directory.CreateDirectory(_temp);
        var big = Path.Combine(_temp, "gross.bin");
        File.WriteAllBytes(big, new byte[5000]);
        bool asked = false;
        b.FileOffered += _ => asked = true;
        // Over the service limit: declined without asking.
        Assert.Equal(FileTransferResult.TooLarge, await a.SendFileAsync(idB.DeviceId, big).WaitAsync(Wait));
        Assert.False(asked);

        // Under the service limit but over the one passed to Accept.
        var small = Path.Combine(_temp, "klein.bin");
        File.WriteAllBytes(small, new byte[800]);
        var offered = Next<IncomingFileOffer>(h => b.FileOffered += h);
        var send = a.SendFileAsync(idB.DeviceId, small);
        Assert.False(await (await offered).Accept(Path.Combine(_temp, "ziel"), maxBytes: 100));
        Assert.Equal(FileTransferResult.TooLarge, await send.WaitAsync(Wait));

        // Declined by the user.
        var offered2 = Next<IncomingFileOffer>(h => b.FileOffered += h);
        var send2 = a.SendFileAsync(idB.DeviceId, small);
        (await offered2).Decline();
        Assert.Equal(FileTransferResult.Declined, await send2.WaitAsync(Wait));
        Assert.False(Directory.Exists(Path.Combine(_temp, "ziel")) && Directory.EnumerateFileSystemEntries(Path.Combine(_temp, "ziel")).Any());
    }

    [Fact]
    public async Task Reconnects_after_the_other_side_restarts()
    {
        var (a, b, idA, idB) = await Connected();
        var offline = Next<PairedDevice>(h => a.PeerOffline += h);
        b.Stop();
        Assert.Equal(idB.DeviceId, (await offline).Id);
        Assert.NotEqual(LinkPeerState.Online, a.GetState(idB.DeviceId));

        var online = Next<PairedDevice>(h => a.PeerOnline += h);
        var b2 = Start(idB, "Laptop", b.PairedDevices); // same identity, new process, new port
        Assert.Equal(idB.DeviceId, (await online).Id);
        await Until(() => b2.IsOnline(idA.DeviceId), "b2 online");
        // A learned B's new port from the hello.
        Assert.Equal($"127.0.0.1:{b2.ListeningPort}", Assert.Single(a.PairedDevices).LastAddresses[0]);

        var got = Next<LinkMessage>(h => b2.MessageReceived += h);
        Assert.True(await a.SendAsync(idB.DeviceId, "status", new { ok = true }));
        Assert.True((await got).Payload!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Both_dialing_ends_with_one_shared_connection()
    {
        for (int round = 0; round < 3; round++)
        {
            var idA = NewIdentity("PC");
            var idB = NewIdentity("Laptop");
            int portA = FreePort(), portB = FreePort();
            // Both know each other's address and dial right after starting.
            var a = new LinkService(idA, "PC", new[] { new PairedDevice { Id = idB.DeviceId, Fingerprint = idB.Fingerprint, LastAddresses = { $"127.0.0.1:{portB}" } } }, Options(port: portA));
            var b = new LinkService(idB, "Laptop", new[] { new PairedDevice { Id = idA.DeviceId, Fingerprint = idA.Fingerprint, LastAddresses = { $"127.0.0.1:{portA}" } } }, Options(port: portB));
            _cleanup.Add(a); _cleanup.Add(b);
            a.Start(); b.Start();
            await Until(() => a.IsOnline(idB.DeviceId) && b.IsOnline(idA.DeviceId), "both online");
            await Task.Delay(1500); // let any duplicate connection be sorted out
            var onA = a.ConnectionInfo(idB.DeviceId)!.Value;
            var onB = b.ConnectionInfo(idA.DeviceId)!.Value;
            Assert.Equal(onA.Local!.Port, onB.Remote!.Port);
            Assert.Equal(onA.Remote!.Port, onB.Local!.Port);
            Assert.NotEqual(onA.Outbound, onB.Outbound);
            Assert.Contains(onA.Protocol, new[] { System.Security.Authentication.SslProtocols.Tls13, System.Security.Authentication.SslProtocols.Tls12 });
            var got = Next<LinkMessage>(h => b.MessageReceived += h);
            Assert.True(await a.SendAsync(idB.DeviceId, "status"));
            await got;
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Unpair_closes_and_the_other_side_sees_rejected()
    {
        var (a, b, idA, idB) = await Connected();
        var rejected = Next<(string, LinkPeerState)>(h => b.PeerStateChanged += (id, st) => { if (st == LinkPeerState.Rejected) h((id, st)); });
        await a.UnpairAsync(idB.DeviceId);
        Assert.Empty(a.PairedDevices);
        Assert.Equal(idA.DeviceId, (await rejected).Item1);
        Assert.Equal(LinkPeerState.Rejected, b.GetState(idA.DeviceId));
        Assert.True(Assert.Single(b.PairedDevices).Rejected);
        await Task.Delay(800); // B does not keep trying
        Assert.False(a.IsOnline(idB.DeviceId));
        Assert.False(b.IsOnline(idA.DeviceId));
    }
}
