using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>A focus switched on or off on the iPhone.</summary>
public sealed record FocusReport(string Name, bool On);

/// <summary>The start of an HTTP request: method, path, query and headers (names ignore case).</summary>
public sealed record HttpHead(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// iPhone focus over HTTP (docs/INTEGRATIONS.md#iphone-fokus): a Shortcuts automation posts
/// <c>{"focus": "Arbeit", "on": true}</c> to <c>http://&lt;pc&gt;:43825/focus</c> with <c>Authorization: Bearer &lt;key&gt;</c>.
/// Parsing (tolerant: also as query, "on" as text), the rules of the listener and the focus afterwards. No WPF, no network.
/// </summary>
public static class PhoneFocusProtocol
{
    public const int Port = 43825;
    public const int MaxHead = 8 * 1024, MaxBody = 4 * 1024;
    public const int MaxName = 40, MaxProfiles = 12;
    /// <summary>A focus whose "off" never came (the iPhone left home without Tailscale) ends by itself after this.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(16);

    // 32 letters without 0/o and 1/l: typed on the iPhone by hand, 24 of them are 120 bits.
    private const string KeyAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>A new key, "abcd-efgh-…" (6 groups of 4).</summary>
    public static string NewKey()
    {
        var sb = new StringBuilder(29);
        for (int i = 0; i < 24; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(KeyAlphabet[RandomNumberGenerator.GetInt32(KeyAlphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>Request line and headers (the text before the empty line), or null when it is not HTTP/1.x.</summary>
    public static HttpHead? ParseHead(string text)
    {
        var lines = text.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || first[1].Length == 0) return null;
        string target = first[1];
        int q = target.IndexOf('?');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new HttpHead(first[0], q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..], headers);
    }

    public static long ContentLength(HttpHead head) => long.TryParse(head.Header("Content-Length"), out long n) ? n : 0;

    /// <summary>
    /// Null when the request may be read, else the status: 403 from outside the LAN and Tailscale or from a browser
    /// (Origin, Sec-Fetch-Site), 404 for another path, 405 for anything but POST, 401 without the right key,
    /// 411 for a chunked body, 413 for a body over 4 KB.
    /// </summary>
    public static int? Refuse(HttpHead head, IPAddress remote, string key)
    {
        if (!IsAllowedRemote(remote)) return 403;
        if (head.Header("Origin") is not null || head.Header("Sec-Fetch-Site") is not null) return 403;
        if (!string.Equals(head.Path, "/focus", StringComparison.OrdinalIgnoreCase)) return 404;
        if (head.Method != "POST") return 405;
        if (!KeyMatches(head.Header("Authorization"), key)) return 401;
        if (head.Header("Transfer-Encoding") is not null) return 411;
        long length = ContentLength(head);
        if (length < 0 || length > MaxBody) return 413;
        return null;
    }

    /// <summary>Loopback, the private ranges of a home network (also link-local) and Tailscale (100.64.0.0/10, fd7a:115c:a1e0::/48).</summary>
    public static bool IsAllowedRemote(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && (b[1] & 0xC0) == 64);
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        return false;
    }

    /// <summary>"Bearer &lt;key&gt;", compared in constant time; case, spaces and dashes do not matter (typed on a phone).</summary>
    public static bool KeyMatches(string? authorization, string key)
    {
        if (key.Length == 0 || authorization is null) return false;
        var a = authorization.Trim();
        if (!a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(NormalizeKey(a[7..])), Encoding.UTF8.GetBytes(NormalizeKey(key)));
    }

    private static string NormalizeKey(string s) => new(s.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The report from a JSON body (<c>focus</c>/<c>name</c>, <c>on</c>/<c>state</c>/<c>active</c>) or the query; null when name or state is missing.</summary>
    public static FocusReport? Parse(string query, string body)
    {
        string? name = null;
        bool? on = null;
        if (body.Trim().Length > 0)
        {
            JsonNode? json;
            try { json = JsonNode.Parse(body); }
            catch (JsonException) { return null; }
            if (json is JsonObject o)
            {
                name = Text(o["focus"] ?? o["name"]);
                on = OnOf(o["on"] ?? o["state"] ?? o["active"]);
            }
        }
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            string key = Unescape(kv[0]), value = kv.Length > 1 ? Unescape(kv[1]) : "";
            if (key is "focus" or "name") name ??= value;
            else if (key is "on" or "state" or "active") on ??= OnOf(value);
        }
        name = Clean(name);
        return name.Length == 0 || on is null ? null : new FocusReport(name, on.Value);
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return ""; }
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? OnOf(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out bool b)) return b;
        if (v.TryGetValue<double>(out double d)) return d != 0;
        return v.TryGetValue<string>(out var s) ? OnOf(s) : null;
    }

    internal static bool? OnOf(string s) => s.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "an" or "ein" or "aktiv" or "active" or "yes" or "ja" => true,
        "off" or "false" or "0" or "aus" or "inaktiv" or "inactive" or "no" or "nein" => false,
        _ => null,
    };

    /// <summary>The name without control and format characters, at most 40 characters.</summary>
    public static string Clean(string? name)
    {
        if (name is null) return "";
        var s = new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        return s.Length > MaxName ? s[..MaxName].Trim() : s;
    }

    /// <summary>
    /// The focus after a report: "on" takes it; "off" only ends that same focus. iOS turns the old focus off after the
    /// new one is on, and the two automations may arrive in either order.
    /// </summary>
    public static string Next(string current, FocusReport r) =>
        r.On ? r.Name : string.Equals(current, r.Name, StringComparison.OrdinalIgnoreCase) ? "" : current;
}

