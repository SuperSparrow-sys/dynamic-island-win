using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using DynamicBay.Services.Calendars;
using Ical.Net;

namespace DynamicBay.Services;

public sealed class CalendarEvent
{
    public string Title { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool AllDay { get; init; }
    public string? Location { get; init; }
    public string? Color { get; init; }
    public string AccountId { get; set; } = "";
    public string TimeText => AllDay ? Loc.T("Cal.AllDay") : $"{Start:HH:mm} – {End:HH:mm}";
    public Brush Brush => TryBrush(Color) ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A));

    private static Brush? TryBrush(string? hex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex.Length == 9 && hex[0] == '#' ? "#" + hex[7..9] + hex[1..7] : hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch { return null; }
    }
}

public sealed class DayCell
{
    public string Weekday { get; init; } = "";
    public int Day { get; init; }
    public bool IsToday { get; init; }
}

/// <summary>
/// Merges events from all connected calendars: ICS links (Outlook, Google secret address, iCloud public),
/// iCloud via CalDAV (Apple ID + app-specific password) and Google via OAuth.
/// </summary>
public sealed partial class CalendarService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _minute = new() { Interval = TimeSpan.FromSeconds(30) };
    private List<CalendarEvent> _all = new();
    private readonly HashSet<string> _announced = new();
    private readonly Dictionary<string, string> _status = new();

    public ObservableCollection<CalendarEvent> Upcoming { get; } = new();
    public ObservableCollection<DayCell> Week { get; } = new();
    [ObservableProperty] private CalendarEvent? _next;
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isSoon;
    [ObservableProperty] private string _soonText = "";
    [ObservableProperty] private string _monthText = "";
    [ObservableProperty] private string _todayText = "";

    public event Action<CalendarEvent>? EventStartingSoon;
    public event Action? StatusChanged;

    public CalendarService(AppSettings settings) => _settings = settings;

    public string StatusOf(CalendarAccount a) => _status.TryGetValue(a.Id, out var s) ? s : "";

    public void Start()
    {
        _settings.Migrate();
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _minute.Tick += (_, _) => Recompute();
        _refresh.Start();
        _minute.Start();
        _settings.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.CalendarAccounts) or nameof(AppSettings.CalendarEnabled)) await RefreshAsync();
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
        var accounts = _settings.CalendarAccounts.Where(a => a.Enabled).ToList();
        IsConfigured = accounts.Count > 0;
        if (!IsConfigured || !_settings.CalendarEnabled) { _all.Clear(); Recompute(); return; }

        var from = DateTime.Today;
        var to = from.AddDays(2);
        var merged = new List<CalendarEvent>();
        foreach (var a in accounts)
        {
            try
            {
                var events = await FetchAsync(a, from, to);
                foreach (var ev in events) ev.AccountId = a.Id;
                merged.AddRange(events);
                _status[a.Id] = Loc.German ? $"OK · {events.Count} Termine in 48 h" : $"OK · {events.Count} events in 48 h";
            }
            catch (Exception ex)
            {
                _status[a.Id] = (Loc.German ? "Fehler: " : "Error: ") + ex.Message;
                Log.Error("Calendar " + a.Kind, ex);
                // keep the previous events of this account if the network hiccups
                merged.AddRange(_all.Where(e => e.AccountId == a.Id));
            }
        }
        _all = merged.OrderBy(e => e.Start).ToList();
        StatusChanged?.Invoke();
        Recompute();
    }

    public async Task<List<CalendarEvent>> FetchAsync(CalendarAccount a, DateTime from, DateTime to)
    {
        switch (a.Kind)
        {
            case CalendarKind.Ics:
            {
                var ics = await _http.GetStringAsync(a.Url.Trim().Replace("webcal://", "https://"));
                return Parse(ics, from, to, null);
            }
            case CalendarKind.ICloud:
            {
                var client = new CalDavClient(string.IsNullOrWhiteSpace(a.Url) ? "https://caldav.icloud.com/" : a.Url, a.User,
                    SecretStore.Get($"cal:{a.Id}:password") ?? "");
                var list = new List<CalendarEvent>();
                foreach (var cal in await client.DiscoverAsync())
                    foreach (var blob in await client.FetchAsync(cal.Url, from, to))
                        list.AddRange(Parse(blob, from, to, cal.Color));
                return list;
            }
            case CalendarKind.Google:
            {
                var client = new GoogleCalendarClient(a.User, SecretStore.Get($"cal:{a.Id}:secret") ?? "", SecretStore.Get($"cal:{a.Id}:refresh"));
                var list = await client.FetchAsync(from, to);
                if (client.RefreshToken is not null) SecretStore.Set($"cal:{a.Id}:refresh", client.RefreshToken);
                return list;
            }
        }
        return new();
    }

    private static List<CalendarEvent> Parse(string ics, DateTime from, DateTime to, string? color)
    {
        var cal = Calendar.Load(ics);
        return cal.GetOccurrences(from, to)
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
                    Color = color,
                };
            })
            .ToList();
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
            new CalendarEvent { Title = "Lunch mit Lena", Start = t.AddHours(13), End = t.AddHours(14), Color = "#0A84FF" },
        };
        IsConfigured = true;
        Recompute();
    }
}
