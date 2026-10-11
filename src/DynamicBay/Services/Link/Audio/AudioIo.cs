using System.Diagnostics;
using DynamicBay.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DynamicBay.Services.Link.Audio;

/// <summary>Where the forwarded sound comes from: 48 kHz stereo float, interleaved, in blocks of any size.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>New samples (buffer, number of floats), on the capture thread. Nothing comes while nothing plays.</summary>
    event Action<float[], int>? Samples;
    /// <summary>Capture ended by itself (device gone, audio service restarted).</summary>
    event Action<string>? Failed;
    /// <summary>The output whose sound is captured (to mute it here), null for generated sound.</summary>
    string? EndpointId { get; }
    Task StartAsync();
}

/// <summary>Where received sound goes: it reads the jitter buffer at the sound card's pace.</summary>
public interface IAudioSink : IDisposable
{
    event Action<string>? Failed;
    Task StartAsync(AudioJitterBuffer buffer);
    /// <summary>The sound card's own buffer in ms (part of the delay).</summary>
    int LatencyMs { get; }
}

/// <summary>
/// The system sound: WASAPI loopback of the current default output (what all apps play, after Windows mixed it). Works
/// while that output is muted (measured, see docs/ARCHITECTURE.md). Exclusive-mode and DRM-protected playback is not
/// in the mix and therefore not captured.
/// </summary>
public sealed class LoopbackAudioSource : IAudioSource
{
    private WasapiLoopbackCapture? _capture;
    private AudioConvert? _convert;
    private readonly List<float> _out = new(4096);
    private float[] _in = Array.Empty<float>();
    private float[] _block = Array.Empty<float>();
    private bool _disposed;
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    public event Action<float[], int>? Samples;
    public event Action<string>? Failed;
    public string? EndpointId { get; private set; }

    // Core Audio objects are created on a pool thread (MTA): created on the UI thread they cannot be used from NAudio's capture thread.
    public Task StartAsync() => Task.Run(() =>
    {
        using var en = new MMDeviceEnumerator();
        var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        EndpointId = device.ID;
        var capture = new WasapiLoopbackCapture(device);
        var fmt = capture.WaveFormat;
        _convert = new AudioConvert(fmt.SampleRate, fmt.Channels);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                       || (fmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        int bits = fmt.BitsPerSample;
        if (!isFloat && bits is not (16 or 24 or 32)) { capture.Dispose(); throw new NotSupportedException($"mix format {fmt}"); }
        capture.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded, isFloat, bits);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null && !_disposed) Failed?.Invoke(e.Exception.Message);
        };
        _capture = capture;
        capture.StartRecording();
        Log.Info($"Audio link: capturing {device.FriendlyName} ({fmt.SampleRate} Hz, {fmt.Channels} ch, {(isFloat ? "float" : bits + " bit")})");
    });

    private void OnData(byte[] buffer, int bytes, bool isFloat, int bits)
    {
        if (_disposed || _convert is null || bytes <= 0) return;
        int bytesPer = isFloat ? 4 : bits / 8;
        int n = bytes / bytesPer;
        if (_in.Length < n) _in = new float[n];
        for (int i = 0, o = 0; o < n; i += bytesPer, o++)
            _in[o] = isFloat ? BitConverter.ToSingle(buffer, i)
                : bits == 16 ? BitConverter.ToInt16(buffer, i) / 32768f
                : bits == 24 ? ((buffer[i] << 8 | buffer[i + 1] << 16 | buffer[i + 2] << 24) >> 8) / 8388608f
                : BitConverter.ToInt32(buffer, i) / 2147483648f;
        _out.Clear();
        _convert.Convert(_in.AsSpan(0, n), _out);
        if (_out.Count == 0) return;
        if (_block.Length < _out.Count) _block = new float[_out.Count];
        _out.CopyTo(_block);
        Samples?.Invoke(_block, _out.Count);
    }

    public void Dispose()
    {
        _disposed = true;
        var c = Interlocked.Exchange(ref _capture, null);
        if (c is null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }
}

