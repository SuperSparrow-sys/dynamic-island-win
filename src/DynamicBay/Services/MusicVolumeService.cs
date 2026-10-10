using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Volume of the music only, not of the whole PC. Spotify connected in the settings: Spotify's own volume through its
/// Web API (also when it plays on the phone or a speaker). Otherwise - Apple Music, Spotify without the connection,
/// any other player - the app's own volume in the Windows volume mixer.
/// </summary>
public sealed partial class MusicVolumeService : ObservableObject
{
    private readonly MediaService _media;
    private readonly SpotifyService _spotify;
    private readonly DispatcherTimer _read = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _send = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _panelOpen, _dragging, _spotifyDirty, _demo;
    private DateTime _setAt;

    [ObservableProperty] private double _level = 50;   // 0..100
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isMuted;
    /// <summary>Where the slider acts, for the tooltip.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Tip))] private string _target = "";
    public string Tip => Loc.German ? $"Lautstärke {Target} (nur die Musik)" : $"{Target} volume (music only)";

    public MusicVolumeService(MediaService media, SpotifyService spotify)
    {
        _media = media;
        _spotify = spotify;
        _read.Tick += (_, _) => Read();
        _send.Tick += (_, _) => FlushSpotify();
        _media.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MediaService.SourceAppId) or nameof(MediaService.HasSession)) Read(); };
        _spotify.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SpotifyService.Volume) or nameof(SpotifyService.SupportsVolume) or nameof(SpotifyService.IsConnected)) Read();
        };
    }

    private bool UsesSpotifyApi => _media.IsSpotify && _spotify.IsConnected && _spotify.SupportsVolume;

    /// <summary>Read only while the panel is open: on opening and every 2 s (changes made in the app itself).</summary>
    public void SetPanelOpen(bool open)
    {
        if (_panelOpen == open) return;
        _panelOpen = open;
        if (open) { Read(); _read.Start(); } else _read.Stop();
    }

    private void Read()
    {
        if (!_panelOpen || _dragging || _demo || DateTime.UtcNow - _setAt < TimeSpan.FromSeconds(1.5)) return;
        if (!_media.HasSession) { IsAvailable = false; return; }
        if (UsesSpotifyApi)
        {
            Level = _spotify.Volume;
            IsMuted = _spotify.Volume == 0;
            IsAvailable = true;
            Target = "Spotify";
            return;
        }
        var v = AppVolume.Get(_media.SourceAppId);
        IsAvailable = v is not null;
        if (v is { } r)
        {
            Level = Math.Round(r.level * 100);
            IsMuted = r.muted || r.level < 0.005f;
            Target = _media.SourceApp;
        }
    }

    public void BeginDrag() => _dragging = true;

    /// <summary>While dragging: Windows takes every step at once, Spotify gets the newest value at most every 150 ms.</summary>
    public void Set(double percent)
    {
        Level = Math.Round(Math.Clamp(percent, 0, 100));
        IsMuted = Level == 0;
        _setAt = DateTime.UtcNow;
        if (UsesSpotifyApi)
        {
            _spotifyDirty = true;
            if (!_send.IsEnabled) { FlushSpotify(); _send.Start(); }
        }
        else AppVolume.Set(_media.SourceAppId, (float)(Level / 100));
    }

    public void EndDrag()
    {
        _dragging = false;
        _setAt = DateTime.UtcNow;
        FlushSpotify();
    }

    private void FlushSpotify()
    {
        if (!_spotifyDirty) { _send.Stop(); return; }
        _spotifyDirty = false;
        _ = _spotify.SetVolumeAsync((int)Level);
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo(double level)
    {
        _demo = true;
        Level = level; IsAvailable = true; Target = "Spotify";
    }
}

/// <summary>
/// An app's own volume in the Windows volume mixer (Core Audio sessions on the default speaker). The media session's
/// app id is matched to the audio sessions by package name ("AppleInc.AppleMusicWin_…!App") or exe ("Spotify.exe").
/// An app can have several sessions (Spotify has a few): all of them are set.
/// </summary>
public static class AppVolume
{
    public static (float level, bool muted)? Get(string appId)
    {
        foreach (var v in Sessions(appId))
        {
            try
            {
                v.GetMasterVolume(out float level);
                v.GetMute(out bool muted);
                return (level, muted);
            }
            catch { }
        }
        return null;
    }

    public static void Set(string appId, float level)
    {
        var ctx = Guid.Empty;
        foreach (var v in Sessions(appId))
        {
            try
            {
                v.SetMasterVolume(level, ref ctx);
                if (level > 0) v.SetMute(false, ref ctx);
            }
            catch { }
        }
    }

    /// <summary>"AppleInc.AppleMusicWin_nzyj5cx40ttqa!App" → "AppleInc.AppleMusicWin"; "Spotify.exe" → "Spotify".</summary>
    public static string MatchKey(string appId)
    {
        string key = appId.Split('!')[0].Split('_')[0];
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) key = key[..^4];
        return key;
    }

    private static List<ISimpleAudioVolume> Sessions(string appId)
    {
        var found = new List<ISimpleAudioVolume>();
        if (string.IsNullOrWhiteSpace(appId)) return found;
        string key = MatchKey(appId);
        if (key.Length < 3) return found;
        try
        {
            var enumerator = (IMMDeviceEnumerator)CreateEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0 || device is null) return found;
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out var obj);
            if (obj is not IAudioSessionManager2 manager) return found;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);
            int self = Environment.ProcessId;
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var control);
                if (control is not IAudioSessionControl2 c2) continue;
                c2.GetProcessId(out uint pid);
                if (pid == 0 || pid == self) continue;
                c2.GetSessionIdentifier(out string? id);
                bool match = id?.Contains(key, StringComparison.OrdinalIgnoreCase) == true;
                if (!match)
                {
                    try { using var p = Process.GetProcessById((int)pid); match = p.ProcessName.Equals(key, StringComparison.OrdinalIgnoreCase); }
                    catch { }
                }
                if (match && control is ISimpleAudioVolume v) found.Add(v);
            }
        }
        catch (Exception ex) { Log.Info($"AppVolume: {ex.Message}"); }
        return found;
    }

    /// <summary>
    /// The Core Audio device enumerator, created from its CLSID. Not with "new" on a [ComImport] class: several services
    /// declare one for the same CLSID, the runtime then hands out the first one's wrapper type and the cast fails
    /// (only in the published app, depending on which service starts first).
    /// </summary>
    private static object CreateEnumerator() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    // ---------- Core Audio interop ----------

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        void GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        void GetGroupingParam(out Guid param);
        void SetGroupingParam(ref Guid param, ref Guid ctx);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        void GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid ctx);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
