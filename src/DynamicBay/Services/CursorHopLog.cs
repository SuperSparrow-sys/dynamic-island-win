using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicBay.Services;

public enum CursorHopEventKind
{
    /// <summary>The CursorHop core (re)started: every connection from before is gone.</summary>
    CoreStarted,
    /// <summary>A peer finished its handshake ("PeerHello processed").</summary>
    PeerConnected,
    /// <summary>A peer's connection ended.</summary>
    PeerDisconnected,
    /// <summary>No peer is connected any more (the "room beacon" that looks for peers runs again).</summary>
    AllPeersGone,
    /// <summary>A peer took over this machine's input: its mouse is on this screen now (secondary side).</summary>
    RemoteControlStarted,
    /// <summary>The peer let go of this machine: its mouse went back to it (secondary side).</summary>
    RemoteControlEnded,
    /// <summary>This machine's mouse and keyboard went over to a peer (primary side).</summary>
    ControllingRemote,
    /// <summary>This machine's mouse and keyboard came back here (primary side).</summary>
    ReturnedLocal,
    /// <summary>Clipboard content from a peer was put on the local clipboard.</summary>
    ClipboardReceived,
    /// <summary>Files from a peer were written to a folder (clipboard file transfer).</summary>
    FilesReceived,
}

/// <summary>One line of the CursorHop log that DynamicBay understands. Fields that the line did not carry are null.</summary>
public sealed record CursorHopEvent(CursorHopEventKind Kind, DateTime Time)
{
    /// <summary>Device id (a GUID) of the peer, or its connection id ("inbound-192.168.10.***:36285").</summary>
    public string? Peer { get; init; }
    /// <summary>Connection id of the peer ("synthetic_id" / "synthetic"), when the line has one besides the device id.</summary>
    public string? Connection { get; init; }
    /// <summary>Device name ("PC-Jonathan").</summary>
    public string? Name { get; init; }
    public string? Platform { get; init; }
    /// <summary>The peer is the Primary (mouse and keyboard are attached there), seen from this machine.</summary>
    public bool? PeerIsPrimary { get; init; }
    /// <summary>Clipboard format ("PlainText", "Image", "Html" ...).</summary>
    public string? Format { get; init; }
    public int? Count { get; init; }
    public string? Folder { get; init; }
}

