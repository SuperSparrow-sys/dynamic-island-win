using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>A peek another device handed over because this one is the main device.</summary>
public sealed record LinkPeek(string PeerId, string PeerName, string Title, string Subtitle, string? IconKey);

/// <summary>
/// Hauptgerät (Einstellungen → Geräte): while two paired devices are connected, one of them shows everything and the
/// other stays quiet - so no message arrives twice. The choice ("", "auto" or a device id) is the same on both: who
/// changes it last wins and tells the other. "auto" = the device with the mouse (CursorHop) or, without CursorHop, the
/// one used last. The quiet device sends its peeks to the main one; offline, each device shows everything itself again.
/// </summary>
public sealed class LinkRoleFeature : LinkFeature
{
    public const string Type = "role", PeekType = "peek";
    public const string Auto = "auto", This = "this", Other = "other";

    // The setting is relative ("this" / "other": it means the same after a settings export to another PC); on the
    // wire the main device is named by its id, so both devices read the same choice.
    internal string ToWire(string setting, string peerId) => setting switch { This => Link.DeviceId, Other => peerId, Auto => Auto, _ => "" };

    /// <summary>The other device's choice as this device's setting; null: not a choice it knows.</summary>
    internal string? FromWire(string wire, string peerId) =>
        wire.Length == 0 ? "" : wire == Auto ? Auto : wire == peerId ? Other : wire == Link.DeviceId ? This : null;
    /// <summary>The other device must have been used this much more recently before this one steps back (no flapping).</summary>
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

    private readonly Func<double> _idleSeconds;
    private readonly Func<bool?> _cursorHere;
    private readonly Dictionary<string, (double Idle, bool? Cursor)> _peers = new(StringComparer.Ordinal);
    private bool _isMain = true;

    /// <summary>The main device changed (or there is none any more).</summary>
    public event Action? RoleChanged;
    /// <summary>The other device chose another main device (the settings were updated already).</summary>
    public event Action? ModeAdopted;
    public event Action<LinkPeek>? PeekReceived;

    /// <param name="idleSeconds">Seconds since the last keyboard or mouse input here.</param>
    /// <param name="cursorHere">CursorHop: the mouse is on this device (true), on the other (false), unknown (null).</param>
    public LinkRoleFeature(LinkService link, AppSettings settings, Func<double> idleSeconds, Func<bool?> cursorHere) : base(link, settings)
    {
        _idleSeconds = idleSeconds;
        _cursorHere = cursorHere;
        Handle(Type, OnRole);
        Handle(PeekType, OnPeek);
        link.PeerOnline += OnPeerOnline;
        link.PeerOffline += OnPeerOffline;
    }

    /// <summary>Always on: the choice itself ("" = no main device) decides what happens.</summary>
    public override bool Enabled => true;

    /// <summary>This device shows everything (no main device chosen, nothing connected, or it is the main one).</summary>
    public bool IsMain => _isMain;

    /// <summary>The connected device this one hands its peeks to (null: none, this one shows everything).</summary>
    public string? MainPeer { get; private set; }

    private void OnPeerOnline(PairedDevice p)
    {
        _ = SendStateAsync(p.Id);
        Update();
    }

    private void OnPeerOffline(PairedDevice p)
    {
        lock (_peers) _peers.Remove(p.Id);
        Update();
    }

    /// <summary>Tells the connected devices the choice and how recently this one was used.</summary>
    public async Task<int> AnnounceAsync()
    {
        int n = 0;
        foreach (var p in OnlinePeers) if (await SendStateAsync(p.Id).ConfigureAwait(false)) n++;
        return n;
    }

    private Task<bool> SendStateAsync(string peerId) => SendAsync(peerId, Type, new JsonObject
    {
        ["main"] = ToWire(Settings.LinkMain, peerId), ["hide"] = Settings.LinkHideWhenNotMain, ["mute"] = Settings.LinkMuteNotificationsWhenNotMain, ["changed"] = Settings.LinkMainChanged.ToUniversalTime().Ticks,
        ["idle"] = Math.Round(Math.Max(0, _idleSeconds()), 1), ["cursor"] = _cursorHere(),
    });

