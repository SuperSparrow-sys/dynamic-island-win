using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DynamicBay.Core;

public enum IslandEdge { Top, Bottom, Left, Right }
public enum IslandAlign { Start, Center, End }
public enum LayerMode { Floating, Desktop }
public enum IdleStyle { Bar, Hidden }
public enum AccentSource { Cover, Windows, Custom }

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

    // Placement (primary monitor only)
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
