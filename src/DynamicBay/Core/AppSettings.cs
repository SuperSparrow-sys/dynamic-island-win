using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DynamicBay.Core;

public enum IslandEdge { Top, Bottom, Left, Right }
public enum IslandAlign { Start, Center, End }
/// <summary>Floating: above all windows. Desktop: behind them. AboveAll: also above fullscreen films and games (borderless).</summary>
public enum LayerMode { Floating, Desktop, AboveAll }
public enum IdleStyle { Bar, Clock, Hidden }
/// <summary>Island: a floating pill. Notch: grows out of the screen edge like the MacBook camera notch (top or bottom edge).</summary>
public enum IslandShape { Island, Notch }

/// <summary>Widget and live-activity identifiers used in the customizable layout.</summary>
public static class Widgets
{
    public const string Media = "media", Clock = "clock", Calendar = "calendar", Timer = "timer", System = "system", Shortcuts = "shortcuts", Messenger = "messenger", Claude = "claude", Muted = "muted", Mic = "mic", Devices = "devices", Todo = "todo", Teams = "teams", TimeTrack = "timetrack", Contacts = "contacts", Audio = "audio", Notes = "notes", Downloads = "downloads", CursorHop = "cursorhop", Paired = "paired";
    public static readonly string[] AllHome = { Media, Clock, Calendar, Timer, System, Shortcuts, Messenger, Claude, Devices, Todo, Teams, TimeTrack, Contacts, CursorHop, Paired };
    public const string Battery = "battery";
    /// <summary>Settings page and group heading (start of its text) of each widget - for the gear in the island and the board in the settings.</summary>
    public static (SettingsPage page, string? group) SettingsOf(string id) => id switch
    {
        Media => (SettingsPage.Media, null),
        Calendar => (SettingsPage.Calendar, null),
        Timer => (SettingsPage.Calendar, "Timer"),
        Clock => (SettingsPage.Appearance, Loc.German ? "Uhr" : "Clock"),
        Shortcuts => (SettingsPage.Apps, Loc.German ? "Schnellstart" : "Launcher"),
        Claude => (SettingsPage.Apps, "Claude"),
        CursorHop => (SettingsPage.Devices, "CursorHop"),
        Messenger => (SettingsPage.Notifications, null),
        Devices => (SettingsPage.Audio, "Bluetooth"),
        Audio => (SettingsPage.Audio, Loc.German ? "Kopfhörer" : "Headphones"),
        Todo => (SettingsPage.Calendar, Loc.German ? "Aufgaben" : "Tasks"),
        Teams or Contacts => (SettingsPage.Accounts, "Microsoft 365"),
        TimeTrack => (SettingsPage.Calendar, Loc.German ? "Zeiterfassung" : "Time tracking"),
        Paired => (SettingsPage.Devices, Loc.German ? "Gekoppelte" : "Paired"),
        _ when id.StartsWith(ScriptPrefix, StringComparison.Ordinal) => (SettingsPage.Apps, Loc.German ? "Eigene Widgets" : "Own widgets"),
        _ => (SettingsPage.Content, null),
    };

    public static readonly string[] AllCompact = { Media, Timer, Calendar, Battery, Clock, Claude, Muted, Mic, TimeTrack, Downloads };
    /// <summary>Home widget ids of user scripts: "script:{id}".</summary>
    public const string ScriptPrefix = "script:";
}
/// <summary>Pages of the settings window, in the order of the sidebar.</summary>
public enum SettingsPage { General, Appearance, Position, Behavior, Quiet, Content, Media, Audio, Notifications, Shelf, Calendar, Apps, Accounts, Devices, Advanced, About }
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

