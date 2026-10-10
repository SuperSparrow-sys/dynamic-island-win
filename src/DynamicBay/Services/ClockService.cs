using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using Microsoft.Win32;

namespace DynamicBay.Services;

/// <summary>Current time/date for the clock widget and the clock live activity.</summary>
public sealed partial class ClockService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private string _zone = Zone();

    /// <summary>The time zone of Windows changed (travel, by hand): times shown elsewhere must be worked out again.</summary>
    public static event Action? TimeZoneChanged;

    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _seconds = "";
    [ObservableProperty] private string _amPm = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _weekday = "";
    [ObservableProperty] private string _shortTime = "";

    public ClockService(AppSettings settings)
    {
        _settings = settings;
        _tick.Tick += (_, _) => Update();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.Clock24h) or nameof(AppSettings.ClockSeconds)) Update();
        };
        // .NET keeps the time zone it read first: without this the clock stayed in the old zone until a restart.
        SystemEvents.TimeChanged += (_, _) => _ui.BeginInvoke(OnTimeChanged);
        Update();
        _tick.Start();
    }

    private static string Zone() => TimeZoneInfo.Local.Id + "|" + TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);

    private void OnTimeChanged()
    {
        TimeZoneInfo.ClearCachedData();
        Update();
        var zone = Zone();
        if (zone == _zone) return; // only the clock was set (time sync)
        _zone = zone;
        Log.Info($"Time zone now {TimeZoneInfo.Local.Id}");
        TimeZoneChanged?.Invoke();
    }

    /// <summary>Next tick right after the next second (with seconds) or minute (without) - no busy 250 ms polling.</summary>
    private void ScheduleNext(DateTime now)
    {
        var next = _settings.ClockSeconds ? now.AddSeconds(1) : now.AddMinutes(1);
        next = _settings.ClockSeconds ? next.AddMilliseconds(-next.Millisecond) : next.AddSeconds(-next.Second).AddMilliseconds(-next.Millisecond);
        _tick.Interval = (next - now) + TimeSpan.FromMilliseconds(15);
    }

    private void Update()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.GetCultureInfo(Loc.German ? "de-DE" : "en-US"); // cached, read-only
        ScheduleNext(now);
        Time = _settings.Clock24h ? now.ToString("HH:mm") : now.ToString("h:mm");
        AmPm = _settings.Clock24h ? "" : now.ToString("tt", CultureInfo.InvariantCulture);
        Seconds = _settings.ClockSeconds ? now.ToString(":ss") : "";
        ShortTime = Time + (AmPm.Length > 0 ? " " + AmPm : "");
        Weekday = now.ToString("dddd", culture);
        Date = now.ToString(Loc.German ? "d. MMMM" : "MMMM d", culture);
    }
}