    private void OnRole(LinkMessage m)
    {
        var main = Str(m.Payload, "main", 32);
        long ticks = m.Payload is JsonObject o ? LinkProtocol.Long(o, "changed") ?? 0 : 0;
        var changed = ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        bool? cursor = m.Payload is JsonObject c && c["cursor"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        lock (_peers) _peers[m.PeerId] = (Math.Max(0, Num(m.Payload, "idle")), cursor);
        // The newer choice wins on both devices; an unknown value is no choice.
        var mine = FromWire(main, m.PeerId);
        if (changed > Settings.LinkMainChanged.ToUniversalTime() && mine is not null)
        {
            Adopting = true;
            try
            {
                Settings.LinkMainChanged = changed;
                Settings.LinkMain = mine;
                Settings.LinkHideWhenNotMain = Bool(m.Payload, "hide");
                // Older versions do not send it: their choice keeps ours.
                if (m.Payload is JsonObject p && p.ContainsKey("mute")) Settings.LinkMuteNotificationsWhenNotMain = Bool(p, "mute");
            }
            finally { Adopting = false; }
            Log.Info($"Link: main device set to \"{(mine!.Length == 0 ? "none" : mine)}\" by {m.PeerName}");
            ModeAdopted?.Invoke();
        }
        Update();
    }

    /// <summary>True while the other device's choice is written into the settings (do not send it back as new).</summary>
    public bool Adopting { get; private set; }

    /// <summary>"Insel ausblenden" changed here: it counts as a new choice for both devices.</summary>
    public Task<int> HideChangedAsync()
    {
        if (Adopting) return Task.FromResult(0);
        Settings.LinkMainChanged = DateTime.UtcNow;
        return AnnounceAsync();
    }

    /// <summary>The user picked a main device here ("" = none, "this", "other" or "auto"): saved and sent to the other device.</summary>
    public Task<int> ChooseAsync(string main)
    {
        Settings.LinkMainChanged = DateTime.UtcNow;
        Settings.LinkMain = main;
        Update();
        return AnnounceAsync();
    }

    /// <summary>Works out again who the main device is (call it when the mouse moved or input started again).</summary>
    public void Update()
    {
        var peer = OnlinePeers.FirstOrDefault();
        (double Idle, bool? Cursor)? state = null;
        if (peer is not null) lock (_peers) if (_peers.TryGetValue(peer.Id, out var s)) state = s;
        bool main = IsMainDevice(ToWire(Settings.LinkMain, peer?.Id ?? ""), Link.DeviceId, peer?.Id, _idleSeconds(), state?.Idle, _cursorHere(), state?.Cursor);
        string? mainPeer = main ? null : peer?.Id;
        if (main == _isMain && mainPeer == MainPeer) return;
        _isMain = main;
        MainPeer = mainPeer;
        Log.Info(main ? "Link: this device shows everything" : $"Link: {peer?.Name} is the main device, this one stays quiet");
        RoleChanged?.Invoke();
    }

    /// <summary>
    /// Pure rule, the same on both devices. Without a connected device or without a choice every device is main.
    /// "auto": CursorHop knows where the mouse is; otherwise this device steps back only when the other one was used
    /// clearly more recently (both main for a moment is fine, both quiet must not happen).
    /// </summary>
    internal static bool IsMainDevice(string mode, string localId, string? peerId, double localIdle, double? peerIdle, bool? localCursor, bool? peerCursor)
    {
        if (peerId is null || mode.Length == 0) return true;
        if (mode == localId) return true;
        if (mode == peerId) return false;
        if (mode != Auto) return true; // a device that is not connected (or gone): no choice
        if (localCursor is bool lc) return lc;
        if (peerCursor is bool pc) return !pc;
        if (peerIdle is not double pi) return true;
        return !(pi + Margin.TotalSeconds < localIdle);
    }

    /// <summary>Hands a peek to the main device. False: this device is main, it is offline, or sharing notifications is off.</summary>
    public Task<bool> ForwardPeekAsync(string title, string subtitle, string? iconKey)
    {
        if (_isMain || MainPeer is not { } main || !Settings.LinkShareNotifications) return Task.FromResult(false);
        // Never back to where it came from: a peek of the other device that ends up here again is shown, not sent
        if (_received.CameBack(title, subtitle, DateTime.UtcNow)) return Task.FromResult(false);
        return SendAsync(main, PeekType, new JsonObject { ["title"] = title, ["subtitle"] = subtitle, ["icon"] = iconKey });
    }

    private readonly RecentPeeks _received = new();

    /// <summary>Peeks that just came from the other device (title + subtitle, 15 s): they must not be forwarded back.</summary>
    internal sealed class RecentPeeks
    {
        private readonly Dictionary<string, DateTime> _seen = new();
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

        public void Add(string title, string subtitle, DateTime now)
        {
            lock (_seen)
            {
                foreach (var old in _seen.Where(kv => now - kv.Value > Window).Select(kv => kv.Key).ToList()) _seen.Remove(old);
                _seen[title + "|" + subtitle] = now;
            }
        }

        public bool CameBack(string title, string subtitle, DateTime now)
        {
            lock (_seen) return _seen.TryGetValue(title + "|" + subtitle, out var t) && now - t <= Window;
        }
    }

    private void OnPeek(LinkMessage m)
    {
        if (!Settings.LinkShareNotifications) return;
        var title = Str(m.Payload, "title", 300);
        if (title.Length == 0) return;
        var icon = Str(m.Payload, "icon", 40);
        var subtitle = Str(m.Payload, "subtitle", 300);
        _received.Add(title, subtitle, DateTime.UtcNow);
        _received.Add(title, m.PeerName, DateTime.UtcNow); // shown with the device name when it has no subtitle
        PeekReceived?.Invoke(new LinkPeek(m.PeerId, m.PeerName, title, Str(m.Payload, "subtitle", 300), icon.StartsWith("Icon.", StringComparison.Ordinal) ? icon : null));
    }

    public override void Dispose()
    {
        Link.PeerOnline -= OnPeerOnline;
        Link.PeerOffline -= OnPeerOffline;
        base.Dispose();
    }
}
