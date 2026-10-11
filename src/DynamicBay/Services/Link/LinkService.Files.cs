using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DynamicBay.Services.Link;

// File transfer: offer (name, size, SHA-256) → the receiver accepts or declines → chunks of 256 KiB as binary frames →
// end → the receiver checks size and hash, renames the .part file and answers done. Nothing is opened automatically.
public sealed partial class LinkService
{
    private sealed class Outgoing(LinkConnection conn)
    {
        public readonly LinkConnection Conn = conn;
        /// <summary>null = accepted, otherwise the reason it was not.</summary>
        public readonly TaskCompletionSource<string?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Incoming(IncomingFileOffer offer, LinkConnection conn, string sha256)
    {
        public readonly IncomingFileOffer Offer = offer;
        public readonly LinkConnection Conn = conn;
        public readonly string Sha256 = sha256;
        public bool Accepted;
        public FileStream? Stream;
        public IncrementalHash? Hash;
        public string Folder = "", PartPath = "";
        public long Received;
        public DateTime LastProgress;
    }

    private readonly Dictionary<string, Outgoing> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Incoming> _incoming = new(StringComparer.Ordinal);

    /// <summary>
    /// Sends a file to a connected device. The other side has to accept it first (<see cref="FileOffered"/> there).
    /// <paramref name="progress"/> gets 0..1.
    /// </summary>
    public Task<FileTransferResult> SendFileAsync(string peerId, string path, IProgress<double>? progress = null, CancellationToken ct = default) =>
        SendFileAsync(peerId, path, null, progress, ct);

