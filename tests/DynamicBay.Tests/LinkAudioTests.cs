using System.IO;
using System.Net;
using System.Text.Json.Nodes;
using DynamicBay.Core;
using DynamicBay.Services.Link;
using DynamicBay.Services.Link.Audio;

namespace DynamicBay.Tests;

/// <summary>"Ton an Gerät" without sound cards: jitter buffer, codec, conversion, frames and the mute rules.</summary>
public class LinkAudioPureTests
{
    private const int Rate = 48000;

    private static float[] Sine(int frames, double hz, double amp = 0.5, int channels = 2, int rate = Rate, int offset = 0)
    {
        var s = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++) s[f * channels + c] = (float)(amp * Math.Sin(2 * Math.PI * hz * (f + offset) / rate));
        return s;
    }

    /// <summary>Largest step between two neighbouring samples of one channel: a click shows as a big jump.</summary>
    private static double MaxStep(IReadOnlyList<float> s, int channels = 2)
    {
        double max = 0;
        for (int i = channels; i < s.Count; i++) max = Math.Max(max, Math.Abs(s[i] - s[i - channels]));
        return max;
    }

    /// <summary>Normalized correlation of the left channels at the best lag up to <paramref name="maxLag"/> frames.</summary>
    private static double BestCorrelation(float[] a, float[] b, int maxLag, int channels = 2)
    {
        double best = 0;
        int n = Math.Min(a.Length, b.Length) / channels;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int f = 0; f + lag < n; f++)
            {
                double x = a[f * channels], y = b[(f + lag) * channels];
                ab += x * y; aa += x * x; bb += y * y;
            }
            if (aa > 0 && bb > 0) best = Math.Max(best, ab / Math.Sqrt(aa * bb));
        }
        return best;
    }

    // ---------- jitter buffer ----------

    [Fact]
    public void Buffer_waits_for_the_target_then_plays_in_order()
    {
        var jb = new AudioJitterBuffer(Rate, 2, targetMs: 100);
        var out10 = new float[480 * 2];
        jb.Push(Sine(960 * 4, 440)); // 80 ms: below the target
        jb.Read(out10);
        Assert.False(jb.Playing);
        Assert.All(out10, v => Assert.Equal(0f, v));
        jb.Push(Sine(960, 440, offset: 960 * 4)); // 100 ms now
        jb.Read(out10);
        Assert.True(jb.Playing);
        Assert.InRange(jb.LevelMs, 85, 95);
    }

    [Fact]
    public void Steady_stream_stays_near_the_target_without_clicks()
    {
        var jb = new AudioJitterBuffer(Rate, 2, targetMs: 100);
        var played = new List<float>();
        var block = new float[480 * 2];
        int pushed = 0;
        // 20 ms packets in bursts of 3 (60 ms), read in 10 ms like a sound card: 10 s.
        for (int ms = 0; ms < 10_000; ms += 10)
        {
            if (ms % 60 == 0) for (int k = 0; k < 3; k++) { jb.Push(Sine(960, 440, offset: pushed)); pushed += 960; }
            jb.Read(block);
            if (jb.Playing) played.AddRange(block);
        }
        Assert.Equal(0, jb.Underruns);
        Assert.InRange(jb.LevelMs, 50, 170);
        Assert.True(MaxStep(played) < 0.05, $"step {MaxStep(played)}"); // 440 Hz at 0.5: at most ~0.029 per sample
    }

    [Fact]
    public void Running_dry_fades_out_and_back_in()
    {
        var jb = new AudioJitterBuffer(Rate, 2, targetMs: 100);
        var played = new List<float>();
        var block = new float[480 * 2];
        jb.Push(Sine(Rate / 5, 440)); // 200 ms, then nothing
        for (int i = 0; i < 40; i++) { jb.Read(block); played.AddRange(block); }
        Assert.Equal(1, jb.Underruns);
        Assert.False(jb.Playing);
        // Comes back: waits for the target, then fades in.
        jb.Push(Sine(Rate / 5, 440, offset: Rate / 5));
        for (int i = 0; i < 40; i++) { jb.Read(block); played.AddRange(block); }
        Assert.True(MaxStep(played) < 0.05, $"step {MaxStep(played)}");
        Assert.Contains(played, v => Math.Abs(v) > 0.4f); // it really played
    }

    [Fact]
    public void Too_much_is_dropped_with_a_fade()
    {
        var jb = new AudioJitterBuffer(Rate, 2, targetMs: 100, maxMs: 400);
        var played = new List<float>();
        var block = new float[480 * 2];
        jb.Push(Sine(Rate, 440)); // a whole second at once (a stall that ended), then in step again
        for (int i = 0; i < 20; i++) { jb.Read(block); played.AddRange(block); jb.Push(Sine(480, 440, offset: Rate + i * 480)); }
        Assert.True(jb.Dropped > 0);
        Assert.InRange(jb.LevelMs, 60, 140);
        Assert.True(MaxStep(played) < 0.05, $"step {MaxStep(played)}");
    }

    [Theory]
    [InlineData(1.002)]  // sender's clock 0.2 % fast
    [InlineData(0.998)]  // 0.2 % slow
    public void Clock_drift_is_absorbed_by_the_rate(double drift)
    {
        var jb = new AudioJitterBuffer(Rate, 2, targetMs: 100);
        var block = new float[480 * 2];
        double owed = 0;
        int pushed = 0;
        // 60 s: the sender produces drift x real time, the sound card reads exactly real time.
        for (int ms = 0; ms < 60_000; ms += 10)
        {
            owed += 480 * drift;
            int n = (int)owed;
            owed -= n;
            jb.Push(Sine(n, 440, offset: pushed));
            pushed += n;
            jb.Read(block);
        }
        Assert.Equal(0, jb.Underruns);
        Assert.Equal(0, jb.Dropped);
        Assert.InRange(jb.LevelMs, 70, 160);
        Assert.InRange(jb.Rate, 0.995, 1.005);
    }

    // ---------- codec and conversion ----------

    [Fact]
    public void Opus_round_trip_keeps_a_sine()
    {
        var enc = new OpusFrameEncoder();
        var dec = new OpusFrameDecoder();
        var input = Sine(Rate, 440, 0.3); // 1 s
        var output = new List<float>();
        var packet = new byte[OpusFrameEncoder.MaxPacket];
        var pcm = new float[OpusFrameEncoder.FrameFloats];
        int bytes = 0;
        for (int i = 0; i + OpusFrameEncoder.FrameFloats <= input.Length; i += OpusFrameEncoder.FrameFloats)
        {
            int len = enc.Encode(input.AsSpan(i, OpusFrameEncoder.FrameFloats), packet);
            Assert.InRange(len, 1, OpusFrameEncoder.MaxPacket);
            bytes += len;
            int n = dec.Decode(packet.AsSpan(0, len), pcm);
            Assert.Equal(OpusFrameEncoder.FrameSamples, n);
            output.AddRange(pcm);
        }
        // Opus delays by a few ms (look-ahead): compare at the best lag.
        Assert.True(BestCorrelation(input, output.ToArray(), maxLag: 960) > 0.98);
        Assert.InRange(bytes * 8, 40_000, 260_000); // about 160 kbit/s or less for a plain tone
    }

    [Fact]
    public void Convert_resamples_44k_to_48k_and_keeps_the_pitch()
    {
        var conv = new AudioConvert(44100, 2);
        var input = Sine(44100, 1000, 0.5, rate: 44100);
        var output = new List<float>();
        // Odd block sizes like a sound card.
        for (int i = 0; i < input.Length; i += 441 * 2) conv.Convert(input.AsSpan(i, Math.Min(441 * 2, input.Length - i)), output);
        Assert.InRange(output.Count / 2, 47_990, 48_010);
        var ideal = Sine(output.Count / 2, 1000, 0.5);
        Assert.True(BestCorrelation(ideal, output.ToArray(), maxLag: 5) > 0.99);
        Assert.True(MaxStep(output) < 0.08);
    }

    [Fact]
    public void Convert_mixes_mono_and_surround_down_to_stereo()
    {
        var mono = new List<float>();
        new AudioConvert(48000, 1).Convert(new[] { 0.25f, -0.5f }, mono);
        Assert.Equal(new[] { 0.25f, 0.25f, -0.5f, -0.5f }, mono);

        var six = new List<float>();
        new AudioConvert(48000, 6).Convert(new[] { 1f, -1f, 0f, 0f, 0f, 0f }, six);
        Assert.Equal(2, six.Count);
        Assert.True(six[0] > 0.5f && six[1] < -0.5f);
    }

    // ---------- frames ----------

    [Fact]
    public void Sound_frames_round_trip()
    {
        var frame = LinkAudioProtocol.SoundFrame(7, 123456, new byte[] { 1, 2, 3 });
        Assert.True(LinkAudioProtocol.TryReadSound(frame, out var seq, out var tick, out var packet));
        Assert.Equal((7u, 123456u), (seq, tick));
        Assert.Equal(new byte[] { 1, 2, 3 }, packet.ToArray());
        Assert.False(LinkAudioProtocol.TryReadSound(LinkAudioProtocol.KeepAliveFrame, out _, out _, out _));
        Assert.Null(LinkAudioProtocol.ReadHeader(frame));
    }

    [Theory]
    [InlineData("{\"v\":1,\"codec\":\"opus\",\"rate\":48000,\"ch\":2,\"frame\":960}", true)]
    [InlineData("{\"v\":2}", true)]                      // missing fields: the defaults
    [InlineData("{\"codec\":\"pcm\"}", false)]
    [InlineData("{\"rate\":44100}", false)]
    [InlineData("{\"ch\":6,\"extra\":[1]}", false)]
    public void Offers_are_read_tolerantly(string json, bool ok) =>
        Assert.Equal(ok, LinkAudioProtocol.AcceptsOffer(LinkAudioProtocol.ReadHeader(LinkAudioProtocol.HeaderFrame((JsonObject)JsonNode.Parse(json)!))));

    [Fact]
    public void Broken_headers_are_none() =>
        Assert.Null(LinkAudioProtocol.ReadHeader(new byte[] { LinkAudioProtocol.Header, (byte)'{', (byte)'x' }));

    // ---------- mute ----------

    internal sealed class FakeOutput : IOutputMute
    {
        public readonly Dictionary<string, bool> Muted = new();
        public readonly List<string> Calls = new();
        public bool? IsMuted(string id) => Muted.TryGetValue(id, out var m) ? m : null;
        public void SetMuted(string id, bool muted) { Muted[id] = muted; Calls.Add($"{id}={muted}"); }
    }

    internal sealed class MemoryBackup : IMuteBackup
    {
        public (string Id, bool WasMuted)? Saved;
        public (string Id, bool WasMuted)? Load() => Saved;
        public void Save(string id, bool wasMuted) => Saved = (id, wasMuted);
        public void Clear() => Saved = null;
    }

    [Fact]
    public void Mute_is_restored_exactly()
    {
        var output = new FakeOutput { Muted = { ["spk"] = false } };
        var backup = new MemoryBackup();
        var guard = new LocalMuteGuard(output, backup);
        guard.Engage("spk");
        Assert.True(output.Muted["spk"]);
        Assert.Equal(("spk", false), backup.Saved);
        guard.Release();
        Assert.False(output.Muted["spk"]);
        Assert.Null(backup.Saved);
        guard.Release(); // twice: nothing more
        Assert.Equal(new[] { "spk=True", "spk=False" }, output.Calls);
    }

    [Fact]
    public void Output_muted_by_the_user_is_never_unmuted()
    {
        var output = new FakeOutput { Muted = { ["spk"] = true } };
        var backup = new MemoryBackup();
        var guard = new LocalMuteGuard(output, backup);
        guard.Engage("spk");
        guard.Release();
        Assert.True(output.Muted["spk"]);
        Assert.Empty(output.Calls);
        Assert.Null(backup.Saved);
    }

    [Fact]
    public void New_default_output_takes_over_the_mute()
    {
        var output = new FakeOutput { Muted = { ["spk"] = false, ["hdmi"] = false } };
        var guard = new LocalMuteGuard(output, new MemoryBackup());
        guard.Engage("spk");
        guard.Engage("hdmi");
        Assert.False(output.Muted["spk"]);
        Assert.True(output.Muted["hdmi"]);
        Assert.Equal("hdmi", guard.Engaged);
        guard.Release();
        Assert.False(output.Muted["hdmi"]);
    }

    [Fact]
    public void Gone_output_is_left_alone()
    {
        var output = new FakeOutput();
        var guard = new LocalMuteGuard(output, new MemoryBackup());
        guard.Engage("usb");
        Assert.Null(guard.Engaged);
        guard.Release();
        Assert.Empty(output.Calls);
    }

    [Fact]
    public void Leftover_after_a_crash_is_undone_once()
    {
        var output = new FakeOutput { Muted = { ["spk"] = true } };
        var backup = new MemoryBackup { Saved = ("spk", false) };
        Assert.True(new LocalMuteGuard(output, backup).RecoverLeftover());
        Assert.False(output.Muted["spk"]);
        Assert.Null(backup.Saved);
        Assert.False(new LocalMuteGuard(output, backup).RecoverLeftover());

        // The user unmuted it meanwhile (or it is gone): nothing to do.
        var unmuted = new FakeOutput { Muted = { ["spk"] = false } };
        Assert.False(new LocalMuteGuard(unmuted, new MemoryBackup { Saved = ("spk", false) }).RecoverLeftover());
        Assert.Empty(unmuted.Calls);
    }

    [Fact]
    public void Mute_backup_file_round_trips_and_tolerates_junk()
    {
        string path = Path.Combine(Path.GetTempPath(), "dynbay-mute-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var file = new FileMuteBackup(path);
            Assert.Null(file.Load());
            file.Save("{0.0.0.00000000}.{abc}", false);
            Assert.Equal(("{0.0.0.00000000}.{abc}", false), file.Load());
            File.WriteAllText(path, "{\"id\": 5}");
            Assert.Null(file.Load());
            File.WriteAllText(path, "kein json");
            Assert.Null(file.Load());
            file.Clear();
            Assert.False(File.Exists(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}

/// <summary>Streams and the sound feature on two link endpoints in one process (loopback only, no sound cards).</summary>
public class LinkAudioStreamTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public void Dispose()
    {
        for (int i = _cleanup.Count - 1; i >= 0; i--) try { _cleanup[i].Dispose(); } catch { }
    }

    private T Keep<T>(T d) where T : IDisposable { _cleanup.Add(d); return d; }

    private static LinkOptions Options(int port = 0) => new()
    {
        Port = port, LoopbackOnly = true, LanDiscovery = false, TailscaleDiscovery = false, WatchSystem = false,
        MinBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(500),
        DownloadFolder = Path.Combine(Path.GetTempPath(), "dynbay-audio-dl"), Log = _ => { },
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

    private sealed record Pair(LinkService A, LinkService B, DeviceIdentity IdA, DeviceIdentity IdB, AppSettings SA, AppSettings SB);

    private async Task<Pair> Connected(Action<Pair>? features = null)
    {
        var idA = Keep(DeviceIdentity.Create("PC"));
        var idB = Keep(DeviceIdentity.Create("Laptop"));
        int portA = FreePort();
        var a = Keep(new LinkService(idA, "PC", new[] { new PairedDevice { Id = idB.DeviceId, Name = "Laptop", Fingerprint = idB.Fingerprint } }, Options(portA)));
        var b = Keep(new LinkService(idB, "Laptop", new[] { new PairedDevice { Id = idA.DeviceId, Name = "PC", Fingerprint = idA.Fingerprint, LastAddresses = { $"127.0.0.1:{portA}" } } }, Options()));
        var pair = new Pair(a, b, idA, idB, new AppSettings(), new AppSettings());
        features?.Invoke(pair);
        a.Start();
        b.Start();
        await Until(() => a.IsOnline(idB.DeviceId) && b.IsOnline(idA.DeviceId), "connect");
        return pair;
    }

    // ---------- streams ----------

    [Fact]
    public async Task Stream_carries_frames_both_ways_next_to_the_main_connection()
    {
        var p = await Connected();
        var opened = new TaskCompletionSource<LinkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.A.StreamOpened += s => opened.TrySetResult(s);
        using var fromB = await p.B.OpenStreamAsync(p.IdA.DeviceId, "audio");
        using var atA = await opened.Task.WaitAsync(Wait);
        Assert.Equal(("audio", p.IdB.DeviceId, "Laptop", false), (atA.Purpose, atA.PeerId, atA.PeerName, atA.Outbound));
        Assert.True(fromB.Outbound);

        await fromB.SendAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await atA.ReceiveAsync().WaitAsync(Wait));
        var big = fromB.ReceiveAsync(); // read while it is written: a full socket buffer would wait for the reader
        await atA.SendAsync(new byte[LinkProtocol.MaxFrame]).WaitAsync(Wait);
        Assert.Equal(LinkProtocol.MaxFrame, (await big.WaitAsync(Wait))!.Length);
        // The main connection still works and is another one.
        var got = new TaskCompletionSource<LinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = p.A.RegisterHandler("probe", m => got.TrySetResult(m));
        Assert.True(await p.B.SendAsync(p.IdA.DeviceId, "probe", new { x = 1 }));
        await got.Task.WaitAsync(Wait);
        Assert.Equal(1, p.A.StreamCount);

        fromB.Dispose();
        Assert.Null(await atA.ReceiveAsync().WaitAsync(Wait));
        await Until(() => p.A.StreamCount == 0 && p.B.StreamCount == 0, "streams forgotten");
        Assert.True(p.A.IsOnline(p.IdB.DeviceId)); // closing a stream leaves the device online
    }

    [Fact]
    public async Task Streams_close_when_the_device_goes_offline()
    {
        var p = await Connected();
        var opened = new TaskCompletionSource<LinkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.A.StreamOpened += s => opened.TrySetResult(s);
        var fromB = await p.B.OpenStreamAsync(p.IdA.DeviceId, "audio");
        var atA = await opened.Task.WaitAsync(Wait);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fromB.Ended += _ => ended.TrySetResult(true);
        p.A.Stop();
        await ended.Task.WaitAsync(Wait);
        Assert.Null(await fromB.ReceiveAsync().WaitAsync(Wait));
        Assert.True(atA.IsClosed);
        await Assert.ThrowsAsync<System.IO.IOException>(() => fromB.SendAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task Streams_need_an_online_device_and_a_subscriber()
    {
        var p = await Connected();
        var ex = await Assert.ThrowsAsync<LinkException>(() => p.B.OpenStreamAsync("0123456789ABCDEF", "audio"));
        Assert.Equal("offline", ex.Reason);
        await Assert.ThrowsAsync<ArgumentException>(() => p.B.OpenStreamAsync(p.IdA.DeviceId, "Audio Stream!"));

        // Nobody at A takes it: closed right away.
        var s = await p.B.OpenStreamAsync(p.IdA.DeviceId, "audio");
        Assert.Null(await s.ReceiveAsync().WaitAsync(Wait));
    }

    // ---------- the sound feature ----------

    private sealed class Rig
    {
        public LinkAudioFeature Sender = null!, Receiver = null!;
        public LinkAudioPureTests.FakeOutput Output = new() { Muted = { ["dev-tone"] = false } };
        public LinkAudioPureTests.MemoryBackup Backup = new();
        public List<NullAudioSink> Sinks = new();
    }

    private async Task<(Pair P, Rig R)> AudioPair(Action<Pair>? before = null)
    {
        var rig = new Rig();
        var p = await Connected(x =>
        {
            before?.Invoke(x);
            LinkAudioOptions Opts() => new()
            {
                Source = () => new ToneAudioSource(),
                Sink = () => { var s = new NullAudioSink(); lock (rig.Sinks) rig.Sinks.Add(s); return s; },
                Mute = new LocalMuteGuard(rig.Output, rig.Backup),
                KeepAlive = TimeSpan.FromMilliseconds(200), DeadAfter = TimeSpan.FromSeconds(2), SamePc = true, Log = _ => { },
            };
            rig.Sender = Keep(new LinkAudioFeature(x.B, x.SB, Opts()));
            rig.Receiver = Keep(new LinkAudioFeature(x.A, x.SA, Opts()));
        });
        return (p, rig);
    }

    [Fact]
    public async Task Sound_flows_and_the_mute_comes_back()
    {
        var (p, r) = await AudioPair();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Receiver.ReceivingStarted += (_, name) => started.TrySetResult(name);
        Assert.Null(await r.Sender.StartSendingAsync(p.IdA.DeviceId));
        Assert.Equal(p.IdA.DeviceId, r.Sender.SendingTo);
        Assert.Equal("Laptop", await started.Task.WaitAsync(Wait));
        Assert.True(r.Output.Muted["dev-tone"]); // muted here while it plays there

        await Until(() => r.Receiver.FramesReceived >= 25 && r.Receiver.ReceiveBuffer?.Playing == true, "sound arrives and plays");
        Assert.InRange(r.Receiver.ReceiveBuffer!.LevelMs, 20, 400);

        var ended = new TaskCompletionSource<AudioForwardEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Sender.SendingEnded += (_, _, why) => ended.TrySetResult(why);
        var gone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Receiver.ReceivingEnded += (_, _) => gone.TrySetResult(true);
        r.Sender.StopSending();
        Assert.Equal(AudioForwardEnd.Stopped, await ended.Task.WaitAsync(Wait));
        await gone.Task.WaitAsync(Wait);
        Assert.False(r.Output.Muted["dev-tone"]);
        Assert.Null(r.Backup.Saved);
        Assert.Null(r.Sender.SendingTo);
        await Until(() => r.Receiver.ReceivingFrom is null, "receiver idle");
    }

    [Fact]
    public async Task Receiver_switch_off_refuses_and_nothing_is_muted()
    {
        var (p, r) = await AudioPair(x => x.SA.LinkShareAudio = false);
        Assert.Equal(AudioForwardEnd.Refused, await r.Sender.StartSendingAsync(p.IdA.DeviceId));
        Assert.Null(r.Sender.SendingTo);
        Assert.Empty(r.Output.Calls);
        Assert.Null(r.Receiver.ReceivingFrom);
    }

    [Fact]
    public async Task Sender_switch_off_sends_nothing()
    {
        var (p, r) = await AudioPair(x => x.SB.LinkShareAudio = false);
        Assert.Equal(AudioForwardEnd.Refused, await r.Sender.StartSendingAsync(p.IdA.DeviceId));
        Assert.Equal(0, p.A.StreamCount);
        Assert.Empty(r.Output.Calls);
    }

    [Fact]
    public async Task Receiver_turning_the_switch_off_ends_it_and_says_why()
    {
        var (p, r) = await AudioPair();
        Assert.Null(await r.Sender.StartSendingAsync(p.IdA.DeviceId));
        var ended = new TaskCompletionSource<AudioForwardEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Sender.SendingEnded += (_, _, why) => ended.TrySetResult(why);
        p.SA.LinkShareAudio = false;
        Assert.Equal(AudioForwardEnd.Refused, await ended.Task.WaitAsync(Wait));
        Assert.False(r.Output.Muted["dev-tone"]);
    }

    [Fact]
    public async Task Link_lost_brings_the_sound_back()
    {
        var (p, r) = await AudioPair();
        Assert.Null(await r.Sender.StartSendingAsync(p.IdA.DeviceId));
        Assert.True(r.Output.Muted["dev-tone"]);
        var ended = new TaskCompletionSource<AudioForwardEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Sender.SendingEnded += (_, _, why) => ended.TrySetResult(why);
        p.A.Stop(); // the PC is gone
        Assert.Equal(AudioForwardEnd.LinkLost, await ended.Task.WaitAsync(Wait));
        Assert.False(r.Output.Muted["dev-tone"]);
        Assert.Null(r.Backup.Saved);
    }

    [Fact]
    public async Task Receiver_quitting_is_not_a_lost_connection()
    {
        var (p, r) = await AudioPair();
        Assert.Null(await r.Sender.StartSendingAsync(p.IdA.DeviceId));
        var ended = new TaskCompletionSource<AudioForwardEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Sender.SendingEnded += (_, _, why) => ended.TrySetResult(why);
        r.Receiver.Dispose();
        Assert.Equal(AudioForwardEnd.RemoteStopped, await ended.Task.WaitAsync(Wait));
        Assert.True(p.B.IsOnline(p.IdA.DeviceId));
        Assert.False(r.Output.Muted["dev-tone"]);
    }

    [Fact]
    public async Task Disposing_the_feature_restores_the_mute()
    {
        var (p, r) = await AudioPair();
        Assert.Null(await r.Sender.StartSendingAsync(p.IdA.DeviceId));
        r.Sender.Dispose();
        Assert.False(r.Output.Muted["dev-tone"]);
        Assert.Null(r.Sender.SendingTo);
    }
}
