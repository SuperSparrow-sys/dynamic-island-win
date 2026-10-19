using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using DynamicBay.Services.Link;
using NAudio.CoreAudioApi;

namespace DynamicBay.Services;

/// <summary>A Windows output as the volume rows see it: how loud it plays right now (peak), its own volume and mute.</summary>
public readonly record struct OutputLevel(string Id, string Name, bool Headphones, float Peak, float Level, bool Muted = false);

/// <summary>One place the sound plays: a Windows output or a paired device ("Ton an Gerät", or its sound here).</summary>
public sealed partial class VolumeChannel : ObservableObject
{
    public string Key { get; init; } = "";
    [ObservableProperty] private string _name = "";
    public bool Headphones { get; init; }
    /// <summary>A paired device: this PC's sound plays there, or (<see cref="IsIncoming"/>) its sound plays here.</summary>
    public bool IsRemote { get; init; }
    public bool IsLaptop { get; init; }
    /// <summary>The other device's sound playing here: on top of the outputs, so the slider for all leaves it alone.</summary>
    public bool IsIncoming { get; init; }
    /// <summary>Muted in Windows: shown dimmed; moving its own slider unmutes it (like the Windows sound settings).</summary>
    [ObservableProperty] private bool _isMuted;
    /// <summary>The slider cannot act ("ab 1.1.13": the device is older); shown instead of the slider.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSet))] private string _note = "";
    public bool CanSet => Note.Length == 0;
    [ObservableProperty] private string _tip = "";
    /// <summary>0..100, set by the slider (two-way) or by <see cref="Show"/>.</summary>
    [ObservableProperty] private double _level;

    internal Action<VolumeChannel>? Moved;
    private bool _showing;

    /// <summary>A value read from Windows or the paired device: shown, not sent back.</summary>
    internal void Show(double level)
    {
        _showing = true;
        try { Level = level; } finally { _showing = false; }
    }

    partial void OnLevelChanged(double value) { if (!_showing) Moved?.Invoke(this); }
}

/// <summary>A row as read: what <see cref="OutputVolumeService"/> shows.</summary>
public sealed record VolumeRow(string Key, string Name, double Level, bool Headphones = false, bool Remote = false, bool Laptop = false,
    bool Incoming = false, bool Muted = false, string Note = "", string Tip = "");

/// <summary>
/// The pure part of the volume rows: which outputs get a row, and how the slider for all of them moves each one.
/// </summary>
public static class VolumeMix
{
    /// <summary>Peak above this: the output plays something.</summary>
    public const float Sounding = 0.002f;
    /// <summary>An output that went quiet keeps its row this long (pauses between songs, a skip).</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The outputs with a row: the default output (unless its sound goes to a paired device and it is muted for
    /// that), and every other output that plays now or played within <see cref="Linger"/>. A Windows "geteiltes
    /// Audio" endpoint is one output; Windows splits it to the headsets itself. <paramref name="heard"/> is updated.
    /// </summary>
    public static List<OutputLevel> Places(IReadOnlyList<OutputLevel> outputs, string? defaultId, string? forwardedFrom,
        Dictionary<string, DateTime> heard, DateTime now)
    {
        foreach (var o in outputs) if (o.Peak > Sounding) heard[o.Id] = now;
        foreach (var gone in heard.Keys.Where(k => outputs.All(o => o.Id != k)).ToList()) heard.Remove(gone);
        return outputs
            .Where(o => o.Id != forwardedFrom
                        && (o.Id == defaultId || (heard.TryGetValue(o.Id, out var at) && now - at <= Linger)))
            .OrderBy(o => o.Id == defaultId ? 0 : 1)
            .ToList();
    }

    /// <summary>The slider for all of them stands at the loudest one.</summary>
    public static double Master(IEnumerable<double> levels) => levels.DefaultIfEmpty(0).Max();

    /// <summary>Each level as a share of the loudest: what the slider for all of them keeps. All silent: equal shares.</summary>
    public static double[] Weights(IReadOnlyList<double> levels)
    {
        double max = Master(levels);
        return max < 0.5 ? levels.Select(_ => 1.0).ToArray() : levels.Select(l => l / max).ToArray();
    }

