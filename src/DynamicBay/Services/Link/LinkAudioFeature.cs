using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DynamicBay.Core;
using DynamicBay.Services.Link.Audio;

namespace DynamicBay.Services.Link;

/// <summary>Why the sound came back (or never left).</summary>
public enum AudioForwardEnd
{
    /// <summary>Chosen here: a local output, or the device again.</summary>
    Stopped,
    /// <summary>"Ton" is off on the other device (or here).</summary>
    Refused,
    /// <summary>The connection is gone (device offline, sleep, network).</summary>
    LinkLost,
    /// <summary>The other device ended it (its switch went off, it quit).</summary>
    RemoteStopped,
    /// <summary>Capturing or playing did not work.</summary>
    Failed,
}

/// <summary>How the feature captures, plays and mutes (the real devices in the app, fakes in tests and dev mode).</summary>
public sealed class LinkAudioOptions
{
    public required Func<IAudioSource> Source { get; init; }
    public required Func<IAudioSink> Sink { get; init; }
    public required LocalMuteGuard Mute { get; init; }
    /// <summary>Jitter buffer target per device (ms): about 100 on the LAN, more over a Tailscale relay.</summary>
    public Func<string, int> TargetMs { get; init; } = _ => 100;
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>A stream without any frame for this long is dead.</summary>
    public TimeSpan DeadAfter { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Statistics in the log this often (frames, buffer, delay).</summary>
    public TimeSpan StatsEvery { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Both devices are one PC (test instances): the sender's tick gives the real network delay.</summary>
    public bool SamePc { get; init; }
    public Action<string> Log { get; init; } = DynamicBay.Core.Log.Info;
}

/// <summary>
/// Ton (Einstellungen → Geräte → Ton): the sound of this PC plays on a paired device. The sender captures the system
/// sound (WASAPI loopback of its default output), encodes 20 ms Opus frames and sends them over a stream of its own
/// (<see cref="LinkService.OpenStreamAsync"/>), so files and messages never hold it up; its own output is muted
/// meanwhile and gets its state back afterwards (<see cref="LocalMuteGuard"/>). The receiver decodes into a jitter
/// buffer that its default output reads. Both switches count: the sender sends nothing while its switch is off, the
/// receiver answers "off". Either side ends it by closing the stream; offline closes it too. No WPF in here.
/// </summary>
public sealed class LinkAudioFeature : LinkFeature
{
    private readonly LinkAudioOptions _opts;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private SendSession? _send;
    private ReceiveSession? _receive;

    public LinkAudioFeature(LinkService link, AppSettings settings, LinkAudioOptions options) : base(link, settings)
    {
        _opts = options;
        _context = SynchronizationContext.Current;
        link.StreamOpened += OnStreamOpened;
        settings.PropertyChanged += OnSettingChanged;
    }

    public override bool Enabled => Settings.LinkShareAudio;

    /// <summary>The device this PC's sound goes to (null: plays here).</summary>
    public string? SendingTo { get { lock (_gate) return _send?.PeerId; } }
    /// <summary>The device whose sound plays here (null: none).</summary>
    public string? ReceivingFrom { get { lock (_gate) return _receive?.PeerId; } }
    /// <summary>The buffer of the sound being received (diagnostics, tests).</summary>
    public AudioJitterBuffer? ReceiveBuffer { get { lock (_gate) return _receive?.Buffer; } }
    /// <summary>Frames sent and received in the current sessions (diagnostics, tests).</summary>
    public long FramesSent { get { lock (_gate) return _send?.Frames ?? 0; } }
    public long FramesReceived { get { lock (_gate) return _receive?.Frames ?? 0; } }

    /// <summary>Sound from here plays on that device now (peer id, name).</summary>
    public event Action<string, string>? SendingStarted;
    /// <summary>The sound is back here (peer id, name, why).</summary>
    public event Action<string, string, AudioForwardEnd>? SendingEnded;
    public event Action<string, string>? ReceivingStarted;
    public event Action<string, string>? ReceivingEnded;

    // ---------- sending ----------

    /// <summary>
    /// Sends this PC's sound to <paramref name="peerId"/>: null when it plays there now, else why not (and nothing
    /// changed here: the output is muted only once the other side said yes).
    /// </summary>
    public async Task<AudioForwardEnd?> StartSendingAsync(string peerId)
    {
        if (!Enabled) return AudioForwardEnd.Refused;
        SendSession? old;
        lock (_gate) old = _send;
        if (old is { } o && o.PeerId == peerId && !o.Ended) return null;
        old?.End(AudioForwardEnd.Stopped);

        LinkStream stream;
        try { stream = await Link.OpenStreamAsync(peerId, LinkAudioProtocol.Purpose).ConfigureAwait(false); }
        catch (LinkException ex)
        {
            _opts.Log($"Audio link: no stream to {peerId} ({ex.Reason})");
            return AudioForwardEnd.LinkLost;
        }
        var session = new SendSession(this, stream);
        try
        {
            await stream.SendAsync(LinkAudioProtocol.HeaderFrame(LinkAudioProtocol.Offer())).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(_opts.AnswerTimeout);
            var answer = LinkAudioProtocol.ReadHeader(await stream.ReceiveAsync(timeout.Token).ConfigureAwait(false));
            if (answer is null) { stream.Dispose(); return AudioForwardEnd.LinkLost; }
            if (!LinkProtocol.Bool(answer, "ok"))
            {
                _opts.Log($"Audio link: {stream.PeerName} said no ({LinkProtocol.Str(answer, "reason") ?? "?"})");
                stream.Dispose();
                return LinkProtocol.Str(answer, "reason") == "output" ? AudioForwardEnd.Failed : AudioForwardEnd.Refused;
            }
            if (!Enabled) { stream.Dispose(); return AudioForwardEnd.Refused; } // switched off meanwhile
            lock (_gate) _send = session;
            await session.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            session.End(AudioForwardEnd.LinkLost, quiet: true);
            return AudioForwardEnd.LinkLost;
        }
        catch (Exception ex)
        {
            Core.Log.Error("Audio link capture", ex);
            session.End(AudioForwardEnd.Failed, quiet: true);
            return AudioForwardEnd.Failed;
        }
        if (session.Ended) return session.EndReason;
        _opts.Log($"Audio link: sound goes to {stream.PeerName}");
        Raise(() => SendingStarted?.Invoke(stream.PeerId, stream.PeerName));
        return null;
    }

    /// <summary>The sound plays here again (a local output was chosen).</summary>
    public void StopSending()
    {
        SendSession? s;
        lock (_gate) s = _send;
        s?.End(AudioForwardEnd.Stopped);
    }

    /// <summary>The default output changed while sending: capture the new one and mute it instead (the old one gets its state back).</summary>
    public async Task FollowDefaultOutputAsync()
    {
        SendSession? s;
        lock (_gate) s = _send;
        if (s is null || s.Ended) return;
        try { await s.RestartSourceAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Core.Log.Error("Audio link capture", ex);
            s.End(AudioForwardEnd.Failed);
        }
    }

    private sealed class SendSession
    {
        private readonly LinkAudioFeature _f;
        private readonly LinkStream _stream;
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10)
        {
            // 200 ms: a stalled connection loses the oldest sound instead of piling up delay.
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
        });
        private readonly OpusFrameEncoder _encoder = new();
        private readonly float[] _pcm = new float[OpusFrameEncoder.FrameFloats];
        private readonly byte[] _packet = new byte[OpusFrameEncoder.MaxPacket];
        private int _filled;
        private uint _seq;
        private IAudioSource? _source;
        private int _ended;
        private DateTime _lastHeard = DateTime.UtcNow, _lastSent = DateTime.UtcNow;
        public long Frames;

        public SendSession(LinkAudioFeature f, LinkStream stream) { _f = f; _stream = stream; }

        public string PeerId => _stream.PeerId;
        public bool Ended => _ended != 0;
        public AudioForwardEnd EndReason { get; private set; }

        public async Task StartAsync()
        {
            await RestartSourceAsync().ConfigureAwait(false);
            var ct = _cts.Token;
            _ = Task.Run(() => WriteLoop(ct));
            _ = Task.Run(() => ReadLoop(ct));
            _ = Task.Run(() => KeepAliveLoop(ct));
        }

        public async Task RestartSourceAsync()
        {
            var old = Interlocked.Exchange(ref _source, null);
            old?.Dispose();
            if (Ended) return;
            var source = _f._opts.Source();
            source.Samples += OnSamples;
            source.Failed += why => { _f._opts.Log($"Audio link: capture stopped ({why})"); End(AudioForwardEnd.Failed); };
            await source.StartAsync().ConfigureAwait(false);
            if (Ended) { source.Dispose(); return; }
            _source = source;
            if (source.EndpointId is { } id) _f._opts.Mute.Engage(id);
        }

        /// <summary>Capture thread: 20 ms blocks to Opus, into the queue.</summary>
        private void OnSamples(float[] samples, int count)
        {
            if (Ended) return;
            int i = 0;
            while (i < count)
            {
                int take = Math.Min(count - i, _pcm.Length - _filled);
                Array.Copy(samples, i, _pcm, _filled, take);
                _filled += take;
                i += take;
                if (_filled < _pcm.Length) break;
                _filled = 0;
                int len = _encoder.Encode(_pcm, _packet);
                if (len <= 0) continue;
                _queue.Writer.TryWrite(LinkAudioProtocol.SoundFrame(_seq++, (uint)Environment.TickCount, _packet.AsSpan(0, len)));
            }
        }

        private async Task WriteLoop(CancellationToken ct)
        {
            try
            {
                await foreach (var frame in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await _stream.SendAsync(frame, ct).ConfigureAwait(false);
                    _lastSent = DateTime.UtcNow;
                    if (frame[0] == LinkAudioProtocol.Sound) Interlocked.Increment(ref Frames);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ChannelClosedException) { }
            // A failed write closed the stream: the read loop ends too and knows why (the receiver's last header).
        }

        /// <summary>
        /// The receiver only sends keep-alives, and a last header when it ends on purpose ("off", "output", "stopped").
        /// A stream that just ends means the connection is gone.
        /// </summary>
        private async Task ReadLoop(CancellationToken ct)
        {
            var why = AudioForwardEnd.LinkLost;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = await _stream.ReceiveAsync(ct).ConfigureAwait(false);
                    if (frame is null) break;
                    _lastHeard = DateTime.UtcNow;
                    if (LinkAudioProtocol.ReadHeader(frame) is { } h && LinkProtocol.Str(h, "end") is { } end)
                    {
                        why = end switch { "off" => AudioForwardEnd.Refused, "output" => AudioForwardEnd.Failed, _ => AudioForwardEnd.RemoteStopped };
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            End(why);
        }

        private async Task KeepAliveLoop(CancellationToken ct)
        {
            var stats = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(_f._opts.KeepAlive, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                var now = DateTime.UtcNow;
                if (now - _lastHeard > _f._opts.DeadAfter) { _f._opts.Log("Audio link: receiver silent, stopping"); End(AudioForwardEnd.LinkLost); return; }
                if (now - _lastSent >= _f._opts.KeepAlive) _queue.Writer.TryWrite(LinkAudioProtocol.KeepAliveFrame);
                if (now - stats >= _f._opts.StatsEvery)
                {
                    stats = now;
                    _f._opts.Log($"Audio link: sent {Interlocked.Read(ref Frames)} frames to {_stream.PeerName}");
                }
            }
        }

        /// <summary>Once: capture off, stream closed, the output gets its mute state back, then the event.</summary>
        public void End(AudioForwardEnd why, bool quiet = false)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            EndReason = why;
            try { _cts.Cancel(); } catch { }
            Interlocked.Exchange(ref _source, null)?.Dispose();
            _queue.Writer.TryComplete();
            _stream.Dispose();
            _f._opts.Mute.Release();
            bool current;
            lock (_f._gate)
            {
                current = _f._send == this;
                if (current) _f._send = null;
            }
            _f._opts.Log($"Audio link: sound back here ({why}, {Interlocked.Read(ref Frames)} frames sent)");
            if (current && !quiet) _f.Raise(() => _f.SendingEnded?.Invoke(_stream.PeerId, _stream.PeerName, why));
        }
    }

    // ---------- receiving ----------

    private void OnStreamOpened(LinkStream stream)
    {
        if (stream.Purpose != LinkAudioProtocol.Purpose) return;
        _ = Task.Run(() => ReceiveAsync(stream));
    }

    private async Task ReceiveAsync(LinkStream stream)
    {
        JsonObject? offer;
        try
        {
            using var timeout = new CancellationTokenSource(_opts.AnswerTimeout);
            offer = LinkAudioProtocol.ReadHeader(await stream.ReceiveAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { stream.Dispose(); return; }
        string? refuse = !Enabled ? "off" : !LinkAudioProtocol.AcceptsOffer(offer) ? "format" : null;
        if (refuse is not null)
        {
            _opts.Log($"Audio link: sound from {stream.PeerName} refused ({refuse})");
            await AnswerAndClose(stream, new JsonObject { ["ok"] = false, ["reason"] = refuse }).ConfigureAwait(false);
            return;
        }
        ReceiveSession? old;
        lock (_gate) old = _receive;
        old?.End("stopped"); // one sound at a time: the newest wins

        var session = new ReceiveSession(this, stream, _opts.TargetMs(stream.PeerId));
        try { await session.StartSinkAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Core.Log.Error("Audio link playback", ex);
            session.DisposeSink();
            await AnswerAndClose(stream, new JsonObject { ["ok"] = false, ["reason"] = "output" }).ConfigureAwait(false);
            return;
        }
        lock (_gate) _receive = session;
        try { await stream.SendAsync(LinkAudioProtocol.HeaderFrame(new JsonObject { ["ok"] = true })).ConfigureAwait(false); }
        catch (IOException) { session.End(); return; }
        _opts.Log($"Audio link: playing sound from {stream.PeerName} (buffer {session.Buffer.TargetMs} ms)");
        Raise(() => ReceivingStarted?.Invoke(stream.PeerId, stream.PeerName));
        await session.RunAsync().ConfigureAwait(false);
    }

    /// <summary>Says no and waits for the sender to close first, so the answer is not lost in a reset.</summary>
    private static async Task AnswerAndClose(LinkStream stream, JsonObject answer)
    {
        try
        {
            await stream.SendAsync(LinkAudioProtocol.HeaderFrame(answer)).ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (await stream.ReceiveAsync(cts.Token).ConfigureAwait(false) is not null) { }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        stream.Dispose();
    }

    private sealed class ReceiveSession
    {
        private readonly LinkAudioFeature _f;
        private readonly LinkStream _stream;
        private readonly OpusFrameDecoder _decoder = new();
        private readonly float[] _pcm = new float[OpusFrameEncoder.FrameFloats * 3]; // up to 60 ms per packet
        private readonly CancellationTokenSource _cts = new();
        private IAudioSink? _sink;
        private int _ended;
        private DateTime _lastHeard = DateTime.UtcNow;
        private long _delaySum, _delayCount;
        public long Frames;

        public ReceiveSession(LinkAudioFeature f, LinkStream stream, int targetMs)
        {
            _f = f;
            _stream = stream;
            Buffer = new AudioJitterBuffer(OpusFrameEncoder.SampleRate, OpusFrameEncoder.Channels, Math.Clamp(targetMs, 40, 300), Math.Clamp(targetMs, 40, 300) * 4);
        }

        public AudioJitterBuffer Buffer { get; }
        public string PeerId => _stream.PeerId;

        public async Task StartSinkAsync()
        {
            var sink = _f._opts.Sink();
            _sink = sink;
            sink.Failed += why => { _f._opts.Log($"Audio link: playback stopped ({why})"); End("output"); };
            await sink.StartAsync(Buffer).ConfigureAwait(false);
        }

        public void DisposeSink() => Interlocked.Exchange(ref _sink, null)?.Dispose();

        public async Task RunAsync()
        {
            var ct = _cts.Token;
            _ = Task.Run(() => KeepAliveLoop(ct));
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = await _stream.ReceiveAsync(ct).ConfigureAwait(false);
                    if (frame is null) break;
                    _lastHeard = DateTime.UtcNow;
                    if (!LinkAudioProtocol.TryReadSound(frame, out _, out uint tick, out var packet)) continue;
                    int n;
                    try { n = _decoder.Decode(packet.Span, _pcm); }
                    catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { continue; } // a broken packet: skip it
                    if (n <= 0) continue;
                    Buffer.Push(_pcm.AsSpan(0, n * OpusFrameEncoder.Channels));
                    Interlocked.Increment(ref Frames);
                    if (_f._opts.SamePc)
                    {
                        uint delay = unchecked((uint)Environment.TickCount - tick);
                        if (delay < 10_000) { _delaySum += delay; _delayCount++; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            End();
        }

        private async Task KeepAliveLoop(CancellationToken ct)
        {
            var stats = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(_f._opts.KeepAlive, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                var now = DateTime.UtcNow;
                if (now - _lastHeard > _f._opts.DeadAfter) { _f._opts.Log("Audio link: sender silent, stopping"); End(); return; }
                try { await _stream.SendAsync(LinkAudioProtocol.KeepAliveFrame, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { End(); return; }
                if (now - stats >= _f._opts.StatsEvery)
                {
                    stats = now;
                    LogStats();
                }
            }
        }

        private void LogStats()
        {
            string delay = _delayCount > 0 ? $", network {_delaySum / _delayCount} ms" : "";
            string rms = _sink is NullAudioSink ns ? $", level {ns.TakeRms():0.000}" : "";
            _f._opts.Log($"Audio link: received {Interlocked.Read(ref Frames)} frames from {_stream.PeerName}, buffer {Buffer.LevelMs:0} ms (target {Buffer.TargetMs}), "
                         + $"rate {Buffer.Rate:0.0000}, underruns {Buffer.Underruns}{delay}{rms}, output {_sink?.LatencyMs ?? 0} ms");
            _delaySum = _delayCount = 0;
        }

        /// <summary>
        /// Once: the stream closes, what is buffered plays out (it fades at the end), then the output stops.
        /// <paramref name="tell"/>: why it ends here on purpose ("off", "output", "stopped"), so the sender does not take
        /// it for a lost connection; null when the stream is gone anyway.
        /// </summary>
        public void End(string? tell = null)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            if (tell is not null)
            {
                try { _stream.SendAsync(LinkAudioProtocol.HeaderFrame(new JsonObject { ["end"] = tell })).Wait(500); } catch { }
            }
            _stream.Dispose();
            bool current;
            lock (_f._gate)
            {
                current = _f._receive == this;
                if (current) _f._receive = null;
            }
            LogStats();
            int drain = (int)Math.Min(400, Buffer.LevelMs + 60);
            _ = Task.Delay(drain).ContinueWith(_ => DisposeSink(), TaskScheduler.Default);
            _f._opts.Log($"Audio link: sound from {_stream.PeerName} ended");
            if (current) _f.Raise(() => _f.ReceivingEnded?.Invoke(_stream.PeerId, _stream.PeerName));
        }
    }

    // ---------- switch ----------

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.LinkShareAudio) || Enabled) return;
        SendSession? s;
        ReceiveSession? r;
        lock (_gate) { s = _send; r = _receive; }
        s?.End(AudioForwardEnd.Refused);
        if (r is not null) _ = Task.Run(() => r.End("off"));
    }

    private void Raise(Action a)
    {
        void Run()
        {
            try { a(); } catch (Exception ex) { Core.Log.Error("Audio link event", ex); }
        }
        if (_context is not null) _context.Post(_ => Run(), null);
        else Run();
    }

    public override void Dispose()
    {
        Link.StreamOpened -= OnStreamOpened;
        Settings.PropertyChanged -= OnSettingChanged;
        SendSession? s;
        ReceiveSession? r;
        lock (_gate) { s = _send; r = _receive; }
        s?.End(AudioForwardEnd.Stopped);
        r?.End("stopped");
        base.Dispose();
    }
}
