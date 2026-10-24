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

    /// <summary>The camera as Media Foundation reports it (also where Windows no longer writes the registry records).</summary>
    public CameraActivity? Sensor { get; set; }

    /// <summary>A recording started (app name) - used for the "Stumm?" question.</summary>
    public event Action<string>? MicStarted;
    public event Action<bool>? MicMuteChanged;

    public AudioService(AppSettings settings)
    {
        _settings = settings;
        // Mute and volume are only shown: nobody looking (locked, display off), nothing to read. The microphone use stays
        // polled, it decides about the call mode that the paired device follows.
        _fast.Tick += (_, _) => { if (!ActivityState.Current.Away) ReadMute(); };
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
    private readonly Dictionary<(EDataFlow, ERole), (IAudioEndpointVolume? ep, string? id, DateTime at)> _endpoints = new();
    private string? _micId;
    private bool _enumeratorLost;

    /// <summary>The Windows audio service restarted and is reachable again (the device notifications must be registered anew).</summary>
    public event Action? AudioServiceRestarted;

    private IAudioEndpointVolume? Endpoint(EDataFlow flow, ERole role) => Endpoint(flow, role, out _);

    private IAudioEndpointVolume? Endpoint(EDataFlow flow, ERole role, out string? id)
    {
        if (_endpoints.TryGetValue((flow, role), out var cached) && DateTime.UtcNow - cached.at < TimeSpan.FromSeconds(5)) { id = cached.id; return cached.ep; }
        var ep = FreshEndpoint(flow, role, out id);
        _endpoints[(flow, role)] = (ep, id, DateTime.UtcNow);
        return ep;
    }

    private IAudioEndpointVolume? FreshEndpoint(EDataFlow flow, ERole role, out string? id)
    {
        id = null;
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)CreateEnumerator();
            int hr = _enumerator.GetDefaultAudioEndpoint(flow, role, out var device);
            // No such device (E_NOTFOUND) is normal. Anything else: the audio service restarted, this enumerator is dead.
            if (hr != 0 && hr != unchecked((int)0x80070490)) { _enumerator = null; _enumeratorLost = true; return null; }
            if (_enumeratorLost) { _enumeratorLost = false; AudioServiceRestarted?.Invoke(); }
            if (hr != 0 || device is null) return null;
            device.GetId(out id);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
            return obj as IAudioEndpointVolume;
        }
        catch { return null; }
    }

    /// <summary>A cached device is gone (unplugged, or the audio service restarted): ask Windows again next time.</summary>
    private void Forget(EDataFlow flow)
    {
        foreach (var key in _endpoints.Keys.Where(k => k.Item1 == flow).ToList()) _endpoints.Remove(key);
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
            try
            {
                speaker.GetMute(out bool m);
                speaker.GetMasterVolumeLevelScalar(out float v);
                SpeakerMuted = m || v < 0.005f; // volume at 0 counts as "sound off" too
                Volume = (int)Math.Round(v * 100);
            }
            catch (COMException) { Forget(EDataFlow.Render); }
        }
        var mic = Endpoint(EDataFlow.Capture, ERole.Communications, out var micId);
        if (mic is null) mic = Endpoint(EDataFlow.Capture, ERole.Console, out micId);
        if (mic is not null)
        {
            try
            {
                mic.GetMute(out bool mm);
                // Another microphone (switched by hand or by the Bluetooth rule): its state is a start, not a change.
                if (micId != _micId) { _micId = micId; _muteKnown = false; }
                // The first reading is the starting state, not a change.
                if (mm != MicMuted || !_muteKnown) { bool changed = _muteKnown && mm != MicMuted; MicMuted = mm; _muteKnown = true; if (changed) MicMuteChanged?.Invoke(mm); }
            }
            catch (COMException) { Forget(EDataFlow.Capture); }
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
        try
        {
            var speaker = Endpoint(EDataFlow.Render, ERole.Multimedia);
            if (speaker is not null && SpeakerMuted)
            {
                speaker.SetMute(false, ref ctx);
                // "Off" can also mean volume 0: unmuting alone would stay silent.
                speaker.GetMasterVolumeLevelScalar(out float v);
                if (v < 0.005f) speaker.SetMasterVolumeLevelScalar(0.3f, ref ctx);
            }
            else speaker?.SetMute(true, ref ctx);
        }
        catch { }
        ReadMute();
    }

    // ---------- microphone / camera in use (privacy usage records) ----------

    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private void ReadUsage(bool initial = false)
    {
        var running = new Lazy<HashSet<string>>(RunningImagePaths); // only looked at when Windows reports a use
        // No microphone at all: whatever the privacy list says, nothing can be recording
        var mic = HasMicrophone() ? ActiveApps("microphone", running) : new List<string>();
        var cam = ActiveApps("webcam", running);
        bool wasMic = MicInUse;
        MicInUse = mic.Count > 0;
        MicApp = mic.Count switch { 0 => "", 1 => mic[0], _ => $"{mic[0]} +{mic.Count - 1}" };
        if (Sensor?.Apps is { Count: > 0 } streaming) cam.AddRange(streaming.Where(a => !cam.Contains(a, StringComparer.OrdinalIgnoreCase)));
        CameraInUse = cam.Count > 0;
        CameraApp = cam.Count == 0 ? "" : cam[0];
        if (MicInUse && !wasMic && !initial) MicStarted?.Invoke(MicApp);
    }

    private bool HasMicrophone()
    {
        try { return Endpoint(EDataFlow.Capture, ERole.Console) is not null; }
        catch { return true; } // unsure: keep the privacy list's word
    }

    /// <summary>
    /// Apps whose last usage has started but not stopped (that's how Windows marks "in use") and that still run:
    /// an app that crashed while recording leaves its entry "started" for good, and the mic stayed orange forever.
    /// </summary>
    private static List<string> ActiveApps(string capability, Lazy<HashSet<string>> running)
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
                        if (InUse(k) && StillRunning(exe, packaged: false, running.Value)) result.Add(FriendlyExe(exe));
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(name);
                    if (InUse(k) && StillRunning(name, packaged: true, running.Value)) result.Add(FriendlyPackage(name));
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

    /// <summary>
    /// Whether the app of a privacy entry runs: an exe key ("C:#Program Files#Discord#Discord.exe") by its path, a package
    /// family ("Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe") by a process from its WindowsApps folder. Without a
    /// process list (empty) every entry counts, as before.
    /// </summary>
    internal static bool StillRunning(string key, bool packaged, IReadOnlyCollection<string> runningPaths)
    {
        if (runningPaths.Count == 0) return true;
        if (!packaged) return runningPaths.Contains(key.Replace('#', '\\'), StringComparer.OrdinalIgnoreCase);
        int cut = key.LastIndexOf('_');
        if (cut <= 0) return true;
        string name = key[..cut], publisher = key[(cut + 1)..];
        return runningPaths.Any(p => p.Contains(@"\WindowsApps\" + name + "_", StringComparison.OrdinalIgnoreCase)
                                     && p.Contains("__" + publisher, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Full paths of the running processes this user may query (others are skipped).</summary>
    private static HashSet<string> RunningImagePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new System.Text.StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) paths.Add(sb.ToString(0, size));
                }
                finally { CloseHandle(h); }
            }
        }
        return paths;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

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

    private static object CreateEnumerator() => AudioEndpoints.CreateEnumerator();

    // ---------- Core Audio interop ----------

    private enum EDataFlow { Render = 0, Capture = 1 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void OpenPropertyStore(int access, out IntPtr store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
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
