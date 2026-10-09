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
    public const string Media = "media", Clock = "clock", Calendar = "calendar", Timer = "timer", System = "system", Shortcuts = "shortcuts", Messenger = "messenger", Claude = "claude";
    public static readonly string[] AllHome = { Media, Clock, Calendar, Timer, System, Shortcuts, Messenger, Claude };
    public const string Battery = "battery";
    public static readonly string[] AllCompact = { Media, Timer, Calendar, Battery, Clock, Claude };
}
public enum AccentSource { Cover, Windows, Custom }
public enum DisplayMode { Single, Mirror }
public enum AppIconStyle { Mono, Dark, Color, Original }
public enum CalendarKind { Ics, ICloud, Google }

/// <summary>A connected calendar. Secrets (passwords, tokens) live in <see cref="SecretStore"/> under "cal:{Id}:*".</summary>
public sealed class CalendarAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public CalendarKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";       // ICS link or CalDAV server
    public string User { get; set; } = "";      // Apple ID / Google client id
    public bool Enabled { get; set; } = true;
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
    [ObservableProperty] private AccentSource _accent = AccentSource.Cover;
    [ObservableProperty] private string _customAccent = "#0A84FF";
    [ObservableProperty] private double _animationSpeed = 1.0;
    [ObservableProperty] private bool _shadow = true;
    [ObservableProperty] private IdleStyle _idle = IdleStyle.Bar;

    // Customizable layout
    private ObservableCollection<string> _homeWidgets = new() { Widgets.Media, Widgets.Calendar, Widgets.Timer };
    private ObservableCollection<string> _compactItems = new() { Widgets.Media, Widgets.Timer, Widgets.Calendar, Widgets.Battery, Widgets.Claude };
    private ObservableCollection<string> _shortcuts = new();

    public ObservableCollection<string> HomeWidgets { get => _homeWidgets; set => Hook(ref _homeWidgets, value, nameof(HomeWidgets)); }
    public ObservableCollection<string> CompactItems { get => _compactItems; set => Hook(ref _compactItems, value, nameof(CompactItems)); }
    public ObservableCollection<string> Shortcuts { get => _shortcuts; set => Hook(ref _shortcuts, value, nameof(Shortcuts)); }

    // Windows banner suppression while DynamicBay runs (see BannerSuppressor)
    [ObservableProperty] private bool _suppressBanners = true;
    private ObservableCollection<string> _suppressBannerApps = new() { "Microsoft.ScreenSketch_8wekyb3d8bbwe!App" };
    public ObservableCollection<string> SuppressBannerApps { get => _suppressBannerApps; set => Hook(ref _suppressBannerApps, value, nameof(SuppressBannerApps)); }

    public AppSettings()
    {
        _homeWidgets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HomeWidgets));
        _compactItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CompactItems));
        _shortcuts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Shortcuts));
        _calendarAccounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CalendarAccounts));
        _suppressBannerApps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SuppressBannerApps));
    }

    /// <summary>Collections raise PropertyChanged for their own name on every change, so listeners and autosave see edits.</summary>
    private void Hook(ref ObservableCollection<string> field, ObservableCollection<string> value, string name)
    {
        field = value ?? new();
        field.CollectionChanged += (_, _) => OnPropertyChanged(name);
        OnPropertyChanged(name);
    }

    public bool HasCompact(string id) => CompactItems.Contains(id);

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
    [ObservableProperty] private bool _hidden;
    public ObservableCollection<string> ExcludedApps { get; set; } = new();

    // Placement. Monitor = device name ("" = primary); used in Single mode only.
    [ObservableProperty] private DisplayMode _displays = DisplayMode.Single;
    [ObservableProperty] private string _monitor = "";
    [ObservableProperty] private IslandEdge _edge = IslandEdge.Top;
    [ObservableProperty] private IslandAlign _align = IslandAlign.Center;
    [ObservableProperty] private double _along = 0.5;
    [ObservableProperty] private double _inset = 8;
    [ObservableProperty] private bool _magneticSnap = true;

    // Media
    [ObservableProperty] private bool _mediaEnabled = true;
    [ObservableProperty] private bool _mediaPeekOnTrackChange = true;
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

    // Notifications
    [ObservableProperty] private bool _notificationsEnabled = true;
    [ObservableProperty] private int _notificationSeconds = 5;
    [ObservableProperty] private bool _notificationShowBody = true;
    public ObservableCollection<string> MutedApps { get; set; } = new();

    // Battery / Bluetooth
    [ObservableProperty] private bool _batteryEnabled = true;
    [ObservableProperty] private int _batteryLowThreshold = 20;
    [ObservableProperty] private bool _bluetoothEnabled = true;

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
