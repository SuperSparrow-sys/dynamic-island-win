using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using Microsoft.Win32;

namespace DynamicBay.Services;

/// <summary>
/// Speaker mute, microphone mute and which apps currently use the microphone or camera.
/// Mute state comes from Core Audio (default endpoints); "in use" comes from the same per-app usage records
/// Windows uses for its own privacy indicator (CapabilityAccessManager).
/// </summary>
public sealed partial class AudioService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _usage = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private IMMDeviceEnumerator? _enumerator;
    private bool _started, _muteKnown;

    [ObservableProperty] private bool _speakerMuted;
    [ObservableProperty] private int _volume;            // 0..100
    [ObservableProperty] private bool _micMuted;
    [ObservableProperty] private bool _micInUse;
    [ObservableProperty] private string _micApp = "";
    [ObservableProperty] private bool _cameraInUse;
    [ObservableProperty] private string _cameraApp = "";

    /// <summary>A recording started (app name) - used for the "Stumm?" question.</summary>
    public event Action<string>? MicStarted;
    public event Action<bool>? MicMuteChanged;

    public AudioService(AppSettings settings)
    {
        _settings = settings;
        _fast.Tick += (_, _) => ReadMute();
        _usage.Tick += (_, _) => ReadUsage();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        if (_enumerator is null)
        {
            try { _enumerator = (IMMDeviceEnumerator)CreateEnumerator(); }
            catch (Exception ex) { Log.Error("Audio", ex); }
        }
        ReadMute();
        ReadUsage(initial: true);
        _fast.Start();
        _usage.Start();
    }

    /// <summary>Switched off in the settings: no more polling, no symbols.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _fast.Stop();
        _usage.Stop();
        MicInUse = CameraInUse = false;
    }

    // ---------- mute (Core Audio) ----------

    // The default devices rarely change: polling every half second reuses them and asks Windows again every 5 s.
    private readonly Dictionary<(EDataFlow, ERole), (IAudioEndpointVolume? ep, DateTime at)> _endpoints = new();

    private IAudioEndpointVolume? Endpoint(EDataFlow flow, ERole role)
    {
        if (_endpoints.TryGetValue((flow, role), out var cached) && DateTime.UtcNow - cached.at < TimeSpan.FromSeconds(5)) return cached.ep;
        var ep = FreshEndpoint(flow, role);
        _endpoints[(flow, role)] = (ep, DateTime.UtcNow);
        return ep;
    }

    private IAudioEndpointVolume? FreshEndpoint(EDataFlow flow, ERole role)
    {
        try
        {
            if (_enumerator is null || _enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device is null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
            return obj as IAudioEndpointVolume;
        }
        catch { return null; }
    }

    /// <summary>The default device changed: forget the cached endpoints, so mute and volume act on the new one at once.</summary>
    public void DevicesChanged()
    {
        _endpoints.Clear();
        if (_started) ReadMute();
    }

    private void ReadMute()
    {
        var speaker = Endpoint(EDataFlow.Render, ERole.Multimedia);
        if (speaker is not null)
        {
            speaker.GetMute(out bool m);
            speaker.GetMasterVolumeLevelScalar(out float v);
            SpeakerMuted = m || v < 0.005f; // volume at 0 counts as "sound off" too
            Volume = (int)Math.Round(v * 100);
        }
        var mic = Endpoint(EDataFlow.Capture, ERole.Communications) ?? Endpoint(EDataFlow.Capture, ERole.Console);
        if (mic is not null)
        {
            mic.GetMute(out bool mm);
            // The first reading is the starting state, not a change.
            if (mm != MicMuted || !_muteKnown) { bool changed = _muteKnown && mm != MicMuted; MicMuted = mm; _muteKnown = true; if (changed) MicMuteChanged?.Invoke(mm); }
        }
    }

    /// <summary>Mutes/unmutes the default microphones (communications and console, if they differ).</summary>
    [RelayCommand]
    public void ToggleMicMute() => SetMicMute(!MicMuted);

    public void SetMicMute(bool mute)
    {
        var ctx = Guid.Empty;
        foreach (var role in new[] { ERole.Communications, ERole.Console })
            try { Endpoint(EDataFlow.Capture, role)?.SetMute(mute, ref ctx); } catch { }
        ReadMute();
    }

    [RelayCommand]
    public void ToggleSpeakerMute()
    {
        var ctx = Guid.Empty;
        try { Endpoint(EDataFlow.Render, ERole.Multimedia)?.SetMute(!SpeakerMuted, ref ctx); } catch { }
        ReadMute();
    }

    // ---------- microphone / camera in use (privacy usage records) ----------

    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private void ReadUsage(bool initial = false)
    {
        var mic = ActiveApps("microphone");
        var cam = ActiveApps("webcam");
        bool wasMic = MicInUse;
        MicInUse = mic.Count > 0;
        MicApp = mic.Count switch { 0 => "", 1 => mic[0], _ => $"{mic[0]} +{mic.Count - 1}" };
        CameraInUse = cam.Count > 0;
        CameraApp = cam.Count == 0 ? "" : cam[0];
        if (MicInUse && !wasMic && !initial) MicStarted?.Invoke(MicApp);
    }

    /// <summary>Apps whose last usage has started but not stopped (that's how Windows marks "in use").</summary>
    private static List<string> ActiveApps(string capability)
    {
        var result = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{Store}\{capability}");
            if (root is null) return result;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var np = root.OpenSubKey(name);
                    foreach (var exe in np?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var k = np!.OpenSubKey(exe);
                        if (InUse(k)) result.Add(FriendlyExe(exe));
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(name);
                    if (InUse(k)) result.Add(FriendlyPackage(name));
                }
            }
        }
        catch { }
        // DynamicBay itself never counts
        return result.Where(n => !n.Equals("DynamicBay", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
    }

    private static bool InUse(RegistryKey? k)
    {
        if (k is null) return false;
        long start = k.GetValue("LastUsedTimeStart") is long s ? s : 0;
        long stop = k.GetValue("LastUsedTimeStop") is long t ? t : -1;
        return start > 0 && stop == 0;
    }

    private static string FriendlyExe(string key)
    {
        // "C:#Program Files#Discord#app#Discord.exe" -> "Discord"
        var name = Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : key;
    }

    private static string FriendlyPackage(string family)
    {
        // Package family "Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe" -> Start menu name if known
        var app = InstalledApps.Cached?.FirstOrDefault(a => a.LaunchPath.Contains(family + "!", StringComparison.OrdinalIgnoreCase));
        if (app is not null) return app.Name;
        var core = family.Split('_')[0];
        var last = core.Split('.').Last();
        return last.StartsWith("Windows", StringComparison.Ordinal) && last.Length > 7 ? last[7..] : last;
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo(bool micInUse, string app, bool micMuted, bool speakerMuted)
    {
        _fast.Stop(); _usage.Stop(); // keep real readings from overwriting the demo
        MicInUse = micInUse; MicApp = app; MicMuted = micMuted; SpeakerMuted = speakerMuted;
    }

    /// <summary>
    /// The Core Audio device enumerator, created from its CLSID. Not with "new" on a [ComImport] class: several services
    /// declare one for the same CLSID, the runtime then hands out the first one's wrapper type and the cast fails
    /// (only in the published app, depending on which service starts first).
    /// </summary>
    private static object CreateEnumerator() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    // ---------- Core Audio interop ----------

    private enum EDataFlow { Render = 0, Capture = 1 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
