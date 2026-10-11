using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services.M365;

/// <summary>One dot in the Teams status widget.</summary>
public sealed record PresenceChoice(string Availability, string Label, Brush Brush, bool Hollow = false, bool Bar = false, bool Reset = false);

/// <summary>
/// The own Teams status (work and school accounts): shown in the island and changed with one click
/// (Available, Busy, Do not disturb, Be right back, Away, Appear offline - or back to automatic).
/// The ring shows what YOU chose: Teams takes a few seconds to pass a new status on, and "automatic" has no status of
/// its own - reading it back right away made the ring jump to the old dot, so grey and yellow looked broken.
/// Optional: during a focus session or while sharing the screen the status turns to "Do not disturb" by itself and
/// goes back afterwards.
/// </summary>
public sealed partial class PresenceService : ObservableObject
{
    private static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    /// <summary>The dots, top to bottom; the last one (arrow) hands the status back to Teams.</summary>
    public static IReadOnlyList<PresenceChoice> Choices { get; } = new[]
    {
        new PresenceChoice("Available", Loc.German ? "Verfügbar" : "Available", B(0x30, 0xD1, 0x58)),
        new PresenceChoice("Busy", Loc.German ? "Beschäftigt" : "Busy", B(0xFF, 0x45, 0x3A)),
        new PresenceChoice("DoNotDisturb", Loc.German ? "Nicht stören" : "Do not disturb", B(0xFF, 0x45, 0x3A), Bar: true),
        new PresenceChoice("BeRightBack", Loc.German ? "Bin gleich zurück" : "Be right back", B(0xFF, 0xD6, 0x0A)),
        new PresenceChoice("Away", Loc.German ? "Als abwesend anzeigen" : "Appear away", B(0xFF, 0xD6, 0x0A), Hollow: true),
        new PresenceChoice("Offline", Loc.German ? "Als offline anzeigen" : "Appear offline", B(0x8E, 0x8E, 0x93), Hollow: true),
        new PresenceChoice("", Loc.German ? "Status zurücksetzen (Teams entscheidet)" : "Reset status (Teams decides)", B(0x8E, 0x8E, 0x93), Reset: true),
    };

