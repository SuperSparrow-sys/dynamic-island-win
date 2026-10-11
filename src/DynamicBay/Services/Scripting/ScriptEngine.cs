using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicBay.Core;
using Jint;
using Jint.Native;

namespace DynamicBay.Services.Scripting;

/// <summary>Result of one script run: the widget tree (or an error) and when to run again.</summary>
public sealed record ScriptResult(JsonNode? Widget, string? Error, DateTime NextRun, DateTime RanAt);

/// <summary>
/// Runs a user script in a sandbox. Jint is a JavaScript interpreter written in .NET; it gets no access to .NET,
/// Windows or other files (CLR interop stays off). The only ways out are a few host functions: HTTP(S) requests
/// to public internet addresses (size and time limited), files inside the script's own data folder, and logging. Each run is limited in time,
/// memory, statements and recursion depth.
/// </summary>
public static class ScriptEngine
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    public const int MaxFileBytes = 1024 * 1024;
    // Every connection (redirects too) goes through ConnectPublicAsync: scripts reach the internet, never this PC or
    // the local network. No proxy here: the check has to see the real target.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseProxy = false, ConnectCallback = ConnectPublicAsync,
    }) { Timeout = TimeSpan.FromSeconds(20) };
    // Behind a proxy (company networks) the connection goes to the proxy: each target, redirects included, is
    // resolved and checked here before the request is sent (see SendViaProxy).
    private static readonly HttpClient ProxiedHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = true })
    { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>For tests with a local server only.</summary>
    internal static bool AllowLocalAddresses { get; set; }
    private static readonly Lazy<string> Prelude = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DynamicBay.Scripting.Prelude.js")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    /// <summary>Widget family names as in Scriptable: "small", "medium", "large", "accessoryInline".</summary>
    public static ScriptResult Run(string code, string family, string dataFolder, bool allowNetwork, string scriptName, Action<string>? log = null)
    {
        var started = DateTime.Now;
        var logs = new List<string>();
        void Log(string m) { if (logs.Count < 200) logs.Add(m); log?.Invoke(m); }
        try
        {
            Directory.CreateDirectory(dataFolder);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var engine = new Engine(o => o
                .LimitMemory(64_000_000)
                .TimeoutInterval(TimeSpan.FromSeconds(40))
                .MaxStatements(20_000_000)
                .LimitRecursion(400)
                .CancellationToken(cts.Token)
                .Strict(false));

            engine.SetValue("__host_http", new Func<string, string, string, string?, double, string>((url, method, headers, body, timeout) =>
                HttpCall(url, method, headers, body, timeout, allowNetwork)));
            engine.SetValue("__host_read", new Func<string, string?>(n => { var p = DataPath(dataFolder, n); return p is not null && File.Exists(p) ? File.ReadAllText(p) : null; }));
            engine.SetValue("__host_write", new Action<string, string>((n, s) =>
            {
                var p = DataPath(dataFolder, n) ?? throw new InvalidOperationException("Ungültiger Dateiname");
                if (Encoding.UTF8.GetByteCount(s) > MaxFileBytes) throw new InvalidOperationException("Datei zu groß (max. 1 MB)");
                if (Directory.GetFiles(dataFolder).Length > 50 && !File.Exists(p)) throw new InvalidOperationException("Zu viele Dateien");
                File.WriteAllText(p, s);
            }));
            engine.SetValue("__host_exists", new Func<string, bool>(n => DataPath(dataFolder, n) is { } p && File.Exists(p)));
            engine.SetValue("__host_remove", new Action<string>(n => { if (DataPath(dataFolder, n) is { } p && File.Exists(p)) File.Delete(p); }));
            engine.SetValue("__host_list", new Func<string>(() => JsonSerializer.Serialize(Directory.GetFiles(dataFolder).Select(Path.GetFileName))));
            engine.SetValue("__host_log", new Action<string>(m => Log(m)));
            engine.SetValue("__scriptName", scriptName);

            engine.Execute(Prelude.Value);
            engine.Execute($"var config = {{ runsInWidget: true, runsInApp: false, runsInAccessoryWidget: {(family.StartsWith("accessory") ? "true" : "false")}, runsWithSiri: false, runsInActionExtension: false, runsInNotification: false, runsFromHomeScreen: false, widgetFamily: {JsonSerializer.Serialize(family)} }};");
            // Scriptable scripts use top-level await: run the script as the body of an async function.
            var promise = engine.Evaluate("(async () => {\n" + code + "\n})()");
            promise.UnwrapIfPromise();
            var json = engine.Evaluate("__serializeResult()").AsString();
            var widget = JsonNode.Parse(json);
            if (widget is null) return new ScriptResult(null, "Das Skript hat kein Widget gesetzt (Script.setWidget fehlt).", started.AddMinutes(15), started);
            return new ScriptResult(widget, null, NextRunFrom(widget, started), started);
        }
        catch (Exception ex)
        {
            string msg = ex switch
            {
                Jint.Runtime.PromiseRejectedException pr => Describe(pr.RejectedValue),
                Jint.Runtime.JavaScriptException js => js.Message + (js.Location.Start.Line > 1 ? $" (Zeile {js.Location.Start.Line - 1})" : ""),
                TimeoutException or OperationCanceledException => "Zeitlimit überschritten",
                Jint.Runtime.MemoryLimitExceededException => "Speicherlimit überschritten",
                Jint.Runtime.StatementsCountOverflowException => "Zu viele Rechenschritte",
                Jint.Runtime.RecursionDepthOverflowException => "Zu tiefe Rekursion",
                _ => ex.Message,
            };
            return new ScriptResult(null, msg, started.AddMinutes(5), started);
        }
    }

    /// <summary>Message and line of an error thrown inside the (async) script.</summary>
    private static string Describe(JsValue value)
    {
        if (value is not Jint.Native.Object.ObjectInstance o) return value.ToString();
        var msg = o.Get("message").ToString();
        var name = o.Get("name").ToString();
        var stack = o.Get("stack");
        var m = stack.IsString() ? System.Text.RegularExpressions.Regex.Match(stack.AsString(), @":(\d+):\d+") : null;
        string where = m is { Success: true } && int.TryParse(m.Groups[1].Value, out int line) && line > 1 ? $" (Zeile {line - 1})" : "";
        return (name is "Error" or "undefined" ? "" : name + ": ") + msg + where;
    }

    /// <summary>refreshAfterDate of the widget, clamped to 1 minute .. 6 hours (default 15 minutes).</summary>
    private static DateTime NextRunFrom(JsonNode widget, DateTime now)
    {
        var ms = widget["refresh"]?.GetValue<double?>();
        if (ms is null) return now.AddMinutes(15);
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms.Value).LocalDateTime;
        if (at < now.AddMinutes(1)) at = now.AddMinutes(1);
        if (at > now.AddHours(6)) at = now.AddHours(6);
        return at;
    }

    /// <summary>File inside the data folder, or null for anything that tries to leave it.</summary>
    private static string? DataPath(string folder, string name)
    {
        var file = Path.GetFileName(name ?? "");
        if (file.Length == 0 || file.Length > 100 || file.StartsWith('.') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var full = Path.GetFullPath(Path.Combine(folder, file));
        return full.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string HttpCall(string url, string method, string headersJson, string? body, double timeout, bool allowNetwork)
    {
        try
        {
            if (!allowNetwork) return Error("Netzwerkzugriff ist für dieses Skript ausgeschaltet");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Error("Nur http- und https-Adressen sind erlaubt");
            var req = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant()), uri);
            if (JsonNode.Parse(headersJson) is JsonObject headers)
                foreach (var (k, v) in headers)
                    if (v is not null && !req.Headers.TryAddWithoutValidation(k, v.ToString()) && body is not null)
                        req.Content ??= new StringContent(body);
            if (body is not null)
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                if (JsonNode.Parse(headersJson) is JsonObject h && h.FirstOrDefault(p => p.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value is { } ct)
                    req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ct.ToString());
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeout, 1, 20)));
            using var resp = UsesProxy(uri) ? SendViaProxy(req, cts.Token) : Http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var stream = resp.Content.ReadAsStream(cts.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxResponseBytes) return Error("Antwort zu groß (max. 2 MB)");
            }
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            return JsonSerializer.Serialize(new { status = (int)resp.StatusCode, body = text, error = resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}" });
        }
        catch (OperationCanceledException) { return Error("Zeitüberschreitung bei der Anfrage"); }
        catch (Exception ex) { return Error(ex.InnerException?.Message ?? ex.Message); }

        static string Error(string m) => JsonSerializer.Serialize(new { status = 0, body = "", error = m });
    }

    private static bool UsesProxy(Uri uri)
    {
        try { return HttpClient.DefaultProxy.GetProxy(uri) is { } proxy && proxy != uri && !HttpClient.DefaultProxy.IsBypassed(uri); }
        catch { return false; }
    }

    /// <summary>Throws if the host resolves to an address a script must not reach.</summary>
    private static void CheckPublic(Uri uri, CancellationToken ct)
    {
        var host = uri.IdnHost;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? new[] { literal! } : Dns.GetHostAddressesAsync(host, ct).GetAwaiter().GetResult();
        if (addresses.Length == 0 || (!AllowLocalAddresses && addresses.Any(IsForbidden)))
            throw new HttpRequestException(Loc.German ? "Adressen in diesem PC oder im lokalen Netz sind gesperrt" : "Addresses on this PC or the local network are blocked");
    }

    /// <summary>Through the system proxy, following at most 5 redirects itself so every target is checked first.</summary>
    private static HttpResponseMessage SendViaProxy(HttpRequestMessage req, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            CheckPublic(req.RequestUri!, ct);
            var resp = ProxiedHttp.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)resp.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || resp.Headers.Location is not { } location || hop >= 5) return resp;
            var next = new Uri(req.RequestUri!, location);
            resp.Dispose();
            if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException("Nur http- und https-Adressen sind erlaubt");
            // 307/308 keep method and body; the others continue with a GET, like the automatic redirects.
            var follow = new HttpRequestMessage(code is 307 or 308 ? req.Method : HttpMethod.Get, next);
            foreach (var h in req.Headers) if (!h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) follow.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (code is 307 or 308) follow.Content = req.Content;
            req = follow;
        }
    }

    /// <summary>Resolves the host itself and connects only to public addresses (the checked one, so DNS cannot switch).</summary>
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? new[] { literal! } : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(a => AllowLocalAddresses || !IsForbidden(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(Loc.German ? "Adressen in diesem PC oder im lokalen Netz sind gesperrt" : "Addresses on this PC or the local network are blocked");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Blocked ranges plus the networks of this PC's own adapters (global IPv6 of the router, NAS, the PC itself).</summary>
    private static bool IsForbidden(IPAddress ip) => IsBlockedAddress(ip) || InLocalSubnet(ip);

    private static bool InLocalSubnet(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == ip.AddressFamily && SamePrefix(ip, u.Address, u.PrefixLength)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>True if the first <paramref name="bits"/> bits of both addresses match.</summary>
    internal static bool SamePrefix(IPAddress a, IPAddress b, int bits)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        if (x.Length != y.Length) return false;
        bits = Math.Clamp(bits, 0, x.Length * 8);
        // A /0 or /1 prefix would cover the whole internet: only the address itself counts then.
        if (bits < 8) bits = x.Length * 8;
        int i = 0;
        for (; bits >= 8; bits -= 8, i++) if (x[i] != y[i]) return false;
        return bits == 0 || (x[i] >> (8 - bits)) == (y[i] >> (8 - bits));
    }

    /// <summary>
    /// True for addresses a script must not reach: loopback, private networks (10/8, 172.16/12, 192.168/16),
    /// link-local (169.254/16, fe80::/10), carrier-grade NAT (100.64/10), unique local (fc00::/7), multicast,
    /// "this network" (0/8, ::) and reserved ranges; IPv4 inside IPv6 (mapped, 6to4, NAT64) is checked as IPv4.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is 0 or 10 or 127
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && (b[1] & 0xC0) == 64)
                || b[0] >= 224; // multicast 224/4, reserved 240/4, broadcast
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return true;
        if (b.Take(12).All(x => x == 0)) return true; // ::, ::1 and the old IPv4-compatible form
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC) return true;
        if (b[0] == 0x20 && b[1] == 0x02) return IsBlockedAddress(new IPAddress(b[2..6])); // 6to4
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.Skip(4).Take(8).All(x => x == 0))
            return IsBlockedAddress(new IPAddress(b[12..16])); // NAT64
        return false;
    }
}