/// <summary>
/// A device paired through the link (see Services/Link). Only the fingerprint of its certificate is stored; the private
/// keys never leave their device.
/// </summary>
public sealed class PairedDevice
{
    /// <summary>First 16 hex characters of the SHA-256 of the device's public key.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SHA-256 of the device certificate (hex), pinned at pairing.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Where it was reached last ("host" or "host:port"), newest first.</summary>
    public List<string> LastAddresses { get; set; } = new();
    public DateTime PairedAt { get; set; }
    /// <summary>The device removed this one (or got a new identity): no more connection attempts until paired again.</summary>
    public bool Rejected { get; set; }
}

/// <summary>All user settings. Flat on purpose: binds directly into the settings window and serializes as one JSON file.</summary>
public sealed partial class AppSettings : ObservableObject
{
    // General
    [ObservableProperty] private bool _startWithWindows = true;
    [ObservableProperty] private string _language = "auto";
    /// <summary>Look of the settings windows: "auto" (like Windows), "light" or "dark". The island is always black.</summary>
    [ObservableProperty] private string _windowTheme = "auto";
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
    [ObservableProperty] private IslandShape _shapeStyle = IslandShape.Island;
    /// <summary>Frames per second of the island animations; 0 = as many as the display offers.</summary>
    [ObservableProperty] private int _frameRate;

    // Customizable layout
    private ObservableCollection<string> _homeWidgets = new() { Widgets.Media, Widgets.Calendar, Widgets.Timer };
    private ObservableCollection<string> _compactItems = new() { Widgets.Mic, Widgets.Media, Widgets.Timer, Widgets.Calendar, Widgets.Battery, Widgets.Claude, Widgets.Muted, Widgets.TimeTrack, Widgets.Downloads };
    private ObservableCollection<string> _shortcuts = new();

