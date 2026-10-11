using System.IO;
using System.Net;
using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>Extra TLS connections to a device that is online, one per long-running flow (see <see cref="LinkStream"/>).</summary>
public sealed partial class LinkService
{
    private readonly List<LinkStream> _streams = new();

    /// <summary>
    /// A paired device opened a stream to this one (raised on the context). The subscriber keeps it or disposes it;
    /// without a subscriber it is closed right away.
    /// </summary>
    public event Action<LinkStream>? StreamOpened;

    /// <summary>"audio", "screen.v2" …: lower-case letters, digits, dots and dashes.</summary>
    internal static bool ValidPurpose(string? purpose) =>
        purpose is { Length: > 0 and <= 32 } p && p.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-');

    /// <summary>
    /// Opens a stream to a device that is online right now: a new TLS connection to the address of the main one, with
    /// the same pinned certificate. Throws <see cref="LinkException"/> ("offline", "unreachable", "auth", "busy" …).
    /// </summary>
    public async Task<LinkStream> OpenStreamAsync(string peerId, string purpose, CancellationToken ct = default)
    {
        if (!ValidPurpose(purpose)) throw new ArgumentException("invalid purpose", nameof(purpose));
        LinkConnection? session;
        PairedDevice? device;
        lock (_gate)
        {
            _sessions.TryGetValue(peerId, out session);
            _paired.TryGetValue(peerId, out device);
        }
        if (!IsRunning || session is null || device is null || device.Rejected || session.Remote is not { } remote) throw new LinkException("offline");
        var ip = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        LinkConnection conn;
        try { conn = await ConnectAsync(ip.ToString(), session.PeerPort, device.Fingerprint, "stream", null, ct, purpose).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not LinkException and not OperationCanceledException) { throw new LinkException("unreachable", ex.Message, ex); }
        if (conn.PeerId != peerId) { conn.Close(); throw new LinkException("wrong-device"); }
        var stream = Track(conn, purpose);
        if (stream is null) throw new LinkException("offline");
        Log($"Link: {purpose} stream to {conn.PeerName} open");
        return stream;
    }

    /// <summary>The other side asked for a stream: only paired devices that are online with their main connection.</summary>
    private async Task AcceptStream(LinkConnection conn, JsonObject hello, CancellationToken ct)
    {
        string? purpose = LinkProtocol.Str(hello, "purpose");
        bool ok;
        lock (_gate)
            ok = _paired.TryGetValue(conn.PeerId, out var p) && !p.Rejected && p.Fingerprint == conn.PeerFingerprint && _sessions.ContainsKey(conn.PeerId);
        if (!ok || !ValidPurpose(purpose))
        {
            await SendReject(conn, ok ? "protocol" : "unpaired-or-offline").ConfigureAwait(false);
            return;
        }
        await conn.SendAsync(HelloEnvelope("stream"), null, ct).ConfigureAwait(false);
        var stream = Track(conn, purpose!);
        if (stream is null) return;
        Log($"Link: {purpose} stream from {conn.PeerName} open");
        Post(() =>
        {
            if (StreamOpened is null || stream.IsClosed) { stream.Dispose(); return; }
            StreamOpened.Invoke(stream);
        });
    }

    /// <summary>Remembers the stream so it closes with its device; null (and closed) when the device is gone meanwhile.</summary>
    private LinkStream? Track(LinkConnection conn, string purpose)
    {
        var stream = new LinkStream(conn, purpose);
        bool keep;
        lock (_gate)
        {
            keep = IsRunning && _sessions.ContainsKey(conn.PeerId);
            if (keep) _streams.Add(stream);
        }
        if (!keep) { conn.Close(); return null; }
        stream.Ended += s =>
        {
            lock (_gate) _streams.Remove(s);
        };
        if (conn.IsClosed) lock (_gate) _streams.Remove(stream);
        return stream;
    }

    /// <summary>Closes the streams of one device (it went offline, was removed) or all (null: the link stops).</summary>
    private void CloseStreams(string? peerId)
    {
        List<LinkStream> close;
        lock (_gate) close = _streams.Where(s => peerId is null || s.PeerId == peerId).ToList();
        foreach (var s in close) s.Dispose();
    }

    /// <summary>Open streams (diagnostics and tests).</summary>
    internal int StreamCount { get { lock (_gate) return _streams.Count; } }
}
