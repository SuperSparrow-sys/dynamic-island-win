using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicBay.Services.Link.Audio;

/// <summary>
/// Frames on an "audio" stream. The first byte says what follows:
/// <list type="bullet">
/// <item>1 header, UTF-8 JSON: the sender's offer <c>{v, codec, rate, ch, frame}</c>, the receiver's answer <c>{ok, reason}</c>,</item>
/// <item>2 sound: sequence number (uint32 BE), the sender's millisecond tick (uint32 BE, for the delay on one PC), Opus packet,</item>
/// <item>3 keep-alive (nothing else): both sides every second, so a dead stream is noticed within a few seconds.</item>
/// </list>
/// </summary>
public static class LinkAudioProtocol
{
    public const string Purpose = "audio";
    public const byte Header = 1, Sound = 2, KeepAlive = 3;
    public const int Version = 1;

    public static byte[] HeaderFrame(JsonObject json)
    {
        var body = Encoding.UTF8.GetBytes(json.ToJsonString());
        var frame = new byte[body.Length + 1];
        frame[0] = Header;
        body.CopyTo(frame, 1);
        return frame;
    }

    public static JsonObject Offer() => new()
    {
        ["v"] = Version, ["codec"] = "opus", ["rate"] = OpusFrameEncoder.SampleRate, ["ch"] = OpusFrameEncoder.Channels,
        ["frame"] = OpusFrameEncoder.FrameSamples,
    };

    /// <summary>The header in a frame, or null when it is none (or no JSON object).</summary>
    public static JsonObject? ReadHeader(byte[]? frame)
    {
        if (frame is not { Length: > 1 } || frame[0] != Header) return null;
        try { return JsonNode.Parse(frame.AsSpan(1)) is JsonObject o ? o : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The receiver understands the offer (Opus, 48 kHz, stereo, 20 ms); missing fields mean the defaults.</summary>
    public static bool AcceptsOffer(JsonObject? offer) =>
        offer is not null
        && (LinkProtocol.Str(offer, "codec") ?? "opus") == "opus"
        && (LinkProtocol.Long(offer, "rate") ?? OpusFrameEncoder.SampleRate) == OpusFrameEncoder.SampleRate
        && (LinkProtocol.Long(offer, "ch") ?? OpusFrameEncoder.Channels) == OpusFrameEncoder.Channels
        && (LinkProtocol.Long(offer, "frame") ?? OpusFrameEncoder.FrameSamples) == OpusFrameEncoder.FrameSamples;

    public static byte[] SoundFrame(uint seq, uint tick, ReadOnlySpan<byte> packet)
    {
        var frame = new byte[9 + packet.Length];
        frame[0] = Sound;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), seq);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), tick);
        packet.CopyTo(frame.AsSpan(9));
        return frame;
    }

    public static bool TryReadSound(byte[] frame, out uint seq, out uint tick, out ReadOnlyMemory<byte> packet)
    {
        seq = tick = 0;
        packet = default;
        if (frame.Length < 10 || frame[0] != Sound) return false;
        seq = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1));
        tick = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(5));
        packet = frame.AsMemory(9);
        return true;
    }

    public static readonly byte[] KeepAliveFrame = { KeepAlive };
}
