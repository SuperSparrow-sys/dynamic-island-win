using System.IO;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>Another feature takes an incoming file (a big clipboard picture): where it goes and what happens then.</summary>
public sealed record LinkFileClaim(string Folder, long MaxBytes, Action<string> Received);

/// <summary>
/// Dateien (Einstellungen → Geräte → Dateien): "An Laptop senden" from the shelf, and files from paired devices are
/// taken without asking, saved in Downloads\DynamicBay and put into the shelf. Nothing is opened. With the switch off
/// this side sends nothing and declines every offer (except the ones another feature claims).
/// </summary>
public sealed class LinkFilesFeature : LinkFeature
{
    private readonly List<Func<IncomingFileOffer, LinkFileClaim?>> _claimers = new();
    private readonly Dictionary<string, LinkFileClaim> _claimed = new(StringComparer.Ordinal);

    /// <summary>Received files go here (null: the link's download folder, Downloads\DynamicBay).</summary>
    public string? Folder { get; init; }

    /// <summary>Received files land in this shelf when it is switched on.</summary>
    public ShelfService? Shelf { get; set; }

    public event Action<IncomingFileOffer>? Started;
    public event Action<IncomingFileOffer, long>? Progress;
    public event Action<IncomingFileOffer, string>? Received;
    public event Action<IncomingFileOffer, string>? Failed;

    public LinkFilesFeature(LinkService link, AppSettings settings) : base(link, settings)
    {
        link.FileOffered += OnOffered;
        link.FileReceived += OnReceived;
        link.FileFailed += OnFailed;
    }

    public override bool Enabled => Settings.LinkShareFiles;

    /// <summary>Lets another feature take offers before this one (see <see cref="LinkFileClaim"/>).</summary>
    internal void AddClaimer(Func<IncomingFileOffer, LinkFileClaim?> claimer)
    {
        lock (_claimers) _claimers.Add(claimer);
    }

    /// <summary>Sends a file. Declined right away while the switch is off; Offline when the device is not connected.</summary>
    public async Task<FileTransferResult> SendAsync(string peerId, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Enabled) return FileTransferResult.Declined;
        if (!Link.IsOnline(peerId)) return FileTransferResult.Offline;
        if (!File.Exists(path)) return FileTransferResult.Failed; // folders are not sent (yet)
        try { return await Link.SendFileAsync(peerId, path, progress, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Link send file", ex);
            return FileTransferResult.Failed;
        }
    }

    private void OnOffered(IncomingFileOffer offer)
    {
        LinkFileClaim? claim = null;
        lock (_claimers)
            foreach (var c in _claimers)
                if ((claim = c(offer)) is not null) break;
        if (claim is not null)
        {
            lock (_claimed) _claimed[offer.TransferId] = claim;
            _ = offer.Accept(claim.Folder, claim.MaxBytes);
            return;
        }
        if (!Enabled) { offer.Decline(); return; }
        offer.Progress += bytes => Progress?.Invoke(offer, bytes);
        _ = AcceptAsync(offer);
    }

    private async Task AcceptAsync(IncomingFileOffer offer)
    {
        if (await offer.Accept(Folder).ConfigureAwait(false)) Started?.Invoke(offer);
    }

    private void OnReceived(IncomingFileOffer offer, string path)
    {
        LinkFileClaim? claim;
        lock (_claimed) _claimed.Remove(offer.TransferId, out claim);
        if (claim is not null)
        {
            try { claim.Received(path); } catch (Exception ex) { Log.Error("Link claimed file", ex); }
            return;
        }
        if (Shelf is not null && Settings.ShelfEnabled) Shelf.Add(new[] { path }, announce: false);
        Received?.Invoke(offer, path);
    }

    private void OnFailed(IncomingFileOffer offer, string reason)
    {
        bool claimed;
        lock (_claimed) claimed = _claimed.Remove(offer.TransferId);
        if (!claimed) Failed?.Invoke(offer, reason);
    }

    public override void Dispose()
    {
        Link.FileOffered -= OnOffered;
        Link.FileReceived -= OnReceived;
        Link.FileFailed -= OnFailed;
        base.Dispose();
    }
}