/// <summary>
/// The HTTP endpoint for the iPhone on plain TCP (HttpListener needs an URL reservation by an admin for anything but
/// localhost). One request per connection, 5 s to send it, at most 8 at a time; the answer is 204 or an error status.
/// Reports and refusals come on the listener's thread.
/// </summary>
public sealed class PhoneFocusListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string> _key;
    private readonly Action<FocusReport, IPAddress> _report;
    private readonly Action<int, IPAddress> _refused;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(8);

    public int Port { get; }

    public PhoneFocusListener(int port, Func<string> key, Action<FocusReport, IPAddress> report, Action<int, IPAddress> refused, bool loopbackOnly = false)
    {
        _key = key;
        _report = report;
        _refused = refused;
        _listener = loopbackOnly ? new TcpListener(IPAddress.Loopback, port) : new TcpListener(IPAddress.IPv6Any, port);
        if (!loopbackOnly) _listener.Server.DualMode = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; continue; }
            if (!_slots.Wait(0)) { client.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client); }
                finally { _slots.Release(); client.Dispose(); }
            });
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = timeout.Token;
        var stream = client.GetStream();
        var remote = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address : IPAddress.None;
        try
        {
            if (!PhoneFocusProtocol.IsAllowedRemote(remote)) { await RefuseAsync(stream, 403, remote, ct); return; }
            var (head, rest) = await ReadHeadAsync(stream, ct);
            if (head is null) { await RefuseAsync(stream, 400, remote, ct); return; }
            if (PhoneFocusProtocol.Refuse(head, remote, _key()) is { } status) { await RefuseAsync(stream, status, remote, ct); return; }
            int length = (int)PhoneFocusProtocol.ContentLength(head);
            var body = new byte[length];
            int have = Math.Min(rest.Length, length);
            Array.Copy(rest, body, have);
            while (have < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(have), ct);
                if (n == 0) return;
                have += n;
            }
            if (PhoneFocusProtocol.Parse(head.Query, Encoding.UTF8.GetString(body)) is not { } report) { await RefuseAsync(stream, 400, remote, ct); return; }
            _report(report, remote); // first, like a refusal: whoever got the answer can rely on the report
            await RespondAsync(stream, 204, ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    /// <summary>The head and the bytes of the body that came with it; head null when it is broken or over 8 KB.</summary>
    private static async Task<(HttpHead? Head, byte[] Body)> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[PhoneFocusProtocol.MaxHead + PhoneFocusProtocol.MaxBody];
        int have = 0;
        while (have < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(have), ct);
            if (n == 0) return (null, Array.Empty<byte>());
            int from = Math.Max(0, have - 3);
            have += n;
            int end = buffer.AsSpan(from, have - from).IndexOf("\r\n\r\n"u8);
            if (end < 0)
            {
                if (have > PhoneFocusProtocol.MaxHead) return (null, Array.Empty<byte>());
                continue;
            }
            end += from;
            if (end > PhoneFocusProtocol.MaxHead) return (null, Array.Empty<byte>());
            var head = PhoneFocusProtocol.ParseHead(Encoding.ASCII.GetString(buffer, 0, end));
            return (head, buffer[(end + 4)..have]);
        }
        return (null, Array.Empty<byte>());
    }

    private async Task RefuseAsync(NetworkStream stream, int status, IPAddress remote, CancellationToken ct)
    {
        _refused(status, remote);
        await RespondAsync(stream, status, ct);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, CancellationToken ct)
    {
        string reason = status switch
        {
            204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 411 => "Length Required", 413 => "Payload Too Large", _ => "Error",
        };
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>
/// iPhone focus (Einstellungen → Ruhe und Fokus): listens while the switch is on and sets
/// <see cref="AppSettings.PhoneFocusHere"/>. The key lives in the DPAPI store, never in settings.json (exports leave it
/// out). An unknown focus that turns on gets an empty profile, so it shows up in the settings. Which focus is on is not
/// saved: after a restart none is.
/// </summary>
public sealed partial class PhoneFocusService : ObservableObject, IDisposable
{
    private const string KeyName = "phonefocus.key";
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _expiry;
    private PhoneFocusListener? _listener;
    private DateTime _since;
    private DateTime _lastRefusalLog;
    private bool _demo;

    /// <summary>Since when the focus reported here is on, "Arbeit seit 09:12" - or why nothing listens.</summary>
    [ObservableProperty] private string _status = "";
    /// <summary>The last request: "Arbeit an, 09:12" or a refusal with its reason.</summary>
    [ObservableProperty] private string _lastEvent = "";
    [ObservableProperty] private string _key = "";

    /// <summary>43825; test instances only with DYNAMICBAY_FOCUS_PORT (the port belongs to the installed app).</summary>
    public int Port { get; } = DevMode.On ? DevMode.FocusPort ?? 0 : PhoneFocusProtocol.Port;

    public bool IsListening => _listener is not null;

    public PhoneFocusService(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        _expiry = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(1) };
        _expiry.Tick += (_, _) => CheckExpiry(DateTime.Now);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.PhoneFocusEnabled)) Apply();
            if (e.PropertyName is nameof(AppSettings.PhoneFocusHere) or nameof(AppSettings.PhoneFocusPeer) or nameof(AppSettings.PhoneFocusServer)) UpdateStatus();
        };
    }

    public void Start() => Apply();

    private void Apply()
    {
        if (_demo) return;
        if (_settings.PhoneFocusEnabled && Port != 0) StartListener();
        else StopListener();
        UpdateStatus();
    }

    private void StartListener()
    {
        if (_listener is not null) return;
        Key = SecretStore.Get(KeyName) ?? "";
        if (Key.Length == 0) NewKey();
        try
        {
            _listener = new PhoneFocusListener(Port, () => Key,
                (r, from) => _ui.BeginInvoke(() => Receive(r, from.ToString(), DateTime.Now)),
                (status, from) => _ui.BeginInvoke(() => Refused(status, from.ToString(), DateTime.Now)), DevMode.LoopbackOnly);
            Log.Info($"Phone focus: listening on port {Port}");
        }
        catch (SocketException ex)
        {
            Log.Error("PhoneFocus listen", ex);
            _listener = null;
        }
    }

    private void StopListener()
    {
        if (_listener is null) return;
        _listener.Dispose();
        _listener = null;
        Log.Info("Phone focus: stopped");
        End();
    }

    /// <summary>For design snapshots only: a key and a state, without listening.</summary>
    public void LoadDemo()
    {
        _demo = true;
        Key = "k7m2-q9xa-p4wd-r8tn-c3hv-z6ye";
        Status = Loc.German ? "Schule seit 07:45" : "Schule since 07:45";
        LastEvent = Loc.German ? "Schule an, 07:45" : "Schule on, 07:45";
    }

    /// <summary>A new key: the old one stops working right away (the automations need the new one).</summary>
    public void NewKey()
    {
        Key = PhoneFocusProtocol.NewKey();
        SecretStore.Set(KeyName, Key);
    }

    /// <summary>UI thread: a report from the iPhone.</summary>
    internal void Receive(FocusReport r, string from, DateTime now)
    {
        if (r.On && _settings.ProfileFor(r.Name) is null && _settings.FocusProfiles.Count < PhoneFocusProtocol.MaxProfiles)
        {
            _settings.FocusProfiles.Add(new FocusProfile { Name = r.Name });
            _settings.SaveSoon();
            Log.Info($"Phone focus: new profile {r.Name}");
        }
        string next = PhoneFocusProtocol.Next(_settings.PhoneFocusHere, r);
        Log.Info($"Phone focus: {r.Name} {(r.On ? "on" : "off")} from {from}");
        LastEvent = $"{r.Name} {(r.On ? (Loc.German ? "an" : "on") : (Loc.German ? "aus" : "off"))}, {now:HH:mm}";
        if (next != _settings.PhoneFocusHere)
        {
            _since = now;
            _settings.PhoneFocusHere = next;
        }
        if (next.Length > 0) _expiry.Start(); else _expiry.Stop();
    }

    /// <summary>UI thread: a refused request, for the settings (wrong key, wrong address) and the log (at most every 10 s).</summary>
    internal void Refused(int status, string from, DateTime now)
    {
        string why = status switch
        {
            401 => Loc.German ? "falscher Schlüssel" : "wrong key",
            403 => Loc.German ? "Adresse nicht erlaubt" : "address not allowed",
            404 or 405 => Loc.German ? "falsche Adresse oder Methode" : "wrong address or method",
            _ => Loc.German ? "Anfrage nicht lesbar" : "request not readable",
        };
        LastEvent = (Loc.German ? "Abgelehnt: " : "Refused: ") + why + $", {now:HH:mm}";
        if (now - _lastRefusalLog < TimeSpan.FromSeconds(10)) return;
        _lastRefusalLog = now;
        Log.Info($"Phone focus: refused {status} from {from}");
    }

    /// <summary>Ends the focus reported here (button in the settings, switch off, or it ran out).</summary>
    public void End()
    {
        _expiry.Stop();
        if (_settings.PhoneFocusHere.Length == 0) return;
        Log.Info($"Phone focus: {_settings.PhoneFocusHere} ended here");
        _settings.PhoneFocusHere = "";
    }

    internal void CheckExpiry(DateTime now)
    {
        if (_settings.PhoneFocusHere.Length == 0 || now - _since < PhoneFocusProtocol.Expiry) return;
        Log.Info($"Phone focus: {_settings.PhoneFocusHere} ran out after {PhoneFocusProtocol.Expiry.TotalHours:0} h without an end");
        LastEndExpired = true;
        try { End(); } finally { LastEndExpired = false; }
    }

    /// <summary>True while the focus is being ended because it ran out (not reported off): leaving home is not assumed then.</summary>
    public bool LastEndExpired { get; private set; }

    private void UpdateStatus()
    {
        if (_settings.PhoneFocusEnabled && Port != 0 && _listener is null)
            Status = Loc.German ? $"Port {Port} ist belegt." : $"Port {Port} is in use.";
        else if (_settings.PhoneFocusHere.Length > 0)
            Status = Loc.German ? $"{_settings.PhoneFocusHere} seit {_since:HH:mm}" : $"{_settings.PhoneFocusHere} since {_since:HH:mm}";
        else if (_settings.PhoneFocusServer.Length > 0)
            Status = Loc.German ? $"{_settings.PhoneFocusServer} (über den Server)" : $"{_settings.PhoneFocusServer} (from the server)";
        else if (_settings.PhoneFocusPeer.Length > 0)
            Status = Loc.German ? $"{_settings.PhoneFocusPeer} (über Kopplung)" : $"{_settings.PhoneFocusPeer} (from paired device)";
        else Status = Loc.German ? "Kein Fokus" : "No focus";
        OnPropertyChanged(nameof(IsListening));
    }

    /// <summary>Where the iPhone can reach this PC: Tailscale addresses first (they work at home and away), then the LAN.</summary>
    public static IReadOnlyList<string> Addresses()
    {
        var found = new List<(bool Tailscale, string Ip)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || !PhoneFocusProtocol.IsAllowedRemote(a) || IPAddress.IsLoopback(a)) continue;
                    var b = a.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // link-local: no DHCP, not reachable from the phone
                    found.Add((b[0] == 100 && (b[1] & 0xC0) == 64, a.ToString()));
                }
            }
        }
        catch (NetworkInformationException) { }
        return found.OrderByDescending(f => f.Tailscale).Select(f => f.Ip).Distinct().ToList();
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
        _expiry.Stop();
    }
}
