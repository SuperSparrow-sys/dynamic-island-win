using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>A Windows notification of another device. Icon: small PNG of the app's logo (may be missing).</summary>
public sealed record LinkNotification(string PeerId, string PeerName, string App, string Title, string Body, byte[]? Icon);

/// <summary>
/// Mitteilungen (Einstellungen → Geräte → Mitteilungen): the Windows notifications the island mirrors here (WhatsApp,
/// Outlook …) also show on the paired devices, marked "von Laptop". Never forwarded again: a notification that came over
/// the link stays on the device it arrived at. The receiving side treats them like its own (quiet hours, muted apps).
/// </summary>
public sealed class LinkNotificationsFeature : LinkFeature
{
    public const string Type = "notification";
    internal const int MaxIconBytes = 48 * 1024;
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(90);

    private readonly Dictionary<string, DateTime> _received = new(StringComparer.Ordinal);

    public event Action<LinkNotification>? Received;

    public LinkNotificationsFeature(LinkService link, AppSettings settings) : base(link, settings) => Handle(Type, OnNotification);

    public override bool Enabled => Settings.LinkShareNotifications;

    /// <summary>
    /// A notification seen here. <paramref name="fromDevice"/>: it came from another device over the link (never sent
    /// on). The same app, title and text that just arrived from a device is not sent back either.
    /// </summary>
    public Task<int> ForwardAsync(string app, string title, string body, byte[]? iconPng, string? fromDevice = null)
    {
        if (!Enabled || fromDevice is not null) return Task.FromResult(0);
        var key = Key(app, title, body);
        lock (_received)
            if (_received.TryGetValue(key, out var at) && DateTime.UtcNow - at < EchoWindow) return Task.FromResult(0);
        var payload = new JsonObject
        {
            ["app"] = Cut(app, 80), ["title"] = Cut(title, 300), ["body"] = Cut(body, 2000),
            ["icon"] = iconPng is { Length: > 0 and <= MaxIconBytes } ? Convert.ToBase64String(iconPng) : null,
        };
        return BroadcastAsync(Type, payload);
    }

    private void OnNotification(LinkMessage m)
    {
        var app = Str(m.Payload, "app", 80);
        var title = Str(m.Payload, "title", 300);
        var body = Str(m.Payload, "body", 2000);
        if (app.Length == 0 && title.Length == 0) return;
        var now = DateTime.UtcNow;
        lock (_received)
        {
            foreach (var old in _received.Where(kv => now - kv.Value > EchoWindow).Select(kv => kv.Key).ToList()) _received.Remove(old);
            _received[Key(app, title, body)] = now;
        }
        Received?.Invoke(new LinkNotification(m.PeerId, m.PeerName, app.Length > 0 ? app : title, title.Length > 0 ? title : app, body, Bytes(m.Payload, "icon", MaxIconBytes)));
    }

    private static string Key(string app, string title, string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(app + "\n" + title + "\n" + body)));

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}
