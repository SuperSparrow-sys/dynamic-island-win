using Concentus;
using Concentus.Enums;

namespace DynamicBay.Services.Link.Audio;

/// <summary>
/// Opus (Concentus, pure C#) for the sound between devices: 48 kHz stereo, 20 ms frames, 160 kbit/s VBR - transparent
/// for music and small enough for a Tailscale relay. Complexity 5 keeps the encoder at a few percent of one core.
/// </summary>
public sealed class OpusFrameEncoder
{
    public const int SampleRate = 48000, Channels = 2, FrameSamples = 960; // 20 ms
    public const int FrameFloats = FrameSamples * Channels;
    public const int MaxPacket = 1500;
    private readonly IOpusEncoder _enc;

    public OpusFrameEncoder(int bitrate = 160_000, int complexity = 5)
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _enc = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _enc.Bitrate = bitrate;
        _enc.Complexity = complexity;
        _enc.UseVBR = true;
        _enc.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
    }

    /// <summary>One 20 ms frame (<see cref="FrameFloats"/> interleaved samples) to a packet; its length.</summary>
    public int Encode(ReadOnlySpan<float> frame, Span<byte> packet)
    {
        if (frame.Length != FrameFloats) throw new ArgumentException("one 20 ms frame expected", nameof(frame));
        return _enc.Encode(frame, FrameSamples, packet, packet.Length);
    }
}

public sealed class OpusFrameDecoder
{
    private readonly IOpusDecoder _dec;

    public OpusFrameDecoder()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _dec = OpusCodecFactory.CreateDecoder(OpusFrameEncoder.SampleRate, OpusFrameEncoder.Channels);
    }

    /// <summary>A packet back to interleaved samples; the number of frames (960 per 20 ms). Throws on a broken packet.</summary>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm) =>
        _dec.Decode(packet, pcm, pcm.Length / OpusFrameEncoder.Channels, false);
}
