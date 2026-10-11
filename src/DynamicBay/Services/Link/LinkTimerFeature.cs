using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>
/// A timer as it goes over the link. Mode: "timer", "focus" or "break". Owner: id of the device it belongs to - where
/// it was started or that it was handed to ("Timer übernehmen"); without a main device only the owner shows its end.
/// </summary>
public sealed record LinkTimerState(bool Active, bool Running, string Mode, double TotalSeconds, double RemainingSeconds, string Owner);

/// <summary>
/// Timer (Einstellungen → Geräte → Timer): a timer or Pomodoro started, paused, extended or stopped on one device shows
/// the same on the other. Who changed last wins on both: every change carries a counter that is one higher than the
/// highest either device has seen (no clocks involved, they may differ); at the same count the higher device id wins.
/// The end is not sent: both run out at the same moment, and <see cref="ShowsEnd"/> decides where it is announced.
/// </summary>
public sealed class LinkTimerFeature : LinkFeature
{
    public const string Type = "timer";

    private readonly Func<LinkTimerState?> _local;
    private long _seq;
    private string _seqFrom = "";

    /// <summary>The other device's switch: only then does it have the timer, too.</summary>
    public LinkPeerSwitch Peer { get; }

    /// <summary>The other device changed the timer (peer id, peer name, the new state; Owner may be this device's id).</summary>
    public event Action<string, string, LinkTimerState>? Received;

    /// <param name="local">The timer here right now; null or inactive: none.</param>
    public LinkTimerFeature(LinkService link, AppSettings settings, Func<LinkTimerState?> local) : base(link, settings)
    {
        _local = local;
        Peer = new LinkPeerSwitch(link, Type, () => Enabled);
        Handle(Type, OnTimer);
        link.PeerOnline += OnPeerOnline;
    }

    public override bool Enabled => Settings.LinkShareTimer;

    /// <summary>
    /// The device that connects learns the timer here: a running one that belongs here (taken unless it has a newer
    /// one), or "none" (a copy of this device's timer that ended or was stopped meanwhile goes away there).
    /// </summary>
    private void OnPeerOnline(PairedDevice p)
    {
        var s = _local() is { Active: true } l && l.Owner == Link.DeviceId ? l : new LinkTimerState(false, false, "timer", 0, 0, Link.DeviceId);
        var json = ToJson(s, Current());
        json["sync"] = true;
        _ = SendAsync(p.Id, Type, json);
    }

    private long Current() { lock (this) return _seq; }

    /// <summary>The timer changed here (by the user, or handed over): tell the connected devices.</summary>
    public Task<int> PublishAsync(LinkTimerState state)
    {
        long seq;
        lock (this) { seq = ++_seq; _seqFrom = Link.DeviceId; }
        return BroadcastAsync(Type, ToJson(state, seq));
    }

    private void OnTimer(LinkMessage m)
    {
        if (Parse(m.Payload) is not { } s) return;
        long seq = m.Payload is JsonObject o ? LinkProtocol.Long(o, "seq") ?? 0 : 0;
        bool sync = Bool(m.Payload, "sync");
        var here = _local();
        bool hereActive = here is { Active: true };
        if (sync && !s.Active && !(hereActive && here!.Owner == m.PeerId)) return; // "none" only ends a copy of its own timer
        lock (this)
        {
            // On connect a running timer is taken when there is none here, whatever the counters say.
            if (!Newer(seq, m.PeerId, _seq, _seqFrom) && !(sync && s.Active && !hereActive)) return;
            if (seq >= _seq) { _seq = seq; _seqFrom = m.PeerId; }
        }
        Log.Info($"Link: timer from {m.PeerName}: {(s.Active ? (s.Running ? "running" : "paused") + $", {s.Mode}, {Math.Round(s.RemainingSeconds)} s left" : "stopped")}");
        Received?.Invoke(m.PeerId, m.PeerName, s);
    }

    /// <summary>A change wins with a higher counter; at the same counter the device with the higher id wins (on both sides alike).</summary>
    internal static bool Newer(long seq, string from, long currentSeq, string currentFrom) =>
        seq > currentSeq || seq == currentSeq && string.CompareOrdinal(from, currentFrom) > 0;

    internal static JsonObject ToJson(LinkTimerState s, long seq) => new()
    {
        ["active"] = s.Active, ["running"] = s.Running, ["mode"] = s.Mode, ["total"] = Math.Round(s.TotalSeconds, 2),
        ["remaining"] = Math.Round(s.RemainingSeconds, 2), ["owner"] = s.Owner, ["seq"] = seq,
    };

    /// <summary>Tolerant: unknown modes become "timer", times are clamped to a day; a running timer without time left is stopped.</summary>
    internal static LinkTimerState? Parse(JsonNode? p)
    {
        if (p is not JsonObject) return null;
        var mode = Str(p, "mode", 16) is "focus" or "break" ? Str(p, "mode", 16) : "timer";
        double total = Math.Clamp(Num(p, "total"), 0, 86400), remaining = Math.Clamp(Num(p, "remaining"), 0, 86400);
        bool active = Bool(p, "active") && remaining > 0;
        return new LinkTimerState(active, active && Bool(p, "running"), mode, Math.Max(total, remaining), remaining, Str(p, "owner", 32));
    }

    /// <summary>
    /// Where the end of a shared timer is announced, once: with a main device there (the quiet one stays silent),
    /// otherwise on the device the timer belongs to. A timer the other device does not have (its switch off, not
    /// connected) is announced here as always.
    /// </summary>
    internal static bool ShowsEnd(bool sharedWithPeer, bool mainDeviceChosen, bool isMain, bool ownedHere) =>
        !sharedWithPeer || (mainDeviceChosen ? isMain : ownedHere);

    public override void Dispose()
    {
        Link.PeerOnline -= OnPeerOnline;
        Peer.Dispose();
        base.Dispose();
    }
}
