using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link.Audio;

/// <summary>The local sound output as far as the forwarding needs it (the real one, or a fake in tests and dev mode).</summary>
public interface IOutputMute
{
    /// <summary>Muted, or null when the device cannot be read (gone).</summary>
    bool? IsMuted(string endpointId);
    void SetMuted(string endpointId, bool muted);
}

/// <summary>Where the guard writes down what it muted, so a crash or a hard kill can be undone at the next start.</summary>
public interface IMuteBackup
{
    (string Id, bool WasMuted)? Load();
    void Save(string id, bool wasMuted);
    void Clear();
}

/// <summary>
/// While the sound plays on another device, this PC's output is muted (WASAPI loopback still delivers the sound then,
/// see docs/ARCHITECTURE.md). The guard remembers the state from before and puts exactly that back:
/// <list type="bullet">
/// <item>an output that was muted already stays muted and is never unmuted by us,</item>
/// <item>the default output changes while forwarding: the old one gets its state back, the new one is muted,</item>
/// <item>what it muted is written to a backup first, so the next start can undo it after a crash.</item>
/// </list>
/// Thread-safe: the exit handlers may call <see cref="Release"/> from any thread.
/// </summary>
public sealed class LocalMuteGuard
{
    private readonly IOutputMute _output;
    private readonly IMuteBackup _backup;
    private readonly object _gate = new();
    private string? _id;
    private bool _wasMuted;

    public LocalMuteGuard(IOutputMute output, IMuteBackup backup)
    {
        _output = output;
        _backup = backup;
    }

    /// <summary>The output muted for the forwarding right now (null: none).</summary>
    public string? Engaged { get { lock (_gate) return _id; } }

    /// <summary>Mutes <paramref name="endpointId"/> (remembering its state); another output engaged before gets its state back.</summary>
    public void Engage(string endpointId)
    {
        lock (_gate)
        {
            if (_id == endpointId) return;
            ReleaseLocked();
            if (_output.IsMuted(endpointId) is not { } muted) return; // gone: nothing to mute or restore
            _id = endpointId;
            _wasMuted = muted;
            if (muted) return; // the user's own mute: leave it, and leave it alone afterwards
            _backup.Save(endpointId, false);
            _output.SetMuted(endpointId, true);
        }
    }

    /// <summary>The forwarding ended: the output gets the state from before (only what we muted is unmuted).</summary>
    public void Release()
    {
        lock (_gate) ReleaseLocked();
    }

    private void ReleaseLocked()
    {
        if (_id is not { } id) return;
        _id = null;
        if (!_wasMuted) _output.SetMuted(id, false);
        _backup.Clear();
    }

    /// <summary>
    /// At start: a backup left behind (crash, hard kill while forwarding) means the output is still muted by us. Unmute
    /// it when it is still muted; nothing else.
    /// </summary>
    public bool RecoverLeftover()
    {
        lock (_gate)
        {
            if (_backup.Load() is not { } left) return false;
            _backup.Clear();
            if (left.WasMuted || _output.IsMuted(left.Id) != true) return false;
            _output.SetMuted(left.Id, false);
            return true;
        }
    }
}

/// <summary>The real output (Core Audio endpoint volume).</summary>
public sealed class EndpointOutputMute : IOutputMute
{
    public bool? IsMuted(string endpointId) => AudioEndpoints.State(endpointId)?.muted;
    public void SetMuted(string endpointId, bool muted) => AudioEndpoints.SetMute(endpointId, muted);
}

/// <summary>Test instances: never touch the real output, only say what would happen.</summary>
public sealed class LoggedOutputMute : IOutputMute
{
    private readonly Dictionary<string, bool> _state = new();
    public bool? IsMuted(string endpointId) { lock (_state) return _state.TryGetValue(endpointId, out var m) && m; }
    public void SetMuted(string endpointId, bool muted)
    {
        lock (_state) _state[endpointId] = muted;
        Log.Info($"Dev: would {(muted ? "mute" : "unmute")} the local output (real output left alone)");
    }
}

/// <summary><c>audio-mute-backup.json</c> in the data folder.</summary>
public sealed class FileMuteBackup(string path) : IMuteBackup
{
    public static FileMuteBackup Default => new(Path.Combine(AppSettings.Folder, "audio-mute-backup.json"));

    public (string Id, bool WasMuted)? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o || LinkProtocol.Str(o, "id") is not { Length: > 0 } id) return null;
            return (id, LinkProtocol.Bool(o, "wasMuted"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string id, bool wasMuted)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject { ["id"] = id, ["wasMuted"] = wasMuted }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"Audio link: mute backup not written ({ex.Message})"); }
    }

    public void Clear()
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
