using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DynamicBay.Core;

public enum IslandEdge { Top, Bottom, Left, Right }
public enum IslandAlign { Start, Center, End }
public enum LayerMode { Floating, Desktop }
public enum IdleStyle { Bar, Clock, Hidden }

/// <summary>Widget and live-activity identifiers used in the customizable layout.</summary>
public static class Widgets
{
    public const string Media = "media", Clock = "clock", Calendar = "calendar", Timer = "timer", System = "system", Shortcuts = "shortcuts", Messenger = "messenger", Claude = "claude", Muted = "muted", Mic = "mic", Devices = "devices", Todo = "todo", Teams = "teams", TimeTrack = "timetrack", Contacts = "contacts", Audio = "audio";
    public static readonly string[] AllHome = { Media, Clock, Calendar, Timer, System, Shortcuts, Messenger, Claude, Devices, Todo, Teams, TimeTrack, Contacts };
    public const string Battery = "battery";
    public static readonly string[] AllCompact = { Media, Timer, Calendar, Battery, Clock, Claude, Muted, Mic, TimeTrack };
    /// <summary>Home widget ids of user scripts: "script:{id}".</summary>
    public const string ScriptPrefix = "script:";
}
public enum DisplayMode { Single, Mirror }
public enum AppIconStyle { Mono, Dark, Color, Original }
public enum CalendarKind { Ics, ICloud, Google, Microsoft }

public enum ScriptSize { Small, Large }

/// <summary>A user script widget (JavaScript, Scriptable-style API) - see docs/SCRIPTS.md. The code lives in scripts{File}.</summary>
/// <summary>Where the island sits for one arrangement of displays (see IslandManager: dock profiles).</summary>
public sealed class DockProfile
{
    public IslandEdge Edge { get; set; }
    public IslandAlign Align { get; set; }
    public double Along { get; set; }
    public double Inset { get; set; }
    public string Monitor { get; set; } = "";
}

public sealed class ScriptWidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public ScriptSize Size { get; set; } = ScriptSize.Large;
    /// <summary>Also show the one-line "Mini" version in the compact island.</summary>
    public bool ShowInCompact { get; set; }
    public bool AllowNetwork { get; set; } = true;
    public string HomeId => Widgets.ScriptPrefix + Id;
}

/// <summary>A connected calendar. Secrets (passwords, tokens) live in <see cref="SecretStore"/> under "cal:{Id}:*".</summary>
public sealed class CalendarAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public CalendarKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";       // ICS link or CalDAV server
    public string User { get; set; } = "";      // Apple ID / Google client id
    public bool Enabled { get; set; } = true;
    /// <summary>Calendars of this account the user switched off (CalDAV URL or Google calendar id).</summary>
    public List<string> Hidden { get; set; } = new();
}

/// <summary>All user settings. Flat on purpose: binds directly into the settings window and serializes as one JSON file.</summary>
public sealed partial class AppSettings : ObservableObject
{
    // General
    [ObservableProperty] private bool _startWithWindows = true;
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private string _toggleHotkey = "Ctrl+Alt+I";
    [ObservableProperty] private bool _checkForUpdates = true;


    // Appearance
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _animationSpeed = 1.0;
    [ObservableProperty] private bool _shadow = true;
    /// <summary>Clock widget as a dial (like the iPhone Clock icon) instead of digits.</summary>
    [ObservableProperty] private bool _clockAnalog;
    /// <summary>Off (default): the island draws in software - much less RAM and CPU for its transparent window.</summary>
    [ObservableProperty] private bool _gpuRendering;
    [ObservableProperty] private IdleStyle _idle = IdleStyle.Bar;
    /// <summary>Frames per second of the island animations; 0 = as many as the display offers.</summary>
    [ObservableProperty] private int _frameRate;

    // Customizable layout
    private ObservableCollection<string> _homeWidgets = new() { Widgets.Media, Widgets.Calendar, Widgets.Timer };
    private ObservableCollection<string> _compactItems = new() { Widgets.Mic, Widgets.Media, Widgets.Timer, Widgets.Calendar, Widgets.Battery, Widgets.Claude, Widgets.Muted, Widgets.TimeTrack };
    private ObservableCollection<string> _shortcuts = new();

