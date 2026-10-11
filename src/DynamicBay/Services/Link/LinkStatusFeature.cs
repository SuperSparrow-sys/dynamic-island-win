using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>What a device tells the other about itself. Battery null: no battery (desktop PC).</summary>
public sealed record LinkStatus(string Name, int? Battery, bool Charging, string Version);

/// <summary>
/// Status (Einstellungen → Geräte → Status): name, battery and DynamicBay version, sent when a device connects and
/// whenever it changes here. The other side shows it in the "Gekoppelte Geräte" card.
/// </summary>
public sealed class LinkStatusFeature : LinkFeature
{
    public const string Type = "status";

    private readonly Func<LinkStatus> _local;
    private readonly Dictionary<string, LinkStatus> _remote = new(StringComparer.Ordinal);
    private LinkStatus? _lastSent;

    /// <summary>A device sent its status (peer id, status).</summary>
    public event Action<string, LinkStatus>? Received;

    public LinkStatusFeature(LinkService link, AppSettings settings, Func<LinkStatus> local) : base(link, settings)
    {
        _local = local;
        Handle(Type, OnStatus);
        link.PeerOnline += OnPeerOnline;
        link.PeerOffline += OnPeerOffline;
    }

    public override bool Enabled => Settings.LinkShareStatus;

    public LinkStatus? StatusOf(string peerId)
    {
        lock (_remote) return _remote.TryGetValue(peerId, out var s) ? s : null;
    }

    private void OnPeerOnline(PairedDevice p) => _ = SendAsync(p.Id, Type, ToJson(_local()));

    private void OnPeerOffline(PairedDevice p)
    {
        lock (_remote) _remote.Remove(p.Id);
    }

    /// <summary>Battery or name changed here: tell the connected devices (nothing when it is the same as last time).</summary>
    public async Task<int> PublishAsync(bool force = false)
    {
        var now = _local();
        if (!force && now == _lastSent) return 0;
        _lastSent = now;
        return await BroadcastAsync(Type, ToJson(now)).ConfigureAwait(false);
    }

    private void OnStatus(LinkMessage m)
    {
        if (Parse(m.Payload, m.PeerName) is not { } s) return;
        lock (_remote) _remote[m.PeerId] = s;
        Received?.Invoke(m.PeerId, s);
    }

    internal static JsonObject ToJson(LinkStatus s) => new()
    {
        ["name"] = s.Name, ["battery"] = s.Battery, ["charging"] = s.Charging, ["version"] = s.Version,
    };

    /// <summary>Tolerant: missing fields are normal, a battery outside 0..100 counts as none.</summary>
    internal static LinkStatus? Parse(JsonNode? p, string fallbackName)
    {
        if (p is not JsonObject) return null;
        var name = LinkService.CleanName(Str(p, "name", 64) is { Length: > 0 } n ? n : fallbackName);
        int? battery = Int(p, "battery") is int b and >= 0 and <= 100 ? b : null;
        return new LinkStatus(name, battery, battery is not null && Bool(p, "charging"), Str(p, "version", 32));
    }

    public override void Dispose()
    {
        Link.PeerOnline -= OnPeerOnline;
        Link.PeerOffline -= OnPeerOffline;
        base.Dispose();
    }
}
