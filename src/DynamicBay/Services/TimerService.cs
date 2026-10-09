using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public enum TimerMode { Timer, Focus, Break }

/// <summary>Countdown timer and Pomodoro (focus/break cycles), shown as a live activity.</summary>
public sealed partial class TimerService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _endsAt;
    private TimeSpan _remainingWhenPaused;

    [ObservableProperty] private bool _isActive;   // running or paused
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private TimerMode _mode = TimerMode.Timer;
    [ObservableProperty] private TimeSpan _total;
    [ObservableProperty] private TimeSpan _remaining;
    [ObservableProperty] private string _remainingText = "0:00";
    [ObservableProperty] private double _progress; // 0..1 elapsed
    [ObservableProperty] private string _modeText = Loc.T("Timer.Title");
    [ObservableProperty] private int _completedFocusSessions;

    public event Action<TimerMode>? Finished;

    public TimerService(AppSettings settings)
    {
        _settings = settings;
        _tick.Tick += (_, _) => Update();
    }

    [RelayCommand]
    public void StartMinutes(object? minutes)
    {
        double m = Convert.ToDouble(minutes ?? 5, System.Globalization.CultureInfo.InvariantCulture);
        Begin(TimerMode.Timer, TimeSpan.FromMinutes(m));
    }

    [RelayCommand]
    public void StartFocus() => Begin(TimerMode.Focus, TimeSpan.FromMinutes(_settings.PomodoroWorkMinutes));

    [RelayCommand]
    public void StartBreak() => Begin(TimerMode.Break, TimeSpan.FromMinutes(_settings.PomodoroBreakMinutes));

    private void Begin(TimerMode mode, TimeSpan duration)
    {
        Mode = mode;
        ModeText = mode switch
        {
            TimerMode.Focus => Loc.T("Timer.Pomodoro"),
            TimerMode.Break => Loc.T("Timer.Break"),
            _ => Loc.T("Timer.Title"),
        };
        Total = duration;
        _endsAt = DateTime.UtcNow + duration;
        IsActive = IsRunning = true;
        _tick.Start();
        Update();
    }

    [RelayCommand]
    public void TogglePause()
    {
        if (!IsActive) return;
        if (IsRunning)
        {
            _remainingWhenPaused = _endsAt - DateTime.UtcNow;
            IsRunning = false;
            _tick.Stop();
        }
        else
        {
            _endsAt = DateTime.UtcNow + _remainingWhenPaused;
            IsRunning = true;
            _tick.Start();
        }
        Update();
    }

    [RelayCommand]
    public void Add(object? minutes)
    {
        if (!IsActive) return;
        var add = TimeSpan.FromMinutes(Convert.ToDouble(minutes ?? 1, System.Globalization.CultureInfo.InvariantCulture));
        Total += add;
        if (IsRunning) _endsAt += add; else _remainingWhenPaused += add;
        Update();
    }

    [RelayCommand]
    public void Stop()
    {
        IsActive = IsRunning = false;
        _tick.Stop();
        Remaining = TimeSpan.Zero;
        RemainingText = "0:00";
        Progress = 0;
    }

    private void Update()
    {
        var rem = IsRunning ? _endsAt - DateTime.UtcNow : _remainingWhenPaused;
        if (rem <= TimeSpan.Zero && IsRunning)
        {
            var mode = Mode;
            Stop();
            if (mode == TimerMode.Focus) CompletedFocusSessions++;
            Finished?.Invoke(mode);
            return;
        }
        Remaining = rem;
        // Round up so the display never shows 0:00 while time is left.
        var shown = TimeSpan.FromSeconds(Math.Ceiling(rem.TotalSeconds));
        RemainingText = shown.TotalHours >= 1 ? shown.ToString(@"h\:mm\:ss") : $"{(int)shown.TotalMinutes}:{shown.Seconds:00}";
        Progress = Total.TotalSeconds > 0 ? 1 - rem.TotalSeconds / Total.TotalSeconds : 0;
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        Begin(TimerMode.Focus, TimeSpan.FromMinutes(25));
        _endsAt = DateTime.UtcNow + TimeSpan.FromSeconds(18 * 60 + 42);
        Update();
        TogglePause();
    }
}
