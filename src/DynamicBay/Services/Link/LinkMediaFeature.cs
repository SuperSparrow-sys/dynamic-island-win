using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>What plays on a device. Position was sampled at <see cref="Stamp"/> (UTC); Cover: small JPEG or none.</summary>
public sealed record LinkNowPlaying(bool Active, string Title, string Artist, string App, bool Playing, double Position, double Duration, byte[]? Cover)
{
    public DateTime Stamp { get; init; } = DateTime.UtcNow;

    public static readonly LinkNowPlaying None = new(false, "", "", "", false, 0, 0, null);

    /// <summary>Same song and state (the position and the stamp move all the time and do not count).</summary>
    public bool SameAs(LinkNowPlaying? o) =>
        o is not null && Active == o.Active && Title == o.Title && Artist == o.Artist && App == o.App && Playing == o.Playing
        && Math.Abs(Duration - o.Duration) < 1 && (Cover?.Length ?? 0) == (o.Cover?.Length ?? 0);
}

/// <summary>
/// Musik (Einstellungen → Geräte → Musik): each device tells the others what plays (title, artist, cover, position),
/// and the island can show and steer it when nothing plays here. Play/pause, next and previous are sent back and
/// done by the device that plays; a device never starts music on its own (no session there: nothing happens).
/// </summary>
public sealed class LinkMediaFeature : LinkFeature
{
    public const string Type = "media", CommandType = "media.cmd";
    public static readonly string[] Commands = { "playpause", "next", "previous" };
    internal const int MaxCoverBytes = 96 * 1024;

    private LinkNowPlaying _local = LinkNowPlaying.None;
    private LinkNowPlaying? _lastSent;

    /// <summary>A device's playback changed (peer id, name, now playing; null = gone or nothing plays).</summary>
    public event Action<string, string, LinkNowPlaying?>? RemoteChanged;
    /// <summary>Another device pressed play/pause, next or previous for this device's music (on the context).</summary>
    public event Action<string, string>? CommandReceived;

    public LinkMediaFeature(LinkService link, AppSettings settings) : base(link, settings)
    {
        Handle(Type, OnMedia);
        Handle(CommandType, OnCommand);
        link.PeerOnline += OnPeerOnline;
        link.PeerOffline += OnPeerOffline;
    }

    public override bool Enabled => Settings.LinkShareMedia;

    private void OnPeerOnline(PairedDevice p)
    {
        if (_local.Active) _ = SendAsync(p.Id, Type, ToJson(_local));
    }

    private void OnPeerOffline(PairedDevice p) => RemoteChanged?.Invoke(p.Id, p.Name, null);

    /// <summary>The playback here changed. Sent only when the song or its state really changed (or <paramref name="force"/>).</summary>
    public async Task<int> PublishAsync(LinkNowPlaying now, bool force = false)
    {
        _local = now;
        if (!force && now.SameAs(_lastSent)) return 0;
        _lastSent = now;
        return await BroadcastAsync(Type, ToJson(now)).ConfigureAwait(false);
    }

    /// <summary>Play/pause, next or previous on the device that plays. False: offline, unknown command or the switch is off.</summary>
    public Task<bool> SendCommandAsync(string peerId, string command) =>
        Commands.Contains(command) ? SendAsync(peerId, CommandType, new JsonObject { ["cmd"] = command }) : Task.FromResult(false);

    private void OnMedia(LinkMessage m)
    {
        var np = Parse(m.Payload);
        RemoteChanged?.Invoke(m.PeerId, m.PeerName, np is { Active: true } ? np : null);
    }

    private void OnCommand(LinkMessage m)
    {
        var cmd = Str(m.Payload, "cmd", 16);
        if (Commands.Contains(cmd)) CommandReceived?.Invoke(m.PeerId, cmd);
    }

    internal static JsonObject ToJson(LinkNowPlaying n) => new()
    {
        ["active"] = n.Active, ["title"] = n.Title, ["artist"] = n.Artist, ["app"] = n.App, ["playing"] = n.Playing,
        ["position"] = Math.Round(n.Position, 1), ["duration"] = Math.Round(n.Duration, 1),
        ["cover"] = n.Cover is { Length: > 0 and <= MaxCoverBytes } ? Convert.ToBase64String(n.Cover) : null,
    };

    /// <summary>Tolerant: missing fields are normal; the position is taken as sampled when the message arrived.</summary>
    internal static LinkNowPlaying? Parse(JsonNode? p)
    {
        if (p is not JsonObject) return null;
        double duration = Math.Max(0, Num(p, "duration"));
        double position = Math.Clamp(Num(p, "position"), 0, duration > 0 ? duration : double.MaxValue);
        return new LinkNowPlaying(Bool(p, "active"), Str(p, "title", 300), Str(p, "artist", 300), Str(p, "app", 80), Bool(p, "playing"),
            position, duration, Bytes(p, "cover", MaxCoverBytes));
    }

    public override void Dispose()
    {
        Link.PeerOnline -= OnPeerOnline;
        Link.PeerOffline -= OnPeerOffline;
        base.Dispose();
    }
}
