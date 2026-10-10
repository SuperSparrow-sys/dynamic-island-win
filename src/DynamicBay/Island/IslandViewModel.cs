using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Island;

public enum PeekPriority { Low, Normal, High }

/// <summary>A short-lived announcement (screenshot taken, device connected, notification...).</summary>
public sealed class PeekItem
{
    public Geometry? Icon { get; init; }
    public bool IconFilled { get; init; }
    public Brush IconBrush { get; init; } = Brushes.White;
    public Brush IconBackground { get; init; } = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    public ImageSource? Image { get; init; }
    public bool RoundImage { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string? Trailing { get; init; }
    public Brush TrailingBrush { get; init; } = Brushes.White;
    public double? Progress { get; init; }
    public double Seconds { get; init; } = 3.2;
    public PeekPriority Priority { get; init; } = PeekPriority.Normal;
    /// <summary>Also shown in "Nicht stören" (timer finished, feedback to a key the user just pressed).</summary>
    public bool ShowInDnd { get; init; }
    public Action? OnClick { get; init; }
    public object? DragPayload { get; init; }
    public bool ShowWaveform { get; init; }
    /// <summary>Optional question with two buttons (e.g. "Installieren" / "Später").</summary>
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public string? DismissText { get; init; }
    public bool HasActions => ActionText is not null;
    /// <summary>Set while the message waits for a hidden island (fullscreen app).</summary>
    public DateTime? Queued { get; set; }
    public bool HasTrailingInfo => !HasActions;
    public Brush WaveformBrush { get; init; } = Brushes.White;
    public bool HasImage => Image is not null;
    public bool HasIcon => Image is null && Icon is not null;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool HasTrailing => !string.IsNullOrEmpty(Trailing);
    public bool HasProgress => Progress is not null;
}

/// <summary>Per-window holder for the peek currently shown (PeekView binds to Peek).</summary>
public sealed partial class PeekHolder : ObservableObject
{
    [ObservableProperty] private PeekItem? _peek;
}

public sealed partial class IslandViewModel : ObservableObject
{
    public AppSettings Settings { get; }
    public MediaService Media { get; }
    public ClipboardService Clipboard { get; }
    public ShelfService Shelf { get; }
    public NotificationService Notifications { get; }
    public TimerService Timer { get; }
    public CalendarService Calendar { get; }
    public BatteryService Battery { get; }
    public SpotifyService Spotify { get; }
    /// <summary>Volume of the music alone (Spotify's own or the player's in the Windows mixer).</summary>
    public MusicVolumeService MusicVolume { get; }
    /// <summary>Sound output, microphone and Bluetooth headsets (the "Audio" widget and its rules).</summary>
    public AudioDevicesService AudioDevices { get; }

    [ObservableProperty] private int _tab;           // 0 home, 1 tray, 2 notifications
    [ObservableProperty] private bool _isVertical;
    [ObservableProperty] private bool _isPinned;

    [ObservableProperty] private bool _showMedia;
    [ObservableProperty] private bool _showTimer;
    [ObservableProperty] private bool _showCalendar;
    [ObservableProperty] private bool _showBatteryLow;
    [ObservableProperty] private bool _showClock;
    [ObservableProperty] private bool _hasCompact;
    public ClockService Clock { get; }
    public SystemService System { get; }
    public ShortcutsService Shortcuts { get; }
    public ClaudeService Claude { get; }
    public AudioService Audio { get; }
    public WorkModeService Work { get; }
    public TimeTrackingService Time { get; }
    public Services.Scripting.ScriptWidgetsService Scripts { get; }
    /// <summary>Connected Bluetooth devices for the "Geräte" widget (set by the app right after construction).</summary>
    public BluetoothService? Bluetooth { get; set; }
    public Services.M365.MicrosoftAccount? Microsoft { get; set; }
    public Services.M365.TodoService? Todo { get; set; }
    public Services.M365.PresenceService? Presence { get; set; }
    public Services.M365.ContactsService? Contacts { get; set; }
    [ObservableProperty] private bool _showMuted;
    [ObservableProperty] private bool _showMic;
    [ObservableProperty] private bool _showCamera;
    [ObservableProperty] private bool _showScripts;
    [ObservableProperty] private bool _showStatus;
    [ObservableProperty] private bool _showClaude;
    [ObservableProperty] private bool _showMeeting;
    [ObservableProperty] private bool _showTimeTrack;

    public event Action? OpenSettingsRequested;
    public event Action? HideRequested;
    public event Action? QuitRequested;

    /// <summary>Alt held while the panel is open: the hide button turns into a hidden quit button (like Option on the Mac).</summary>
    [ObservableProperty] private bool _altHeld;
    public event Action? CompactChanged;