    public ObservableCollection<string> HomeWidgets { get => _homeWidgets; set => Hook(ref _homeWidgets, value, nameof(HomeWidgets)); }
    public ObservableCollection<string> CompactItems { get => _compactItems; set => Hook(ref _compactItems, value, nameof(CompactItems)); }
    public ObservableCollection<string> Shortcuts { get => _shortcuts; set => Hook(ref _shortcuts, value, nameof(Shortcuts)); }
    private ObservableCollection<string> _timeProjects = new() { "Allgemein" };
    /// <summary>The one CSV file time tracking writes to ("" = Documents\Zeiterfassung.csv).</summary>
    public string TimeTrackingFile { get => _timeTrackingFile; set => SetProperty(ref _timeTrackingFile, value ?? ""); }
    private string _timeTrackingFile = "";
    /// <summary>Projects of the time tracking widget.</summary>
    public ObservableCollection<string> TimeProjects { get => _timeProjects; set => Hook(ref _timeProjects, value, nameof(TimeProjects)); }

    // Windows banners and notification sound off while DynamicBay runs (see BannerSuppressor); exceptions keep their banner.
    [ObservableProperty] private bool _suppressBanners = true;
    private ObservableCollection<string> _bannerExceptions = new();
    public ObservableCollection<string> BannerExceptions { get => _bannerExceptions; set => Hook(ref _bannerExceptions, value, nameof(BannerExceptions)); }

