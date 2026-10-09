using System.IO;
using System.Net.Http;
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
/// (size and time limited), files inside the script's own data folder, and logging. Each run is limited in time,
/// memory, statements and recursion depth.
/// </summary>
public static class ScriptEngine
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    public const int MaxFileBytes = 1024 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(20) };
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
            using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
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
}
