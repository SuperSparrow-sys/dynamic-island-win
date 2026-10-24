using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>What the Anzeigetafel holds as override: who switched what, until when.</summary>
public sealed record PanelOverride(string State, int Percent, string Reason, string Source, string UntilText, string Duration);

/// <summary>
/// <c>GET /api/geraet/panel</c> of the Anzeigetafel (dashboard-zentrale), see Fixtures/Api/Anzeigetafel: the panel as
/// read (<see cref="On"/>, <see cref="Percent"/>; null when unknown), what it should show and why, the override.
/// </summary>
public sealed record PanelState(bool Reachable, bool? On, int? Percent, string Target, int TargetPercent, string Reason,
    string NextText, PanelOverride? Override, bool Steers);

/// <summary>
/// The Anzeigetafel's device API: reading its answer, building the commands (widget and focus), the status line.
/// The Anzeigetafel only executes; which focus does what is the profile's <see cref="FocusProfile.Panel"/>. No WPF, no network.
/// </summary>
public static class PanelProtocol
{
    public const string Path = "/api/geraet/panel";
    /// <summary>The reason of a change from the widget; a focus sends "Fokus &lt;name&gt;".</summary>
    public const string Island = "Insel";
    public const int MinPercent = 5, MaxPercent = 100, MaxText = 60;

    /// <summary>The endpoint for what the user typed: the server's base address, or already the whole path.</summary>
    public static Uri? Endpoint(string address)
    {
        string a = address.Trim().TrimEnd('/');
        if (a.Length == 0) return null;
        if (!a.Contains("://", StringComparison.Ordinal)) a = "https://" + a;
        if (!a.EndsWith(Path, StringComparison.OrdinalIgnoreCase)) a += Path;
        return Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    }

    /// <summary>The answer, tolerant (missing fields are normal); null when it is not the panel's JSON.</summary>
    public static PanelState? Parse(string json)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
        if (o?["panel"] is not JsonObject panel) return null;
        var soll = o["soll"] as JsonObject;
        PanelOverride? over = o["vorrang"] is JsonObject v
            ? new PanelOverride(Text(v, "zustand"), Int(v, "prozent") ?? 0, Text(v, "grund"), Text(v, "quelle"), Text(v, "bis_text"), Text(v, "dauer"))
            : null;
        return new PanelState(Bool(panel, "erreichbar") ?? false, Bool(panel, "an"), Int(panel, "prozent"),
            Text(soll, "zustand"), Int(soll, "prozent") ?? 0, Text(soll, "grund"), Text(soll, "naechster"), over,
            Bool(o, "steuern") ?? true);
    }

    /// <summary>
    /// One command. The Anzeigetafel refuses extra fields: <c>prozent</c> only with <c>an</c>/<c>helligkeit</c>,
    /// <c>dauer</c> never with <c>plan</c>, <c>nur_wenn_grund</c> only with <c>plan</c>.
    /// </summary>
    public static JsonObject Command(string action, int? percent, string? duration, string reason, string source, string? onlyIfReason = null)
    {
        var o = new JsonObject { ["v"] = 1, ["aktion"] = action };
        if (percent is { } p && action is "an" or "helligkeit") o["prozent"] = Math.Clamp(p, MinPercent, MaxPercent);
        if (action != "plan") o["dauer"] = duration ?? "naechster";
        o["grund"] = Cut(reason);
        o["quelle"] = Cut(source);
        if (action == "plan" && onlyIfReason is not null) o["nur_wenn_grund"] = Cut(onlyIfReason);
        return o;
    }

    public static string FocusReason(string name) => "Fokus " + name;

    /// <summary>
    /// The commands when the focus reported here changes from <paramref name="before"/> to <paramref name="after"/>
    /// ("" = none): the new focus's own one (it replaces any override), otherwise back to the plan - but only if the
    /// override is still the old focus's, so a change by hand afterwards stays.
    /// </summary>
    public static List<JsonObject> ForFocus(string before, string after, Func<string, FocusProfile?> profile, string source)
    {
        var list = new List<JsonObject>();
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return list;
        var next = after.Length > 0 ? profile(after) : null;
        if (next?.Panel is "dimmen")
            list.Add(Command("helligkeit", next.PanelPercent, "ende", FocusReason(next.Name), source));
        else if (next?.Panel is "aus")
            list.Add(Command("aus", null, "ende", FocusReason(next.Name), source));
        else if (before.Length > 0 && profile(before)?.Panel is "dimmen" or "aus")
        {
            string reason = FocusReason(profile(before)!.Name);
            list.Add(Command("plan", null, null, reason, source, reason));
        }
        return list;
    }

    /// <summary>One line under the widget: why the panel shows what it shows, and until when.</summary>
    public static string Describe(PanelState s)
    {
        bool de = Loc.German;
        if (!s.Reachable) return de ? "Panel antwortet nicht" : "Panel does not answer";
        string text;
        if (s.Override is { } o)
        {
            string who = o.Reason == Island ? (de ? "Von Hand" : "By hand") + (o.Source.Length > 0 ? $" ({o.Source})" : "") : o.Reason;
            text = o.UntilText.Length > 0 ? $"{who} · {(de ? "bis" : "until")} {o.UntilText}" : who;
        }
        else if (s.Reason == "Plan aus") text = de ? "Wochenplan aus" : "Weekly plan off";
        else text = (de ? "Wochenplan" : "Weekly plan") + (s.NextText.Length > 0 ? $" · {(de ? "nächster Wechsel" : "next change")} {s.NextText}" : "");
        if (!s.Steers) text += de ? " · steuert nicht" : " · not controlling";
        return text;
    }

    private static string Cut(string s) => s.Length > MaxText ? s[..MaxText] : s;

    private static string Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    private static int? Int(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d)) return (int)Math.Round(d);
        return null;
    }

    private static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
}

