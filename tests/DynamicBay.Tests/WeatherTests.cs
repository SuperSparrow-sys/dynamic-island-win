using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using DynamicBay.Controls;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>
/// The weather widget: Open-Meteo's answers in Fixtures/Api/OpenMeteo (forecast for Pirna on 25.10.2026, the place
/// search, an empty search, an error), the unit, the icons and the service against a fake HTTP handler. No test calls
/// Open-Meteo.
/// </summary>
[Collection("Loc")]
public class WeatherTests
{
    private static string Fixture(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "OpenMeteo", file));

    [Fact]
    public void The_forecast_starts_at_the_current_hour()
    {
        var f = OpenMeteo.Parse(Fixture("forecast.json"));
        Assert.NotNull(f);
        Assert.Equal(10.0, f!.Now.Temp);
        Assert.Equal(61, f.Now.Code);
        Assert.True(f.Now.IsDay);
        // current.time is 11:45: the hours begin with 11:00, not at midnight.
        Assert.Equal(new DateTime(2026, 10, 25, 11, 0, 0), f.Hours[0].Time);
        Assert.True(f.Hours.Count >= 24);
        Assert.Equal(4, f.Days.Count);
        Assert.Equal(new DateTime(2026, 10, 25), f.Days[0].Date);
        Assert.True(f.Days.All(d => d.Max >= d.Min));
    }

    [Theory]
    [InlineData("error.json")]
    [InlineData("search-empty.json")]
    public void An_answer_without_current_weather_is_no_forecast(string file) => Assert.Null(OpenMeteo.Parse(Fixture(file)));

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("{\"current\":{\"temperature_2m\":null}}")]
    public void Broken_answers_are_no_forecast(string json) => Assert.Null(OpenMeteo.Parse(json));

    [Fact]
    public void Missing_hours_and_days_are_normal()
    {
        var f = OpenMeteo.Parse("{\"current\":{\"temperature_2m\":3.4}}");
        Assert.NotNull(f);
        Assert.Empty(f!.Hours);
        Assert.Empty(f.Days);
    }

    [Fact]
    public void The_search_finds_places_with_their_region()
    {
        var places = OpenMeteo.ParsePlaces(Fixture("search.json"));
        Assert.True(places.Count >= 2);
        Assert.Equal("Pirna, Sachsen", places[0].Label);
        Assert.Equal(50.95843, places[0].Latitude, 5);
        Assert.Empty(OpenMeteo.ParsePlaces(Fixture("search-empty.json")));
        Assert.Empty(OpenMeteo.ParsePlaces("nope"));
    }

    [Fact]
    public void A_place_without_region_names_its_country() =>
        Assert.Equal("Monaco, Monaco", new WeatherPlace("Monaco", "", "Monaco", 43.7, 7.4).Label);

    [Theory]
    [InlineData(10.0, false, "10°")]
    [InlineData(10.0, true, "50°")]
    [InlineData(-0.4, false, "0°")]
    [InlineData(2.5, false, "3°")]
    [InlineData(-17.8, true, "0°")]
    [InlineData(37.0, true, "99°")]
    public void Temperatures_in_both_units(double celsius, bool fahrenheit, string text) =>
        Assert.Equal(text, OpenMeteo.Temp(celsius, fahrenheit));

    [Theory]
    [InlineData(0, true, "Icon.Sun")]
    [InlineData(0, false, "Icon.Moon")]
    [InlineData(2, true, "Icon.CloudSun")]
    [InlineData(3, true, "Icon.Cloud")]
    [InlineData(45, true, "Icon.CloudFog")]
    [InlineData(53, true, "Icon.CloudDrizzle")]
    [InlineData(61, true, "Icon.CloudRain")]
    [InlineData(81, true, "Icon.CloudRain")]
    [InlineData(73, true, "Icon.CloudSnow")]
    [InlineData(95, true, "Icon.CloudLightning")]
    [InlineData(123, true, "Icon.CloudLightning")]
    public void Every_weather_code_has_an_icon(int code, bool day, string icon) => Assert.Equal(icon, OpenMeteo.Icon(code, day));

    [Fact]
    public void The_forecast_url_uses_a_dot_in_every_language()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string url = OpenMeteo.ForecastUrl(50.95843, 13.93702);
            Assert.Contains("latitude=50.9584&longitude=13.937&", url);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void Day_bars_stay_inside_and_never_shrink_to_a_dot()
    {
        Assert.Equal(0.5, WeatherService.Range(4, 4, 4)); // all days alike
        Assert.Equal(0, WeatherService.Range(-3, 0, 10));
        Assert.Equal(1, WeatherService.Range(12, 0, 10));
        var d = new WeatherDay("Mo", "Icon.Sun", "8°", "8°", "", 0.97, 0.97);
        Assert.True(d.Span >= 0.06);
        Assert.True(d.After >= 0);
    }

    [Fact]
    public void The_curve_puts_the_warmest_hour_on_top()
    {
        var pts = TempCurve.Layout(new[] { 5.0, 9.0, 7.0 }, 100, 30);
        Assert.Equal(3, pts.Length);
        Assert.Equal(0, pts[0].X);
        Assert.Equal(100, pts[2].X);
        Assert.True(pts[1].Y < pts[2].Y && pts[2].Y < pts[0].Y);
        Assert.InRange(pts.Min(p => p.Y), 1.9, 2.1);
        Assert.InRange(pts.Max(p => p.Y), 27.9, 28.1);
        Assert.Empty(TempCurve.Layout(new[] { 5.0 }, 100, 30));
    }

    // ---------- service ----------

    private sealed class FakeMeteo : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public bool Offline;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            lock (Calls) Calls.Add(url);
            if (Offline) throw new HttpRequestException("offline");
            string file = url.Contains("geocoding-api") ? "search.json" : "forecast.json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture(file), Encoding.UTF8, "application/json") });
        }
    }

    private static (WeatherService Service, FakeMeteo Fake, AppSettings Settings) Service(bool place = true)
    {
        var s = new AppSettings { WeatherFahrenheit = false };
        if (place) { s.WeatherLatitude = 50.9584; s.WeatherLongitude = 13.937; s.WeatherPlace = "Pirna, Sachsen"; }
        var fake = new FakeMeteo();
        return (new WeatherService(s, Dispatcher.CurrentDispatcher, fake), fake, s);
    }

    [Fact]
    public async Task Fetches_the_place_and_switches_the_unit_without_asking_again()
    {
        var (w, fake, s) = Service();
        await w.RefreshAsync();
        Assert.True(w.HasData);
        Assert.Equal("Pirna, Sachsen", w.Place);
        Assert.Equal("10°", w.Temp);
        Assert.Equal("Icon.CloudRain", w.Icon);
        Assert.Equal(5, w.Hours.Count);
        Assert.Equal("10°", w.Hours[0].Temp);
        Assert.Equal(3, w.Days.Count);
        Assert.Equal(24, w.Curve.Count);
        Assert.Single(fake.Calls);
        Assert.Contains("latitude=50.9584&longitude=13.937", fake.Calls[0]);

        s.WeatherFahrenheit = true;
        Assert.Equal("50°", w.Temp);
        Assert.Equal("50°", w.Hours[0].Temp);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Without_a_place_nothing_is_fetched()
    {
        var (w, fake, _) = Service(place: false);
        await w.RefreshAsync();
        Assert.False(w.HasData);
        Assert.NotEqual("", w.Status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Offline_says_so_and_keeps_the_last_weather()
    {
        var (w, fake, _) = Service();
        fake.Offline = true;
        await w.RefreshAsync();
        Assert.False(w.HasData);
        Assert.NotEqual("", w.Status);

        fake.Offline = false;
        await w.RefreshAsync();
        Assert.True(w.HasData);
        fake.Offline = true;
        await w.RefreshAsync();
        Assert.True(w.HasData); // a failed refresh does not wipe what is still fresh
        Assert.Equal("10°", w.Temp);
    }

    [Fact]
    public async Task A_new_place_drops_the_old_weather()
    {
        var (w, _, s) = Service();
        await w.RefreshAsync();
        Assert.True(w.HasData);
        s.WeatherLatitude = 48.1374; s.WeatherLongitude = 11.5755; s.WeatherPlace = "München, Bayern";
        Assert.False(w.HasData);
        Assert.Equal("München, Bayern", w.Place);
    }

    [Fact]
    public async Task The_search_returns_places_and_nothing_for_one_letter()
    {
        var (w, fake, _) = Service(place: false);
        Assert.Empty(await w.SearchAsync("P"));
        Assert.Empty(fake.Calls);
        var places = await w.SearchAsync("Pirna");
        Assert.Equal("Pirna, Sachsen", places[0].Label);
        Assert.Contains("name=Pirna", fake.Calls[0]);
    }
}