    public IslandViewModel(AppSettings settings, MediaService media, ClipboardService clipboard, ShelfService shelf,
        NotificationService notifications, TimerService timer, CalendarService calendar, BatteryService battery,
        SpotifyService spotify)
    {
        Settings = settings; Media = media; Clipboard = clipboard; Shelf = shelf; Notifications = notifications;
        Timer = timer; Calendar = calendar; Battery = battery; Spotify = spotify;
        MusicVolume = new MusicVolumeService(media, spotify);
        AudioDevices = new AudioDevicesService(settings, media);
        Clock = new ClockService(settings);
        System = new SystemService();
        Shortcuts = new ShortcutsService(settings);
        Claude = new ClaudeService(settings);
        Audio = new AudioService(settings);
        Work = new WorkModeService(settings, Audio, timer);
        Time = new TimeTrackingService(settings);
        Scripts = new Services.Scripting.ScriptWidgetsService(settings);
        Scripts.CompactChanged += () => { RecomputeCompact(); CompactChanged?.Invoke(); };

        PropertyChangedEventHandler recompute = (_, _) => RecomputeCompact();
        Media.PropertyChanged += recompute;
        Timer.PropertyChanged += recompute;
        Calendar.PropertyChanged += recompute;
        Battery.PropertyChanged += recompute;
        Claude.PropertyChanged += recompute;
        Audio.PropertyChanged += recompute;
        Time.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TimeTrackingService.IsRunning)) RecomputeCompact(); };
        Settings.PropertyChanged += recompute;
        RecomputeCompact();
    }

    private void RecomputeCompact()
    {
        var s = Settings;
        // "Nicht stören": only music and a running timer stay, everything else leaves the island.
        // Quiet (by hand, in a call, while sharing, in a focus session): only music, the timer and the call stay.
        bool all = !s.Quiet;
        bool meeting = s.InMeeting;
        bool tracking = s.HasCompact(Widgets.TimeTrack) && Time.IsRunning; // like the timer: stays visible when the island is quiet
        bool media = s.MediaEnabled && s.HasCompact(Widgets.Media) && Media.HasSession && Media.IsPlaying;
        bool timer = s.TimerEnabled && s.HasCompact(Widgets.Timer) && Timer.IsActive;
        bool cal = all && s.CalendarEnabled && s.HasCompact(Widgets.Calendar) && Calendar.IsSoon;
        bool low = all && s.BatteryEnabled && s.HasCompact(Widgets.Battery) && Battery.IsLow;
        bool claude = all && s.ClaudeEnabled && s.HasCompact(Widgets.Claude) && (Claude.AnyWorking || Claude.AnyWaiting);
        bool muted = all && s.AudioEnabled && s.HasCompact(Widgets.Muted) && Audio.SpeakerMuted;
        bool mic = (all || meeting) && s.AudioEnabled && s.HasCompact(Widgets.Mic) && Audio.MicInUse;
        bool cam = (all || meeting) && s.AudioEnabled && s.HasCompact(Widgets.Mic) && Audio.CameraInUse;
        bool scripts = all && Scripts.CompactIds.Count > 0;
        bool activities = media || timer || cal || low || claude || muted || mic || cam || scripts || meeting || tracking;
        // The clock shows either as a permanent segment, or as the idle face when nothing else is going on.
        bool clock = all && (s.HasCompact(Widgets.Clock) || (s.Idle == IdleStyle.Clock && !activities));
        bool any = activities || clock;
        bool changed = media != ShowMedia || timer != ShowTimer || cal != ShowCalendar || low != ShowBatteryLow
                       || clock != ShowClock || claude != ShowClaude || muted != ShowMuted || mic != ShowMic || cam != ShowCamera || scripts != ShowScripts || meeting != ShowMeeting || tracking != ShowTimeTrack || any != HasCompact;
        ShowMedia = media; ShowTimer = timer; ShowCalendar = cal; ShowBatteryLow = low; ShowClock = clock; ShowClaude = claude; ShowMuted = muted; ShowMic = mic; ShowCamera = cam; ShowStatus = mic || cam || muted; ShowScripts = scripts; ShowMeeting = meeting; ShowTimeTrack = tracking; HasCompact = any;
        if (changed) CompactChanged?.Invoke();
    }

    [RelayCommand] private void OpenSettings() => OpenSettingsRequested?.Invoke();
    [RelayCommand] private void Hide() { if (AltHeld) QuitRequested?.Invoke(); else HideRequested?.Invoke(); }
    [RelayCommand] private void Quit() => QuitRequested?.Invoke();
    [RelayCommand] private void TogglePin() => IsPinned = !IsPinned;
    [RelayCommand] private void SelectTab(object? index) => Tab = Convert.ToInt32(index);
}
