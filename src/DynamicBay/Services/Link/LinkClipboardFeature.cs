using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>Clipboard content from another device: text or a PNG picture.</summary>
public sealed record LinkClip(string PeerId, string PeerName, string? Text, byte[]? Png);

/// <summary>
/// Zwischenablage (Einstellungen → Geräte → Zwischenablage): what is copied here (text, pictures) joins the clipboard
/// history of the paired devices, marked with this device's name. The receiving side never writes the Windows
/// clipboard by itself (CursorHop already does that, and twice would fight); a click on the entry copies it.
/// Password-manager content never gets here (the clipboard history skips it). Nothing that came from another device
/// is sent on again: neither entries from the link nor ones CursorHop brought, and not the same content within a minute.
/// </summary>
public sealed class LinkClipboardFeature : LinkFeature
{
    public const string Type = "clipboard";
    /// <summary>Pictures up to this size go inside the message (base64 stays under the 1 MiB frame).</summary>
    internal const int InlineImageBytes = 700 * 1024;
    /// <summary>Larger pictures go as a file, up to this size; bigger ones are not shared.</summary>
    internal const int MaxImageBytes = 5 * 1024 * 1024;
    internal const int MaxTextChars = 100_000;
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(60);

    private readonly string _tempFolder;
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PeerId, string PeerName, DateTime Until)> _expected = new(StringComparer.OrdinalIgnoreCase);

    public event Action<LinkClip>? Received;

    public LinkClipboardFeature(LinkService link, AppSettings settings, LinkFilesFeature files, string tempFolder) : base(link, settings)
    {
        _tempFolder = tempFolder;
        Handle(Type, OnClip);
        files.AddClaimer(Claim);
    }

    public override bool Enabled => Settings.LinkShareClipboard;

    /// <summary>
    /// The user copied text here. <paramref name="sourceDevice"/>: the entry came from another PC (CursorHop) - then
    /// it is not sent. Returns the number of devices it went to.
    /// </summary>
    public Task<int> ShareTextAsync(string text, string? sourceDevice = null)
    {
        if (!Enabled || sourceDevice is not null || string.IsNullOrWhiteSpace(text) || text.Length > MaxTextChars) return Task.FromResult(0);
        if (!Fresh(Key(text))) return Task.FromResult(0);
        return BroadcastAsync(Type, new JsonObject { ["kind"] = "text", ["text"] = text });
    }

    /// <summary>A picture copied here (PNG bytes).</summary>
    public async Task<int> ShareImageAsync(byte[] png, string? sourceDevice = null)
    {
        if (!Enabled || sourceDevice is not null || png.Length == 0 || png.Length > MaxImageBytes) return 0;
        if (!Fresh(Key(png))) return 0;
        if (png.Length <= InlineImageBytes)
            return await BroadcastAsync(Type, new JsonObject { ["kind"] = "image", ["png"] = Convert.ToBase64String(png) }).ConfigureAwait(false);

        // Too big for one message: announce the file, then send it (the other side claims it by its name).
        Directory.CreateDirectory(_tempFolder);
        string name = "clip-" + Guid.NewGuid().ToString("N")[..12] + ".png";
        string path = Path.Combine(_tempFolder, name);
        await File.WriteAllBytesAsync(path, png).ConfigureAwait(false);
        int sent = 0;
        try
        {
            foreach (var p in OnlinePeers)
            {
                if (!await SendAsync(p.Id, Type, new JsonObject { ["kind"] = "image-file", ["name"] = name, ["size"] = png.Length }).ConfigureAwait(false)) continue;
                if (await Link.SendFileAsync(p.Id, path).ConfigureAwait(false) == FileTransferResult.Done) sent++;
            }
        }
        finally { try { File.Delete(path); } catch { } }
        return sent;
    }

    /// <summary>Not the same content again within a minute, in either direction (no ping-pong with the other side or CursorHop).</summary>
    private bool Fresh(string key)
    {
        var now = DateTime.UtcNow;
        lock (_seen)
        {
            foreach (var old in _seen.Where(kv => now - kv.Value > EchoWindow).Select(kv => kv.Key).ToList()) _seen.Remove(old);
            if (_seen.ContainsKey(key)) return false;
            _seen[key] = now;
            return true;
        }
    }

    private void Remember(string key)
    {
        lock (_seen) _seen[key] = DateTime.UtcNow;
    }

    internal static string Key(string text) => "t:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));
    internal static string Key(byte[] png) => "i:" + Convert.ToHexString(SHA256.HashData(png));

    private void OnClip(LinkMessage m)
    {
        switch (Str(m.Payload, "kind", 16))
        {
            case "text":
            {
                var text = Str(m.Payload, "text", MaxTextChars);
                if (string.IsNullOrWhiteSpace(text)) return;
                Remember(Key(text));
                Received?.Invoke(new LinkClip(m.PeerId, m.PeerName, text, null));
                return;
            }
            case "image":
            {
                if (Bytes(m.Payload, "png", InlineImageBytes) is not { Length: > 8 } png || !IsPng(png)) return;
                Remember(Key(png));
                Received?.Invoke(new LinkClip(m.PeerId, m.PeerName, null, png));
                return;
            }
            case "image-file":
            {
                var name = LinkProtocol.SanitizeFileName(Str(m.Payload, "name", 64));
                if (!name.StartsWith("clip-", StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
                lock (_expected) _expected[name] = (m.PeerId, m.PeerName, DateTime.UtcNow.AddMinutes(2));
                return;
            }
        }
    }

    /// <summary>The announced picture file: into the temp folder, then handed on as clipboard content.</summary>
    private LinkFileClaim? Claim(IncomingFileOffer offer)
    {
        (string PeerId, string PeerName, DateTime Until) e;
        lock (_expected)
        {
            if (!_expected.Remove(offer.FileName, out e) || e.PeerId != offer.PeerId || DateTime.UtcNow > e.Until) return null;
        }
        // Claimed even when it turns out too big (declined then) or the switch went off meanwhile (dropped): an
        // announced clipboard picture never ends up in the downloads as a file.
        return new LinkFileClaim(_tempFolder, MaxImageBytes, path =>
        {
            byte[] png;
            try { png = File.ReadAllBytes(path); }
            finally { try { File.Delete(path); } catch { } }
            if (!Enabled || !IsPng(png)) return;
            Remember(Key(png));
            Received?.Invoke(new LinkClip(e.PeerId, e.PeerName, null, png));
        });
    }

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
}