/// <summary>
/// Reads lines of CursorHop's core log (<c>%LocalAppData%\CursorHop\logs\core.log.yyyy-MM-dd</c>, tracing format:
/// <c>2026-10-11T09:42:45.269263Z  INFO cursorhop::forwarding: [PEER] PeerHello processed — connection ready peer=… name=…</c>).
/// CursorHop has no public interface; the log is the only stable thing to read. Pure and tolerant: an unknown or
/// broken line gives null, so a new CursorHop version can only switch the display off, never break DynamicBay.
/// The lines seen in real logs are fixtures in tests/DynamicBay.Tests/Fixtures/CursorHop; lines marked "binary" below
/// come from the message texts in cursorhop.exe 0.12.1 and were not seen in a log yet (their fields are guessed).
/// </summary>
internal static class CursorHopLog
{
    private static readonly Regex Line = new(
        @"^(?<time>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z)\s+(?<level>[A-Z]+)\s+(?<target>[\w:.\-]+):\s(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>key=value and key="quoted value" pairs (tracing fields).</summary>
    private static readonly Regex Field = new(@"(?<![\w-])(?<key>[a-z_][a-z0-9_]*)=(?:""(?<q>[^""]*)""|(?<v>\S+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CursorHopEvent? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = Line.Match(line.Trim('\0', ' ', '\r', '\n', '\t'));
        if (!m.Success) return null;
        if (!DateTime.TryParse(m.Groups["time"].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) return null;
        string msg = m.Groups["msg"].Value;
        var f = Fields(msg);

        CursorHopEvent Make(CursorHopEventKind kind) => new(kind, time);

        // Seen: "CursorHop core starting version="0.12.1""
        if (msg.StartsWith("CursorHop core starting", StringComparison.Ordinal)) return Make(CursorHopEventKind.CoreStarted);

        // Seen: "[PEER] PeerHello processed — connection ready peer=<guid> name=PC-Jonathan platform=Windows
        //        synthetic_id=inbound-…:17041 is_tofu_pending=false peer_is_primary_over_us=true"
        // Binary: "[PEER] PeerHello re-advertise processed" (same peer again, e.g. after a display change)
        if (msg.StartsWith("[PEER] PeerHello", StringComparison.Ordinal) && msg.Contains("processed", StringComparison.Ordinal))
        {
            string? peer = Get(f, "peer", "device", "device_id");
            if (peer is null) return null;
            return Make(CursorHopEventKind.PeerConnected) with
            {
                Peer = peer, Name = Get(f, "name", "device_name"), Platform = Get(f, "platform"),
                Connection = Get(f, "synthetic_id", "synthetic"),
                PeerIsPrimary = Get(f, "peer_is_primary_over_us") is { } p ? p == "true" : null,
            };
        }

        // Seen: "cleared peer keyboard presence after disconnect event="peer_presence_cleared" peer=<guid or inbound-…>"
        // Binary: "device manually disconnected (auto-reconnect suppressed, roles cleared)", "disconnected from peer"
        if (msg.StartsWith("cleared peer keyboard presence after disconnect", StringComparison.Ordinal)
            || msg.StartsWith("device manually disconnected", StringComparison.Ordinal)
            || msg.StartsWith("disconnected from peer", StringComparison.Ordinal))
        {
            string? peer = Get(f, "peer", "device", "device_id", "peer_id", "target_id");
            return peer is null ? null : Make(CursorHopEventKind.PeerDisconnected) with { Peer = peer };
        }

        // Seen: "room beacon started on UDP port 24801" - logged at start and each time the last peer went away.
        if (msg.StartsWith("room beacon started", StringComparison.Ordinal)) return Make(CursorHopEventKind.AllPeersGone);

        // Seen (secondary): "[ROLE] peer claimed Primary role — blocking local edge forwarding to them peer=<guid> synthetic=inbound-…"
        // right before "[IDLE_CURSOR] reveal reason=remote_input": the peer's mouse entered this screen.
        if (msg.StartsWith("[ROLE] peer claimed Primary role", StringComparison.Ordinal))
            return Make(CursorHopEventKind.RemoteControlStarted) with { Peer = Get(f, "peer"), Connection = Get(f, "synthetic") };
        // Seen (secondary): "[ROLE] peer released Primary role — edge forwarding re-enabled peer=<guid>", then
        // "[IDLE_CURSOR] hide reason=controller_left": the mouse went back to the peer.
        if (msg.StartsWith("[ROLE] peer released Primary role", StringComparison.Ordinal))
            return Make(CursorHopEventKind.RemoteControlEnded) with { Peer = Get(f, "peer"), Connection = Get(f, "synthetic") };

        // Binary (primary): "[SWITCH] now controlling remote device", "[SWITCH] hopped to new …",
        // "[SWITCH] returned to local control" (also "… without an edge crossing").
        if (msg.StartsWith("[SWITCH] now controlling remote device", StringComparison.Ordinal) || msg.StartsWith("[SWITCH] hopped to", StringComparison.Ordinal))
            return Make(CursorHopEventKind.ControllingRemote) with
            {
                Peer = Get(f, "peer", "target", "device", "target_id", "device_id", "to"), Name = Get(f, "name", "device_name", "target_name"),
            };
        if (msg.StartsWith("[SWITCH] returned to local control", StringComparison.Ordinal)) return Make(CursorHopEventKind.ReturnedLocal);

        // Seen: "[CLIP_RECV] received REGULAR clipboard frame — injecting to local clipboard (Ctrl+V will paste into
        //        focused window) format=PlainText data_len=32 peer=inbound-192.168.10.***:36285"
        if (msg.StartsWith("[CLIP_RECV] received", StringComparison.Ordinal))
            return Make(CursorHopEventKind.ClipboardReceived) with { Peer = Get(f, "peer"), Format = Get(f, "format") };

        // Binary: "[CLIP_TRANSFER] files delivered to paste directory", "[CLIP_TRANSFER] files delivered to fallback folder"
        if (msg.StartsWith("[CLIP_TRANSFER] files delivered", StringComparison.Ordinal))
            return Make(CursorHopEventKind.FilesReceived) with
            {
                Peer = Get(f, "peer", "from", "sender"),
                Count = int.TryParse(Get(f, "files", "count", "file_count", "delivered"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null,
                Folder = Get(f, "dir", "folder", "path", "paste_dir", "destination", "dest", "fallback_folder", "target_dir"),
            };

        return null;
    }

    private static Dictionary<string, string> Fields(string msg)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Field.Matches(msg))
            d.TryAdd(m.Groups["key"].Value, m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value);
        return d;
    }

    private static string? Get(Dictionary<string, string> f, params string[] keys)
    {
        foreach (var k in keys)
            if (f.TryGetValue(k, out var v) && v.Length > 0) return v;
        return null;
    }
}

/// <summary>
/// What the log says about now: connected peers and where the mouse is. Pure (no clock, no files) so it can be
/// tested line by line. <see cref="Apply"/> returns the events that changed something, with the device name filled in.
/// Works on either side: on the Primary the mouse leaves with "[SWITCH]", on a secondary a peer claims the Primary
/// role while its mouse is on this screen.
/// </summary>
internal sealed class CursorHopState
{
    public sealed class PeerInfo
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "";
        public string? Connection { get; set; }
        public bool IsPrimary { get; set; }
    }

    private readonly List<PeerInfo> _peers = new();
    public IReadOnlyList<PeerInfo> Peers => _peers;
    /// <summary>Name of the device the mouse is on, null = on this machine.</summary>
    public string? CursorOn { get; private set; }

    public void Reset()
    {
        _peers.Clear();
        CursorOn = null;
    }

    public IReadOnlyList<CursorHopEvent> Apply(CursorHopEvent e)
    {
        var changes = new List<CursorHopEvent>(1);
        switch (e.Kind)
        {
            case CursorHopEventKind.CoreStarted:
                foreach (var p in _peers.ToList()) changes.Add(Remove(p, e.Time));
                CursorOn = null;
                break;
            case CursorHopEventKind.PeerConnected:
            {
                var known = Find(e.Peer);
                bool isNew = known is null;
                known ??= new PeerInfo { Id = e.Peer! };
                if (e.Name is { Length: > 0 } name) known.Name = name;
                if (known.Name.Length == 0) known.Name = e.Platform ?? "CursorHop";
                if (e.Connection is not null) known.Connection = e.Connection;
                if (e.PeerIsPrimary is bool primary) known.IsPrimary = primary;
                if (isNew)
                {
                    _peers.Add(known);
                    // A secondary starts with the mouse over at the Primary until the Primary hands it over.
                    if (known.IsPrimary && CursorOn is null) CursorOn = known.Name;
                    changes.Add(e with { Name = known.Name });
                }
                break;
            }
            case CursorHopEventKind.PeerDisconnected:
            {
                // An old connection id ending after the peer already came back on a new one changes nothing.
                var p = _peers.FirstOrDefault(x => x.Id == e.Peer) ?? _peers.FirstOrDefault(x => x.Connection is not null && x.Connection == e.Peer);
                if (p is not null) changes.Add(Remove(p, e.Time));
                break;
            }
            case CursorHopEventKind.AllPeersGone:
                foreach (var p in _peers.ToList()) changes.Add(Remove(p, e.Time));
                break;
            case CursorHopEventKind.RemoteControlStarted:
            case CursorHopEventKind.ReturnedLocal:
                if (CursorOn is not null) { CursorOn = null; changes.Add(e with { Name = null }); }
                break;
            case CursorHopEventKind.RemoteControlEnded:
            case CursorHopEventKind.ControllingRemote:
            {
                string name = NameOf(e.Peer) ?? NameOf(e.Connection) ?? e.Name ?? (_peers.Count == 1 ? _peers[0].Name : "?");
                if (CursorOn != name) { CursorOn = name; changes.Add(e with { Name = name }); }
                break;
            }
            case CursorHopEventKind.ClipboardReceived:
            case CursorHopEventKind.FilesReceived:
                changes.Add(e with { Name = NameOf(e.Peer) ?? (_peers.Count == 1 ? _peers[0].Name : null) });
                break;
        }
        return changes;
    }

    private CursorHopEvent Remove(PeerInfo p, DateTime time)
    {
        _peers.Remove(p);
        if (CursorOn == p.Name) CursorOn = null;
        return new CursorHopEvent(CursorHopEventKind.PeerDisconnected, time) { Peer = p.Id, Name = p.Name };
    }

    private PeerInfo? Find(string? id) => id is null ? null : _peers.FirstOrDefault(p => p.Id == id);

    private string? NameOf(string? id) =>
        id is null ? null : (_peers.FirstOrDefault(p => p.Id == id) ?? _peers.FirstOrDefault(p => p.Connection == id))?.Name;
}

/// <summary>
/// CursorHop reconnects in bursts (seen: connected and gone again every 15 s for a minute after waking up). Only a
/// connection that holds for <see cref="ConnectDelay"/> is announced, and a drop only when the device stays away for
/// <see cref="DisconnectDelay"/>; a drop and return in between announces nothing. Pure: the caller passes the time.
/// </summary>
internal sealed class CursorHopAnnouncer
{
    public static readonly TimeSpan ConnectDelay = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DisconnectDelay = TimeSpan.FromSeconds(40);

    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _connecting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _leaving = new(StringComparer.OrdinalIgnoreCase);

