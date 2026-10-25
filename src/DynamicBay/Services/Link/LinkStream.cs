using System.IO;
using System.Security.Authentication;

namespace DynamicBay.Services.Link;

/// <summary>
/// A second TLS connection to a paired device for one long-running flow (the sound of "Ton an anderes Gerät"), so it
/// never waits behind messages or file chunks on the main connection. Same pinned certificates as the main one; it
/// carries bare frames (4-byte length + bytes, at most <see cref="LinkProtocol.MaxFrame"/>), the meaning is up to the
/// feature. It closes when either side disposes it, when the device goes offline or when the link stops.
/// </summary>
public sealed class LinkStream : IDisposable
{
    private readonly LinkConnection _conn;
    private int _ended;

    internal LinkStream(LinkConnection conn, string purpose)
    {
        _conn = conn;
        Purpose = purpose;
        conn.OnClosed += _ => RaiseEnded();
    }

    public string PeerId => _conn.PeerId;
    public string PeerName => _conn.PeerName;
    /// <summary>What the stream is for ("audio"); the opener chooses it.</summary>
    public string Purpose { get; }
    /// <summary>This device opened it.</summary>
    public bool Outbound => _conn.Outbound;
    public bool IsClosed => _conn.IsClosed;
    public CancellationToken Closed => _conn.Closed;

    /// <summary>Raised once when the stream is gone (either side, offline, link stopped), from any thread.</summary>
    public event Action<LinkStream>? Ended;

    /// <summary>Sends one frame. Throws <see cref="IOException"/> when the stream is closed.</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        try { await _conn.SendRawAsync(frame, ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new IOException("stream closed", ex); }
    }

    /// <summary>The next frame, or null once the stream is closed (by anyone).</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            var frame = await _conn.ReceiveRawAsync(ct).ConfigureAwait(false);
            if (frame is null) _conn.Close();
            return frame;
        }
        catch (OperationCanceledException) when (!_conn.IsClosed && ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or AuthenticationException or InvalidOperationException)
        {
            _conn.Close();
            return null;
        }
    }

    public void Dispose() => _conn.Close();

    /// <summary>
    /// Closes after the last frame had time to arrive (or as soon as the other side closes). Closing at once with unread
    /// frames from the other side resets the connection, and the other side may lose the frame just sent.
    /// </summary>
    public void CloseSoon() => _conn.CloseSoon();

    private void RaiseEnded()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        try { Ended?.Invoke(this); } catch { }
    }

    public override string ToString() => $"{Purpose} {_conn}";
}
