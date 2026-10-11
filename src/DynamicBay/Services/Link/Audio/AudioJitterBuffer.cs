namespace DynamicBay.Services.Link.Audio;

/// <summary>
/// Sound from another device, between the network and the speaker. Packets arrive in bursts and the two sound cards
/// run on slightly different clocks, so the buffer:
/// <list type="bullet">
/// <item>waits until <see cref="TargetMs"/> is buffered before it plays (and again after running dry),</item>
/// <item>steers towards the target by reading a little faster or slower (at most ±0.5 %, linear interpolation, not
/// audible) instead of dropping samples,</item>
/// <item>drops what is too much only when it is far over <see cref="MaxMs"/> (a stall that ended), with a short fade,</item>
/// <item>never jumps: running dry fades out over the last samples it has, starting again fades in.</item>
/// </list>
/// Interleaved float samples; thread-safe (the network pushes, the sound card reads).
/// </summary>
public sealed class AudioJitterBuffer
{
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _channels, _capacity, _target, _max, _fade;
    private int _start, _count;     // in frames
    private double _frac;           // read position between frame _start and _start + 1
    private bool _playing;
    private double _gain;           // 0..1, ramps by 1/_fade per frame
    private bool _fadingOut;        // too much buffered: fade out, skip, fade in
    private double _smoothed;       // smoothed level in frames for the rate control
    private readonly float[] _last; // the last frame played (for the fade-out when nothing is left)

    public AudioJitterBuffer(int sampleRate = 48000, int channels = 2, int targetMs = 100, int maxMs = 400, double fadeMs = 4)
    {
        SampleRate = sampleRate;
        _channels = channels;
        _target = sampleRate * targetMs / 1000;
        _max = sampleRate * maxMs / 1000;
        _capacity = _max * 2 + sampleRate / 10;
        _ring = new float[_capacity * channels];
        _fade = Math.Max(1, (int)(sampleRate * fadeMs / 1000));
        _last = new float[channels];
        TargetMs = targetMs;
        MaxMs = maxMs;
    }

    public int SampleRate { get; }
    public int TargetMs { get; }
    public int MaxMs { get; }

    /// <summary>Buffered sound in milliseconds.</summary>
    public double LevelMs { get { lock (_gate) return _count * 1000.0 / SampleRate; } }
    /// <summary>Playing (false: waiting for enough sound).</summary>
    public bool Playing { get { lock (_gate) return _playing; } }
    /// <summary>Times it ran dry while playing, and frames dropped because too much was buffered (diagnostics).</summary>
    public int Underruns { get; private set; }
    public long Dropped { get; private set; }
    /// <summary>Current read speed (1 = in step; above 1 = catching up).</summary>
    public double Rate { get; private set; } = 1;

    /// <summary>Adds sound (whole frames: interleaved, a multiple of the channel count).</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / _channels;
        lock (_gate)
        {
            // A burst far over the limit: keep the newest part (the ring never overflows).
            if (frames > _capacity - _count)
            {
                int drop = Math.Min(_count, frames - (_capacity - _count));
                Skip(drop);
                if (frames > _capacity) { samples = samples[((frames - _capacity) * _channels)..]; Dropped += frames - _capacity; frames = _capacity; }
            }
            int end = (_start + _count) % _capacity;
            for (int f = 0; f < frames; f++)
            {
                int at = ((end + f) % _capacity) * _channels;
                for (int c = 0; c < _channels; c++) _ring[at + c] = samples[f * _channels + c];
            }
            _count += frames;
            if (_count > _max && _playing && !_fadingOut) _fadingOut = true;
        }
    }

    /// <summary>Fills <paramref name="output"/> completely (silence while waiting), like a sound card asks for it.</summary>
    public void Read(Span<float> output)
    {
        int frames = output.Length / _channels;
        lock (_gate)
        {
            if (!_playing && _count >= _target)
            {
                _playing = true;
                _gain = 0;
                _smoothed = _count;
            }
            if (_playing && _count > _max) _fadingOut = true;
            for (int f = 0; f < frames; f++)
            {
                var o = output.Slice(f * _channels, _channels);
                if (!_playing) { FadeTail(o); continue; }
                if (_count < 2)
                {
                    // Dry: fade from the last frame played, then wait for the target again.
                    _playing = false;
                    Underruns++;
                    _frac = 0;
                    FadeTail(o);
                    continue;
                }
                // Not much left: fade out before it runs dry instead of stopping in the middle of a wave.
                bool ending = _count - 1 <= _fade && _gain > 0;
                int a = _start * _channels, b = ((_start + 1) % _capacity) * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    float v = (float)(_ring[a + c] + (_ring[b + c] - _ring[a + c]) * _frac);
                    _last[c] = v;
                    o[c] = (float)(v * _gain);
                }
                if (_fadingOut || ending) _gain = Math.Max(0, _gain - 1.0 / _fade);
                else _gain = Math.Min(1, _gain + 1.0 / _fade);
                if (_fadingOut && _gain <= 0)
                {
                    // Silent now: drop down to the target and fade in again.
                    int drop = Math.Max(0, _count - _target);
                    Skip(drop);
                    _fadingOut = false;
                }
                _frac += Rate;
                while (_frac >= 1 && _count > 1) { _frac -= 1; _start = (_start + 1) % _capacity; _count--; }
            }
            Steer(frames);
        }
    }

    /// <summary>After the last real frame: the output glides to silence from where the wave was.</summary>
    private void FadeTail(Span<float> o)
    {
        _gain = Math.Max(0, _gain - 1.0 / _fade);
        for (int c = 0; c < _channels; c++) o[c] = (float)(_last[c] * _gain);
        if (_gain <= 0) Array.Clear(_last);
    }

    /// <summary>
    /// Rate control: the smoothed level against the target, proportional and limited to ±0.5 % (under 9 cent). Sound
    /// cards differ by well under 0.1 %, which this evens out a few ms off the target.
    /// </summary>
    private void Steer(int frames)
    {
        if (!_playing) { Rate = 1; return; }
        double k = Math.Min(1, frames / (double)SampleRate * 2); // time constant about half a second
        _smoothed += (_count - _smoothed) * k;
        double error = (_smoothed - _target) / _target;
        Rate = 1 + Math.Clamp(error * 0.025, -MaxRateChange, MaxRateChange);
    }

    private const double MaxRateChange = 0.005;

    private void Skip(int frames)
    {
        frames = Math.Min(frames, _count);
        _start = (_start + frames) % _capacity;
        _count -= frames;
        Dropped += frames;
    }

    /// <summary>Forgets everything (a new stream starts).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _start = _count = 0;
            _frac = 0;
            _playing = _fadingOut = false;
            _gain = 0;
            Array.Clear(_last);
        }
    }
}
