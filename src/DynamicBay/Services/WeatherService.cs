using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;

namespace DynamicBay.Services;

public sealed record WeatherNow(double Temp, int Code, bool IsDay);
public sealed record WeatherHourData(DateTime Time, double Temp, int Code, bool IsDay, int? Rain);
public sealed record WeatherDayData(DateTime Date, int Code, double Max, double Min, int? Rain);
/// <summary>One answer of Open-Meteo, always in °C; local times of the place.</summary>
public sealed record WeatherForecast(WeatherNow Now, IReadOnlyList<WeatherHourData> Hours, IReadOnlyList<WeatherDayData> Days);
public sealed record WeatherPlace(string Name, string Region, string Country, double Latitude, double Longitude)
{
    /// <summary>"Pirna, Sachsen" - the country only when the region is unknown.</summary>
    public string Label => string.Join(", ", new[] { Name, Region.Length > 0 ? Region : Country }.Where(s => s.Length > 0));
}

/// <summary>
/// Open-Meteo (no key, free for non-commercial use): the forecast and the place search. Tolerant of missing fields;
/// fixtures of the real answers in tests/DynamicBay.Tests/Fixtures/Api/OpenMeteo.
/// </summary>
public static class OpenMeteo
{
    public static string ForecastUrl(double lat, double lon) =>
        FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}")
        + "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,precipitation_probability,is_day"
        + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=4";

    public static string SearchUrl(string name) =>
        $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=8&language={(Loc.German ? "de" : "en")}&format=json";

    /// <summary>The forecast, or null when the answer has no current weather (an error answer, a changed format).</summary>
    public static WeatherForecast? Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
        var cur = root?["current"];
        if (Num(cur?["temperature_2m"]) is not double t) return null;
        var now = new WeatherNow(t, Int(cur?["weather_code"]) ?? 0, Int(cur?["is_day"]) != 0);

        var hours = new List<WeatherHourData>();
        var h = root?["hourly"];
        if (h?["time"] is JsonArray ht)
            for (int i = 0; i < ht.Count; i++)
            {
                if (Time(ht[i]) is not DateTime time || Num(At(h["temperature_2m"], i)) is not double temp) continue;
                hours.Add(new WeatherHourData(time, temp, Int(At(h["weather_code"], i)) ?? 0, Int(At(h["is_day"], i)) != 0, Int(At(h["precipitation_probability"], i))));
            }

        var days = new List<WeatherDayData>();
        var d = root?["daily"];
        if (d?["time"] is JsonArray dt)
            for (int i = 0; i < dt.Count; i++)
            {
                if (Time(dt[i]) is not DateTime date || Num(At(d["temperature_2m_max"], i)) is not double max || Num(At(d["temperature_2m_min"], i)) is not double min) continue;
                days.Add(new WeatherDayData(date.Date, Int(At(d["weather_code"], i)) ?? 0, max, min, Int(At(d["precipitation_probability_max"], i))));
            }

        // Hours from the current one on (the answer starts at midnight).
        var start = Time(cur?["time"]) is DateTime ct ? ct.Date.AddHours(ct.Hour) : (hours.Count > 0 ? hours[0].Time : DateTime.MinValue);
        return new WeatherForecast(now, hours.Where(x => x.Time >= start).ToList(), days);
    }

    public static List<WeatherPlace> ParsePlaces(string json)
    {
        var list = new List<WeatherPlace>();
        JsonArray? results;
        try { results = JsonNode.Parse(json)?["results"] as JsonArray; }
        catch (System.Text.Json.JsonException) { return list; }
        foreach (var r in results ?? new JsonArray())
        {
            if (r?["name"]?.GetValue<string>() is not { Length: > 0 } name || Num(r["latitude"]) is not double lat || Num(r["longitude"]) is not double lon) continue;
            list.Add(new WeatherPlace(name, Str(r["admin1"]), Str(r["country"]), lat, lon));
        }
        return list;
    }

    /// <summary>"10°" in the chosen unit, rounded like the weather apps do.</summary>
    public static string Temp(double celsius, bool fahrenheit) =>
        ((int)Math.Round(fahrenheit ? celsius * 9 / 5 + 32 : celsius, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "°";

    /// <summary>The icon of a WMO weather code (Lucide, see Themes/Icons.Lucide.xaml).</summary>
    public static string Icon(int code, bool isDay) => code switch
    {
        0 or 1 => isDay ? "Icon.Sun" : "Icon.Moon",
        2 => isDay ? "Icon.CloudSun" : "Icon.CloudMoon",
        45 or 48 => "Icon.CloudFog",
        >= 51 and <= 57 => "Icon.CloudDrizzle",
        >= 61 and <= 67 or >= 80 and <= 82 => "Icon.CloudRain",
        >= 71 and <= 77 or 85 or 86 => "Icon.CloudSnow",
        >= 95 => "Icon.CloudLightning",
        _ => "Icon.Cloud",
    };

    public static string Describe(int code) => Loc.German
        ? code switch
        {
            0 => "Klar", 1 => "Überwiegend klar", 2 => "Teils bewölkt", 3 => "Bedeckt", 45 or 48 => "Nebel",
            >= 51 and <= 55 => "Nieselregen", 56 or 57 or 66 or 67 => "Gefrierender Regen", 61 => "Leichter Regen", 63 => "Regen", 65 => "Starker Regen",
            71 => "Leichter Schnee", 73 => "Schnee", 75 => "Starker Schnee", 77 => "Schneegriesel", >= 80 and <= 82 => "Schauer",
            85 or 86 => "Schneeschauer", >= 95 => "Gewitter", _ => "Bewölkt",
        }
        : code switch
        {
            0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast", 45 or 48 => "Fog",
            >= 51 and <= 55 => "Drizzle", 56 or 57 or 66 or 67 => "Freezing rain", 61 => "Light rain", 63 => "Rain", 65 => "Heavy rain",
            71 => "Light snow", 73 => "Snow", 75 => "Heavy snow", 77 => "Snow grains", >= 80 and <= 82 => "Showers",
            85 or 86 => "Snow showers", >= 95 => "Thunderstorm", _ => "Cloudy",
        };

    private static JsonNode? At(JsonNode? array, int i) => array is JsonArray a && i < a.Count ? a[i] : null;

    private static double? Num(JsonNode? n)
    {
        try { return n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static int? Int(JsonNode? n) => Num(n) is double d ? (int)Math.Round(d) : null;

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static DateTime? Time(JsonNode? n) =>
        DateTime.TryParse(Str(n), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}

/// <summary>An hour or a day as the widget shows it (texts in the chosen unit).</summary>
public sealed record WeatherHour(string Time, string Temp, string Icon, string Rain);
public sealed record WeatherDay(string Name, string Icon, string High, string Low, string Rain, double From, double To)
{
    /// <summary>The range bar: empty space before it, its length (never a dot), the space after it - as star widths.</summary>
    public double Span => Math.Max(To - From, 0.06);
    public double After => Math.Max(1 - From - Span, 0);
}

/// <summary>
/// The "Wetter" widget: now, the next hours and the next three days at the place chosen in the settings. Fetched when
/// the panel opens with the widget on a page and every 15 minutes while it stays open; nothing runs while it is closed.
/// The unit is converted here, so switching °C/°F needs no new request.
/// </summary>
public sealed partial class WeatherService : ObservableObject, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15), TooOld = TimeSpan.FromHours(3);

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _poll;
    private WeatherForecast? _forecast;
    private DateTime _fetched = DateTime.MinValue;
    private bool _demo, _busy, _failed;

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _place = "";
    [ObservableProperty] private string _temp = "";
    [ObservableProperty] private string _icon = "Icon.Cloud";
    [ObservableProperty] private string _condition = "";
    [ObservableProperty] private string _highLow = "";
    /// <summary>No place yet, or why there is no weather: one short line for the card.</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<WeatherHour> _hours = Array.Empty<WeatherHour>();
    [ObservableProperty] private IReadOnlyList<WeatherDay> _days = Array.Empty<WeatherDay>();
    /// <summary>Temperatures of the next 24 hours in °C (the curve of the wide widget; its shape does not depend on the unit).</summary>
    [ObservableProperty] private IReadOnlyList<double> _curve = Array.Empty<double>();
    [ObservableProperty] private string _curveLow = "";
    [ObservableProperty] private string _curveHigh = "";

    public WeatherService(AppSettings settings, Dispatcher ui, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DynamicBay/{App.Version}");
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = Every };
        _poll.Tick += async (_, _) => await RefreshAsync();
        _settings.PropertyChanged += OnSettingsChanged;
        Show();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.WeatherFahrenheit)) Show();
        if (e.PropertyName == nameof(AppSettings.WeatherPlace)) // set after the coordinates
        {
            _forecast = null;
            _fetched = DateTime.MinValue;
            _failed = false;
            Show();
            if (_poll.IsEnabled) _ = RefreshAsync();
        }
    }

    /// <summary>The panel opened or closed (with the widget on a page): fetch when the last answer is old, then every 15 minutes.</summary>
    public void SetPanelOpen(bool open)
    {
        if (_demo) return;
        if (!open) { _poll.Stop(); return; }
        if (!_poll.IsEnabled) _poll.Start();
        if (DateTime.UtcNow - _fetched >= Every) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_demo || _busy || !_settings.HasWeatherPlace) return;
        _busy = true;
        try
        {
            using var res = await _http.GetAsync(OpenMeteo.ForecastUrl(_settings.WeatherLatitude, _settings.WeatherLongitude));
            string body = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode && OpenMeteo.Parse(body) is { } f)
            {
                _forecast = f;
                _fetched = DateTime.UtcNow;
                _failed = false;
            }
            else { _failed = true; Log.Info($"Weather: HTTP {(int)res.StatusCode}"); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _failed = true;
            Log.Info($"Weather: {ex.GetType().Name}");
        }
        finally { _busy = false; }
        Show();
    }

    /// <summary>Places for the settings ("Pirna" → Pirna, Sachsen; Pirna, Goa ...); empty when none or offline.</summary>
    public async Task<List<WeatherPlace>> SearchAsync(string name)
    {
        if (name.Trim().Length < 2) return new();
        try
        {
            using var res = await _http.GetAsync(OpenMeteo.SearchUrl(name));
            return res.IsSuccessStatusCode ? OpenMeteo.ParsePlaces(await res.Content.ReadAsStringAsync()) : new();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"Weather search: {ex.GetType().Name}");
            return new();
        }
    }

    /// <summary>Texts for the card from the last answer, in the chosen unit. An answer older than 3 hours is not shown.</summary>
    private void Show()
    {
        bool f = _settings.WeatherFahrenheit;
        Place = _settings.WeatherPlace;
        var data = _forecast is not null && (_demo || DateTime.UtcNow - _fetched < TooOld) ? _forecast : null;
        HasData = data is not null;
        Status = !_settings.HasWeatherPlace ? (Loc.German ? "Ort in den Einstellungen wählen" : "Choose a place in the settings")
            : data is null ? (_failed ? (Loc.German ? "Gerade kein Wetter verfügbar" : "No weather right now") : (Loc.German ? "Wird geladen" : "Loading"))
            : "";
        if (data is null) { Hours = Array.Empty<WeatherHour>(); Days = Array.Empty<WeatherDay>(); Curve = Array.Empty<double>(); return; }

        Temp = OpenMeteo.Temp(data.Now.Temp, f);
        Icon = OpenMeteo.Icon(data.Now.Code, data.Now.IsDay);
        Condition = OpenMeteo.Describe(data.Now.Code);
        var today = data.Days.FirstOrDefault();
        HighLow = today is null ? "" : $"H {OpenMeteo.Temp(today.Max, f)}  {(Loc.German ? "T" : "L")} {OpenMeteo.Temp(today.Min, f)}";

        var hours = new List<WeatherHour> { new(Loc.German ? "Jetzt" : "Now", Temp, Icon, Rain(data.Hours.FirstOrDefault()?.Rain)) };
        foreach (var h in data.Hours.Skip(1).Take(4))
            hours.Add(new WeatherHour(h.Time.ToString(Loc.German ? "HH" : "h tt", Loc.German ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US")),
                OpenMeteo.Temp(h.Temp, f), OpenMeteo.Icon(h.Code, h.IsDay), Rain(h.Rain)));
        Hours = hours;

        var next = data.Days.Skip(1).Take(3).ToList();
        double lo = next.Count > 0 ? next.Min(d => d.Min) : 0, hi = next.Count > 0 ? next.Max(d => d.Max) : 1;
        var culture = Loc.German ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");
        Days = next.Select((d, i) => new WeatherDay(
            i == 0 ? (Loc.German ? "Morgen" : "Tomorrow") : d.Date.ToString("dddd", culture),
            OpenMeteo.Icon(d.Code, true), OpenMeteo.Temp(d.Max, f), OpenMeteo.Temp(d.Min, f), Rain(d.Rain),
            Range(d.Min, lo, hi), Range(d.Max, lo, hi))).ToList();

        var curve = data.Hours.Take(24).Select(h => h.Temp).ToList();
        Curve = curve;
        CurveLow = curve.Count > 0 ? OpenMeteo.Temp(curve.Min(), f) : "";
        CurveHigh = curve.Count > 0 ? OpenMeteo.Temp(curve.Max(), f) : "";
    }

    /// <summary>Rain chance worth showing (from 30 %), else "".</summary>
    public static string Rain(int? percent) => percent is >= 30 ? $"{percent} %" : "";

    /// <summary>Where a temperature sits between the lowest and highest of the days (0..1), for the range bars.</summary>
    public static double Range(double t, double lo, double hi) => hi - lo < 0.1 ? 0.5 : Math.Clamp((t - lo) / (hi - lo), 0, 1);

    /// <summary>Snapshot mode: a fixed forecast, nothing is fetched.</summary>
    public void LoadDemo(WeatherForecast forecast)
    {
        _demo = true;
        _forecast = forecast;
        _fetched = DateTime.UtcNow;
        Show();
    }

    /// <summary>Tests: an answer as if it had just been fetched.</summary>
    internal void Load(WeatherForecast forecast)
    {
        _forecast = forecast;
        _fetched = DateTime.UtcNow;
        Show();
    }

    public void Dispose()
    {
        _poll.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
        _http.Dispose();
    }
}
