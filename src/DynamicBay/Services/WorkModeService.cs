using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using Microsoft.Win32;

namespace DynamicBay.Services;

/// <summary>
/// Work situations that make the island quiet on their own (AppSettings.Quiet):
/// <list type="bullet">
/// <item>Meeting: Teams or Zoom uses the microphone - "Nicht stören", the island shows the call and its duration.</item>
/// <item>Sharing: the screen is being captured (Teams, Zoom or a browser share it, Windows records that like microphone use)
///   or a PowerPoint slide show runs - the island shrinks to its bar and notifications show no text.</item>
/// <item>Focus: a Pomodoro focus session runs - "Nicht stören", with a small summary of today's sessions.</item>
/// </list>
/// </summary>
public sealed partial class WorkModeService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AudioService _audio;
    private readonly TimerService _timer;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _meetingStart;
    private int _tickCount;

    [ObservableProperty] private string _meetingApp = "";
    [ObservableProperty] private string _meetingDuration = "0:00";
    [ObservableProperty] private string _focusToday = "";

    public WorkModeService(AppSettings settings, AudioService audio, TimerService timer)
    {
        _settings = settings;
        _audio = audio;
        _timer = timer;
        _audio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AudioService.MicInUse) or nameof(AudioService.MicApp)) UpdateMeeting();
        };
        _timer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TimerService.IsActive) or nameof(TimerService.Mode)) UpdateFocus();
        };
        _timer.Finished += mode => { if (mode == TimerMode.Focus) AddFocusSession(_settings.PomodoroWorkMinutes); };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.MeetingMode)) UpdateMeeting();
            if (e.PropertyName == nameof(AppSettings.FocusQuiet)) UpdateFocus();
            if (e.PropertyName == nameof(AppSettings.HideWhenSharing)) CheckSharing();
        };
        _tick.Tick += (_, _) => OnTick();
        LoadFocusStats();
    }

    public void Start()
    {
        UpdateMeeting();
        UpdateFocus();
        _tick.Start();
    }

    private void OnTick()
    {
        if (_settings.InMeeting) MeetingDuration = Format(DateTime.Now - _meetingStart);
        // Screen capture is checked every 2 s (a few registry keys and one window lookup).
        if (++_tickCount % 2 == 0) CheckSharing();
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------- meeting ----------

    private void UpdateMeeting()
    {
        string app = MeetingAppOf(_audio.MicApp);
        bool inCall = _settings.MeetingMode && _settings.AudioEnabled && _audio.MicInUse && app.Length > 0;
        if (inCall == _settings.InMeeting) return;
        if (inCall) { _meetingStart = DateTime.Now; MeetingApp = app; MeetingDuration = "0:00"; }
        _settings.InMeeting = inCall;
        Log.Info(inCall ? $"Meeting started ({app})" : $"Meeting ended after {MeetingDuration}");
    }

    /// <summary>"Microsoft Teams", "MSTeams", "Zoom" (also among several apps using the microphone).</summary>
    public static string MeetingAppOf(string micApps)
    {
        if (micApps.Contains("Teams", StringComparison.OrdinalIgnoreCase)) return "Teams";
        if (micApps.Contains("Zoom", StringComparison.OrdinalIgnoreCase)) return "Zoom";
        return "";
    }

    // ---------- screen sharing / presenting ----------

    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly string[] CaptureStores = { "graphicsCaptureProgrammatic", "graphicsCaptureWithoutBorder" };
    private string _lastShareSource = "";

    private void CheckSharing()
    {
        string source = _settings.HideWhenSharing ? SharingSource() : "";
        bool sharing = source.Length > 0;
        if (sharing == _settings.Sharing) return;
        _settings.Sharing = sharing;
        Log.Info(sharing ? $"Screen sharing detected ({source})" : $"Screen sharing ended ({_lastShareSource})");
        _lastShareSource = source;
    }

    /// <summary>What is capturing the screen right now ("" = nothing).</summary>
    private static string SharingSource()
    {
        // PowerPoint slide show (also on a second display or in a window).
        if (FindWindow("screenClass", null) != IntPtr.Zero) return "PowerPoint";
        foreach (var store in CaptureStores)
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey($@"{Store}\{store}");
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    if (name == "NonPackaged")
                    {
                        using var np = root.OpenSubKey(name);
                        foreach (var exe in np?.GetSubKeyNames() ?? Array.Empty<string>())
                        {
                            using var k = np!.OpenSubKey(exe);
                            if (Capturing(k) && !IsOwn(exe)) return Path.GetFileNameWithoutExtension(exe.Replace('#', '\\'));
                        }
                    }
                    else
                    {
                        using var k = root.OpenSubKey(name);
                        // Snipping Tool captures for a moment for every screenshot - that is no sharing.
                        if (Capturing(k) && !name.StartsWith("Microsoft.ScreenSketch", StringComparison.OrdinalIgnoreCase)) return name.Split('_')[0];
                    }
                }
            }
            catch { }
        }
        return "";
    }

    private static bool IsOwn(string exeKey) => exeKey.EndsWith("#DynamicBay.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Started and not stopped (the same marks Windows uses for its microphone/camera indicator).</summary>
    private static bool Capturing(RegistryKey? k)
    {
        if (k is null) return false;
        long start = k.GetValue("LastUsedTimeStart") is long s ? s : 0;
        long stop = k.GetValue("LastUsedTimeStop") is long t ? t : 0;
        return start > 0 && stop == 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);

    // ---------- focus sessions ----------

    private void UpdateFocus()
    {
        bool focus = _settings.FocusQuiet && _timer.IsActive && _timer.Mode == TimerMode.Focus;
        if (focus != _settings.FocusActive) _settings.FocusActive = focus;
    }

    private static string StatsFile => Path.Combine(AppSettings.Folder, "focus.json");
    private Dictionary<string, int[]> _stats = new(); // "yyyy-MM-dd" -> [sessions, minutes]

    private void LoadFocusStats()
    {
        try { if (File.Exists(StatsFile)) _stats = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(StatsFile)) ?? new(); }
        catch { }
        UpdateFocusText();
    }

    private void AddFocusSession(int minutes)
    {
        string day = DateTime.Today.ToString("yyyy-MM-dd");
        var v = _stats.TryGetValue(day, out var d) ? d : new int[2];
        _stats[day] = new[] { v[0] + 1, v[1] + minutes };
        // Keep a month.
        foreach (var old in _stats.Keys.Where(k => string.CompareOrdinal(k, DateTime.Today.AddDays(-31).ToString("yyyy-MM-dd")) < 0).ToList()) _stats.Remove(old);
        try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(StatsFile, JsonSerializer.Serialize(_stats)); } catch { }
        UpdateFocusText();
    }

    private void UpdateFocusText()
    {
        if (!_stats.TryGetValue(DateTime.Today.ToString("yyyy-MM-dd"), out var d) || d[0] == 0) { FocusToday = ""; return; }
        FocusToday = Loc.German
            ? $"Heute: {d[0]} Fokus-{(d[0] == 1 ? "Sitzung" : "Sitzungen")} · {d[1]} Min."
            : $"Today: {d[0]} focus {(d[0] == 1 ? "session" : "sessions")} · {d[1]} min";
    }
}
