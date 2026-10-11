using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>
/// One thing mirrored between paired devices (status, files, clipboard, notifications, music). Each feature has its own
/// switch in the settings and checks it on both ends: nothing is sent while it is off, and what arrives is dropped.
/// Messages only ever go to paired devices that are online (the link refuses the rest anyway). No WPF in here, so two
/// link instances in a test can talk to each other.
/// </summary>
public abstract class LinkFeature : IDisposable
{
    protected readonly LinkService Link;
    protected readonly AppSettings Settings;
    private readonly List<IDisposable> _handlers = new();
    private bool _disposed;

    protected LinkFeature(LinkService link, AppSettings settings)
    {
        Link = link;
        Settings = settings;
    }

    /// <summary>The feature's own switch (Einstellungen → Geräte → Teilen).</summary>
    public abstract bool Enabled { get; }

    /// <summary>A handler that only runs while the switch is on.</summary>
    protected void Handle(string type, Action<LinkMessage> handler) =>
        _handlers.Add(Link.RegisterHandler(type, m =>
        {
            if (_disposed || !Enabled) return;
            handler(m);
        }));

    /// <summary>Paired devices that are connected right now.</summary>
    public IReadOnlyList<PairedDevice> OnlinePeers => Link.PairedDevices.Where(p => !p.Rejected && Link.IsOnline(p.Id)).ToList();

    /// <summary>Sends to every connected paired device; the number that got it. Nothing while the switch is off.</summary>
    protected async Task<int> BroadcastAsync(string type, JsonNode payload)
    {
        if (_disposed || !Enabled) return 0;
        int sent = 0;
        foreach (var p in OnlinePeers)
            if (await SendAsync(p.Id, type, payload).ConfigureAwait(false)) sent++;
        return sent;
    }

    /// <summary>One device; false when it is offline, the switch is off or the message is too large.</summary>
    protected async Task<bool> SendAsync(string peerId, string type, JsonNode payload)
    {
        if (_disposed || !Enabled) return false;
        try { return await Link.SendAsync(peerId, type, payload).ConfigureAwait(false); }
        catch (ArgumentException ex) { Log.Info($"Link: {type} not sent ({ex.Message})"); return false; }
    }

    public virtual void Dispose()
    {
        _disposed = true;
        foreach (var h in _handlers) h.Dispose();
        _handlers.Clear();
    }

    // ---------- tolerant reading of payloads ----------

    protected static string Str(JsonNode? p, string key, int max = 4000)
    {
        var s = p is JsonObject o ? LinkProtocol.Str(o, key) ?? "" : "";
        return s.Length > max ? s[..max] : s;
    }

    protected static bool Bool(JsonNode? p, string key) => p is JsonObject o && LinkProtocol.Bool(o, key);

    protected static double Num(JsonNode? p, string key)
    {
        if (p is not JsonObject o || o[key] is not JsonValue v) return 0;
        return v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : v.TryGetValue<long>(out var l) ? l : 0;
    }

    protected static int? Int(JsonNode? p, string key) => p is JsonObject o && LinkProtocol.Long(o, key) is long l and >= int.MinValue and <= int.MaxValue ? (int)l : null;

    protected static byte[]? Bytes(JsonNode? p, string key, int maxBytes)
    {
        var s = p is JsonObject o ? LinkProtocol.Str(o, key) : null;
        if (string.IsNullOrEmpty(s) || s.Length > maxBytes / 3 * 4 + 4) return null;
        try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
    }
}

public static class PairingCodeText
{
    /// <summary>"482913" → "482 913": two groups are easier to compare between two screens.</summary>
    public static string Format(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : code;
}

/// <summary>
/// "PC verbunden" can come from CursorHop and from the link at nearly the same time: whoever announces a device second
/// within <see cref="Window"/> stays silent. Thread-safe; the caller passes the time (tests).
/// </summary>
public static class DeviceAnnouncements
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly Dictionary<(string, bool), DateTime> Recent = new();

    /// <summary>True: announce it (and remember that it was). False: the same was just announced by the other source.</summary>
    public static bool TryAnnounce(string device, bool connected, DateTime now)
    {
        var key = (device.Trim().ToLowerInvariant(), connected);
        lock (Recent)
        {
            if (Recent.TryGetValue(key, out var at) && now - at < Window) return false;
            Recent[key] = now;
            foreach (var old in Recent.Where(kv => now - kv.Value > TimeSpan.FromMinutes(5)).Select(kv => kv.Key).ToList()) Recent.Remove(old);
            return true;
        }
    }

    internal static void Clear() { lock (Recent) Recent.Clear(); }
}
