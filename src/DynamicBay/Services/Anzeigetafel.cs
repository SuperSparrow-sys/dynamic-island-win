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
    public static JsonObject Command(string action, int? percent, string? duration, string reason, string source, string? onlyIfReason = null, int fade = 0)
    {
        var o = new JsonObject { ["v"] = 1, ["aktion"] = action };
        if (percent is { } p && action is "an" or "helligkeit") o["prozent"] = Math.Clamp(p, MinPercent, MaxPercent);
        if (action != "plan") o["dauer"] = duration ?? "naechster";
        if (fade > 0) o["uebergang_s"] = Math.Clamp(fade, 1, 120);
        o["grund"] = Cut(reason);
        o["quelle"] = Cut(source);
        if (action == "plan" && onlyIfReason is not null) o["nur_wenn_grund"] = Cut(onlyIfReason);
        return o;
    }

    public static string FocusReason(string name) => "Fokus " + name;

    /// <summary>One line under the widget: why the panel shows what it shows, and until when.</summary>
    public static string Describe(PanelState s)
    {
        bool de = Loc.German;
        if (!s.Reachable) return de ? "Panel antwortet nicht" : "Panel does not answer";
        string text;
        if (s.Override is { } o)
        {
            string who = o.Reason == Island ? (de ? "Von Hand" : "By hand") + (o.Source.Length > 0 ? $" ({o.Source})" : "") : o.Reason;
            text = o.UntilText.Length > 0 ? $"{who} · {(de ? "bis" : "until")} {o.UntilText}"
                : o.Duration == "offen" ? $"{who} · {(de ? "bis auf Weiteres" : "until further notice")}" : who;
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

/// <summary>One line of the rule table: when report <see cref="When"/> ("Zuhause|aus") arrives, after <see cref="WaitS"/>
/// seconds and unless a remembered report has the value in <see cref="Unless"/>, these commands go to the Anzeigetafel.</summary>
public sealed record PanelRule(string When, IReadOnlyDictionary<string, bool> Unless, int WaitS, IReadOnlyList<JsonObject> Commands);

/// <summary>
/// What the automations do at the panel, as a table the DynamicBay server runs without understanding it
/// (<c>automatik</c>, see Fixtures/Api/Server) - and the same table run here without a server. Reports from the
/// iPhone (at home, focus) are in the table; triggers that happen on this PC (camera, focus session, "Nicht stören")
/// send their command from here (<see cref="Local"/>). "Weg" wins: the panel stays off while away, only the widget
/// switches it then. No WPF, no network.
/// </summary>
public static class PanelRules
{
    public const string Away = "Weg", Home = "Zu Hause";

    public static string Reason(FocusProfile p) => p.Trigger == Triggers.Focus ? PanelProtocol.FocusReason(p.Name) : "Automation " + p.Name;

    /// <summary>The command an automation sends while it holds, or null when it leaves the panel alone.</summary>
    public static JsonObject? While(FocusProfile p, string source, string duration = "ende") => p.Panel switch
    {
        "dimmen" => PanelProtocol.Command("helligkeit", p.PanelPercent, duration, Reason(p), source, fade: p.PanelFade),
        "aus" => PanelProtocol.Command("aus", null, duration, Reason(p), source, fade: p.PanelFade),
        "an" => PanelProtocol.Command("an", null, duration, Reason(p), source, fade: p.PanelFade),
        _ => null,
    };

    /// <summary>The command that gives the panel back when it no longer holds: only its own override.</summary>
    public static JsonObject Back(FocusProfile p, string source) =>
        PanelProtocol.Command("plan", null, null, Reason(p), source, Reason(p), fade: p.PanelFade);

    public static List<PanelRule> Table(IEnumerable<FocusProfile> all, string presence, string source = "DynamicBay-Server")
    {
        var none = new Dictionary<string, bool>();
        var awayBlocks = new Dictionary<string, bool> { [presence] = false };
        var list = new List<PanelRule>();
        foreach (var p in all.Where(p => p.Enabled))
        {
            switch (p.Trigger)
            {
                case Triggers.Home when p.Panel == "plan":
                    list.Add(new PanelRule($"{presence}|an", none, 0,
                        new[] { PanelProtocol.Command("plan", null, null, Home, source, Away, fade: p.PanelFade) }));
                    break;
                case Triggers.Away when p.Panel == "aus":
                    list.Add(new PanelRule($"{presence}|aus", none, Math.Clamp(p.PanelDelay, 0, 3600),
                        new[] { PanelProtocol.Command("aus", null, "offen", Away, source, fade: p.PanelFade) }));
                    break;
                case Triggers.Focus when While(p, source) is { } on:
                    list.Add(new PanelRule($"{p.Name}|an", awayBlocks, 0, new[] { on }));
                    list.Add(new PanelRule($"{p.Name}|aus", none, 0, new[] { Back(p, source) }));
                    break;
            }
        }
        return list;
    }

    /// <summary>The table as the server's <c>automatik</c> (commands without <c>quelle</c>: the server sets it).</summary>
    public static JsonObject ToJson(IEnumerable<PanelRule> rules)
    {
        var arr = new JsonArray();
        foreach (var r in rules)
        {
            var o = new JsonObject { ["bei"] = r.When };
            if (r.Unless.Count > 0)
            {
                var u = new JsonObject();
                foreach (var (k, v) in r.Unless) u[k] = v;
                o["ausser"] = u;
            }
            if (r.WaitS > 0) o["warten_s"] = r.WaitS;
            var cmds = new JsonArray();
            foreach (var c in r.Commands)
            {
                var copy = (JsonObject)c.DeepClone();
                copy.Remove("quelle");
                cmds.Add(copy);
            }
            o["anweisungen"] = cmds;
            arr.Add(o);
        }
        return new JsonObject { ["v"] = 1, ["regeln"] = arr };
    }

    /// <summary>The rules for a report that changed ("Zuhause", off).</summary>
    public static IEnumerable<PanelRule> For(IEnumerable<PanelRule> table, string name, bool on) =>
        table.Where(r => string.Equals(r.When, $"{name}|{(on ? "an" : "aus")}", StringComparison.OrdinalIgnoreCase));

    /// <summary>A remembered report rules it out (unknown reports do not).</summary>
    public static bool Blocked(PanelRule r, IReadOnlyDictionary<string, bool> reports) =>
        r.Unless.Any(u => reports.TryGetValue(u.Key, out bool v) && v == u.Value);

    /// <summary>The upper automation of this PC (camera, focus session, "Nicht stören") that wants the panel now, or null.</summary>
    public static FocusProfile? Local(AppSettings s) =>
        s.ActiveRules.FirstOrDefault(p => p.Trigger is Triggers.Camera or Triggers.Session or Triggers.Quiet && p.Panel is "dimmen" or "aus" or "an");
}

/// <summary>
/// The "Anzeigetafel" widget and what the automations do at the panel (docs/INTEGRATIONS.md#anzeigetafel): talks to
/// the Anzeigetafel directly with its key (DPAPI store, never in settings.json), or through the server. Reads while a
/// panel is open (every 3 s) and after each command.
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
    private List<JsonObject> _pending = new();
    private DateTime _pendingSince, _movedAt = DateTime.MinValue;
    private bool _showing, _demo, _disposed;
    /// <summary>Connected to the DynamicBay server: widget and automation commands go through it, it runs the rule table itself.</summary>
    private Func<JsonObject, Task<bool>>? _relay, _ruleRelay;
    /// <summary>The last state of each iPhone report here (no server): what "ausser" looks at.</summary>
    private readonly Dictionary<string, bool> _reports = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Rules waiting for their time (leaving: short Wi-Fi drops do not count), by trigger.</summary>
    private readonly Dictionary<string, DispatcherTimer> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _ui;
    /// <summary>The automation of this PC whose override is at the panel now (its reason), "" none.</summary>
    private string _localReason = "";

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
        _ui = ui;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _readKey = readKey ?? (() => SecretStore.Get(KeyName));
        _writeKey = writeKey ?? (v => SecretStore.Set(KeyName, v));
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += async (_, _) => await RefreshAsync();
        _slider = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(400) };
        _slider.Tick += async (_, _) => { _slider.Stop(); await SendLevelAsync(); };
        _retry = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(30) };
        _retry.Tick += async (_, _) => await RetryAsync();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.AnzeigetafelUrl) or nameof(AppSettings.AnzeigetafelEnabled)) UpdateConfigured();
            if (e.PropertyName == nameof(AppSettings.ActiveRules)) _ = LocalChangedAsync();
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
        IsConfigured = _settings.AnzeigetafelEnabled && (_relay is not null || PanelProtocol.Endpoint(_settings.AnzeigetafelUrl) is not null && Key.Length > 0);

    /// <summary>The server connected (relays) or went away (null): then this device talks to the Anzeigetafel itself again.</summary>
    public void UseServer(Func<JsonObject, Task<bool>>? relay, Func<JsonObject, Task<bool>>? ruleRelay = null)
    {
        _relay = relay;
        _ruleRelay = relay is null ? null : ruleRelay;
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

    // ---- automations ----

    /// <summary>
    /// A report from the iPhone to this PC (no server: the server runs the table itself). Only a change counts; a rule
    /// with a wait runs when the report still holds then.
    /// </summary>
    public async Task ReportAsync(string name, bool on)
    {
        if (_reports.TryGetValue(name, out bool was) && was == on) return;
        _reports[name] = on;
        if (_waiting.Remove(name + (on ? "|aus" : "|an"), out var stale)) stale.Stop(); // it turned back in time: nothing happens
        if (_demo || !IsConfigured || _relay is not null) return;
        var table = PanelRules.Table(_settings.FocusProfiles, _settings.PresenceName, Source);
        foreach (var rule in PanelRules.For(table, name, on))
        {
            if (rule.WaitS > 0)
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(rule.WaitS) };
                var r = rule;
                timer.Tick += async (_, _) => { timer.Stop(); _waiting.Remove(r.When); await RunAsync(r); };
                if (_waiting.Remove(rule.When, out var old)) old.Stop();
                _waiting[rule.When] = timer;
                timer.Start();
            }
            else await RunAsync(rule);
        }
    }

    internal Task WaitedAsync(string when) // tests: the wait is over
    {
        if (!_waiting.Remove(when, out var t)) return Task.CompletedTask;
        t.Stop();
        var parts = when.Split('|');
        var rule = PanelRules.For(PanelRules.Table(_settings.FocusProfiles, _settings.PresenceName, Source), parts[0], parts[1] == "an").First();
        return RunAsync(rule);
    }

    private async Task RunAsync(PanelRule rule)
    {
        if (PanelRules.Blocked(rule, _reports)) return;
        _pending.AddRange(rule.Commands);
        _pendingSince = DateTime.UtcNow;
        await RetryAsync();
    }

    /// <summary>
    /// The automations of this PC changed (the camera went on, off …): the upper one that wants the panel sends its
    /// command, the one before gives the panel back. While away ("Weg") nothing switches the panel on or off from here.
    /// </summary>
    internal async Task LocalChangedAsync()
    {
        if (_demo || !IsConfigured) return;
        var want = _settings.PresenceHome == false ? null : PanelRules.Local(_settings);
        string reason = want is null ? "" : PanelRules.Reason(want);
        if (reason == _localReason) return;
        var before = _localReason.Length > 0 ? _settings.FocusProfiles.FirstOrDefault(p => PanelRules.Reason(p) == _localReason) : null;
        string gone = _localReason;
        _localReason = reason;
        if (want is not null && PanelRules.While(want, Source) is { } on) _pending.Add(on); // replaces the one before
        else if (gone.Length > 0)
            _pending.Add(before is not null ? PanelRules.Back(before, Source) : PanelProtocol.Command("plan", null, null, gone, Source, gone));
        _pendingSince = DateTime.UtcNow;
        await RetryAsync();
    }

    /// <summary>Sends what waits, in order; what did not arrive is tried again every 30 s for 10 min.</summary>
    internal async Task RetryAsync()
    {
        if (DateTime.UtcNow - _pendingSince > RetryFor) _pending.Clear();
        while (_pending.Count > 0)
        {
            var command = _pending[0];
            bool ok = _ruleRelay is not null ? await _ruleRelay(command) : _relay is null && await SendAsync(command, rule: true);
            if (!ok) { _retry.Start(); return; }
            _pending.Remove(command);
        }
        _retry.Stop();
    }

    // ---- HTTP ----

    private async Task<bool> SendAsync(JsonObject command, bool rule = false)
    {
        if (_demo || _disposed || !IsConfigured) return false;
        if (_relay is not null) return !rule && await _relay(command);
        bool ok = await CallAsync(HttpMethod.Post, command);
        if (!ok && !rule) _ = RefreshAsync(); // the widget shows the real state again
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
        foreach (var t in _waiting.Values) t.Stop();
        _waiting.Clear();
        _http.Dispose();
    }
}