    public AppSettings()
    {
        _homeWidgets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HomeWidgets));
        _compactItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CompactItems));
        _shortcuts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Shortcuts));
        _timeProjects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TimeProjects));
        _calendarAccounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CalendarAccounts));
        _scriptWidgets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ScriptWidgets));
        _bannerExceptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BannerExceptions));
    }

    /// <summary>Collections raise PropertyChanged for their own name on every change, so listeners and autosave see edits.</summary>
    private void Hook(ref ObservableCollection<string> field, ObservableCollection<string> value, string name)
    {
        field = value ?? new();
        field.CollectionChanged += (_, _) => OnPropertyChanged(name);
        OnPropertyChanged(name);
    }

    public bool HasCompact(string id) => CompactItems.Contains(id);

    private ObservableCollection<ScriptWidgetConfig> _scriptWidgets = new();
    public ObservableCollection<ScriptWidgetConfig> ScriptWidgets
    {
        get => _scriptWidgets;
        set
        {
            _scriptWidgets = value ?? new();
            _scriptWidgets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ScriptWidgets));
            OnPropertyChanged(nameof(ScriptWidgets));
        }
    }

    /// <summary>An account changed in place (on/off, hidden calendars): refresh the calendars and save - without rebuilding the list.</summary>
    public void CalendarAccountsChanged() { OnPropertyChanged(nameof(CalendarAccounts)); SaveSoon(); }

    /// <summary>A script widget changed in place (size, Mini): tell the island and save.</summary>
    public void ScriptWidgetsChanged() { OnPropertyChanged(nameof(ScriptWidgets)); SaveSoon(); }

    private ObservableCollection<CalendarAccount> _calendarAccounts = new();
    public ObservableCollection<CalendarAccount> CalendarAccounts
    {
        get => _calendarAccounts;
        set
        {
            _calendarAccounts = value ?? new();
            _calendarAccounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CalendarAccounts));
            OnPropertyChanged(nameof(CalendarAccounts));
        }
    }

    /// <summary>Older versions stored one ICS URL; turn it into an account.</summary>
    public void Migrate()
    {
        // v2: track-change peek became opt-in (users found the island growing on every song distracting).
        if (SettingsVersion < 2) { MediaPeekOnTrackChange = false; SettingsVersion = 2; }
        // v3: audio live activities (mic in use first, muted speaker last)
        if (SettingsVersion < 3)
        {
            if (!CompactItems.Contains(Widgets.Mic)) CompactItems.Insert(0, Widgets.Mic);
            if (!CompactItems.Contains(Widgets.Muted)) CompactItems.Add(Widgets.Muted);
            SettingsVersion = 3;
        }
        // v4: the Nook page scrolls now - users of Claude Code get the Claude widget once.
        if (SettingsVersion < 4)
        {
            var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (ClaudeEnabled && Directory.Exists(projects) && !HomeWidgets.Contains(Widgets.Claude)) HomeWidgets.Add(Widgets.Claude);
            SettingsVersion = 4;
        }
        // v5: new "Geräte" widget (battery of this PC and Bluetooth devices) - shown once, can be switched off.
        if (SettingsVersion < 5)
        {
            if (BluetoothEnabled && !HomeWidgets.Contains(Widgets.Devices)) HomeWidgets.Add(Widgets.Devices);
            SettingsVersion = 5;
        }
        // v6: all Windows notifications go to the island (banners and sound off for every app, not just Snipping Tool).
        if (SettingsVersion < 6) { SuppressBanners = true; SettingsVersion = 6; }
        // v7: the running time tracking in the small island became a choice - on, as before.
        if (SettingsVersion < 7)
        {
            if (!CompactItems.Contains(Widgets.TimeTrack)) CompactItems.Add(Widgets.TimeTrack);
            SettingsVersion = 7;
        }
        if (!string.IsNullOrWhiteSpace(CalendarIcsUrl) && !CalendarAccounts.Any(a => a.Url == CalendarIcsUrl))
            CalendarAccounts.Add(new CalendarAccount { Kind = CalendarKind.Ics, Name = "Kalender", Url = CalendarIcsUrl.Trim() });
        CalendarIcsUrl = "";
    }
    [ObservableProperty] private bool _showTrayTab = true;
    [ObservableProperty] private bool _showNotificationsTab = true;
    [ObservableProperty] private bool _clock24h = true;
    [ObservableProperty] private bool _clockSeconds;
    [ObservableProperty] private AppIconStyle _appIcons = AppIconStyle.Mono;
    [ObservableProperty] private bool _claudeEnabled = true;

    // Audio: muted speaker, microphone/camera in use, mic mute
    [ObservableProperty] private bool _audioEnabled = true;
    [ObservableProperty] private bool _askMuteOnMicStart = true;
    [ObservableProperty] private string _micMuteHotkey = "Ctrl+Alt+M";
    [ObservableProperty] private string _claudeShareFolder = "";
    [ObservableProperty] private string _sparseAttemptedVersion = "";

    // Layer and visibility
    [ObservableProperty] private LayerMode _layer = LayerMode.Floating;
    [ObservableProperty] private bool _expandOnHover = true;
    [ObservableProperty] private int _hoverDelayMs = 140;
    [ObservableProperty] private int _collapseDelayMs = 420;
    [ObservableProperty] private bool _autoHide;
    [ObservableProperty] private int _autoHideSeconds = 6;
    [ObservableProperty] private bool _hideInFullscreen = true;
    [ObservableProperty] private bool _doNotDisturb;
    // Work modes (WorkModeService): quiet on their own during calls, screen sharing and focus sessions.
    [ObservableProperty] private bool _meetingMode = true;
    [ObservableProperty] private bool _hideWhenSharing = true;
    [ObservableProperty] private bool _focusQuiet = true;
    [ObservableProperty][property: System.Text.Json.Serialization.JsonIgnore] private bool _inMeeting;
    [ObservableProperty][property: System.Text.Json.Serialization.JsonIgnore] private bool _sharing;
    [ObservableProperty][property: System.Text.Json.Serialization.JsonIgnore] private bool _focusActive;

    /// <summary>"Nicht stören" by hand, or a call, screen sharing or a focus session right now.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Quiet => DoNotDisturb || InMeeting || Sharing || FocusActive;
    partial void OnDoNotDisturbChanged(bool value) => OnPropertyChanged(nameof(Quiet));
    partial void OnInMeetingChanged(bool value) => OnPropertyChanged(nameof(Quiet));
    partial void OnSharingChanged(bool value) => OnPropertyChanged(nameof(Quiet));
    partial void OnFocusActiveChanged(bool value) => OnPropertyChanged(nameof(Quiet));
    [ObservableProperty] private bool _hidden;
    public ObservableCollection<string> ExcludedApps { get; set; } = new();

    // Placement. Monitor = device name ("" = primary); used in Single mode only.
    [ObservableProperty] private DisplayMode _displays = DisplayMode.Single;
    [ObservableProperty] private string _monitor = "";
    /// <summary>Remember the placement per arrangement of displays (laptop alone, docked at the office, ...).</summary>
    [ObservableProperty] private bool _dockProfiles = true;
    public Dictionary<string, DockProfile> DockProfileMap { get; set; } = new();
    [ObservableProperty] private IslandEdge _edge = IslandEdge.Top;
    [ObservableProperty] private IslandAlign _align = IslandAlign.Center;
    [ObservableProperty] private double _along = 0.5;
    [ObservableProperty] private double _inset = 8;
    [ObservableProperty] private bool _magneticSnap = true;

    // Media
    [ObservableProperty] private bool _mediaEnabled = true;
    [ObservableProperty] private bool _mediaPeekOnTrackChange; // off: the island stays small on song changes (like the iPhone)
    public int SettingsVersion { get; set; }
    [ObservableProperty] private string _spotifyClientId = "";

    // Clipboard / screenshots
    [ObservableProperty] private bool _clipboardEnabled = true;
    [ObservableProperty] private int _clipboardMax = 50;
    [ObservableProperty] private bool _clipboardPersist = true;
    [ObservableProperty] private bool _peekOnScreenshot = true;
    [ObservableProperty] private bool _clipboardText = true;

    // Shelf
    [ObservableProperty] private bool _shelfEnabled = true;
    [ObservableProperty] private bool _shelfCopyFiles;
    [ObservableProperty] private bool _shelfRemoveAfterDrag;

    // Taildrop: files from other devices via Tailscale (see TaildropService). Folder "" = leave them in Downloads.
    [ObservableProperty] private bool _taildropEnabled = true;

    // Microsoft 365 (MicrosoftAccount): own app registration; tenant "common" = work and personal accounts.
    [ObservableProperty] private string _microsoftClientId = "";
    [ObservableProperty] private string _microsoftTenant = "common";
    [ObservableProperty] private string _microsoftUser = "";
    [ObservableProperty] private bool _microsoftWorkAccount;
    /// <summary>The Teams status permission was granted (asked for separately).</summary>
    [ObservableProperty] private bool _microsoftPresence;
    [ObservableProperty] private string _taildropFolder = "";

    // Notifications
    [ObservableProperty] private bool _notificationsEnabled = true;
    [ObservableProperty] private int _notificationSeconds = 5;
    [ObservableProperty] private bool _notificationShowBody = true;
    public ObservableCollection<string> MutedApps { get; set; } = new();

    // Battery / Bluetooth
    [ObservableProperty] private bool _batteryEnabled = true;
    [ObservableProperty] private int _batteryLowThreshold = 20;
    [ObservableProperty] private bool _bluetoothEnabled = true;
    /// <summary>Headphones that connect become the sound output right away.</summary>
    [ObservableProperty] private bool _audioSwitchToHeadphones = true;
    /// <summary>A Bluetooth headset as microphone forces its call mode (mono, telephone quality): use the PC's microphone instead.</summary>
    [ObservableProperty] private bool _audioAvoidBluetoothMic = true;
    /// <summary>Music pauses when the headphones drop out, instead of carrying on through the speakers.</summary>
    [ObservableProperty] private bool _audioPauseOnDisconnect = true;

    // Timer / Calendar
    [ObservableProperty] private bool _timerEnabled = true;
    [ObservableProperty] private int _pomodoroWorkMinutes = 25;
    [ObservableProperty] private int _pomodoroBreakMinutes = 5;
    [ObservableProperty] private bool _calendarEnabled = true;
    [ObservableProperty] private string _calendarIcsUrl = "";
    [ObservableProperty] private int _calendarPeekMinutes = 10;

    [JsonIgnore] public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicBay");

    [JsonIgnore] public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>Debounced save so slider drags don't hammer the disk.</summary>
    public void SaveSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Start();
    }

    private void OnSaveTick(object? s, EventArgs e)
    {
        _saveTimer!.Stop();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are best effort */ }
    }
}