    public bool HasPending => _connecting.Count > 0 || _leaving.Count > 0;

    public void Connected(string name, DateTime now)
    {
        if (_leaving.Remove(name)) return;              // back before the drop was announced: nothing happened
        if (!_announced.Contains(name)) _connecting[name] = now + ConnectDelay;
    }

    public void Disconnected(string name, DateTime now)
    {
        if (_connecting.Remove(name)) return;           // gone before it was announced: nothing happened
        if (_announced.Contains(name)) _leaving[name] = now + DisconnectDelay;
    }

    /// <summary>Known without a peek (state read from the log at start).</summary>
    public void Silently(string name) => _announced.Add(name);

    /// <summary>Announcements that are due: (name, connected).</summary>
    public List<(string Name, bool Connected)> Due(DateTime now)
    {
        var due = new List<(string, bool)>();
        foreach (var (name, at) in _connecting.ToList())
            if (now >= at) { _connecting.Remove(name); _announced.Add(name); due.Add((name, true)); }
        foreach (var (name, at) in _leaving.ToList())
            if (now >= at) { _leaving.Remove(name); _announced.Remove(name); due.Add((name, false)); }
        return due;
    }
}

/// <summary>
/// Follows one growing log file: each call returns the lines written since the last one. CursorHop keeps the file
/// open for writing, so it is opened with read, write and delete sharing and closed again right away. A line without
/// its newline yet waits for the next call; a file that got shorter (replaced) is read again from the start.
/// </summary>
internal sealed class CursorHopLogTail
{
    private long _position;
    private readonly List<byte> _partial = new();
    private const int MaxRead = 4 * 1024 * 1024;

