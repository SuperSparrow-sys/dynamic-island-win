namespace DynamicBay.Services.Link.Audio;

/// <summary>
/// The sound card's mix format (any rate, 1 to 8 channels) to what goes over the link: 48 kHz stereo float. Streaming:
/// keeps its position between calls, so blocks of any size join without a seam. Not thread-safe (one capture thread).
/// </summary>
public sealed class AudioConvert
{
    public const int OutRate = 48000, OutChannels = 2;
    private readonly int _inRate, _inChannels;
    private readonly double _step;          // input frames per output frame
    private double _pos;                    // position in the input, relative to _prev (0 = _prev, 1 = first new frame)
    private float _prevL, _prevR;
    private bool _primed;
    private float[] _stereo = Array.Empty<float>();

    public AudioConvert(int inRate, int inChannels)
    {
        if (inRate <= 0 || inChannels <= 0) throw new ArgumentOutOfRangeException(nameof(inRate));
        _inRate = inRate;
        _inChannels = inChannels;
        _step = (double)inRate / OutRate;
    }

    public bool Passthrough => _inRate == OutRate && _inChannels == OutChannels;

    /// <summary>Converts <paramref name="input"/> (interleaved) and appends the result to <paramref name="output"/>.</summary>
    public void Convert(ReadOnlySpan<float> input, List<float> output)
    {
        int frames = input.Length / _inChannels;
        if (frames == 0) return;
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        // 1. Stereo: mono is doubled; more channels: front left/right plus centre and the rest mixed in at -6 dB.
        for (int f = 0; f < frames; f++)
        {
            int i = f * _inChannels;
            float l, r;
            if (_inChannels == 1) l = r = input[i];
            else
            {
                l = input[i]; r = input[i + 1];
                if (_inChannels > 2)
                {
                    float rest = 0;
                    for (int c = 2; c < _inChannels; c++) rest += input[i + c];
                    rest *= 0.5f / (_inChannels - 2);
                    l = Math.Clamp(l * 0.7f + rest, -1f, 1f);
                    r = Math.Clamp(r * 0.7f + rest, -1f, 1f);
                }
            }
            _stereo[f * 2] = l; _stereo[f * 2 + 1] = r;
        }
        if (_inRate == OutRate)
        {
            for (int k = 0; k < frames * 2; k++) output.Add(_stereo[k]);
            return;
        }
        // 2. Rate: linear interpolation between the previous and the next input frame.
        if (!_primed) { _prevL = _stereo[0]; _prevR = _stereo[1]; _primed = true; _pos = 1; }
        while (true)
        {
            int idx = (int)Math.Floor(_pos); // 0 = _prev, n = input frame n-1
            if (idx >= frames) break;
            double t = _pos - idx;
            float aL = idx == 0 ? _prevL : _stereo[(idx - 1) * 2], aR = idx == 0 ? _prevR : _stereo[(idx - 1) * 2 + 1];
            float bL = _stereo[idx * 2], bR = _stereo[idx * 2 + 1];
            output.Add((float)(aL + (bL - aL) * t));
            output.Add((float)(aR + (bR - aR) * t));
            _pos += _step;
        }
        _prevL = _stereo[(frames - 1) * 2];
        _prevR = _stereo[(frames - 1) * 2 + 1];
        _pos -= frames;
    }
}
