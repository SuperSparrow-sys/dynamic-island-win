using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>
/// The other device's switch for one feature ("&lt;type&gt;.share"), told when a device connects and when it changes.
/// A feature that leaves something out here because the other device shows it (the timer's end, a battery peek, a
/// question) checks it first: with the switch off over there nothing would show anywhere. Only the switch goes over,
/// no content, and only to paired devices that are online.
/// </summary>
public sealed class LinkPeerSwitch : IDisposable
{
    private readonly LinkService _link;
    private readonly string _type;
    private readonly Func<bool> _on;
    private readonly Dictionary<string, bool> _peers = new(StringComparer.Ordinal);
    private readonly IDisposable _handler;

    public LinkPeerSwitch(LinkService link, string feature, Func<bool> on)
    {
        _link = link;
        _type = feature + ".share";
        _on = on;
        // Not behind the feature's switch: the other device's switch matters also while this one is off.
        _handler = link.RegisterHandler(_type, m =>
        {
            bool value = m.Payload is JsonObject o && LinkProtocol.Bool(o, "on");
            lock (_peers) _peers[m.PeerId] = value;
            Changed?.Invoke();
        });
        link.PeerOnline += OnPeerOnline;
        link.PeerOffline += OnPeerOffline;
    }

    /// <summary>A device told its switch, or left.</summary>
    public event Action? Changed;

    /// <summary>The switch is on over there (and that device said so since it connected).</summary>
    public bool IsOn(string peerId)
    {
        lock (_peers) return _peers.TryGetValue(peerId, out var on) && on;
    }

    private void OnPeerOnline(PairedDevice p) => _ = Send(p.Id);

    private void OnPeerOffline(PairedDevice p)
    {
        lock (_peers) _peers.Remove(p.Id);
        Changed?.Invoke();
    }

    /// <summary>The switch changed here: tell the connected devices.</summary>
    public async Task AnnounceAsync()
    {
        foreach (var p in _link.PairedDevices.Where(p => !p.Rejected && _link.IsOnline(p.Id)))
            await Send(p.Id).ConfigureAwait(false);
    }

    private async Task<bool> Send(string peerId)
    {
        try { return await _link.SendAsync(peerId, _type, new JsonObject { ["on"] = _on() }).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException) { return false; }
    }

    public void Dispose()
    {
        _link.PeerOnline -= OnPeerOnline;
        _link.PeerOffline -= OnPeerOffline;
        _handler.Dispose();
    }
}