/// <summary>
/// The "Anzeigetafel" widget and the focus profiles' panel switch (docs/INTEGRATIONS.md#anzeigetafel): talks to the
/// Anzeigetafel directly with its key (DPAPI store, never in settings.json). Reads while a panel is open (every 3 s)
/// and after each command. Only the device the iPhone reported its focus to sends the focus command, like Teams.
/// </summary>
public sealed partial class AnzeigetafelService : ObservableObject, IDisposable
{
    private const string KeyName = "anzeigetafel.key";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3), RetryFor = TimeSpan.FromMinutes(10);

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly Func<string?> _readKey;
    private readonly Action<string?> _writeKey;
    private readonly DispatcherTimer _poll, _slider, _retry;
    private string _focus = "";
    private List<JsonObject> _focusPending = new();
    private DateTime _focusSince, _movedAt = DateTime.MinValue;
    private bool _showing, _demo, _disposed;
    /// <summary>Connected to the DynamicBay server: widget commands go through it, it sends the focus commands itself.</summary>
    private Func<JsonObject, Task<bool>>? _relay;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasState))] private PanelState? _state;
    [ObservableProperty] private string _statusText = "";
    /// <summary>Short and localized: wrong key, not reachable, refused. "" when the last call worked.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isOn;
    /// <summary>5..100, two-way with the slider; a move is sent 400 ms after the last one.</summary>
    [ObservableProperty] private double _level = 70;
    [ObservableProperty] private bool _hasOverride;

    public bool HasState => State is not null;
    public bool HasError => Error.Length > 0;

    public AnzeigetafelService(AppSettings settings, Dispatcher ui, HttpMessageHandler? handler = null,
        Func<string?>? readKey = null, Action<string?>? writeKey = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _readKey = readKey ?? (() => SecretStore.Get(KeyName));
        _writeKey = writeKey ?? (v => SecretStore.Set(KeyName, v));
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += async (_, _) => await RefreshAsync();
        _slider = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(400) };
        _slider.Tick += async (_, _) => { _slider.Stop(); await SendLevelAsync(); };
        _retry = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(30) };
        _retry.Tick += async (_, _) => await RetryFocusAsync();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.AnzeigetafelUrl)) UpdateConfigured();
            if (e.PropertyName == nameof(AppSettings.PhoneFocusHere)) _ = FocusChangedAsync();
        };
        UpdateConfigured();
    }

    public string Key => _readKey() ?? "";

    public void SetKey(string key)
    {
        _writeKey(key.Trim().Length == 0 ? null : key.Trim());
        Error = "";
        UpdateConfigured();
        _ = RefreshAsync();
    }

    private void UpdateConfigured() =>
        IsConfigured = _relay is not null || PanelProtocol.Endpoint(_settings.AnzeigetafelUrl) is not null && Key.Length > 0;

    /// <summary>The server connected (relay) or went away (null): then this device talks to the Anzeigetafel itself again.</summary>
    public void UseServer(Func<JsonObject, Task<bool>>? relay)
    {
        _relay = relay;
        if (relay is not null) _poll.Stop();
        else State = null;
        UpdateConfigured();
    }

    /// <summary>The panel from the server's <c>zustand</c>; null: no Anzeigetafel behind the server.</summary>
    public void ApplyServer(PanelState? s)
    {
        if (_relay is null || _demo) return;
        if (s is null) { State = null; HasOverride = false; StatusText = ""; return; }
        Error = "";
        Apply(s);
    }

    /// <summary><c>panel_fehler</c> from the server: its sentence as it is.</summary>
    public void ServerError(string text) => Error = text;

    /// <summary>A panel with the widget opened or closed: read now and every 3 s while it stays open.</summary>
    public void SetPanelOpen(bool open)
    {
        if (_demo || _disposed) return;
        if (open && IsConfigured && _relay is null) { _ = RefreshAsync(); _poll.Start(); }
        else _poll.Stop();
    }

    public async Task RefreshAsync()
    {
        if (_demo || _disposed || !IsConfigured || _relay is not null) return;
        await CallAsync(HttpMethod.Get, null);
    }

    [RelayCommand]
    private Task Toggle()
    {
        bool on = !IsOn;
        Show(() => IsOn = on);
        _movedAt = DateTime.UtcNow;
        return SendAsync(PanelProtocol.Command(on ? "an" : "aus", null, "naechster", PanelProtocol.Island, Source));
    }

    [RelayCommand]
    private Task BackToPlan() => SendAsync(PanelProtocol.Command("plan", null, null, PanelProtocol.Island, Source));

    partial void OnLevelChanged(double value)
    {
        if (_showing || _demo) return;
        _movedAt = DateTime.UtcNow;
        _slider.Stop();
        _slider.Start();
    }

    internal Task SendLevelAsync()
    {
        Show(() => IsOn = true);
        return SendAsync(PanelProtocol.Command("helligkeit", (int)Math.Round(Level), "naechster", PanelProtocol.Island, Source));
    }

    private string Source => _settings.LinkDeviceName.Length > 0 ? _settings.LinkDeviceName : Environment.MachineName;

    // ---- focus ----

    /// <summary>The focus reported here changed: send what its profile says; retry for 10 min while it lasts.</summary>
    internal async Task FocusChangedAsync()
    {
        string before = _focus, after = _settings.PhoneFocusHere;
        if (before == after) return;
        _focus = after;
        if (_demo || !IsConfigured || _relay is not null) return; // the server sends the focus commands itself
        _focusPending = PanelProtocol.ForFocus(before, after, _settings.ProfileFor, Source);
        _focusSince = DateTime.UtcNow;
        await RetryFocusAsync();
    }

    internal async Task RetryFocusAsync()
    {
        if (DateTime.UtcNow - _focusSince > RetryFor) _focusPending.Clear();
        while (_focusPending.Count > 0)
        {
            var command = _focusPending[0];
            if (!await SendAsync(command, focus: true)) { _retry.Start(); return; }
            _focusPending.Remove(command);
        }
        _retry.Stop();
    }

    // ---- HTTP ----

    private async Task<bool> SendAsync(JsonObject command, bool focus = false)
    {
        if (_demo || _disposed || !IsConfigured) return false;
        if (_relay is not null) return !focus && await _relay(command);
        bool ok = await CallAsync(HttpMethod.Post, command);
        if (!ok && !focus) _ = RefreshAsync(); // the widget shows the real state again
        return ok;
    }

    /// <summary>One call; the answer replaces the shown state. False when it did not arrive or was refused.</summary>
    private async Task<bool> CallAsync(HttpMethod method, JsonObject? body)
    {
        if (PanelProtocol.Endpoint(_settings.AnzeigetafelUrl) is not { } url) return false;
        try
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Key);
            if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"Anzeigetafel: {method} {(int)res.StatusCode}{(body is null ? "" : " for " + body["aktion"])}");
                Error = res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => Loc.German ? "Schlüssel stimmt nicht" : "Key does not match",
                    HttpStatusCode.BadRequest => Loc.German ? "Anweisung abgelehnt" : "Command refused",
                    HttpStatusCode.NotFound => Loc.German ? "Adresse stimmt nicht" : "Address does not match",
                    _ => ErrorText.For(res.StatusCode),
                };
                return false;
            }
            if (PanelProtocol.Parse(text) is not { } state) { Error = Loc.T("Err.Unknown"); return false; }
            Error = "";
            Apply(state);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log.Info($"Anzeigetafel: {method} failed ({ex.GetType().Name})");
            Error = ErrorText.For(ex);
            return false;
        }
    }

    internal void Apply(PanelState s)
    {
        State = s;
        StatusText = PanelProtocol.Describe(s);
        HasOverride = s.Override is not null;
        // Right after a move the panel may not have the new value yet: the slider and the switch win a moment.
        if (DateTime.UtcNow - _movedAt < Settle) return;
        Show(() =>
        {
            IsOn = s.On ?? s.Target != "schwarz";
            if (s.Percent is { } p && p > 0) Level = Math.Clamp(p, PanelProtocol.MinPercent, PanelProtocol.MaxPercent);
        });
    }

    private void Show(Action set)
    {
        _showing = true;
        try { set(); } finally { _showing = false; }
    }

    /// <summary>Snapshot mode: an override from the laptop, nothing is sent.</summary>
    public void LoadDemo()
    {
        _demo = true;
        _poll.Stop();
        IsConfigured = true;
        Apply(new PanelState(true, true, 40, "hell", 40, "Vorrang", "Sa 19:00",
            new PanelOverride("hell", 40, PanelProtocol.Island, "Laptop", "Sa 19:00", "naechster"), true));
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        _slider.Stop();
        _retry.Stop();
        _http.Dispose();
    }
}
