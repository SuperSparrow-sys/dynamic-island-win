using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>
/// Ruhe und Fokus (Einstellungen → Geräte): "Nicht stören" switched on one device is switched on the other, too (both
/// ways; the newer change wins, also after both were apart). A call or a focus session on one device makes the other
/// quiet while it lasts (<see cref="AppSettings.PeerQuiet"/>); that is never written back, so nothing echoes.
/// </summary>
public sealed class LinkQuietFeature : LinkFeature
{
    public const string Type = "quiet";

    private readonly Dictionary<string, bool> _peerQuiet = new(StringComparer.Ordinal);

    public LinkQuietFeature(LinkService link, AppSettings settings) : base(link, settings)
    {
        Handle(Type, OnQuiet);
        link.PeerOnline += OnPeerOnline;
        link.PeerOffline += OnPeerOffline;
    }

    public override bool Enabled => Settings.LinkShareQuiet;

    /// <summary>True while the other device's choice is written into the settings (do not send it back as new).</summary>
    public bool Adopting { get; private set; }

    private void OnPeerOnline(PairedDevice p) => _ = SendAsync(p.Id, Type, State());

    private void OnPeerOffline(PairedDevice p)
    {
        lock (_peerQuiet) _peerQuiet.Remove(p.Id);
        UpdatePeerQuiet();
    }

    private JsonObject State() => new()
    {
        ["dnd"] = Settings.DoNotDisturb, ["changed"] = Settings.LinkQuietChanged.ToUniversalTime().Ticks,
        ["focus"] = Settings.FocusActive, ["call"] = Settings.InMeeting,
    };

    /// <summary>"Nicht stören" was switched here by hand: it counts as the newest choice for both devices.</summary>
    public Task<int> DoNotDisturbChangedAsync()
    {
        if (Adopting) return Task.FromResult(0);
        Settings.LinkQuietChanged = DateTime.UtcNow;
        return AnnounceAsync();
    }

    /// <summary>A call or focus session started or ended here (or the switch came on): tell the connected devices.</summary>
    public Task<int> AnnounceAsync() => BroadcastAsync(Type, State());

    private void OnQuiet(LinkMessage m)
    {
        long ticks = m.Payload is JsonObject o ? LinkProtocol.Long(o, "changed") ?? 0 : 0;
        var changed = ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        bool dnd = Bool(m.Payload, "dnd");
        if (changed > Settings.LinkQuietChanged.ToUniversalTime())
        {
            Adopting = true;
            try
            {
                Settings.LinkQuietChanged = changed;
                if (Settings.DoNotDisturb != dnd)
                {
                    Settings.DoNotDisturb = dnd;
                    Log.Info($"Link: do not disturb {(dnd ? "on" : "off")} from {m.PeerName}");
                }
            }
            finally { Adopting = false; }
        }
        bool quiet = Bool(m.Payload, "focus") || Bool(m.Payload, "call");
        bool was;
        lock (_peerQuiet)
        {
            was = _peerQuiet.TryGetValue(m.PeerId, out var q) && q;
            _peerQuiet[m.PeerId] = quiet;
        }
        if (was != quiet) Log.Info($"Link: {m.PeerName} is {(quiet ? "in a call or focus session" : "free again")}");
        UpdatePeerQuiet();
    }

    /// <summary>Sets <see cref="AppSettings.PeerQuiet"/> from what the connected devices said (nothing with the switch off).</summary>
    public void UpdatePeerQuiet()
    {
        bool any;
        lock (_peerQuiet) any = Enabled && _peerQuiet.Any(kv => kv.Value && Link.IsOnline(kv.Key));
        if (Settings.PeerQuiet != any) Settings.PeerQuiet = any;
    }

    public override void Dispose()
    {
        Link.PeerOnline -= OnPeerOnline;
        Link.PeerOffline -= OnPeerOffline;
        base.Dispose();
        if (Settings.PeerQuiet) Settings.PeerQuiet = false;
    }
}
