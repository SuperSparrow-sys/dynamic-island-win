using System.Windows;
using System.Windows.Controls;
using DynamicBay.Core;

namespace DynamicBay.Settings;

/// <summary>The weather widget on the page "Kalender und Zeit": the place (Open-Meteo search), the unit and the size.</summary>
public sealed partial class SettingsWindow
{
    private void WeatherSearch_Loaded(object sender, RoutedEventArgs e) => ShowWeatherPlace();

    private void ShowWeatherPlace()
    {
        WeatherSearch.Text = _ctx.S.WeatherPlace;
        WeatherPlaceRow.ToolTip = _ctx.S.HasWeatherPlace
            ? (Loc.German ? "Wetter von Open-Meteo.com, zum Ändern einen anderen Ort suchen." : "Weather by Open-Meteo.com, search another place to change it.")
            : (Loc.German ? "Ort eingeben und suchen, das Wetter kommt von Open-Meteo.com." : "Type a place and search, the weather comes from Open-Meteo.com.");
    }

    private void WeatherSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        _ = SearchWeatherPlaceAsync();
    }

    private void WeatherSearch_Click(object sender, RoutedEventArgs e) => _ = SearchWeatherPlaceAsync();

    /// <summary>Asks Open-Meteo for the typed name and offers what it found in a menu below the field.</summary>
    private async Task SearchWeatherPlaceAsync()
    {
        string name = WeatherSearch.Text.Trim();
        if (name.Length < 2) return;
        WeatherSearchButton.IsEnabled = false;
        var places = await _ctx.I.Weather.SearchAsync(name);
        WeatherSearchButton.IsEnabled = true;
        var menu = new ContextMenu { PlacementTarget = WeatherSearch, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (places.Count == 0)
            menu.Items.Add(new MenuItem { Header = Loc.German ? "Kein Ort gefunden" : "No place found", IsEnabled = false });
        foreach (var p in places)
        {
            var item = new MenuItem { Header = p.Region.Length > 0 && p.Country.Length > 0 ? $"{p.Label}, {p.Country}" : p.Label };
            item.Click += (_, _) =>
            {
                _ctx.S.WeatherPlace = p.Label;
                _ctx.S.WeatherLatitude = Math.Round(p.Latitude, 4);
                _ctx.S.WeatherLongitude = Math.Round(p.Longitude, 4);
                ShowWeatherPlace();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
