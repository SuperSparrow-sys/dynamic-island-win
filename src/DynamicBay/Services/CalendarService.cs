using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// <summary>Teams or Zoom meeting of this event (from location, description or conference data).</summary>
    public MeetingLink? Meeting { get; init; }
    public bool HasMeeting => Meeting is not null && End > DateTime.Now;
    public string JoinText => Meeting?.Service == "Zoom" ? "Zoom" : "Teams";
    public string AccountId { get; set; } = "";
    public string TimeText => AllDay ? Loc.T("Cal.AllDay") : $"{Start:HH:mm} – {End:HH:mm}";
    public Brush Brush => TryBrush(Color) ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A));
    /// <summary>Tinted background for all-day events.</summary>
    public Brush SoftBrush
    {
        get
        {
            var c = Brush is SolidColorBrush sb ? sb.Color : Colors.Red;
            var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }
    /// <summary>Does the event touch this day? (all-day events end at midnight of the next day)</summary>
    public bool IsOn(DateTime day) => Start < day.AddDays(1) && (End > day || (End == Start && Start.Date == day));

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

public sealed partial class DayCell : ObservableObject
{
    public string Weekday { get; init; } = "";
    public int Day { get; init; }
    public DateTime Date { get; init; }
    public bool IsToday { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowRing))] private bool _isSelected;
    /// <summary>Ring for a chosen day other than today (today is already red).</summary>
    public bool ShowRing => IsSelected && !IsToday;
    [ObservableProperty] private bool _hasEvents;
}

/// <summary>
/// Merges events from all connected calendars: ICS links (Outlook, Google secret address, iCloud public),
/// iCloud via CalDAV (Apple ID + app-specific password) and Google via OAuth.
/// </summary>
/// <summary>One calendar inside an account (for the on/off list in the settings).</summary>
public sealed record CalendarInfo(string Id, string Name, string? Color);

public sealed partial class CalendarService : ObservableObject
{
    private readonly Dictionary<string, List<CalendarInfo>> _calendarsOf = new();

    /// <summary>The calendars found in this account at the last refresh (empty for ICS links or before the first refresh).</summary>
    public IReadOnlyList<CalendarInfo> CalendarsOf(CalendarAccount a) => _calendarsOf.TryGetValue(a.Id, out var l) ? l : Array.Empty<CalendarInfo>();

    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _minute = new() { Interval = TimeSpan.FromSeconds(30) };
    private List<CalendarEvent> _all = new();
    private readonly HashSet<string> _announced = new();
    private readonly Dictionary<string, string> _status = new();
    /// <summary>CalDAV calendar lists per account, with the server/user they were found for (a new login rediscovers).</summary>
    private readonly Dictionary<string, (DateTime at, string login, List<CalDavClient.RemoteCalendar> calendars)> _discovered = new();

    public ObservableCollection<CalendarEvent> Upcoming { get; } = new();
    public ObservableCollection<DayCell> Week { get; } = new();
    /// <summary>The day tapped in the week strip (today by default): its all-day and timed events.</summary>
    public ObservableCollection<CalendarEvent> DayAllDay { get; } = new();
    public ObservableCollection<CalendarEvent> DayEvents { get; } = new();
    [ObservableProperty] private DateTime _selectedDate = DateTime.Today;
    [ObservableProperty] private bool _dayIsEmpty = true;
    [ObservableProperty] private string _dayEmptyText = "";
    [ObservableProperty] private CalendarEvent? _next;
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isSoon;
    [ObservableProperty] private string _soonText = "";
    [ObservableProperty] private string _monthText = "";
    [ObservableProperty] private string _todayText = "";

    public event Action<CalendarEvent>? EventStartingSoon;

    [RelayCommand]
    private void Join(CalendarEvent? ev)
    {
        if (ev?.Meeting is { } m) MeetingLinks.Open(m);
    }
    public event Action? StatusChanged;

    public CalendarService(AppSettings settings) => _settings = settings;

    public string StatusOf(CalendarAccount a) => _status.TryGetValue(a.Id, out var s) ? s : "";

