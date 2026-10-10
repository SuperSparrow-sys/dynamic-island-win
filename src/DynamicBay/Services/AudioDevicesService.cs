using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public sealed partial class AudioDeviceItem : ObservableObject
{
    public string Id { get; init; } = "";
    public AudioFlow Flow { get; init; }
    public string Name { get; init; } = "";
    public bool Bluetooth { get; init; }
    public bool Headphones { get; init; }
    [ObservableProperty] private bool _isDefault;
}

/// <summary>A paired Bluetooth headset or speaker, connected or not.</summary>
public sealed partial class BluetoothAudioItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string ConnectId { get; init; } = "";
    public bool Headphones { get; init; }
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private bool _busy;
}

/// <summary>
/// Sound in and out without the Windows settings: pick the output and the microphone with one click, connect the
/// AirPods (or any paired headset) with one click. Three rules keep Bluetooth headphones painless:
/// - headphones that connect become the output right away,
/// - a Bluetooth microphone is avoided (it switches the headset into call mode: mono, telephone quality) - the PC's
///   microphone is used instead, with a one-click way back for a call,
/// - music pauses when the headphones drop out, instead of carrying on through the speakers.
/// Windows reports every device change; the lists are read again a moment later (off the UI thread).
/// </summary>
public sealed partial class AudioDevicesService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly MediaService _media;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Notifier? _notifier;
    private AudioEndpoints.IMMDeviceEnumerator? _enumerator;
    private bool _started, _refreshing, _again, _first = true;
    private HashSet<string> _activeOutputs = new();
    private string? _defaultOutput;
    private bool _defaultOutputWasHeadphones;
    private string? _allowedBluetoothMic; // the user chose the headset microphone (for a call): leave it until it disconnects

    public ObservableCollection<AudioDeviceItem> Outputs { get; } = new();
    public ObservableCollection<AudioDeviceItem> Inputs { get; } = new();
    public ObservableCollection<BluetoothAudioItem> Headsets { get; } = new();
    [ObservableProperty] private bool _hasHeadsets;

    /// <summary>The default output or microphone changed (the mute/volume reading must follow at once).</summary>
    public event Action? DefaultChanged;

    /// <summary>The Bluetooth microphone was replaced: (headset, microphone now used).</summary>
    public event Action<string, string>? BluetoothMicAvoided;

    public AudioDevicesService(AppSettings settings, MediaService media)
    {
        _settings = settings;
        _media = media;
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(); };
        _settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.InMeeting) && _settings.InMeeting) _ = CallStartedAsync(); };
    }

    /// <summary>
    /// A call (Teams, Zoom, phone via Phone Link) started: listen on the connected headphones, speak into the PC's own
    /// microphone (a Bluetooth headset microphone would turn the sound into telephone quality). The microphone's mute
    /// state is left alone; a headset microphone picked by hand for this call stays.
    /// </summary>
    private async Task CallStartedAsync()
    {
        if (!_settings.AudioCallRouting) return;
        var list = await Task.Run(AudioEndpoints.List);
        var headphones = list.FirstOrDefault(e => e.Flow == AudioFlow.Output && e.Active && e.Bluetooth && !e.HandsFree && IsHeadphones(e));
        var outId = AudioEndpoints.DefaultId(AudioFlow.Output);
        if (headphones is not null && headphones.Id != outId && AudioEndpoints.SetDefault(headphones.Id))
        {
            AudioEndpoints.SetMute(headphones.Id, false);
            Log.Info($"Audio: call - listening on {headphones.Device}");
        }
        var inId = AudioEndpoints.DefaultId(AudioFlow.Input, 2);
        var pcMic = list.FirstOrDefault(e => e.Flow == AudioFlow.Input && e.Active && !e.Bluetooth);
        if (pcMic is not null && pcMic.Id != inId && inId != _allowedBluetoothMic && AudioEndpoints.SetDefault(pcMic.Id))
            Log.Info($"Audio: call - speaking into {ShortName(pcMic)}");
        DefaultChanged?.Invoke();
        await RefreshAsync();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _enumerator = (AudioEndpoints.IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            _notifier = new Notifier(() => _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); DefaultChanged?.Invoke(); }));
            _enumerator.RegisterEndpointNotificationCallback(_notifier);
        }
        catch (Exception ex) { Log.Error("Audio devices", ex); }
        _ = RefreshAsync();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try { if (_notifier is not null) _enumerator?.UnregisterEndpointNotificationCallback(_notifier); } catch { }
    }

    /// <summary>Reads the devices off the UI thread (asking each driver takes a few milliseconds), then applies the rules.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) { _again = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _again = false;
                var (list, outId, inId, commId) = await Task.Run(() => (AudioEndpoints.List(), AudioEndpoints.DefaultId(AudioFlow.Output),
                    AudioEndpoints.DefaultId(AudioFlow.Input), AudioEndpoints.DefaultId(AudioFlow.Input, 2)));
                Apply(list, outId, inId, commId);
            } while (_again);
        }
        finally { _refreshing = false; }
    }

    private static string ShortName(AudioEndpointInfo e)
    {
        if (e.Bluetooth) return e.Device;
        int p = e.Name.IndexOf(" (", StringComparison.Ordinal);
        return p > 0 ? e.Name[..p] : e.Name;
    }

    private static bool IsHeadphones(AudioEndpointInfo e)
    {
        string n = (e.Name + " " + e.Device).ToLowerInvariant();
        return new[] { "kopfhörer", "headphone", "headset", "airpods", "buds", "beats", "wh-", "wf-" }.Any(n.Contains);
    }

    private void Apply(List<AudioEndpointInfo> list, string? outId, string? inId, string? commId)
    {
        // ---- lists ----
        var outputs = list.Where(e => e.Flow == AudioFlow.Output && e.Active && !e.HandsFree).ToList();
        var inputs = list.Where(e => e.Flow == AudioFlow.Input && e.Active).ToList();
        Sync(Outputs, outputs, outId);
        Sync(Inputs, inputs, inId);

        var headsets = list.Where(e => e.Bluetooth && e.Flow == AudioFlow.Output && e.Connectable)
            .GroupBy(e => e.Device, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BluetoothAudioItem
            {
                Name = g.Key,
                ConnectId = g.First().Id,
                Headphones = g.Any(IsHeadphones),
                // Connected: one of its sound devices is active (classic, or through Intel's Bluetooth offload).
                Connected = list.Any(e => e.Active && e.Bluetooth && string.Equals(e.Device, g.Key, StringComparison.OrdinalIgnoreCase)),
            }).OrderByDescending(h => h.Connected).ThenBy(h => h.Name).ToList();
        if (!headsets.Select(h => h.Name + h.Connected).SequenceEqual(Headsets.Select(h => h.Name + h.Connected)))
        {
            Headsets.Clear();
            foreach (var h in headsets) Headsets.Add(h);
        }
        HasHeadsets = Headsets.Count > 0;

        // ---- rules ----
        var active = outputs.Select(o => o.Id).ToHashSet();
        bool first = _first;
        _first = false;

        // 1. The headphones that were playing are gone: pause the music (it would carry on through the speakers),
        //    the speaker Windows switched to is not left muted (so the next sound is not silent by surprise).
        if (!first && _defaultOutput is not null && _defaultOutputWasHeadphones && !active.Contains(_defaultOutput) && _settings.AudioPauseOnDisconnect)
        {
            Log.Info("Audio: headphones gone - pausing music");
            _ = PauseThenUnmuteAsync(outId);
        }

        // 2. Headphones just connected: make them the output (Windows does not always do it).
        if (!first && _settings.AudioSwitchToHeadphones)
        {
            var fresh = outputs.FirstOrDefault(o => o.Bluetooth && IsHeadphones(o) && !_activeOutputs.Contains(o.Id));
            if (fresh is not null && fresh.Id != outId && AudioEndpoints.SetDefault(fresh.Id))
            {
                AudioEndpoints.SetMute(fresh.Id, false); // headphones: only you hear it
                Log.Info($"Audio: {fresh.Device} connected - now the output");
                outId = fresh.Id;
                foreach (var o in Outputs) o.IsDefault = o.Id == outId;
            }
        }
        _activeOutputs = active;
        _defaultOutput = outId;
        _defaultOutputWasHeadphones = outputs.FirstOrDefault(o => o.Id == outId) is { } d && d.Bluetooth && IsHeadphones(d);

        // 3. A Bluetooth headset as microphone: use the PC's microphone, so the headset keeps its good sound.
        if (_allowedBluetoothMic is not null && !inputs.Any(i => i.Id == _allowedBluetoothMic)) _allowedBluetoothMic = null;
        if (_settings.AudioAvoidBluetoothMic)
        {
            var btMic = inputs.FirstOrDefault(i => (i.Id == inId || i.Id == commId) && i.HandsFree && i.Id != _allowedBluetoothMic);
            var pcMic = inputs.FirstOrDefault(i => !i.Bluetooth);
            if (btMic is not null && pcMic is not null && AudioEndpoints.SetDefault(pcMic.Id))
            {
                Log.Info($"Audio: avoided the Bluetooth microphone of {btMic.Device}, using {ShortName(pcMic)}");
                foreach (var i in Inputs) i.IsDefault = i.Id == pcMic.Id;
                BluetoothMicAvoided?.Invoke(btMic.Device, ShortName(pcMic));
            }
        }
    }

    private static void Sync(ObservableCollection<AudioDeviceItem> target, List<AudioEndpointInfo> source, string? defaultId)
    {
        if (!source.Select(e => e.Id).SequenceEqual(target.Select(t => t.Id)))
        {
            target.Clear();
            foreach (var e in source)
                target.Add(new AudioDeviceItem { Id = e.Id, Flow = e.Flow, Name = ShortName(e), Bluetooth = e.Bluetooth, Headphones = IsHeadphones(e) });
        }
        foreach (var t in target) t.IsDefault = t.Id == defaultId;
    }

    // ---------- commands ----------

    /// <summary>
    /// Picks a device. Microphones: just switched (their mute is never touched - no surprise in a call).
    /// Outputs: from headphones to a speaker the music pauses FIRST and the speaker is on; between speakers the
    /// mute state carries over (muted stays muted, on stays on); headphones are always on.
    /// </summary>
    [RelayCommand]
    private async Task Select(AudioDeviceItem? item)
    {
        if (item is null || item.IsDefault) return;
        if (item.Flow == AudioFlow.Input)
        {
            if (item.Bluetooth) _allowedBluetoothMic = item.Id; // chosen on purpose (for a call)
            if (!AudioEndpoints.SetDefault(item.Id)) return;
            Log.Info($"Audio: microphone -> {item.Name}");
            foreach (var i in Inputs) i.IsDefault = i == item;
            DefaultChanged?.Invoke();
            return;
        }
        var old = Outputs.FirstOrDefault(o => o.IsDefault);
        var oldState = old is null ? null : AudioEndpoints.State(old.Id);
        bool fromHeadphones = old?.Headphones == true;
        bool leavingHeadphones = fromHeadphones && !item.Headphones;
        if (leavingHeadphones && _media.IsPlaying) await _media.PauseAsync(); // before the speaker takes over
        if (!AudioEndpoints.SetDefault(item.Id)) return;
        bool mute = !item.Headphones && !fromHeadphones && oldState is { muted: true };
        AudioEndpoints.SetMute(item.Id, mute);
        Log.Info($"Audio: output {old?.Name ?? "?"} -> {item.Name}, {(mute ? "muted like before" : "sound on")}{(leavingHeadphones ? ", music paused" : "")}");
        foreach (var o in Outputs) o.IsDefault = o == item;
        _defaultOutput = item.Id;
        _defaultOutputWasHeadphones = item.Headphones && item.Bluetooth;
        DefaultChanged?.Invoke();
    }

    private async Task PauseThenUnmuteAsync(string? speakerId)
    {
        await _media.PauseAsync();
        if (speakerId is not null) AudioEndpoints.SetMute(speakerId, false);
        DefaultChanged?.Invoke();
    }

    /// <summary>Connects a disconnected headset, disconnects a connected one.</summary>
    [RelayCommand]
    private async Task Toggle(BluetoothAudioItem? item)
    {
        if (item is null || item.Busy) return;
        item.Busy = true;
        bool connect = !item.Connected;
        bool ok = await Task.Run(() => AudioEndpoints.Connect(item.ConnectId, connect));
        // The driver answers at once; the headset needs a few seconds. Device changes refresh the list by themselves.
        if (ok) for (int i = 0; i < 16 && item.Busy && Headsets.Contains(item); i++) await Task.Delay(500);
        item.Busy = false;
        await RefreshAsync();
    }

    /// <summary>For the "disconnected" peek: connect the headset with this Bluetooth name again.</summary>
    public async Task<bool> ConnectAsync(string bluetoothName)
    {
        var item = Headsets.FirstOrDefault(h => h.Name.Equals(bluetoothName, StringComparison.OrdinalIgnoreCase))
                   ?? Headsets.FirstOrDefault(h => h.Name.Contains(bluetoothName, StringComparison.OrdinalIgnoreCase) || bluetoothName.Contains(h.Name, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.Connected) return false;
        await Toggle(item);
        return true;
    }

    public bool CanConnect(string bluetoothName) =>
        Headsets.Any(h => h.Name.Contains(bluetoothName, StringComparison.OrdinalIgnoreCase) || bluetoothName.Contains(h.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>From the "microphone avoided" peek: use the headset microphone after all (until it disconnects).</summary>
    public void UseBluetoothMic(string device)
    {
        var mic = AudioEndpoints.List().FirstOrDefault(e => e.Flow == AudioFlow.Input && e.Active && e.Bluetooth && e.Device == device);
        if (mic is null) return;
        _allowedBluetoothMic = mic.Id;
        AudioEndpoints.SetDefault(mic.Id);
        _ = RefreshAsync();
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        Stop();
        Outputs.Clear(); Inputs.Clear(); Headsets.Clear();
        Outputs.Add(new AudioDeviceItem { Id = "1", Name = "Lautsprecher", IsDefault = false });
        Outputs.Add(new AudioDeviceItem { Id = "2", Name = "DELL S3225QC" });
        Outputs.Add(new AudioDeviceItem { Id = "3", Name = "AirPods Pro", Bluetooth = true, Headphones = true, IsDefault = true });
        Inputs.Add(new AudioDeviceItem { Id = "4", Flow = AudioFlow.Input, Name = "Mikrofonarray", IsDefault = true });
        Headsets.Add(new BluetoothAudioItem { Name = "AirPods Pro", Headphones = true, Connected = true });
        Headsets.Add(new BluetoothAudioItem { Name = "JBL Flip 6" });
        HasHeadsets = true;
    }

    /// <summary>Windows calls this on its own thread; anything but property changes means "look again".</summary>
    [ComVisible(true)]
    private sealed class Notifier(Action changed) : AudioEndpoints.IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, int state) { changed(); return 0; }
        public int OnDeviceAdded(string id) { changed(); return 0; }
        public int OnDeviceRemoved(string id) { changed(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? id) { changed(); return 0; }
        public int OnPropertyValueChanged(string id, AudioEndpoints.PropertyKey key) => 0;
    }
}