    public ObservableCollection<string> HomeWidgets { get => _homeWidgets; set => Hook(ref _homeWidgets, value, nameof(HomeWidgets)); }
    /// <summary>Widgets on the second page "System" (sound, devices …), so the Nook does not get crowded. Empty = no System tab.</summary>
    public ObservableCollection<string> SystemWidgets { get => _systemWidgets; set => Hook(ref _systemWidgets, value, nameof(SystemWidgets)); }
    private ObservableCollection<string> _systemWidgets = new();
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
        _systemWidgets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SystemWidgets));
        _compactItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CompactItems));
        _shortcuts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Shortcuts));
        _timeProjects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TimeProjects));
        _calendarAccounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CalendarAccounts));
        _scriptWidgets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ScriptWidgets));
        _bannerExceptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BannerExceptions));
        _pairedDevices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PairedDevices));
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
    public void Migrate() => Migrate(Services.CursorHopService.IsInstalledOnDisk);

    internal void Migrate(bool cursorHopInstalled)
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
        // v8: downloads in the island (size, speed, the finished file) - on once, can be switched off.
        if (SettingsVersion < 8)
        {
            if (!CompactItems.Contains(Widgets.Downloads)) CompactItems.Add(Widgets.Downloads);
            SettingsVersion = 8;
        }
        // v9: second page "System": sound and devices move there, the Nook keeps the rest.
        if (SettingsVersion < 9)
        {
            foreach (var id in new[] { Widgets.Audio, Widgets.Devices, Widgets.System })
                if (HomeWidgets.Remove(id) && !SystemWidgets.Contains(id)) SystemWidgets.Add(id);
            SettingsVersion = 9;
        }
        // v10: 1.0.15 ran v9 before v8, so updates from v6/v7 never got the downloads - they get them once now.
        if (SettingsVersion < 10)
        {
            if (!CompactItems.Contains(Widgets.Downloads)) CompactItems.Add(Widgets.Downloads);
            SettingsVersion = 10;
        }
        // v11: CursorHop card on the System page, once, where CursorHop is installed.
        if (SettingsVersion < 11)
        {
            if (cursorHopInstalled && CursorHopEnabled && !SystemWidgets.Contains(Widgets.CursorHop) && !HomeWidgets.Contains(Widgets.CursorHop))
                SystemWidgets.Add(Widgets.CursorHop);
            SettingsVersion = 11;
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
    [System.Text.Json.Serialization.JsonIgnore] public bool Quiet => DoNotDisturb || InMeeting || Sharing || FocusActive || PeerQuiet;
    /// <summary>The paired device is in a call or a focus session ("Ruhe und Fokus", see LinkQuietFeature).</summary>
    [ObservableProperty][property: System.Text.Json.Serialization.JsonIgnore] private bool _peerQuiet;
    partial void OnPeerQuietChanged(bool value) => OnPropertyChanged(nameof(Quiet));
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
    /// <summary>Player sends no cover (Apple Music, some browsers): look it up in the iTunes catalogue by title and artist.</summary>
    [ObservableProperty] private bool _mediaCoverLookup = true;
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

    // CursorHop (software KVM, see CursorHopService): read its log, start it when it is not running, peeks.
    // Without CursorHop installed the service does nothing, so "on" is the right default everywhere.
    [ObservableProperty] private bool _cursorHopEnabled = true;
    [ObservableProperty] private bool _cursorHopAutoStart = true;
    [ObservableProperty] private bool _cursorHopPeeks = true;
    /// <summary>Hide CursorHop's own "Receiving" / "Transfer complete" pop-ups; the island shows them once.</summary>
    [ObservableProperty] private bool _cursorHopHideOverlays = true;

    // Microsoft 365 (MicrosoftAccount): own app registration; tenant "common" = work and personal accounts.
    [ObservableProperty] private string _microsoftClientId = "";
    [ObservableProperty] private string _microsoftTenant = "common";
    [ObservableProperty] private string _microsoftUser = "";
    [ObservableProperty] private bool _microsoftWorkAccount;
    /// <summary>The Teams status permission was granted (asked for separately).</summary>
    [ObservableProperty] private bool _microsoftPresence;
    [ObservableProperty] private string _taildropFolder = "";

    // Link: paired DynamicBay devices (see Services/Link). Off until the first pairing; LinkDeviceName "" = computer name.
    [ObservableProperty] private bool _linkEnabled;
    [ObservableProperty] private string _linkDeviceName = "";
    // What is shared with paired devices (checked when sending and when receiving). All on: nothing goes anywhere
    // before the link is switched on and a device is paired.
    [ObservableProperty] private bool _linkShareFiles = true;
    [ObservableProperty] private bool _linkShareClipboard = true;
    [ObservableProperty] private bool _linkShareNotifications = true;
    [ObservableProperty] private bool _linkShareMedia = true;
    /// <summary>"Ton": this PC's sound may play on a paired device, and theirs here (both sides must allow it).</summary>
    [ObservableProperty] private bool _linkShareAudio = true;
    [ObservableProperty] private bool _linkShareStatus = true;
    // Working together (Services/Link/Link*Feature.cs): timer, quiet, questions and battery on; locking together off.
    [ObservableProperty] private bool _linkShareTimer = true;
    [ObservableProperty] private bool _linkShareQuiet = true;
    [ObservableProperty] private bool _linkShareQuestions = true;
    [ObservableProperty] private bool _linkShareBattery = true;
    [ObservableProperty] private bool _linkLockTogether;
    /// <summary>When "Nicht stören" was last switched by hand here or on the paired device (the newer one wins on both).</summary>
    [ObservableProperty] private DateTime _linkQuietChanged;
    /// <summary>
    /// Main device while a paired one is connected: "" = none (both show everything), "this", "other" (the paired
    /// device) or "auto" (where the mouse is or that was used last). Relative on purpose: no ids, so it can be exported.
    /// Synced between the devices (the newer <see cref="LinkMainChanged"/> wins).
    /// </summary>
    [ObservableProperty] private string _linkMain = "";
    [ObservableProperty] private DateTime _linkMainChanged;
    /// <summary>Hide the island on the device that is not the main one, while the other is connected.</summary>
    [ObservableProperty] private bool _linkHideWhenNotMain;
    private ObservableCollection<PairedDevice> _pairedDevices = new();
    public ObservableCollection<PairedDevice> PairedDevices
    {
        get => _pairedDevices;
        set
        {
            _pairedDevices = value ?? new();
            _pairedDevices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PairedDevices));
            OnPropertyChanged(nameof(PairedDevices));
        }
    }

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
    /// <summary>In a call: listen on the connected headphones, speak into the PC's own microphone.</summary>
    [ObservableProperty] private bool _audioCallRouting = true;
    /// <summary>Time tracking asks after 4 hours and in the evening whether the project still runs.</summary>
    [ObservableProperty] private bool _timeReminders = true;
    /// <summary>Teams status chosen in the island, and until when it holds (Teams keeps it a working day).</summary>
    [ObservableProperty] private string _teamsChosen = "";
    [ObservableProperty] private DateTime _teamsChosenUntil;
    /// <summary>"Do not disturb" in Teams during a focus session and while sharing the screen.</summary>
    [ObservableProperty] private bool _teamsAutoStatus = true;
    /// <summary>Microsoft To Do list shown in the widget ("" = all lists).</summary>
    [ObservableProperty] private string _todoListId = "";
    [ObservableProperty] private string _todoListName = "";

    // Timer / Calendar
    [ObservableProperty] private bool _timerEnabled = true;
    [ObservableProperty] private int _pomodoroWorkMinutes = 25;
    [ObservableProperty] private int _pomodoroBreakMinutes = 5;
    [ObservableProperty] private bool _calendarEnabled = true;
    [ObservableProperty] private string _calendarIcsUrl = "";
    [ObservableProperty] private int _calendarPeekMinutes = 10;

    [JsonIgnore] public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DevMode.On ? "DynamicBay-dev-" + DevMode.Name : "DynamicBay");

    [JsonIgnore] public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Snapshot runs and the like: Save and SaveSoon do nothing, the user's file stays as it is.</summary>
    public static bool ReadOnly { get; set; }

    /// <summary>The settings as written to settings.json (export, see <see cref="SettingsTransfer"/>).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads settings in the settings.json format; throws on broken JSON.</summary>
    public static AppSettings FromJson(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, Json) ?? throw new JsonException("empty settings");

    /// <summary>
    /// Reads settings.json. A broken file is copied aside (settings.corrupt-{time}.json) and the backup of the last
    /// good save is used instead; only without one the defaults apply.
    /// </summary>
    public static AppSettings Load()
    {
        if (File.Exists(FilePath))
        {
            if (TryRead(FilePath) is { } s) return s;
            AtomicFile.SetAside(FilePath);
        }
        if (File.Exists(FilePath + ".bak") && TryRead(FilePath + ".bak") is { } backup)
        {
            Log.Info("Settings: using the backup of the last save");
            return backup;
        }
        return new AppSettings();
    }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json)
                   ?? throw new JsonException("empty settings file");
        }
        catch (Exception ex) { Log.Error("Settings load " + Path.GetFileName(path), ex); return null; }
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>Debounced save so slider drags don't hammer the disk.</summary>
    public void SaveSoon()
    {
        if (ReadOnly) return;
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Start();
    }

    private void OnSaveTick(object? s, EventArgs e)
    {
        _saveTimer!.Stop();
        if (ReadOnly) return;
        // The text here (it reads the live collections), the disk write in the background: it held the UI for up to 900 ms.
        try { Writer.Queue(JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { Log.Error("Settings save", ex); }
    }

    // Temp file first, then swapped in: a crash while saving can't leave a half written file behind.
    private static readonly BackgroundWriter Writer = new(text => AtomicFile.WriteAllText(FilePath, text), "Settings");

    /// <summary>Saves now, on this thread (start, exit, import); newer than any pending background save.</summary>
    public void Save()
    {
        if (ReadOnly) return;
        _saveTimer?.Stop();
        try { Writer.WriteNow(JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { Log.Error("Settings save", ex); /* settings are best effort */ }
    }
}