    /// <summary><paramref name="remoteName"/>: the name offered instead of the file's own (tests send hostile names).</summary>
    internal async Task<FileTransferResult> SendFileAsync(string peerId, string path, string? remoteName, IProgress<double>? progress, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("file not found", path);
        LinkConnection? conn;
        lock (_gate) _sessions.TryGetValue(peerId, out conn);
        if (conn is null) return FileTransferResult.Offline;

        string hash;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
        string fid = Guid.NewGuid().ToString("N");
        var o = new Outgoing(conn);
        lock (_gate) _outgoing[fid] = o;
        try
        {
            await conn.SendAsync(new JsonObject
            {
                ["t"] = LinkProtocol.FileOffer, ["fid"] = fid, ["name"] = remoteName ?? info.Name, ["size"] = info.Length, ["sha256"] = hash,
            }, null, ct).ConfigureAwait(false);

            string? answer;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(_opts.OfferTimeout);
                try { answer = await o.Answer.Task.WaitAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { answer = "timeout"; }
            }
            if (answer is not null)
            {
                if (answer == "timeout") _ = SendQuietly(conn, new JsonObject { ["t"] = LinkProtocol.FileCancel, ["fid"] = fid });
                Log($"Link: file not accepted by {conn.PeerName} ({answer})");
                return answer switch { "too-large" => FileTransferResult.TooLarge, "offline" => FileTransferResult.Offline, _ => FileTransferResult.Declined };
            }

            var buffer = new byte[LinkProtocol.ChunkSize];
            long sent = 0;
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                int n;
                while ((n = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (o.Done.Task.IsCompleted) break; // the receiver gave up (disk full, cancelled)
                    await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.FileChunk, ["fid"] = fid }, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    sent += n;
                    progress?.Report(info.Length == 0 ? 1 : (double)sent / info.Length);
                }
            }
            if (!o.Done.Task.IsCompleted)
                await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.FileEnd, ["fid"] = fid }, null, ct).ConfigureAwait(false);
            string? failure;
            try { failure = await o.Done.Task.WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
            catch (TimeoutException) { failure = "timeout"; }
            Log($"Link: file to {conn.PeerName}: {failure ?? "ok"} ({info.Length} bytes)");
            if (failure is null) { progress?.Report(1); return FileTransferResult.Done; }
            return failure switch { "offline" => FileTransferResult.Offline, "too-large" => FileTransferResult.TooLarge, _ => FileTransferResult.Failed };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = SendQuietly(conn, new JsonObject { ["t"] = LinkProtocol.FileCancel, ["fid"] = fid });
            return FileTransferResult.Cancelled;
        }
        catch (IOException) { return FileTransferResult.Offline; }
        finally
        {
            lock (_gate) _outgoing.Remove(fid);
        }
    }

    private async Task OnFileMessage(LinkConnection conn, string type, JsonObject env, byte[]? bin)
    {
        string? fid = LinkProtocol.Str(env, "fid");
        if (fid is null or { Length: 0 or > 64 }) return;
        switch (type)
        {
            case LinkProtocol.FileOffer: await OnOffer(conn, fid, env).ConfigureAwait(false); return;
            case LinkProtocol.FileAccept:
            case LinkProtocol.FileDecline:
            case LinkProtocol.FileDone:
            {
                Outgoing? o;
                lock (_gate) _outgoing.TryGetValue(fid, out o);
                if (o is null || o.Conn != conn) return;
                if (type == LinkProtocol.FileAccept) o.Answer.TrySetResult(null);
                else if (type == LinkProtocol.FileDecline) o.Answer.TrySetResult(LinkProtocol.Str(env, "reason") ?? "declined");
                else o.Done.TrySetResult(LinkProtocol.Bool(env, "ok") ? null : LinkProtocol.Str(env, "reason") ?? "failed");
                return;
            }
            case LinkProtocol.FileCancel:
            {
                Outgoing? o;
                lock (_gate) _outgoing.TryGetValue(fid, out o);
                if (o is not null && o.Conn == conn) { o.Answer.TrySetResult("cancelled"); o.Done.TrySetResult("cancelled"); }
                Incoming? i;
                lock (_gate) _incoming.TryGetValue(fid, out i);
                if (i is not null && i.Conn == conn) DropIncoming(fid, i, "cancelled", tell: false);
                return;
            }
            case LinkProtocol.FileChunk: await OnChunk(conn, fid, bin).ConfigureAwait(false); return;
            case LinkProtocol.FileEnd: await OnEnd(conn, fid).ConfigureAwait(false); return;
        }
    }

    private async Task OnOffer(LinkConnection conn, string fid, JsonObject env)
    {
        long size = LinkProtocol.Long(env, "size") ?? -1;
        string? sha = LinkProtocol.Str(env, "sha256");
        string name = LinkProtocol.SanitizeFileName(LinkProtocol.Str(env, "name"));
        async Task Decline(string reason)
        {
            Log($"Link: file {name} from {conn.PeerName} declined ({reason})");
            await SendQuietly(conn, new JsonObject { ["t"] = LinkProtocol.FileDecline, ["fid"] = fid, ["reason"] = reason }).ConfigureAwait(false);
        }
        if (size < 0 || sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit)) { await Decline("invalid").ConfigureAwait(false); return; }
        if (size > _opts.MaxFileBytes) { await Decline("too-large").ConfigureAwait(false); return; }
        if (FileOffered is null) { await Decline("unsupported").ConfigureAwait(false); return; }

        var offer = new IncomingFileOffer(conn.PeerId, conn.PeerName, fid, name, size);
        var entry = new Incoming(offer, conn, sha.ToUpperInvariant());
        lock (_gate)
        {
            if (_incoming.ContainsKey(fid)) return;
            _incoming[fid] = entry;
        }
        offer.OnAccept = (folder, max) => AcceptOffer(fid, entry, folder, max);
        offer.OnDecline = () =>
        {
            bool mine;
            lock (_gate) mine = _incoming.Remove(fid);
            if (mine) _ = Decline("declined");
        };
        Post(() => FileOffered?.Invoke(offer));
    }

    private async Task<bool> AcceptOffer(string fid, Incoming entry, string? folder, long? maxBytes)
    {
        lock (_gate)
        {
            if (entry.Accepted || !_incoming.TryGetValue(fid, out var e) || e != entry) return false;
            entry.Accepted = true;
        }
        var conn = entry.Conn;
        if (entry.Offer.Size > (maxBytes ?? _opts.MaxFileBytes))
        {
            lock (_gate) _incoming.Remove(fid);
            await SendQuietly(conn, new JsonObject { ["t"] = LinkProtocol.FileDecline, ["fid"] = fid, ["reason"] = "too-large" }).ConfigureAwait(false);
            return false;
        }
        try
        {
            entry.Folder = Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? _opts.DownloadFolder : folder);
            Directory.CreateDirectory(entry.Folder);
            entry.PartPath = LinkProtocol.UniquePath(entry.Folder, entry.Offer.FileName + ".part");
            entry.Stream = new FileStream(entry.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            entry.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Core.Log.Error("Link receive", ex);
            DropIncoming(fid, entry, "disk", tell: true);
            return false;
        }
        try { await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.FileAccept, ["fid"] = fid }).ConfigureAwait(false); }
        catch (IOException) { DropIncoming(fid, entry, "offline", tell: false); return false; }
        return true;
    }

    private async Task OnChunk(LinkConnection conn, string fid, byte[]? bin)
    {
        Incoming? e;
        lock (_gate) _incoming.TryGetValue(fid, out e);
        if (e is null || e.Conn != conn || e.Stream is null || bin is null) return;
        if (e.Received + bin.Length > e.Offer.Size) { DropIncoming(fid, e, "size", tell: true); return; }
        try { await e.Stream.WriteAsync(bin).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; } // dropped meanwhile
        catch (IOException ex) { Core.Log.Error("Link receive", ex); DropIncoming(fid, e, "disk", tell: true); return; }
        e.Hash!.AppendData(bin);
        e.Received += bin.Length;
        if (DateTime.UtcNow - e.LastProgress > TimeSpan.FromMilliseconds(100) || e.Received == e.Offer.Size)
        {
            e.LastProgress = DateTime.UtcNow;
            long r = e.Received;
            Post(() => e.Offer.RaiseProgress(r));
        }
    }

    private async Task OnEnd(LinkConnection conn, string fid)
    {
        Incoming? e;
        lock (_gate) _incoming.TryGetValue(fid, out e);
        if (e is null || e.Conn != conn || e.Stream is null) return;
        string hash = Convert.ToHexString(e.Hash!.GetHashAndReset());
        try { await e.Stream.FlushAsync().ConfigureAwait(false); } catch (IOException) { }
        await e.Stream.DisposeAsync().ConfigureAwait(false);
        e.Stream = null;
        if (e.Received != e.Offer.Size || hash != e.Sha256) { DropIncoming(fid, e, e.Received != e.Offer.Size ? "size" : "hash", tell: true); return; }
        string final;
        try
        {
            final = LinkProtocol.UniquePath(e.Folder, e.Offer.FileName);
            File.Move(e.PartPath, final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.Log.Error("Link receive", ex);
            DropIncoming(fid, e, "disk", tell: true);
            return;
        }
        TaildropService.MarkFromInternet(final); // like a download: SmartScreen checks it before it runs
        lock (_gate) _incoming.Remove(fid);
        e.Hash.Dispose();
        await SendQuietly(conn, new JsonObject { ["t"] = LinkProtocol.FileDone, ["fid"] = fid, ["ok"] = true }).ConfigureAwait(false);
        Log($"Link: received {e.Offer.Size} bytes from {conn.PeerName}");
        Post(() => FileReceived?.Invoke(e.Offer, final));
    }

    /// <summary>Ends an incoming transfer that did not work: .part file away, the sender told (when it still listens).</summary>
    private void DropIncoming(string fid, Incoming e, string reason, bool tell)
    {
        lock (_gate)
        {
            if (!_incoming.TryGetValue(fid, out var cur) || cur != e) return;
            _incoming.Remove(fid);
        }
        try { e.Stream?.Dispose(); } catch { }
        e.Stream = null;
        e.Hash?.Dispose();
        if (e.PartPath.Length > 0) try { File.Delete(e.PartPath); } catch { }
        if (tell) _ = SendQuietly(e.Conn, new JsonObject { ["t"] = LinkProtocol.FileDone, ["fid"] = fid, ["ok"] = false, ["reason"] = reason });
        Log($"Link: file {e.Offer.FileName} from {e.Offer.PeerName} failed ({reason})");
        if (e.Accepted) Post(() => FileFailed?.Invoke(e.Offer, reason));
    }

    /// <summary>The connection is gone: every transfer over it ends.</summary>
    private void FailTransfers(LinkConnection conn)
    {
        List<Outgoing> outgoing;
        List<(string, Incoming)> incoming;
        lock (_gate)
        {
            outgoing = _outgoing.Values.Where(o => o.Conn == conn).ToList();
            incoming = _incoming.Where(kv => kv.Value.Conn == conn).Select(kv => (kv.Key, kv.Value)).ToList();
        }
        foreach (var o in outgoing) { o.Answer.TrySetResult("offline"); o.Done.TrySetResult("offline"); }
        foreach (var (fid, e) in incoming) DropIncoming(fid, e, "offline", tell: false);
    }
}
