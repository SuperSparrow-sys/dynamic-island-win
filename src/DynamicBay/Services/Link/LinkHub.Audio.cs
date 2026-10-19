using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using DynamicBay.Services.Link.Audio;

namespace DynamicBay.Services.Link;

public sealed partial class LinkDeviceView
{
    /// <summary>This PC's sound plays on this device (the audio card shows it as the chosen output).</summary>
    [ObservableProperty] private bool _audioSelected;
}

/// <summary>"Ton an &lt;Gerät&gt;": the paired devices as outputs in the audio card (see <see cref="LinkAudioFeature"/>).</summary>
public sealed partial class LinkHub
{
    /// <summary>
    /// The mute this PC's output gets while its sound plays elsewhere. One for the whole app, so the exit handlers can
    /// give the output back even when the link is already gone. Test instances only log what they would do.
    /// </summary>
    public static LocalMuteGuard AudioMute { get; } = new(DevMode.On ? new LoggedOutputMute() : new EndpointOutputMute(), FileMuteBackup.Default);

    public LinkAudioFeature? Audio { get; private set; }

    /// <summary>Name of the device this PC's sound plays on ("" = here).</summary>
    [ObservableProperty] private string _audioDevice = "";
    [ObservableProperty] private bool _isForwardingAudio;
    /// <summary>Starting: the device was chosen, the other side has not answered yet.</summary>
    [ObservableProperty] private bool _audioBusy;
    /// <summary>Name of the device whose sound plays here ("" = none).</summary>
    [ObservableProperty] private string _audioFrom = "";

    /// <summary>The sound moved: device name and null (plays there now) or why it is here (again).</summary>
    public event Action<string, AudioForwardEnd?>? AudioMoved;

    /// <summary>Devices the sound can go to: online, the link on, "Ton" on here.</summary>
    public IEnumerable<LinkDeviceView> AudioTargets => IsRunning && _settings.LinkShareAudio ? Devices.Where(d => d.IsOnline) : Enumerable.Empty<LinkDeviceView>();
    public bool HasAudioTargets => AudioTargets.Any();

    private LinkAudioFeature CreateAudio(LinkService link)
    {
        var audio = new LinkAudioFeature(link, _settings, new LinkAudioOptions
        {
            // Test instances run on one PC: a generated tone instead of the real sound, and nothing is played.
            Source = DevMode.On ? () => new ToneAudioSource() : () => new LoopbackAudioSource(),
            Sink = DevMode.On ? () => new NullAudioSink() : () => new WasapiAudioSink(),
            Mute = AudioMute,
            // A Tailscale relay jitters more than the LAN.
            TargetMs = id => Service?.ConnectionInfo(id)?.Remote is { } r && LinkDiscovery.IsTailscale(r.Address.IsIPv4MappedToIPv6 ? r.Address.MapToIPv4() : r.Address) ? 150 : 100,
            SamePc = DevMode.On,
        });
        audio.SendingStarted += (id, name) => { SyncAudio(); AudioMoved?.Invoke(NameOf(id, name), null); };
        audio.SendingEnded += (id, name, why) => { SyncAudio(); AudioMoved?.Invoke(NameOf(id, name), why); };
        audio.ReceivingStarted += (id, name) => { AudioFrom = NameOf(id, name); OnPropertyChanged(nameof(ReceiveVolume)); };
        audio.ReceivingEnded += (_, _) => { AudioFrom = Audio?.ReceivingFrom is { } from ? NameOf(from, "") : ""; OnPropertyChanged(nameof(ReceiveVolume)); };
        // Volume changed on the other device: the sliders here follow, and the sending side keeps it for that device.
        audio.SendVolumeChanged += level => { if (Audio?.SendingTo is { } to) RememberVolume(to, level); OnPropertyChanged(nameof(RemoteVolume)); };
        audio.ReceiveVolumeChanged += _ => OnPropertyChanged(nameof(ReceiveVolume));
        return audio;
    }

    private string NameOf(string id, string fallback) => Devices.FirstOrDefault(d => d.Id == id)?.Name ?? (fallback.Length > 0 ? fallback : id);

    /// <summary>The audio card: play this PC's sound on that device (again: nothing changes).</summary>
    [RelayCommand]
    private Task SelectAudioTarget(LinkDeviceView? device) => device is null ? Task.CompletedTask : StartAudioAsync(device.Id);