    public void Start()
    {
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
            Week.Add(new DayCell { Weekday = d.ToString("ddd", culture)[..2].ToUpperInvariant(), Day = d.Day, Date = d, IsToday = i == 0, IsSelected = d == SelectedDate });
        }
    }

    [RelayCommand]
    private void SelectDay(DayCell? day)
    {
        if (day is null) return;
        SelectedDate = day.Date;
        foreach (var d in Week) d.IsSelected = d.Date == day.Date;
        FillDay();
    }

    private void FillDay()
    {
        var day = SelectedDate;
        var now = DateTime.Now;
        var events = _all.Where(e => e.IsOn(day)).ToList();
        var allDay = events.Where(e => e.AllDay).ToList();
        // Today: what is still ahead; other days: the whole day.
        var timed = events.Where(e => !e.AllDay && (day != DateTime.Today || e.End > now)).ToList();
        // Recompute runs every 30 s: only rebuild the lists (and the card) when they really changed.
        if (!allDay.SequenceEqual(DayAllDay)) { DayAllDay.Clear(); foreach (var e in allDay) DayAllDay.Add(e); }
        if (!timed.SequenceEqual(DayEvents)) { DayEvents.Clear(); foreach (var e in timed) DayEvents.Add(e); }
        DayIsEmpty = DayAllDay.Count == 0 && DayEvents.Count == 0;
        DayEmptyText = !IsConfigured ? Loc.T("Cal.Setup")
            : day == DateTime.Today ? Loc.T("Cal.NoEvents")
            : (Loc.German ? "Keine Termine" : "No events");
        foreach (var d in Week) d.HasEvents = _all.Any(e => e.IsOn(d.Date));
    }

    private bool _refreshing, _refreshAgain;

    /// <summary>One refresh at a time; changes during a refresh (several settings events in a row) run once more afterwards.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do { _refreshAgain = false; await RefreshCoreAsync(); } while (_refreshAgain);
        }
        finally { _refreshing = false; }
    }

    private async Task RefreshCoreAsync()
    {
        var accounts = _settings.CalendarAccounts.Where(a => a.Enabled).ToList();
        IsConfigured = accounts.Count > 0;
        if (!IsConfigured || !_settings.CalendarEnabled) { _all.Clear(); Recompute(); return; }

        // The whole week strip (two days back, four ahead), so every day can be tapped.
        var from = DateTime.Today.AddDays(-2);
        var to = DateTime.Today.AddDays(5);
        var merged = new List<CalendarEvent>();
        // All accounts at the same time (each one is a few web requests).
        var fetches = accounts.Select(a => (a, task: FetchAsync(a, from, to))).ToList();
        foreach (var (a, task) in fetches)
        {
            try
            {
                var events = await task;
                foreach (var ev in events) ev.AccountId = a.Id;
                merged.AddRange(events);
                _status[a.Id] = Loc.German ? $"OK · {events.Count} Termine diese Woche" : $"OK · {events.Count} events this week";
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
                using var client = new CalDavClient(string.IsNullOrWhiteSpace(a.Url) ? "https://caldav.icloud.com/" : a.Url, a.User,
                    SecretStore.Get($"cal:{a.Id}:password") ?? "");
                // The calendar list rarely changes: discover it every 6 hours, not on every refresh.
                string login = a.Url + "|" + a.User;
                bool have = _discovered.TryGetValue(a.Id, out var known) && known.login == login;
                if (!have || DateTime.Now - known.at > TimeSpan.FromHours(6))
                {
                    try { _discovered[a.Id] = known = (DateTime.Now, login, await client.DiscoverAsync()); }
                    catch when (have) { /* server busy: keep using the calendars we already know */ }
                }
                _calendarsOf[a.Id] = known.calendars.Select(c => new CalendarInfo(c.Url.ToString(), c.Name, c.Color)).ToList();
                // Switched-off calendars are not even fetched.
                var shown = known.calendars.Where(c => !a.Hidden.Contains(c.Url.ToString()));
                // At most three requests at once - iCloud throttles bursts.
                using var gate = new SemaphoreSlim(3);
                var blobs = await Task.WhenAll(shown.Select(async cal =>
                {
                    await gate.WaitAsync();
                    try { return (cal, items: await client.FetchAsync(cal.Url, from, to)); }
                    finally { gate.Release(); }
                }));
                var list = new List<CalendarEvent>();
                foreach (var (cal, items) in blobs)
                    foreach (var blob in items) list.AddRange(Parse(blob, from, to, cal.Color));
                return list;
            }
            case CalendarKind.Google:
            {
                var client = new GoogleCalendarClient(a.User, SecretStore.Get($"cal:{a.Id}:secret") ?? "", SecretStore.Get($"cal:{a.Id}:refresh"));
                var list = await client.FetchAsync(from, to, a.Hidden);
                _calendarsOf[a.Id] = client.Calendars.Select(c => new CalendarInfo(c.id, c.name, c.color)).ToList();
                if (client.RefreshToken is not null) SecretStore.Set($"cal:{a.Id}:refresh", client.RefreshToken);
                return list;
            }
        }
        return new();
    }

    internal static List<CalendarEvent> Parse(string ics, DateTime from, DateTime to, string? color)
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
                    Meeting = MeetingLinks.Find(ev.Url?.ToString(), ev.Location, ev.Description),
                };
            })
            .ToList();
    }

    private void Recompute()
    {
        if (DateTime.Today.Day != Week.FirstOrDefault(w => w.IsToday)?.Day)
        {
            SelectedDate = DateTime.Today; // new day: start from today again
            BuildWeek();
        }
        FillDay();
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
            new CalendarEvent { Title = "Geburtstag Mia", Start = t, End = t.AddDays(1), AllDay = true, Color = "#BF5AF2" },
            new CalendarEvent { Title = "Zahnarzt", Start = t.AddDays(1).AddHours(9), End = t.AddDays(1).AddHours(10), Color = "#30D158" },
        };
        IsConfigured = true;
        Recompute();
    }
}