    public string? File { get; private set; }

    /// <summary>Follow another file from its start (null: stop).</summary>
    public void Follow(string? path)
    {
        File = path;
        _position = 0;
        _partial.Clear();
    }

    /// <summary>New complete lines; none when the file does not exist (yet).</summary>
    public List<string> ReadNew()
    {
        var lines = new List<string>();
        if (File is null || !System.IO.File.Exists(File)) return lines;
        byte[] chunk;
        using (var fs = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (fs.Length < _position) { _position = 0; _partial.Clear(); }
            if (fs.Length == _position) return lines;
            fs.Position = _position;
            chunk = new byte[Math.Min(fs.Length - _position, MaxRead)]; // the rest comes with the next call
            int read = 0, n;
            while (read < chunk.Length && (n = fs.Read(chunk, read, chunk.Length - read)) > 0) read += n;
            if (read < chunk.Length) Array.Resize(ref chunk, read);
            _position += read;
        }
        int start = 0;
        for (int i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != (byte)'\n') continue;
            if (_partial.Count > 0)
            {
                _partial.AddRange(chunk[start..i]);
                lines.Add(Encoding.UTF8.GetString(_partial.ToArray()).TrimEnd('\r'));
                _partial.Clear();
            }
            else lines.Add(Encoding.UTF8.GetString(chunk, start, i - start).TrimEnd('\r'));
            start = i + 1;
        }
        if (start < chunk.Length) _partial.AddRange(chunk[start..]);
        if (_partial.Count > 64 * 1024) _partial.Clear(); // no log line is that long
        return lines;
    }
}