    /// <summary>Sends this PC's sound to a paired device; a failure comes as <see cref="AudioMoved"/> with the reason.</summary>
    public async Task StartAudioAsync(string peerId)
    {
        if (Audio is not { } audio || AudioBusy || audio.SendingTo == peerId) return;
        string name = NameOf(peerId, "");
        audio.SetSendVolume(_settings.LinkAudioVolumes.TryGetValue(peerId, out var level) ? level : 1); // as last time on that device
        AudioBusy = true;
        try
        {
            Log.Info($"Audio link: sending the sound to {name}");
            if (await audio.StartSendingAsync(peerId) is { } failed) AudioMoved?.Invoke(name, failed);
        }
        finally
        {
            AudioBusy = false;
            SyncAudio();
        }
    }

    /// <summary>Volume of this PC's sound on the device it plays on (0..1).</summary>
    public double RemoteVolume => Audio?.SendVolume ?? 1;
    /// <summary>The device the sound plays on understands the volume (1.1.13 and newer).</summary>
    public bool RemoteHasVolume => Audio?.RemoteHasVolume ?? false;
    /// <summary>Volume of the other device's sound playing here (0..1).</summary>
    public double ReceiveVolume => Audio?.ReceiveVolume ?? 1;

    /// <summary>The slider of the device the sound plays on: sent there and kept for the next time.</summary>
    public void SetRemoteVolume(double level)
    {
        if (Audio is not { SendingTo: { } to } audio) return;
        audio.SetSendVolume(level);
        RememberVolume(to, audio.SendVolume);
    }

    /// <summary>The slider of the sound from the other device: plays at once, the sending device's slider follows.</summary>
    public void SetReceiveVolume(double level) => Audio?.SetReceiveVolume(level);

    private void RememberVolume(string peerId, double level)
    {
        if (_settings.LinkAudioVolumes.TryGetValue(peerId, out var old) && Math.Abs(old - level) < 0.005) return;
        _settings.LinkAudioVolumes[peerId] = Math.Round(level, 3);
        _settings.SaveSoon();
    }

    /// <summary>A local output was chosen: the sound comes back here.</summary>
    public void StopAudio() => Audio?.StopSending();

    /// <summary>Windows switched the default output while the sound plays elsewhere: capture and mute the new one.</summary>
    public void DefaultOutputChanged(string? newDefaultId)
    {
        if (Audio is not { SendingTo: not null } audio || newDefaultId is null || newDefaultId == AudioMute.Engaged || DevMode.On) return;
        _ = audio.FollowDefaultOutputAsync();
    }

    private void SyncAudio()
    {
        string? to = Audio?.SendingTo;
        foreach (var d in Devices) d.AudioSelected = d.Id == to;
        IsForwardingAudio = to is not null;
        AudioDevice = to is null ? "" : NameOf(to, "");
        OnPropertyChanged(nameof(RemoteVolume));
        OnPropertyChanged(nameof(RemoteHasVolume));
        OnPropertyChanged(nameof(AudioTargets));
        OnPropertyChanged(nameof(HasAudioTargets));
    }

    /// <summary>For the compact island: "Ton am Laptop".</summary>
    public string AudioDeviceText => AudioDevice.Length == 0 ? "" : (Loc.German ? "Ton am " : "Sound on ") + AudioDevice;
    partial void OnAudioDeviceChanged(string value) => OnPropertyChanged(nameof(AudioDeviceText));

    /// <summary>The device's glyph in the compact island (laptop or monitor).</summary>
    public bool AudioDeviceIsLaptop => Devices.FirstOrDefault(d => d.AudioSelected)?.IsLaptop ?? false;
    partial void OnIsForwardingAudioChanged(bool value) => OnPropertyChanged(nameof(AudioDeviceIsLaptop));

    /// <summary>Snapshot mode: this PC's sound plays on the laptop.</summary>
    internal void LoadAudioDemo()
    {
        var laptop = Devices.FirstOrDefault(d => d.IsOnline);
        if (laptop is null) return;
        laptop.AudioSelected = true;
        AudioDevice = laptop.Name;
        IsForwardingAudio = true;
        OnPropertyChanged(nameof(AudioTargets));
        OnPropertyChanged(nameof(HasAudioTargets));
    }
}
