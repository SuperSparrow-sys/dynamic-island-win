using System.Net.Http;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services.M365;

/// <summary>
/// The own Teams status (work and school accounts): shown in the island and changed with one click
/// (Available, Busy, Do not disturb, Be right back, Away, or back to automatic).
/// </summary>
/// <summary>One dot in the Teams status widget.</summary>
public sealed record PresenceChoice(string Availability, string Label, Brush Brush, bool Hollow = false, bool Bar = false);

public sealed partial class PresenceService : ObservableObject
{
    private static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    /// <summary>The dots, top to bottom: available, busy, do not disturb, be right back, away, automatic.</summary>
    public static IReadOnlyList<PresenceChoice> Choices { get; } = new[]
    {
        new PresenceChoice("Available", Loc.German ? "Verfügbar" : "Available", B(0x30, 0xD1, 0x58)),
        new PresenceChoice("Busy", Loc.German ? "Beschäftigt" : "Busy", B(0xFF, 0x45, 0x3A)),
        new PresenceChoice("DoNotDisturb", Loc.German ? "Nicht stören" : "Do not disturb", B(0xFF, 0x45, 0x3A), Bar: true),
        new PresenceChoice("BeRightBack", Loc.German ? "Bin gleich zurück" : "Be right back", B(0xFF, 0xD6, 0x0A)),
        new PresenceChoice("Away", Loc.German ? "Abwesend" : "Away", B(0xFF, 0xD6, 0x0A), Hollow: true),
        new PresenceChoice("", Loc.German ? "Automatisch (Teams entscheidet)" : "Automatic (Teams decides)", B(0x8E, 0x8E, 0x93), Hollow: true),
    };

    /// <summary>Which dot gets the ring ("" = automatic).</summary>
    [ObservableProperty] private string _selected = "";
    private readonly MicrosoftAccount _account;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMinutes(1) };

    [ObservableProperty] private string _availability = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private Brush _brush = Brushes.Gray;
    [ObservableProperty] private bool _isKnown;
    [ObservableProperty] private string _status = "";

    public PresenceService(MicrosoftAccount account)
    {
        _account = account;
        _poll.Tick += async (_, _) => await RefreshAsync();
        _account.Connected += () => _ = RefreshAsync();
    }

    public bool Available => _account.IsConnected && _account.IsWorkAccount;

    public void Start()
    {
        _poll.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!Available) { IsKnown = false; return; }
        try
        {
            var p = await _account.GetAsync("me/presence");
            if (p is null) return;
            Apply(p["availability"]?.ToString() ?? "PresenceUnknown");
            Status = "";
        }
        catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            IsKnown = false;
            Status = Loc.German ? "Teams-Status freischalten (Einstellungen → Microsoft)" : "Allow Teams status (Settings → Microsoft)";
        }
        catch (Exception ex) { Log.Error("Teams presence", ex); }
    }

    private void Apply(string availability)
    {
        Availability = availability;
        Selected = availability switch { "AvailableIdle" => "Available", "BusyIdle" => "Busy", "Offline" or "PresenceUnknown" => "", _ => availability };
        (Text, var color) = availability switch
        {
            "Available" or "AvailableIdle" => (Loc.German ? "Verfügbar" : "Available", Color.FromRgb(0x30, 0xD1, 0x58)),
            "Busy" or "BusyIdle" => (Loc.German ? "Beschäftigt" : "Busy", Color.FromRgb(0xFF, 0x45, 0x3A)),
            "DoNotDisturb" => (Loc.German ? "Nicht stören" : "Do not disturb", Color.FromRgb(0xFF, 0x45, 0x3A)),
            "BeRightBack" => (Loc.German ? "Bin gleich zurück" : "Be right back", Color.FromRgb(0xFF, 0xD6, 0x0A)),
            "Away" => (Loc.German ? "Abwesend" : "Away", Color.FromRgb(0xFF, 0xD6, 0x0A)),
            "Offline" => (Loc.German ? "Offline" : "Offline", Color.FromRgb(0x8E, 0x8E, 0x93)),
            _ => (Loc.German ? "Unbekannt" : "Unknown", Color.FromRgb(0x8E, 0x8E, 0x93)),
        };
        var b = new SolidColorBrush(color);
        b.Freeze();
        Brush = b;
        IsKnown = true;
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo() => Apply("Available");

    /// <summary>"Available", "Busy", "DoNotDisturb", "BeRightBack", "Away" - or "" to let Teams decide again.</summary>
    [RelayCommand]
    private async Task Set(string? availability)
    {
        if (!Available) return;
        try
        {
            if (string.IsNullOrEmpty(availability))
                await _account.SendAsync(HttpMethod.Post, "me/presence/clearUserPreferredPresence", new { });
            else
            {
                string activity = availability switch { "DoNotDisturb" => "DoNotDisturb", "BeRightBack" => "BeRightBack", "Away" => "Away", "Busy" => "Busy", _ => "Available" };
                // The preferred status holds for a working day unless changed again.
                await _account.SendAsync(HttpMethod.Post, "me/presence/setUserPreferredPresence",
                    new { availability, activity, expirationDuration = "PT8H" });
                Apply(availability);
            }
            await Task.Delay(1500);
            await RefreshAsync();
        }
        catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            Status = Loc.German ? "Teams-Status setzen ist nicht freigegeben (Einstellungen → Microsoft)" : "Setting the Teams status is not allowed (Settings → Microsoft)";
        }
        catch (Exception ex)
        {
            Log.Error("Teams presence set", ex);
            Status = Loc.German ? "Teams-Status konnte nicht gesetzt werden" : "Could not set the Teams status";
        }
    }
}