/// <summary>Plays received sound on the default output of this PC (shared mode, event driven, 40 ms buffer).</summary>
public sealed class WasapiAudioSink : IAudioSink
{
    private WasapiOut? _out;
    private bool _disposed;
    public event Action<string>? Failed;
    public int LatencyMs => 40;

    public Task StartAsync(AudioJitterBuffer buffer) => Task.Run(() =>
    {
        using var en = new MMDeviceEnumerator();
        var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
        output.PlaybackStopped += (_, e) =>
        {
            if (!_disposed) Failed?.Invoke(e.Exception?.Message ?? "stopped");
        };
        output.Init(new SampleToWaveProvider(new BufferProvider(buffer)));
        _out = output;
        output.Play();
        Log.Info($"Audio link: playing on {device.FriendlyName}");
    });

    public void Dispose()
    {
        _disposed = true;
        var o = Interlocked.Exchange(ref _out, null);
        if (o is null) return;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
    }

    private sealed class BufferProvider(AudioJitterBuffer buffer) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, 2);

        public int Read(float[] samples, int offset, int count)
        {
            buffer.Read(samples.AsSpan(offset, count));
            return count; // silence while waiting: the sound card keeps running
        }
    }
}

/// <summary>
/// Paces itself with a timer instead of a sound card: blocks of whatever time has passed (timer jitter does not add
/// up). Base of the generated tone and the silent sink of test instances.
/// </summary>
public abstract class PacedAudio : IDisposable
{
    private Timer? _timer;
    private readonly Stopwatch _clock = new();
    private long _done; // frames handled
    private readonly object _gate = new();
    protected const int Rate = 48000;

    protected void StartPacing()
    {
        _clock.Restart();
        _timer = new Timer(_ => Tick(), null, 10, 10);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            long due = _clock.ElapsedTicks * Rate / Stopwatch.Frequency;
            int frames = (int)Math.Min(due - _done, Rate / 5);
            if (frames <= 0) return;
            _done = due;
            Process(frames);
        }
    }

    protected abstract void Process(int frames);

    public virtual void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>Test instances (<c>DYNAMICBAY_DEV</c>): a quiet 440 Hz tone generated here, nothing captured, nothing played.</summary>
public sealed class ToneAudioSource : PacedAudio, IAudioSource
{
    private double _phase;
    private float[] _buf = Array.Empty<float>();
    public event Action<float[], int>? Samples;
    public event Action<string>? Failed { add { } remove { } }
    public string? EndpointId => "dev-tone";

    public Task StartAsync()
    {
        StartPacing();
        Log.Info("Dev: forwarding a generated 440 Hz tone (no capture)");
        return Task.CompletedTask;
    }

    protected override void Process(int frames)
    {
        if (_buf.Length < frames * 2) _buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(0.1 * Math.Sin(_phase));
            _phase += 2 * Math.PI * 440 / Rate;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            _buf[f * 2] = _buf[f * 2 + 1] = v;
        }
        Samples?.Invoke(_buf, frames * 2);
    }
}

/// <summary>Test instances: reads the jitter buffer at real-time pace and plays nothing (both instances run on one PC).</summary>
public sealed class NullAudioSink : PacedAudio, IAudioSink
{
    private AudioJitterBuffer? _buffer;
    private float[] _buf = Array.Empty<float>();
    private double _sum;
    private long _n;
    public event Action<string>? Failed { add { } remove { } }
    public int LatencyMs => 10;
    /// <summary>RMS of what was "played" since the last call (the live check logs it).</summary>
    public double TakeRms()
    {
        double rms = _n > 0 ? Math.Sqrt(_sum / _n) : 0;
        _sum = 0; _n = 0;
        return rms;
    }

    public Task StartAsync(AudioJitterBuffer buffer)
    {
        _buffer = buffer;
        StartPacing();
        Log.Info("Dev: received sound goes to a silent sink (nothing played)");
        return Task.CompletedTask;
    }

    protected override void Process(int frames)
    {
        if (_buffer is null) return;
        if (_buf.Length < frames * 2) _buf = new float[frames * 2];
        var span = _buf.AsSpan(0, frames * 2);
        _buffer.Read(span);
        foreach (var v in span) _sum += v * v;
        _n += span.Length;
    }
}
