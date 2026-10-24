using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services.Server;

/// <summary>
/// The optional DynamicBay server (Einstellungen → Geräte, docs/INTEGRATIONS.md#dynamicbay-server): paired once
/// with a code, then one WebSocket that brings the shared settings, the iPhone focus, "Nicht stören" and the
/// Anzeigetafel's panel, and takes this device's changes. Without it (not paired, unreachable) everything works as
/// before. The device key lives in the DPAPI store; messages are handled on the UI thread.
/// </summary>
public sealed partial class ServerService : ObservableObject, IDisposable
{
    private const string KeyName = "server.key";
    private static readonly TimeSpan SendDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Func<string?> _readKey;
    private readonly Action<string?> _writeKey;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<DateTimeOffset> _now;
    private readonly DispatcherTimer _send;
    private readonly HashSet<string> _dirty = new();
    private readonly Dictionary<string, SharedValue> _shared = new(StringComparer.Ordinal), _overrides = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _run;
    private ClientWebSocket? _socket;
    private bool _applying, _disposed;

    public ObservableCollection<ServerDevice> Devices { get; } = new();
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsPaired))] private bool _hasKey;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    /// <summary>The code for the next device ("483 920") and until when; "" when none was asked for.</summary>
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _iphoneAddress = "";
    [ObservableProperty] private string _iphoneKey = "";

    public bool IsPaired => HasKey && ServerProtocol.Base(_settings.ServerAddress) is not null;

    /// <summary>True when this device answers for Teams (the main device, or no paired device): set by the app.</summary>
    public Func<bool> TeamsHere { get; set; } = () => true;
    /// <summary>The Anzeigetafel's panel from <c>zustand</c>, and panel errors; set by the app.</summary>
    public Action<PanelState?>? PanelChanged { get; set; }
    public Action<string>? PanelError { get; set; }
    public event Action<bool>? ConnectedChanged;

    public ServerService(AppSettings settings, Dispatcher ui, Func<string?>? readKey = null, Action<string?>? writeKey = null,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _settings = settings;
        _ui = ui;
        _readKey = readKey ?? (() => SecretStore.Get(KeyName));
        _writeKey = writeKey ?? (v => SecretStore.Set(KeyName, v));
        _handler = handler;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _send = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = SendDelay };
        _send.Tick += async (_, _) => { _send.Stop(); await FlushAsync(); };
        HasKey = Key.Length > 0;
        settings.PropertyChanged += (_, e) =>
        {
            if (_applying) return;
            if (e.PropertyName == nameof(AppSettings.FocusProfiles)) { Changed(SharedSettings.FocusProfiles); Changed(SharedSettings.Automatik); }
            if (e.PropertyName == nameof(AppSettings.PresenceName)) Changed(SharedSettings.Automatik);
            if (e.PropertyName == nameof(AppSettings.DoNotDisturb)) _ = QuietChangedAsync();
        };
        settings.MutedApps.CollectionChanged += (_, _) => { if (!_applying) Changed(SharedSettings.MutedApps); };
        UpdateStatus();
    }

    private string Key => _readKey() ?? "";
    private long NowMs => _now().ToUnixTimeMilliseconds();
    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
    private string DeviceName => _settings.LinkDeviceName.Length > 0 ? _settings.LinkDeviceName : Environment.MachineName;
    private static string DeviceKind => System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) ? "pc" : "laptop";

    private string DeviceId
    {
        get
        {
            if (_settings.ServerDeviceId.Length != 32) { _settings.ServerDeviceId = Guid.NewGuid().ToString("N"); _settings.SaveSoon(); }
            return _settings.ServerDeviceId;
        }
    }

    // ---- pairing ----

    /// <summary>Pairs with the code from the server's page or from another device; true when it worked.</summary>
    public async Task<bool> PairAsync(string address, string typedCode)
    {
        Error = "";
        if (ServerProtocol.Base(address) is not { } b) { Error = Loc.T("Err.Address"); return false; }
        if (ServerProtocol.CleanCode(typedCode) is not { } code) { Error = Loc.German ? "Der Code hat 6 Ziffern." : "The code has 6 digits."; return false; }
        try
        {
            using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(10);
            var body = ServerProtocol.PairRequest(code, DeviceId, DeviceName, DeviceKind, AppVersion).ToJsonString();
            using var res = await http.PostAsync(ServerProtocol.PairUri(b), new StringContent(body, Encoding.UTF8, "application/json"));
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode || ServerProtocol.PairKey(text) is not { } key)
            {
                Log.Info($"Server: pairing refused ({(int)res.StatusCode})");
                Error = (int)res.StatusCode == 403 ? (Loc.German ? "Code falsch oder abgelaufen" : "Code wrong or expired") : ErrorText.For(res.StatusCode);
                return false;
            }
            _settings.ServerAddress = b.ToString().TrimEnd('/');
            _settings.SaveSoon();
            _writeKey(key);
            HasKey = true;
            Log.Info($"Server: paired with {b.Host}");
            Start();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"Server: pairing failed ({ex.GetType().Name})");
            Error = ErrorText.For(ex);
            return false;
        }
    }

    /// <summary>Forgets the server here; the server keeps the device until it is removed there.</summary>
    public void Unpair()
    {
        Stop();
        _writeKey(null);
        HasKey = false;
        _settings.PhoneFocusServer = "";
        Devices.Clear();
        IphoneAddress = IphoneKey = Code = "";
        PanelChanged?.Invoke(null);
        UpdateStatus();
        Log.Info("Server: unpaired here");
    }

    // ---- connection ----

    public void Start()
    {
        if (_disposed || !IsPaired || _run is not null) return;
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        _ = Task.Run(() => RunAsync(ct));
    }

    public void Stop()
    {
        _run?.Cancel();
        _run = null;
        try { _socket?.Abort(); } catch (ObjectDisposedException) { }
        SetConnected(false);
    }

    /// <summary>Connects again and again: 1 s doubling to 60 s; another connection of this device: 30 s; key gone: stop.</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        int backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            int? close = null;
            bool welcomed = false;
            try { (close, welcomed) = await ConnectOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException or System.IO.IOException)
            {
                Log.Info($"Server: connection failed ({ex.GetType().Name})");
            }
            await _ui.InvokeAsync(() => SetConnected(false));
            if (close == ServerProtocol.CloseUnpaired)
            {
                Log.Info("Server: key no longer valid (removed or paired again elsewhere)");
                await _ui.InvokeAsync(() => { Unpair(); Error = Loc.German ? "Der Server kennt dieses Gerät nicht mehr." : "The server no longer knows this device."; });
                return;
            }
            if (welcomed) backoff = 1;
            int wait = close == ServerProtocol.CloseReplaced ? 30 : close == ServerProtocol.CloseProtocol ? 60 : backoff;
            backoff = Math.Min(backoff * 2, 60);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<(int? Close, bool Welcomed)> ConnectOnceAsync(CancellationToken ct)
    {
        if (ServerProtocol.Base(_settings.ServerAddress) is not { } b) return (null, false);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + Key);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(ServerProtocol.SocketUri(b), connectTimeout.Token);
        _socket = ws;
        try { return await ReceiveAsync(ws, ct); }
        finally { _socket = null; }
    }

    private async Task<(int? Close, bool Welcomed)> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var hello = await _ui.InvokeAsync(() => ServerProtocol.Hello(DeviceId, DeviceName, DeviceKind, AppVersion, Open()));
        await SendRawAsync(ws, hello, ct);
        bool welcomed = false;
        var buffer = new byte[16 * 1024];
        var message = new System.IO.MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, r.Count);
            if (message.Length > ServerProtocol.MaxMessage) { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "", ct); break; }
            if (!r.EndOfMessage) continue;
            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (ServerProtocol.Obj(text) is not { } o) continue;
            if (ServerProtocol.Kind(o) == "willkommen") welcomed = true;
            await _ui.InvokeAsync(() => Handle(o));
        }
        return ((int?)ws.CloseStatus, welcomed);
    }

    private static async Task SendRawAsync(ClientWebSocket ws, JsonObject o, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, ct);

    /// <summary>Sends one message; false when there is no connection (the caller keeps it for the next <c>hallo</c>, or drops it).</summary>
    public async Task<bool> SendAsync(JsonObject o)
    {
        var ws = _socket;
        if (ws is null || ws.State != WebSocketState.Open || !IsConnected) return false;
        await _sendLock.WaitAsync();
        try { await SendRawAsync(ws, o, CancellationToken.None); return true; }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Info($"Server: send failed ({ex.GetType().Name})");
            return false;
        }
        finally { _sendLock.Release(); }
    }

    private void SetConnected(bool on)
    {
        if (IsConnected == on) return;
        IsConnected = on;
        if (!on) _settings.PhoneFocusServer = "";
        UpdateStatus();
        ConnectedChanged?.Invoke(on);
    }

    // ---- messages (UI thread) ----

    internal void Handle(JsonObject o)
    {
        switch (ServerProtocol.Kind(o))
        {
            case "willkommen" when ServerProtocol.Welcome(o) is { } w:
                _shared.Clear(); _overrides.Clear();
                foreach (var (k, v) in w.Shared) _shared[k] = v;
                foreach (var (k, v) in w.Overrides) _overrides[k] = v;
                foreach (var key in SharedSettings.Keys) ApplyKey(key);
                IphoneAddress = w.IphoneAddress;
                IphoneKey = w.IphoneKey;
                SetDevices(w.Devices);
                Error = "";
                SetConnected(true);
                ApplyState(w.State);
                Log.Info($"Server: connected (server {w.Version}, {w.Devices.Count} devices)");
                break;
            case "zustand":
                ApplyState(ServerProtocol.State(o));
                break;
            case "einstellung" when ServerProtocol.SettingOf(o) is { } s:
                if (s.OnlyHere)
                {
                    if (s.Remove) _overrides.Remove(s.Key); else _overrides[s.Key] = s.Value;
                }
                else _shared[s.Key] = s.Value;
                ApplyKey(s.Key);
                break;
            case "geraete" when o["geraete"] is JsonArray list:
                SetDevices(ServerProtocol.Devices(list).ToList());
                break;
            case "code" when ServerProtocol.CodeOf(o) is { } c:
                Code = $"{c.Code[..3]} {c.Code[3..]} · " + (Loc.German ? "gültig bis " : "valid until ") + DateTimeOffset.FromUnixTimeSeconds(c.Until).ToLocalTime().ToString("HH:mm");
                break;
            case "panel_fehler":
                PanelError?.Invoke(ServerProtocol.ErrorOf(o));
                break;
            case "fehler":
                Log.Info($"Server: refused {o["zu"]}: {ServerProtocol.ErrorOf(o)}");
                Error = ServerProtocol.ErrorOf(o);
                break;
        }
    }

    /// <summary>The value that counts here for a key (this device's override, else the shared one), if it is newer than ours.</summary>
    private void ApplyKey(string key)
    {
        if (key == SharedSettings.Automatik) return; // derived from the automations, never taken back
        var v = _overrides.TryGetValue(key, out var own) ? own : _shared.TryGetValue(key, out var shared) ? shared : null;
        if (v is null) return;
        _settings.SharedChanged.TryGetValue(key, out long mine);
        bool overridden = _overrides.ContainsKey(key);
        bool wasOverridden = _settings.OverriddenKeys.Contains(key);
        if (v.Changed <= mine && overridden == wasOverridden) return;
        _applying = true;
        try
        {
            if (SharedSettings.Write(_settings, key, v.Value))
            {
                _settings.SharedChanged[key] = v.Changed;
                if (overridden) _settings.OverriddenKeys.Add(key); else _settings.OverriddenKeys.Remove(key);
                _settings.SaveSoon();
            }
        }
        finally { _applying = false; }
    }

    private void ApplyState(ServerState s)
    {
        string focus = s.Focus is { } f && (f.Until == 0 || f.Until > _now().ToUnixTimeSeconds()) ? f.Name : "";
        _settings.ServerTeamsHere = TeamsHere();
        if (_settings.PhoneFocusServer != focus)
        {
            if (focus.Length > 0 && _settings.ProfileFor(focus) is null && _settings.FocusProfiles.Count < PhoneFocusProtocol.MaxProfiles)
                _settings.FocusProfiles.Add(new FocusProfile { Name = focus });
            _settings.PhoneFocusServer = focus;
            Log.Info($"Server: focus {(focus.Length > 0 ? focus : "none")}");
        }
        if (s.QuietChanged > 0)
        {
            var changed = DateTimeOffset.FromUnixTimeMilliseconds(s.QuietChanged).UtcDateTime;
            if (changed > _settings.LinkQuietChanged.ToUniversalTime())
            {
                _applying = true;
                try
                {
                    _settings.LinkQuietChanged = changed;
                    _settings.DoNotDisturb = s.QuietOn;
                }
                finally { _applying = false; }
            }
        }
        // Where the iPhone is: the last "Zuhause" report, kept by the server without a time limit.
        if (s.Reports.TryGetValue(_settings.PresenceName, out bool home) && _settings.PresenceHome != home) _settings.PresenceHome = home;
        PanelChanged?.Invoke(s.Panel);
    }

    private void SetDevices(List<ServerDevice> list)
    {
        Devices.Clear();
        foreach (var d in list.OrderByDescending(d => d.Id == DeviceId).ThenBy(d => d.Name)) Devices.Add(d);
        UpdateStatus();
    }

    // ---- this device's changes ----

    private void Changed(string key)
    {
        _settings.SharedChanged[key] = NowMs;
        _dirty.Add(key);
        _send.Stop();
        _send.Start();
    }

    /// <summary>Sends the settings changed here (a dragged list sends once, 500 ms after the last change).</summary>
    internal async Task FlushAsync()
    {
        foreach (var key in _dirty.ToList())
        {
            bool onlyHere = _settings.OverriddenKeys.Contains(key) && key != SharedSettings.Automatik;
            if (!await SendAsync(ServerProtocol.Setting(key, SharedSettings.Read(_settings, key), _settings.SharedChanged[key], onlyHere))) return;
            _dirty.Remove(key);
        }
    }

    /// <summary>The changes made while the server was away: sent with <c>hallo</c>, the server keeps the newer.</summary>
    private IEnumerable<(string, SharedValue)> Open()
    {
        foreach (var key in _dirty.ToList())
            if (!_settings.OverriddenKeys.Contains(key) && _settings.SharedChanged.TryGetValue(key, out long at))
                yield return (key, new SharedValue(SharedSettings.Read(_settings, key), at, DeviceId));
        _dirty.Clear();
    }

    /// <summary>Makes a shared setting this device's own (or shared again): the next change goes only here.</summary>
    public async Task SetOverrideAsync(string key, bool own)
    {
        if (own) _settings.OverriddenKeys.Add(key); else _settings.OverriddenKeys.Remove(key);
        _settings.SaveSoon();
        long at = NowMs;
        _settings.SharedChanged[key] = at;
        if (own) await SendAsync(ServerProtocol.Setting(key, SharedSettings.Read(_settings, key), at, onlyHere: true));
        else
        {
            await SendAsync(ServerProtocol.Setting(key, null, at, onlyHere: true, remove: true));
            _overrides.Remove(key);
            if (_shared.ContainsKey(key)) { _settings.SharedChanged[key] = 0; ApplyKey(key); }
        }
    }

    private async Task QuietChangedAsync()
    {
        if (DateTime.UtcNow - _settings.LinkQuietChanged.ToUniversalTime() > TimeSpan.FromSeconds(2)) _settings.LinkQuietChanged = DateTime.UtcNow;
        await SendAsync(ServerProtocol.Quiet(_settings.DoNotDisturb, new DateTimeOffset(_settings.LinkQuietChanged.ToUniversalTime()).ToUnixTimeMilliseconds()));
    }

    // ---- actions ----

    [RelayCommand]
    private async Task RequestCodeAsync()
    {
        Code = "";
        if (!await SendAsync(ServerProtocol.RequestCode())) Error = Loc.German ? "Server nicht verbunden" : "Server not connected";
    }

    /// <summary>A new key for the iPhone; the old one stops at once, the server sends <c>willkommen</c> with the new one.</summary>
    public Task<bool> NewIphoneKeyAsync() => SendAsync(new JsonObject { ["art"] = "iphone_neuer_schluessel" });

    public Task<bool> RemoveDeviceAsync(string id) => SendAsync(ServerProtocol.RemoveDevice(id));
    public Task<bool> EndFocusAsync(string name) => SendAsync(ServerProtocol.EndFocus(name));
    public Task<bool> PanelAsync(JsonObject command) => SendAsync(ServerProtocol.Panel(command));
    public Task<bool> RuleAsync(JsonObject command) => SendAsync(ServerProtocol.Rule(command));

    private void UpdateStatus()
    {
        bool de = Loc.German;
        Status = !IsPaired ? (de ? "Nicht gekoppelt" : "Not paired")
            : IsConnected ? (de ? "Verbunden" : "Connected") + (Devices.Count > 1 ? $" · {Devices.Count(d => d.Online)} {(de ? "von" : "of")} {Devices.Count} {(de ? "Geräten online" : "devices online")}" : "")
            : (de ? "Getrennt, neuer Versuch läuft" : "Disconnected, trying again");
    }

    public void Dispose()
    {
        _disposed = true;
        _send.Stop();
        Stop();
    }
}
