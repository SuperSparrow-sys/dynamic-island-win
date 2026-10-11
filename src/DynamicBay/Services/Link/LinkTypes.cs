using System.IO;
using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

public enum LinkPeerState { Offline, Connecting, Online, Rejected }

[Flags]
public enum LinkVia { None = 0, Lan = 1, Tailscale = 2, Manual = 4 }

public enum PairingResult { Paired, Declined, TimedOut, Failed }

public enum FileTransferResult { Done, Declined, TooLarge, Offline, Cancelled, Failed }

/// <summary>Settings of a <see cref="LinkService"/>. The defaults are the real ones; tests change ports and timings.</summary>
public sealed class LinkOptions
{
    /// <summary>TCP port to listen on; 0 picks a free one (tests).</summary>
    public int Port { get; init; } = LinkProtocol.Port;
    public int DiscoveryPort { get; init; } = LinkProtocol.DiscoveryPort;
    public bool LanDiscovery { get; init; } = true;
    public bool TailscaleDiscovery { get; init; } = true;
    /// <summary>Reconnect right away on network changes and after sleep.</summary>
    public bool WatchSystem { get; init; } = true;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long "Koppeln erlauben" stays open and how long both sides have to confirm the code.</summary>
    public TimeSpan PairingWindow { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Pairing attempts accepted per <see cref="PairingRatePeriod"/> (the rest is turned away).</summary>
    public int PairingRateLimit { get; init; } = 5;
    public TimeSpan PairingRatePeriod { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Received files go here unless the accept call names another folder.</summary>
    public string DownloadFolder { get; init; } = Path.Combine(KnownFolders.Downloads, "DynamicBay");
    /// <summary>Larger offers are declined without asking.</summary>
    public long MaxFileBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    /// <summary>How long a sent file waits for the other side to accept it.</summary>
    public TimeSpan OfferTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Where state changes are written (never keys). Default: the app log.</summary>
    public Action<string> Log { get; init; } = DynamicBay.Core.Log.Info;
}

/// <summary>A message from a paired device. <see cref="Payload"/> is what the sender passed to SendAsync.</summary>
public sealed record LinkMessage(string PeerId, string PeerName, string Type, string Id, JsonNode? Payload);

/// <summary>A DynamicBay found nearby, over Tailscale or at an address the user typed in.</summary>
public sealed record DiscoveredDevice(string Id, string Name, IReadOnlyList<string> Addresses, LinkVia Via, bool Paired, DateTime LastSeen)
{
    /// <summary>Tailscale peers answer the probe on the port but may not have said who they are yet: Id is "" then.</summary>
    public bool Known => Id.Length > 0;
}

/// <summary>
/// One pairing between this device and another. Both sides show <see cref="Code"/>; the pairing is stored only after
/// the user confirmed on both (<see cref="Confirm"/>) within the time limit.
/// </summary>
public sealed class PairingSession
{
    internal PairingSession(string peerId, string peerName, string peerFingerprint, string code, bool initiator)
    {
        PeerId = peerId; PeerName = peerName; PeerFingerprint = peerFingerprint; Code = code; Initiator = initiator;
    }

    public string PeerId { get; }
    public string PeerName { get; }
    public string PeerFingerprint { get; }
    /// <summary>Six digits, the same on both devices unless someone sits in between.</summary>
    public string Code { get; }
    /// <summary>This device started the pairing.</summary>
    public bool Initiator { get; }

    internal readonly TaskCompletionSource<PairingResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<PairingResult> Completion => Done.Task;
    internal Action<bool>? OnConfirm;
    internal bool LocalConfirmed, LocalSent, RemoteConfirmed;

    /// <summary>The user compared the code: true = it matches, false = cancel.</summary>
    public void Confirm(bool accept) => OnConfirm?.Invoke(accept);
}

/// <summary>A file another device wants to send. Call <see cref="Accept"/> or <see cref="Decline"/> once.</summary>
public sealed class IncomingFileOffer
{
    internal IncomingFileOffer(string peerId, string peerName, string transferId, string fileName, long size)
    {
        PeerId = peerId; PeerName = peerName; TransferId = transferId; FileName = fileName; Size = size;
    }

    public string PeerId { get; }
    public string PeerName { get; }
    public string TransferId { get; }
    /// <summary>Already cleaned (no folders, no reserved names).</summary>
    public string FileName { get; }
    public long Size { get; }

    internal Func<string?, long?, Task<bool>>? OnAccept;
    internal Action? OnDecline;

    /// <summary>Starts the transfer into <paramref name="folder"/> (default: the service's download folder). False: too large or gone.</summary>
    public Task<bool> Accept(string? folder = null, long? maxBytes = null) => OnAccept?.Invoke(folder, maxBytes) ?? Task.FromResult(false);
    public void Decline() => OnDecline?.Invoke();

    /// <summary>Bytes received so far (raised on the service's context).</summary>
    public event Action<long>? Progress;
    internal void RaiseProgress(long bytes) => Progress?.Invoke(bytes);
}
