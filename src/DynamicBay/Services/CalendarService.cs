using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using Ical.Net;

namespace DynamicBay.Services;

public sealed class CalendarEvent
{
    public string Title { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool AllDay { get; init; }
    public string? Location { get; init; }
    public string TimeText => AllDay ? Loc.T("Cal.AllDay") : $"{Start:HH:mm} – {End:HH:mm}";
}

public sealed class DayCell
{
    public string Weekday { get; init; } = "";
    public int Day { get; init; }
    public bool IsToday { get; init; }
}

/// <summary>Reads one ICS feed (Outlook / Google "secret address") and exposes today's upcoming events.</summary>
public sealed partial class CalendarService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer _minute = new() { Interval = TimeSpan.FromSeconds(30) };
    private List<CalendarEvent> _all = new();
    private readonly HashSet<string> _announced = new();

    public ObservableCollection<CalendarEvent> Upcoming { get; } = new();
    public ObservableCollection<DayCell> Week { get; } = new();
    [ObservableProperty] private CalendarEvent? _next;
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isSoon;
    [ObservableProperty] private string _soonText = "";
    [ObservableProperty] private string _monthText = "";
    [ObservableProperty] private string _todayText = "";

    public event Action<CalendarEvent>? EventStartingSoon;

    public CalendarService(AppSettings settings) => _settings = settings;

    public void Start()
    {
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _minute.Tick += (_, _) => Recompute();
        _refresh.Start();
        _minute.Start();
        _settings.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.CalendarIcsUrl)) await RefreshAsync();
        };
        BuildWeek();
        _ = RefreshAsync();
    }

    private void BuildWeek()
    {
        var culture = Loc.German ? new System.Globalization.CultureInfo("de-DE") : new System.Globalization.CultureInfo("en-US");
        var today = DateTime.Today;
        MonthText = today.ToString("MMM", culture).TrimEnd('.');
        TodayText = today.ToString(Loc.German ? "dddd, d. MMMM" : "dddd, MMMM d", culture);
        Week.Clear();
        for (int i = -2; i <= 4; i++)
        {
            var d = today.AddDays(i);
            Week.Add(new DayCell { Weekday = d.ToString("ddd", culture)[..2].ToUpperInvariant(), Day = d.Day, IsToday = i == 0 });
        }
    }

    public async Task RefreshAsync()
    {
        IsConfigured = !string.IsNullOrWhiteSpace(_settings.CalendarIcsUrl);
        if (!IsConfigured || !_settings.CalendarEnabled) { _all.Clear(); Recompute(); return; }
        try
        {
            var url = _settings.CalendarIcsUrl.Trim().Replace("webcal://", "https://");
            var ics = await _http.GetStringAsync(url);
            var cal = Calendar.Load(ics);
            var from = DateTime.Today;
            var to = from.AddDays(2);
            _all = cal.GetOccurrences(from, to)
                .Select(o =>
                {
                    var ev = (Ical.Net.CalendarComponents.CalendarEvent)o.Source;
                    return new CalendarEvent
                    {
                        Title = ev.Summary ?? "",
                        Start = o.Period.StartTime.AsSystemLocal,
                        End = o.Period.EndTime?.AsSystemLocal ?? o.Period.StartTime.AsSystemLocal.AddHours(1),
                        AllDay = ev.IsAllDay,
                        Location = ev.Location,
                    };
                })
                .OrderBy(e => e.Start)
                .ToList();
        }
        catch { /* keep the last good data */ }
        Recompute();
    }

    private void Recompute()
    {
        if (DateTime.Today.Day != Week.FirstOrDefault(w => w.IsToday)?.Day) BuildWeek();
        var now = DateTime.Now;
        var today = _all.Where(e => e.End > now && e.Start.Date <= DateTime.Today).Take(6).ToList();
        Upcoming.Clear();
        foreach (var e in today) Upcoming.Add(e);
        Next = today.FirstOrDefault(e => !e.AllDay);

        if (Next is not null)
        {
            var mins = (Next.Start - now).TotalMinutes;
            IsSoon = mins <= _settings.CalendarPeekMinutes && mins > -5;
            SoonText = mins <= 0 ? Loc.T("Cal.Now") : Loc.F("Cal.InMinutes", (int)Math.Ceiling(mins));
            string key = Next.Title + Next.Start.ToString("O");
            if (IsSoon && mins > 0 && _announced.Add(key)) EventStartingSoon?.Invoke(Next);
        }
        else IsSoon = false;
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        var t = DateTime.Today;
        _all = new()
        {
            new CalendarEvent { Title = "Design Review", Start = DateTime.Now.AddMinutes(8), End = DateTime.Now.AddMinutes(38) },
            new CalendarEvent { Title = "Lunch mit Lena", Start = t.AddHours(13), End = t.AddHours(14) },
        };
        IsConfigured = true;
        Recompute();
    }
}