    /// <summary>The slider for all of them at <paramref name="master"/>: every level in proportion (0..100, whole numbers).</summary>
    public static double[] Apply(IReadOnlyList<double> weights, double master) =>
        weights.Select(w => Math.Round(Math.Clamp(w * Math.Clamp(master, 0, 100), 0, 100))).ToArray();

    /// <summary>Endpoint form factor (PKEY_AudioEndpoint_FormFactor): 3 headphones, 5 headset.</summary>
    public static bool IsHeadphones(uint? formFactor, string name) => formFactor is { } f
        ? f is 3 or 5
        : name.StartsWith("Kopfh", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Headphones", StringComparison.OrdinalIgnoreCase)
          || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Volume controls of the Windows outputs, opened once and used for every step of a drag: opening one goes through
/// the device enumerator, which is too slow for each mouse move on the UI thread. Kept for a few seconds only, so a
/// device that went away or came back is opened fresh.
/// </summary>
public sealed class LevelControls(Func<string, Action<float>?> open)
{
    public static readonly TimeSpan Keep = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, (Action<float>? Set, DateTime At)> _open = new();

    public void Set(string id, float level, DateTime now)
    {
        if (!_open.TryGetValue(id, out var c) || now - c.At > Keep) c = (open(id), now);
        _open[id] = (c.Set, now); // in use: kept while the drag goes on
        c.Set?.Invoke(level);
    }

    public void Clear() => _open.Clear();
}

/// <summary>Where the volume rows act: Windows outputs and the link. Replaced in tests.</summary>
public interface IVolumeTargets
{
    void SetOutput(string id, float level);
    void Unmute(string id);
    void SetRemote(double level);
    void SetIncoming(double level);
}

/// <summary>
/// "Lautstärke" in the audio card while the sound plays on more than one device, or on a paired one, or a paired
/// device's sound plays here: a slider for all of them and one per device. Windows outputs are set with their own
/// volume; a paired device gets the volume of the forwarded sound only (its own volume stays as it is). Read only
/// while the panel is open.
/// </summary>
public sealed partial class OutputVolumeService : ObservableObject
{
    private const string RemotePrefix = "link:", IncomingPrefix = "from:";
    private readonly DispatcherTimer _read = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, DateTime> _heard = new();
    private readonly Func<string, (string Name, bool Headphones)?> _known;
    private readonly IVolumeTargets _targets;
    private LinkHub? _link;
    private bool _panelOpen, _reading, _demo, _showingMaster;
    private double[] _weights = Array.Empty<double>();
    private DateTime _setAt;

    public ObservableCollection<VolumeChannel> Channels { get; } = new();
    /// <summary>The rows are shown: two or more places, or a paired device.</summary>
    [ObservableProperty] private bool _isVisible;
    /// <summary>The slider for all of them: two or more places (the other device's sound here does not count).</summary>
    [ObservableProperty] private bool _hasMaster;
    [ObservableProperty] private double _master;

    /// <param name="known">The name and kind of an output as the output list shows it ("Lautsprecher", "AirPods Pro").</param>
    public OutputVolumeService(Func<string, (string Name, bool Headphones)?> known, IVolumeTargets? targets = null)
    {
        _known = known;
        _targets = targets ?? new Targets(this);
        _read.Tick += (_, _) => _ = ReadAsync();
    }

    public LinkHub? Link
    {
        get => _link;
        set
        {
            if (_link is not null) _link.PropertyChanged -= OnLinkChanged;
            _link = value;
            if (_link is not null) _link.PropertyChanged += OnLinkChanged;
        }
    }

    private void OnLinkChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LinkHub.IsForwardingAudio) or nameof(LinkHub.AudioFrom) or nameof(LinkHub.RemoteVolume)
            or nameof(LinkHub.ReceiveVolume) or nameof(LinkHub.RemoteHasVolume)) _ = ReadAsync();
    }

    public void SetPanelOpen(bool open)
    {
        if (_panelOpen == open) return;
        _panelOpen = open;
        if (open) { _ = ReadAsync(); _read.Start(); } else { _read.Stop(); (_targets as Targets)?.Controls.Clear(); }
    }

    private async Task ReadAsync()
    {
        if (!_panelOpen || _reading || _demo) return;
        _reading = true;
        try
        {
            var (outputs, defaultId) = await Task.Run(ReadOutputs);
            // A slider being moved wins over what was read a moment before (and the demo over anything read).
            if (_demo || DateTime.UtcNow - _setAt < TimeSpan.FromSeconds(1.5)) return;
            var places = VolumeMix.Places(outputs, defaultId, _link?.IsForwardingAudio == true ? LinkHub.AudioMute.Engaged : null, _heard, DateTime.UtcNow);
            Sync(Rows(places));
        }
        catch (Exception ex) { Log.Info($"Output volume: {ex.Message}"); }
        finally { _reading = false; }
    }

    private List<VolumeRow> Rows(List<OutputLevel> places)
    {
        var rows = places.Select(p => _known(p.Id) is { } k
            ? new VolumeRow(p.Id, k.Name, Math.Round(p.Level * 100), k.Headphones, Muted: p.Muted, Tip: k.Name)
            : new VolumeRow(p.Id, p.Name, Math.Round(p.Level * 100), p.Headphones, Muted: p.Muted, Tip: p.Name)).ToList();
        if (_link is not { } link) return rows;
        if (link.IsForwardingAudio && link.Audio?.SendingTo is { } peer)
        {
            bool can = link.RemoteHasVolume;
            rows.Add(new VolumeRow(RemotePrefix + peer, link.AudioDevice, Math.Round(link.RemoteVolume * 100), Remote: true, Laptop: link.AudioDeviceIsLaptop,
                Note: can ? "" : Loc.German ? "ab 1.1.13" : "needs 1.1.13",
                Tip: can ? link.AudioDevice
                    : Loc.German ? $"{link.AudioDevice} braucht DynamicBay 1.1.13 oder neuer" : $"{link.AudioDevice} needs DynamicBay 1.1.13 or newer"));
        }
        if (link.AudioFrom.Length > 0)
            rows.Add(new VolumeRow(IncomingPrefix + link.AudioFrom, link.AudioFrom, Math.Round(link.ReceiveVolume * 100), Remote: true,
                Laptop: link.Devices.FirstOrDefault(d => d.Name == link.AudioFrom)?.IsLaptop ?? false, Incoming: true,
                Tip: Loc.German ? $"Ton von {link.AudioFrom}" : $"Sound from {link.AudioFrom}"));
        return rows;
    }

    /// <summary>Pool thread: Core Audio objects are created and used there (MTA).</summary>
    private static (List<OutputLevel>, string?) ReadOutputs()
    {
        var list = new List<OutputLevel>();
        using var en = new MMDeviceEnumerator();
        string? defaultId = null;
        try { using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = d.ID; } catch { }
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                string name = dev.FriendlyName;
                int paren = name.IndexOf(" (", StringComparison.Ordinal);
                string shortName = paren > 0 && name.EndsWith(')') ? name[(paren + 2)..^1] : name; // "Kopfhörer (AirPods)" -> "AirPods"
                uint? formFactor = null;
                try { if (dev.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor]?.Value is uint f) formFactor = f; } catch { }
                var vol = dev.AudioEndpointVolume;
                list.Add(new OutputLevel(dev.ID, shortName, VolumeMix.IsHeadphones(formFactor, name), dev.AudioMeterInformation.MasterPeakValue,
                    vol.MasterVolumeLevelScalar, vol.Mute));
            }
            catch { } // gone in between
            finally { dev.Dispose(); }
        }
        return (list, defaultId);
    }

    internal void Sync(List<VolumeRow> rows)
    {
        for (int i = Channels.Count - 1; i >= 0; i--)
            if (rows.All(r => r.Key != Channels[i].Key)) { Channels[i].Moved = null; Channels.RemoveAt(i); }
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var ch = Channels.FirstOrDefault(c => c.Key == r.Key);
            if (ch is null)
            {
                ch = new VolumeChannel { Key = r.Key, Headphones = r.Headphones, IsRemote = r.Remote, IsLaptop = r.Laptop, IsIncoming = r.Incoming };
                ch.Moved = OnChannelMoved;
                Channels.Insert(Math.Min(i, Channels.Count), ch);
            }
            else
            {
                int at = Channels.IndexOf(ch);
                if (at != i) Channels.Move(at, Math.Min(i, Channels.Count - 1));
            }
            ch.Name = r.Name;
            ch.IsMuted = r.Muted;
            ch.Note = r.Note;
            ch.Tip = r.Tip;
            ch.Show(r.Level);
        }
        HasMaster = Mixed().Count() >= 2;
        IsVisible = HasMaster || Channels.Any(c => c.IsRemote);
        _weights = VolumeMix.Weights(Levels());
        ShowMaster();
    }

    /// <summary>The rows the slider for all of them moves: not the other device's sound here (it plays through them).</summary>
    private IEnumerable<VolumeChannel> Mixed() => Channels.Where(c => !c.IsIncoming && c.CanSet);

    private double[] Levels() => Mixed().Select(c => c.Level).ToArray();

    private void ShowMaster()
    {
        _showingMaster = true;
        try { Master = VolumeMix.Master(Levels()); } finally { _showingMaster = false; }
    }

    /// <summary>One device's slider: that device only; the slider for all of them follows the loudest.</summary>
    private void OnChannelMoved(VolumeChannel ch)
    {
        _setAt = DateTime.UtcNow;
        if (!ch.CanSet) return;
        if (ch.IsMuted && !ch.IsRemote && ch.Level > 0)
        {
            ch.IsMuted = false;
            if (!_demo) _targets.Unmute(ch.Key);
        }
        Send(ch);
        _weights = VolumeMix.Weights(Levels());
        ShowMaster();
    }

    /// <summary>The slider for all of them: every device in proportion, so the balance between them stays.</summary>
    partial void OnMasterChanged(double value)
    {
        if (_showingMaster) return;
        _setAt = DateTime.UtcNow;
        var mixed = Mixed().ToList();
        if (_weights.Length != mixed.Count) _weights = VolumeMix.Weights(Levels());
        var levels = VolumeMix.Apply(_weights, value);
        for (int i = 0; i < mixed.Count; i++)
        {
            mixed[i].Show(levels[i]);
            Send(mixed[i]);
        }
    }

    private void Send(VolumeChannel ch)
    {
        if (_demo) return;
        if (ch.IsIncoming) _targets.SetIncoming(ch.Level / 100);
        else if (ch.IsRemote) _targets.SetRemote(ch.Level / 100);
        else _targets.SetOutput(ch.Key, (float)(ch.Level / 100));
    }

    private sealed class Targets(OutputVolumeService owner) : IVolumeTargets
    {
        public readonly LevelControls Controls = new(AudioEndpoints.LevelControl);
        public void SetOutput(string id, float level) => Controls.Set(id, level, DateTime.UtcNow);
        public void Unmute(string id) => AudioEndpoints.SetMute(id, false);
        public void SetRemote(double level) => owner._link?.SetRemoteVolume(level);
        public void SetIncoming(double level) => owner._link?.SetReceiveVolume(level);
    }

    /// <summary>For design snapshots only: speakers, headsets, a muted speaker and the laptop (as the sender), or the receiver's view.</summary>
    internal void LoadDemo(string variant = "sender")
    {
        _demo = true;
        bool de = Loc.German;
        Sync(variant switch
        {
            "receiver" => new()
            {
                new("demo-speakers", de ? "Lautsprecher" : "Speakers", 50),
                new("demo-airpods", "AirPods Pro", 70, Headphones: true),
                new(IncomingPrefix + "PC-Büro", "PC-Büro", 60, Remote: true, Incoming: true),
            },
            "old" => new()
            {
                new("demo-speakers", de ? "Lautsprecher" : "Speakers", 40),
                new(RemotePrefix + "demo", "LaptopJonathan", 100, Remote: true, Laptop: true, Note: de ? "ab 1.1.13" : "needs 1.1.13"),
            },
            _ => new()
            {
                new("demo-speakers", de ? "Lautsprecher" : "Speakers", 40),
                new("demo-airpods", "AirPods Pro", 60, Headphones: true),
                new("demo-jbl", "JBL Flip 6", 30, Muted: true),
                new(RemotePrefix + "demo", "LaptopJonathan", 80, Remote: true, Laptop: true),
            },
        });
    }
}
