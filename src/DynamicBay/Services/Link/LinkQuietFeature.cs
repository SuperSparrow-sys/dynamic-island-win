using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>
/// Ruhe und Fokus (Einstellungen → Geräte): "Nicht stören" switched on one device is switched on the other, too (both
/// ways; the newer change wins, also after both were apart). A call or a focus session on one device makes the other
/// quiet while it lasts (<see cref="AppSettings.PeerQuiet"/>); that is never written back, so nothing echoes.
/// The focus the iPhone reported to one device is applied on the other with its own profile of that name
/// (<see cref="AppSettings.PhoneFocusPeer"/>); only a focus reported directly is sent.
/// </summary>
public sealed class LinkQuietFeature : LinkFeature
{
    public const string Type = "quiet";

    private readonly Dictionary<string, bool> _peerQuiet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _peerPhone = new(StringComparer.Ordinal);

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
        lock (_peerQuiet) { _peerQuiet.Remove(p.Id); _peerPhone.Remove(p.Id); }
        UpdatePeerQuiet();
    }

    private JsonObject State() => new()
    {
        ["dnd"] = Settings.DoNotDisturb, ["changed"] = Settings.LinkQuietChanged.ToUniversalTime().Ticks,
        ["focus"] = Settings.FocusActive, ["call"] = Settings.InMeeting, ["phone"] = Settings.PhoneFocusHere,
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
        string phone = Services.PhoneFocusProtocol.Clean(m.Payload is JsonObject p && p["phone"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "");
        bool was;
        string wasPhone;
        lock (_peerQuiet)
        {
            was = _peerQuiet.TryGetValue(m.PeerId, out var q) && q;
            _peerQuiet[m.PeerId] = quiet;
            wasPhone = _peerPhone.TryGetValue(m.PeerId, out var f) ? f : "";
            _peerPhone[m.PeerId] = phone;
        }
        if (was != quiet) Log.Info($"Link: {m.PeerName} is {(quiet ? "in a call or focus session" : "free again")}");
        if (wasPhone != phone) Log.Info($"Link: iPhone focus from {m.PeerName}: {(phone.Length > 0 ? phone : "none")}");
        UpdatePeerQuiet();
    }

    /// <summary>
    /// Sets <see cref="AppSettings.PeerQuiet"/> and <see cref="AppSettings.PhoneFocusPeer"/> from what the connected
    /// devices said (nothing with the switch off).
    /// </summary>
    public void UpdatePeerQuiet()
    {
        bool any;
        string phone;
        lock (_peerQuiet)
        {
            any = Enabled && _peerQuiet.Any(kv => kv.Value && Link.IsOnline(kv.Key));
            phone = Enabled ? _peerPhone.Where(kv => kv.Value.Length > 0 && Link.IsOnline(kv.Key)).Select(kv => kv.Value).FirstOrDefault() ?? "" : "";
        }
        if (Settings.PeerQuiet != any) Settings.PeerQuiet = any;
        if (Settings.PhoneFocusPeer != phone) Settings.PhoneFocusPeer = phone;
    }

    public override void Dispose()
    {
        Link.PeerOnline -= OnPeerOnline;
        Link.PeerOffline -= OnPeerOffline;
        base.Dispose();
        if (Settings.PeerQuiet) Settings.PeerQuiet = false;
        if (Settings.PhoneFocusPeer.Length > 0) Settings.PhoneFocusPeer = "";
    }
}
