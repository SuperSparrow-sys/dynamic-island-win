using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>A battery event of another device. Kind: "low", "plugged", "unplugged" or "full".</summary>
public sealed record LinkBatteryEvent(string PeerId, string PeerName, string Kind, int Percent);

/// <summary>
/// Akku (Einstellungen → Geräte): the laptop's battery running low, the charger plugged in or out, full - shown on the
/// main device as "Laptop: Akku 15 %". A quiet device leaves its own battery peek out then (see
/// <see cref="ShowsOwnPeek"/>), so it appears once.
/// </summary>
public sealed class LinkBatteryFeature : LinkFeature
{
    public const string Type = "battery";
    internal static readonly string[] Kinds = { "low", "plugged", "unplugged", "full" };

    /// <summary>The other device's switch: only then does it show this device's battery.</summary>
    public LinkPeerSwitch Peer { get; }

    public event Action<LinkBatteryEvent>? Received;

    public LinkBatteryFeature(LinkService link, AppSettings settings) : base(link, settings)
    {
        Peer = new LinkPeerSwitch(link, Type, () => Enabled);
        Handle(Type, OnBattery);
    }

    public override bool Enabled => Settings.LinkShareBattery;

    /// <summary>A battery event here: to the connected devices.</summary>
    public Task<int> PublishAsync(string kind, int percent)
    {
        if (!Kinds.Contains(kind)) return Task.FromResult(0);
        return BroadcastAsync(Type, new JsonObject { ["kind"] = kind, ["percent"] = Math.Clamp(percent, 0, 100) });
    }

    private void OnBattery(LinkMessage m)
    {
        var kind = Str(m.Payload, "kind", 16);
        if (!Kinds.Contains(kind) || Int(m.Payload, "percent") is not int pct || pct is < 0 or > 100) return;
        Log.Info($"Link: battery {kind} on {m.PeerName} ({pct} %)");
        Received?.Invoke(new LinkBatteryEvent(m.PeerId, m.PeerName, kind, pct));
    }

    /// <summary>
    /// A device shows its own battery peek unless it is the quiet one and the main device takes it (its switch on):
    /// then the main device shows it, with this device's name.
    /// </summary>
    internal static bool ShowsOwnPeek(bool isMain, bool sharedWithMain) => isMain || !sharedWithMain;

    public override void Dispose()
    {
        Peer.Dispose();
        base.Dispose();
    }
}