    private readonly MicrosoftAccount _account;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMinutes(1) };
    private string? _beforeAuto; // the status chosen before the automatic "Do not disturb" (null = none set by us)
    private int _manualChoices; // counts clicks on a dot: an automatic change that was under way when one came is dropped
    /// <summary>The status to go back to also lives on disk: after a crash the next start hands it back to Teams.</summary>
    private static readonly string AutoFile = Path.Combine(AppSettings.Folder, "teams-auto.json");

    /// <summary>Which dot gets the ring: the status you chose ("" = Teams decides).</summary>
    [ObservableProperty] private string _selected = "";
    [ObservableProperty] private string _availability = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private Brush _brush = Brushes.Gray;
    [ObservableProperty] private bool _isKnown;
    [ObservableProperty] private string _status = "";

    public PresenceService(MicrosoftAccount account, AppSettings settings)
    {
        _account = account;
        _settings = settings;
        _poll.Tick += async (_, _) => await RefreshAsync();
        _account.Connected += () => _ = RefreshAsync();
        // A chosen status lasts a working day (like in Teams); after that the ring is back on automatic.
        if (_settings.TeamsChosenUntil > DateTime.Now) Selected = _settings.TeamsChosen;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.FocusActive) or nameof(AppSettings.Sharing)) _ = AutoAsync();
        };
    }

    public bool Available => _account.IsConnected && _account.IsWorkAccount;

    public void Start()
    {
        _poll.Start();
        _ = RefreshAsync();
        _ = RestoreSavedAsync();
    }

    /// <summary>
    /// The app ended (or crashed) during a focus session with "Do not disturb" set by us: give the status from
    /// before back to Teams now. Older than 4 hours: Teams has dropped it by itself already.
    /// </summary>
    private async Task RestoreSavedAsync()
    {
        if (_beforeAuto is not null || !Available || LoadAuto() is not { } saved) return;
        if (DateTime.Now - saved.At > TimeSpan.FromHours(4)) { DeleteAuto(); return; }
        if (Quiet) { _beforeAuto = saved.Before; Selected = "DoNotDisturb"; return; }
        string target = ChosenOrAuto(saved.Before);
        int manual = _manualChoices;
        if (await SendAsync(target, RemainingDuration(target)))
        {
            DeleteAuto();
            if (manual == _manualChoices) Selected = target;
        }
    }

    /// <summary>
    /// On exit: hands the status from before the automatic "Do not disturb" back to Teams (waits at most 3 s).
    /// If that does not get through, the next start does it.
    /// </summary>
    public void RestoreBeforeExit()
    {
        if (_beforeAuto is not { } back || !Available) return;
        try
        {
            string target = ChosenOrAuto(back), duration = RemainingDuration(target);
            // Off the UI thread: waiting here for a task that needs the UI thread would never end.
            if (Task.Run(() => SendPresenceAsync(target, duration)).Wait(TimeSpan.FromSeconds(3)))
            {
                _beforeAuto = null;
                Selected = target;
                DeleteAuto();
                Log.Info("Teams status handed back on exit");
            }
        }
        catch (Exception ex) { Log.Error("Teams presence exit", ex); }
    }

    /// <summary>The app keeps running after all (a cancelled sign-out): "Do not disturb" again if focus is still on.</summary>
    public void Reapply() => _ = AutoAsync();

    public async Task RefreshAsync()
    {
        if (!Available) { IsKnown = false; return; }
        try
        {
            var p = await _account.GetAsync("me/presence");
            if (p is null) return;
            Apply(ParseAvailability(p));
            if (_settings.TeamsChosenUntil <= DateTime.Now && _beforeAuto is null) Selected = "";
            Status = "";
        }
        catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            IsKnown = false;
            Status = Loc.German ? "Teams-Status freischalten" : "Allow Teams status";
        }
        catch (Exception ex) { Log.Error("Teams presence", ex); }
    }

    /// <summary>GET me/presence: "Available", "Busy", ... ("PresenceUnknown" if missing).</summary>
    internal static string ParseAvailability(JsonNode? p) => p?["availability"]?.ToString() is { Length: > 0 } a ? a : "PresenceUnknown";

    /// <summary>What Teams reports right now (for the tooltip and the colour).</summary>
    private void Apply(string availability)
    {
        Availability = availability;
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
    public void LoadDemo() { Apply("Available"); Selected = "Available"; }

    /// <summary>A click on a dot: "Available", "Busy", "DoNotDisturb", "BeRightBack", "Away", "Offline" - or "" to let Teams decide again.</summary>
    [RelayCommand]
    private async Task Set(string? availability)
    {
        availability ??= "";
        _manualChoices++;
        _beforeAuto = null; // chosen by hand: the automatic status no longer applies
        DeleteAuto();
        if (await SendAsync(availability))
        {
            Selected = availability;
            _settings.TeamsChosen = availability;
            _settings.TeamsChosenUntil = availability.Length == 0 ? DateTime.MinValue : DateTime.Now.AddHours(8);
        }
    }

    private async Task<bool> SendAsync(string availability, string duration = "PT8H")
    {
        if (!Available) return false;
        try
        {
            await SendPresenceAsync(availability, duration);
            Status = "";
            Log.Info($"Teams status: {(availability.Length == 0 ? "automatic" : availability)}");
            // Teams passes it on after a few seconds: read it back then (the ring already shows the choice).
            _ = Task.Delay(6000).ContinueWith(_ => RefreshAsync(), TaskScheduler.FromCurrentSynchronizationContext());
            return true;
        }
        catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            Status = Loc.German ? "Teams-Status nicht freigegeben" : "Teams status not allowed";
        }
        catch (Exception ex)
        {
            Log.Error("Teams presence set", ex);
            Status = (Loc.German ? "Teams-Status: " : "Teams status: ") + ErrorText.For(ex);
        }
        return false;
    }

    /// <summary>Only the Graph call (no UI): also used on exit from a background thread.</summary>
    private async Task SendPresenceAsync(string availability, string duration)
    {
        if (availability.Length == 0)
            await _account.SendAsync(HttpMethod.Post, "me/presence/clearUserPreferredPresence", new { });
        else
        {
            // Graph only takes these pairs; "appear offline" is Offline + OffWork.
            string activity = availability == "Offline" ? "OffWork" : availability;
            await _account.SendAsync(HttpMethod.Post, "me/presence/setUserPreferredPresence",
                new { availability, activity, expirationDuration = duration });
        }
    }

    private bool Quiet => _settings.FocusActive || _settings.Sharing;

    /// <summary>Focus session or screen sharing: "Do not disturb" by itself; afterwards back to what it was.</summary>
    private async Task AutoAsync()
    {
        if (!_settings.TeamsAutoStatus || !Available) return;
        if (Quiet && _beforeAuto is null && Selected != "DoNotDisturb")
        {
            string before = Selected;
            int manual = _manualChoices;
            if (!await SendAsync("DoNotDisturb", "PT4H")) return;
            if (manual != _manualChoices) return; // a dot was clicked meanwhile: that choice wins
            _beforeAuto = before;
            Selected = "DoNotDisturb";
            SaveAuto(before);
            // Focus or sharing ended while the request was under way: hand the status back right away.
            if (!Quiet) await AutoAsync();
        }
        else if (!Quiet && _beforeAuto is { } back)
        {
            _beforeAuto = null;
            string target = ChosenOrAuto(back);
            int manual = _manualChoices;
            if (await SendAsync(target, RemainingDuration(target)))
            {
                DeleteAuto();
                if (manual == _manualChoices) Selected = target;
                // Focus started again meanwhile: back to "Do not disturb".
                if (Quiet && manual == _manualChoices) await AutoAsync();
            }
            else if (manual == _manualChoices) _beforeAuto ??= back; // next change tries again, the file covers the next start
        }
    }

    /// <summary>A status chosen by hand that ran out during the focus session goes back to automatic.</summary>
    private string ChosenOrAuto(string back) => back.Length > 0 && _settings.TeamsChosenUntil <= DateTime.Now ? "" : back;

    /// <summary>What is left of a status chosen by hand (it lasts a working day from the click, not from now).</summary>
    private string RemainingDuration(string availability)
    {
        if (availability.Length == 0) return "PT8H";
        var left = _settings.TeamsChosenUntil - DateTime.Now;
        if (left > TimeSpan.FromHours(8)) left = TimeSpan.FromHours(8);
        if (left < TimeSpan.FromMinutes(5)) left = TimeSpan.FromMinutes(5);
        return $"PT{(int)left.TotalHours}H{left.Minutes}M";
    }

    private static void SaveAuto(string before)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(AutoFile, JsonSerializer.Serialize(new { before, at = DateTime.Now.ToString("o") }));
        }
        catch (Exception ex) { Log.Error("Teams presence save", ex); }
    }

    private static (string Before, DateTime At)? LoadAuto()
    {
        try
        {
            if (!File.Exists(AutoFile)) return null;
            var n = JsonNode.Parse(File.ReadAllText(AutoFile));
            string before = n?["before"]?.ToString() ?? "";
            if (Choices.All(c => c.Availability != before)) { DeleteAuto(); return null; }
            return (before, DateTime.TryParse(n?["at"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MinValue);
        }
        catch { return null; }
    }

    private static void DeleteAuto()
    {
        try { File.Delete(AutoFile); } catch { }
    }
}
