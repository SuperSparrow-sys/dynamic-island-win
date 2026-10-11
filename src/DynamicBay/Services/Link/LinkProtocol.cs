using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicBay.Services.Link;

/// <summary>
/// Wire format and the pure rules of the link (pairing code, names, tie-break), kept static so they can be tested
/// without sockets. See docs/ARCHITECTURE.md, section "Link".
/// </summary>
public static class LinkProtocol
{
    /// <summary>TCP port of the encrypted connection.</summary>
    public const int Port = 43823;
    /// <summary>UDP port of the LAN announcements (no secrets in them).</summary>
    public const int DiscoveryPort = 43824;
    public const int Version = 1;
    /// <summary>Largest frame (JSON envelope or binary chunk); anything bigger closes the connection.</summary>
    public const int MaxFrame = 1024 * 1024;
    /// <summary>File chunks, well below <see cref="MaxFrame"/>.</summary>
    public const int ChunkSize = 256 * 1024;

    // Envelope types used by the link itself. Features (clipboard, notifications, media, status) pick other names.
    public const string Hello = "hello", Reject = "reject", Ping = "ping", Pong = "pong", Unpair = "unpair";
    public const string PairNonce = "pair.nonce", PairConfirm = "pair.confirm";
    public const string FileOffer = "file.offer", FileAccept = "file.accept", FileDecline = "file.decline",
        FileChunk = "file.chunk", FileEnd = "file.end", FileDone = "file.done", FileCancel = "file.cancel";

    /// <summary>Types a feature may not register or send: the link handles them itself.</summary>
    public static bool IsReserved(string type) =>
        type is Hello or Reject or Ping or Pong or Unpair
        || type.StartsWith("pair.", StringComparison.Ordinal) || type.StartsWith("file.", StringComparison.Ordinal);

    // ---------- framing: 4-byte big-endian length, then the bytes ----------

    public static async Task WriteFrameAsync(Stream s, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        if (body.Length > MaxFrame) throw new InvalidDataException($"frame of {body.Length} bytes is over the limit");
        var head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        if (body.Length > 0) await s.WriteAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>Next frame, or null when the other side closed the stream cleanly between frames.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct = default)
    {
        var head = new byte[4];
        int got = 0;
        while (got < 4)
        {
            int n = await s.ReadAsync(head.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0) return got == 0 ? null : throw new EndOfStreamException("stream ended inside a frame header");
            got += n;
        }
        int len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len < 0 || len > MaxFrame) throw new InvalidDataException($"frame of {len} bytes is over the limit");
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    public static byte[] Encode(JsonObject envelope) => Encoding.UTF8.GetBytes(envelope.ToJsonString());

    /// <summary>The envelope, or null when it is no JSON object or has no type.</summary>
    public static JsonObject? Decode(byte[] frame)
    {
        try
        {
            return JsonNode.Parse(frame) is JsonObject o && o["t"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? o : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Long(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        return v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (long)d : null;
    }

    public static bool Bool(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // ---------- pairing ----------

    /// <summary>
    /// The six digits both sides show: SHA-256 over a label, both certificate fingerprints in sorted order and the
    /// two pairing nonces (initiator first). The responder commits to its nonce before it sees the initiator's, so a
    /// device in the middle cannot search for certificates that give both sides the same code.
    /// </summary>
    public static string PairingCode(string fingerprintA, string fingerprintB, byte[] initiatorNonce, byte[] responderNonce)
    {
        byte[] a = Convert.FromHexString(fingerprintA), b = Convert.FromHexString(fingerprintB);
        if (a.AsSpan().SequenceCompareTo(b) > 0) (a, b) = (b, a);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.ASCII.GetBytes("DynamicBay-pair-v1"));
        h.AppendData(a);
        h.AppendData(b);
        h.AppendData(initiatorNonce);
        h.AppendData(responderNonce);
        uint n = BinaryPrimitives.ReadUInt32BigEndian(h.GetHashAndReset());
        return (n % 1_000_000).ToString("D6");
    }

    /// <summary>The responder's commitment to its nonce, bound to both certificates.</summary>
    public static byte[] Commitment(string responderFingerprint, string initiatorFingerprint, byte[] responderNonce)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.ASCII.GetBytes("DynamicBay-commit-v1"));
        h.AppendData(Convert.FromHexString(responderFingerprint));
        h.AppendData(Convert.FromHexString(initiatorFingerprint));
        h.AppendData(responderNonce);
        return h.GetHashAndReset();
    }

    // ---------- connections ----------

    /// <summary>
    /// Both devices dialed each other: true when the new connection replaces the existing one. The same answer comes
    /// out on both sides: a restarted peer (other boot id) always wins, otherwise the connection dialed by the device
    /// with the smaller id stays, and between two of the same direction the newer one.
    /// </summary>
    public static bool KeepNew(string myId, string peerId, bool existingOutbound, string existingBoot, bool newOutbound, string newBoot)
    {
        if (!string.Equals(existingBoot, newBoot, StringComparison.Ordinal)) return true;
        if (existingOutbound == newOutbound) return true;
        string preferredDialer = string.CompareOrdinal(myId, peerId) < 0 ? myId : peerId;
        bool newDialedByPreferred = (newOutbound ? myId : peerId) == preferredDialer;
        return newDialedByPreferred;
    }

    /// <summary>"host", "host:port", "1.2.3.4:5", "[::1]:5" or "::1" → host and port (default <paramref name="defaultPort"/>).</summary>
    public static bool TryParseAddress(string? text, int defaultPort, out string host, out int port)
    {
        host = ""; port = defaultPort;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0) return false;
            var rest = text[(close + 1)..];
            if (rest.Length > 0 && !(rest.StartsWith(':') && int.TryParse(rest[1..], out port))) return false;
            text = text[1..close];
            if (!IPAddress.TryParse(text, out _) || port is <= 0 or >= 65536) return false;
            host = text;
            return true;
        }
        int colons = text.Count(c => c == ':');
        if (colons > 1) // plain IPv6 without port
        {
            if (!IPAddress.TryParse(text, out var v6)) return false;
            host = v6.ToString();
            return true;
        }
        if (colons == 1)
        {
            int colon = text.IndexOf(':');
            if (!int.TryParse(text[(colon + 1)..], out port)) return false;
            text = text[..colon];
        }
        if (port is <= 0 or >= 65536 || Uri.CheckHostName(text) == UriHostNameType.Unknown) return false;
        host = text;
        return true;
    }

    /// <summary>The address as stored in <see cref="DynamicBay.Core.PairedDevice.LastAddresses"/> (port only when not the default).</summary>
    public static string FormatAddress(IPAddress ip, int port)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        bool v6 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        if (port == Port) return ip.ToString();
        return v6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }

    // ---------- files ----------

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// A file name from another device made safe: only the last path part, no characters Windows forbids or treats
    /// specially (":" would open an alternate stream), no device names like CON, no trailing dots, at most 160 chars.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        name ??= "";
        name = name.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsControl(c) || invalid.Contains(c) ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.All(c => c is '.' or '_')) name = "Datei";
        string stem = name.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem)) name = "_" + name;
        if (name.Length > 160)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(160 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return name;
    }

    /// <summary>"name.ext", or "name (2).ext" … when that exists already.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }
}
