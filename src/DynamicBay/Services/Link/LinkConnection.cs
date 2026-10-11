using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace DynamicBay.Services.Link;

/// <summary>
/// One TLS connection to another device: framed envelopes in, framed envelopes out (one writer at a time), and a
/// binary frame right after an envelope that says <c>"bin": true</c>.
/// </summary>
internal sealed class LinkConnection
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private int _closing;

    public LinkConnection(TcpClient tcp, SslStream ssl, bool outbound)
    {
        _tcp = tcp; _ssl = ssl; Outbound = outbound;
        Remote = tcp.Client.RemoteEndPoint as IPEndPoint;
        Local = tcp.Client.LocalEndPoint as IPEndPoint;
        LastReceived = DateTime.UtcNow;
        Protocol = ssl.SslProtocol;
    }

    public bool Outbound { get; }
    public IPEndPoint? Remote { get; }
    public IPEndPoint? Local { get; }

    // Filled in by the hello exchange.
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string PeerFingerprint { get; set; } = "";
    public int PeerPort { get; set; }
    public string PeerBoot { get; set; } = "";
    /// <summary>Set while this connection only carries a pairing; it becomes a normal one once both confirmed.</summary>
    public PairingSession? Pairing { get; set; }

    public DateTime LastReceived { get; private set; }
    public CancellationToken Closed => _closed.Token;
    public bool IsClosed => _closing != 0;
    /// <summary>Raised once, from whichever thread closed it.</summary>
    public event Action<LinkConnection>? OnClosed;

    public async Task SendAsync(JsonObject envelope, ReadOnlyMemory<byte>? binary = null, CancellationToken ct = default)
    {
        if (binary is not null) envelope["bin"] = true;
        var body = LinkProtocol.Encode(envelope);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await _write.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await LinkProtocol.WriteFrameAsync(_ssl, body, linked.Token).ConfigureAwait(false);
            if (binary is { } b) await LinkProtocol.WriteFrameAsync(_ssl, b, linked.Token).ConfigureAwait(false);
            await _ssl.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            Close();
            throw new IOException("connection closed", ex);
        }
        finally { _write.Release(); }
    }

    /// <summary>Next envelope (and its binary frame), or null when the connection ended.</summary>
    public async Task<(JsonObject Envelope, byte[]? Binary)?> ReceiveAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var frame = await LinkProtocol.ReadFrameAsync(_ssl, linked.Token).ConfigureAwait(false);
        if (frame is null) return null;
        LastReceived = DateTime.UtcNow;
        var env = LinkProtocol.Decode(frame) ?? throw new InvalidDataException("frame is no envelope");
        byte[]? bin = null;
        if (LinkProtocol.Bool(env, "bin"))
        {
            bin = await LinkProtocol.ReadFrameAsync(_ssl, linked.Token).ConfigureAwait(false)
                  ?? throw new EndOfStreamException("binary frame missing");
            LastReceived = DateTime.UtcNow;
        }
        return (env, bin);
    }

    /// <summary>A bare frame without envelope (streams, see <see cref="LinkStream"/>).</summary>
    public async Task SendRawAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await _write.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await LinkProtocol.WriteFrameAsync(_ssl, frame, linked.Token).ConfigureAwait(false);
            await _ssl.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            Close();
            throw new IOException("connection closed", ex);
        }
        finally { _write.Release(); }
    }

    /// <summary>Next bare frame, or null when the connection ended.</summary>
    public async Task<byte[]?> ReceiveRawAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var frame = await LinkProtocol.ReadFrameAsync(_ssl, linked.Token).ConfigureAwait(false);
        if (frame is not null) LastReceived = DateTime.UtcNow;
        return frame;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        try { _closed.Cancel(); } catch { }
        try { _ssl.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        try { OnClosed?.Invoke(this); } catch { }
    }

    /// <summary>
    /// Closes after the last message had time to arrive. Closing a socket with unread data resets it, and Windows then
    /// drops what the other side had not read yet (the decline or unpair it should see). The other side usually closes
    /// first, which ends this one through its read loop.
    /// </summary>
    public void CloseSoon() => _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Close(), TaskScheduler.Default);

    public System.Security.Authentication.SslProtocols Protocol { get; }

    public override string ToString() => $"{(Outbound ? "to" : "from")} {Remote} ({Protocol})";
}
