using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>Current time/date for the clock widget and the clock live activity.</summary>
public sealed partial class ClockService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

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
        Update();
        _tick.Start();
    }

    private void Update()
    {
        var now = DateTime.Now;
        var culture = Loc.German ? new CultureInfo("de-DE") : new CultureInfo("en-US");
        Time = _settings.Clock24h ? now.ToString("HH:mm") : now.ToString("h:mm");
        AmPm = _settings.Clock24h ? "" : now.ToString("tt", CultureInfo.InvariantCulture);
        Seconds = _settings.ClockSeconds ? now.ToString(":ss") : "";
        ShortTime = Time + (AmPm.Length > 0 ? " " + AmPm : "");
        Weekday = now.ToString("dddd", culture);
        Date = now.ToString(Loc.German ? "d. MMMM" : "MMMM d", culture);
    }
}
