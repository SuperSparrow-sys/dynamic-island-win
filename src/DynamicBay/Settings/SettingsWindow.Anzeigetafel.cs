using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DynamicBay.Core;

namespace DynamicBay.Settings;

/// <summary>The Anzeigetafel on the page "Geräte": address, key (DPAPI store, never shown again), state and a check.</summary>
public sealed partial class SettingsWindow
{
    private TextBlock? _tafelStateText, _tafelKeyText;
    private bool _tafelWired;

    private void BuildAnzeigetafelRows()
    {
        var tafel = _ctx.I.Anzeigetafel;
        if (!_tafelWired)
        {
            _tafelWired = true;
            PropertyChangedEventHandler changed = (_, _) => Dispatcher.BeginInvoke(UpdateAnzeigetafelState);
            tafel.PropertyChanged += changed;
            Closed += (_, _) => tafel.PropertyChanged -= changed;
        }
        AnzeigetafelRows.Children.Clear();

        var address = new TextBox { Style = (Style)FindResource("S.TextBox"), Width = 260, Text = _ctx.S.AnzeigetafelUrl };
        address.LostFocus += (_, _) => { _ctx.S.AnzeigetafelUrl = address.Text.Trim(); _ctx.S.SaveSoon(); _ = tafel.RefreshAsync(); };
        AnzeigetafelRows.Children.Add(Row("Icon.Link", Loc.German ? "Adresse" : "Address",
            Loc.German ? "Zum Beispiel https://homeserver.….ts.net:20004" : "For example https://homeserver.….ts.net:20004", address, false));

        var key = new PasswordBox { Width = 260, Height = 32 };
        key.LostFocus += (_, _) => { if (key.Password.Length > 0) { tafel.SetKey(key.Password); key.Password = ""; UpdateAnzeigetafelState(); } };
        AnzeigetafelRows.Children.Add(Row("Icon.Key", Loc.German ? "Schlüssel" : "Key", null, key, true, out var keyText));
        _tafelKeyText = keyText;

        var check = FocusButton(Loc.German ? "Prüfen" : "Check");
        check.Click += async (_, _) => await tafel.RefreshAsync();
        AnzeigetafelRows.Children.Add(Row("Icon.Monitor", "Panel", null, check, true, out var stateText));
        _tafelStateText = stateText;
        UpdateAnzeigetafelState();
    }

    private void UpdateAnzeigetafelState()
    {
        var tafel = _ctx.I.Anzeigetafel;
        if (_tafelKeyText is not null)
            SetDesc(_tafelKeyText, tafel.Key.Length > 0 ? (Loc.German ? "Gespeichert. Ein neuer ersetzt ihn." : "Saved. A new one replaces it.") : (Loc.German ? "Aus der .env der Anzeigetafel" : "From the display board's .env"));
        if (_tafelStateText is null) return;
        string text = !tafel.IsConfigured ? (Loc.German ? "Nicht verbunden" : "Not connected")
            : tafel.HasError ? tafel.Error
            : tafel.State is { } s ? (s.Reachable ? (Loc.German ? (s.On == true ? $"An, {s.Percent} %" : "Aus") : (s.On == true ? $"On, {s.Percent} %" : "Off")) + " · " : "") + tafel.StatusText
            : (Loc.German ? "Noch nicht geprüft" : "Not checked yet");
        SetDesc(_tafelStateText, text);
    }
}
