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
    public Action? OnClick { get; init; }
    public object? DragPayload { get; init; }
    public bool ShowWaveform { get; init; }
    public Brush WaveformBrush { get; init; } = Brushes.White;
    public bool HasImage => Image is not null;
    public bool HasIcon => Image is null && Icon is not null;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool HasTrailing => !string.IsNullOrEmpty(Trailing);
    public bool HasProgress => Progress is not null;
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

    [ObservableProperty] private PeekItem? _peek;
    [ObservableProperty] private int _tab;           // 0 home, 1 tray, 2 notifications
    [ObservableProperty] private bool _isVertical;
    [ObservableProperty] private bool _isPinned;

    [ObservableProperty] private bool _showMedia;
    [ObservableProperty] private bool _showTimer;
    [ObservableProperty] private bool _showCalendar;
    [ObservableProperty] private bool _showBatteryLow;
    [ObservableProperty] private bool _hasCompact;

    public event Action? OpenSettingsRequested;
    public event Action? HideRequested;
    public event Action? CompactChanged;

    public IslandViewModel(AppSettings settings, MediaService media, ClipboardService clipboard, ShelfService shelf,
        NotificationService notifications, TimerService timer, CalendarService calendar, BatteryService battery,
        SpotifyService spotify)
    {
        Settings = settings; Media = media; Clipboard = clipboard; Shelf = shelf; Notifications = notifications;
        Timer = timer; Calendar = calendar; Battery = battery; Spotify = spotify;

        PropertyChangedEventHandler recompute = (_, _) => RecomputeCompact();
        Media.PropertyChanged += recompute;
        Timer.PropertyChanged += recompute;
        Calendar.PropertyChanged += recompute;
        Battery.PropertyChanged += recompute;
        Settings.PropertyChanged += recompute;
        RecomputeCompact();
    }

    private void RecomputeCompact()
    {
        bool media = Settings.MediaEnabled && Media.HasSession && Media.IsPlaying;
        bool timer = Settings.TimerEnabled && Timer.IsActive;
        bool cal = Settings.CalendarEnabled && Calendar.IsSoon;
        bool low = Settings.BatteryEnabled && Battery.IsLow;
        bool any = media || timer || cal || low;
        bool changed = media != ShowMedia || timer != ShowTimer || cal != ShowCalendar || low != ShowBatteryLow || any != HasCompact;
        ShowMedia = media; ShowTimer = timer; ShowCalendar = cal; ShowBatteryLow = low; HasCompact = any;
        if (changed) CompactChanged?.Invoke();
    }

    [RelayCommand] private void OpenSettings() => OpenSettingsRequested?.Invoke();
    [RelayCommand] private void Hide() => HideRequested?.Invoke();
    [RelayCommand] private void TogglePin() => IsPinned = !IsPinned;
    [RelayCommand] private void SelectTab(object? index) => Tab = Convert.ToInt32(index);
}
