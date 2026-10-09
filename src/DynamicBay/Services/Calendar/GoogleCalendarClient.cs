using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicBay.Services.Calendars;

/// <summary>
/// Google Calendar via OAuth 2.0 for installed apps (loopback redirect + PKCE), read-only scope.
/// Each user brings their own OAuth client (Google limits unverified apps), see docs/INTEGRATIONS.md.
/// </summary>
public sealed class GoogleCalendarClient
{
    private const string Scope = "https://www.googleapis.com/auth/calendar.readonly";
    private readonly string _clientId, _clientSecret;
    private string? _accessToken;
    private DateTime _expires;

    public string? RefreshToken { get; private set; }

    public GoogleCalendarClient(string clientId, string clientSecret, string? refreshToken)
    {
        _clientId = clientId.Trim();
        _clientSecret = clientSecret.Trim();
        RefreshToken = refreshToken;
    }

    /// <summary>Opens the browser for consent and stores the refresh token on success.</summary>
    public async Task SignInAsync()
    {
        int port = FreePort();
        string redirect = $"http://127.0.0.1:{port}/";
        string verifier = B64(RandomNumberGenerator.GetBytes(48));
        string challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = B64(RandomNumberGenerator.GetBytes(12));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        Process.Start(new ProcessStartInfo("https://accounts.google.com/o/oauth2/v2/auth" +
            $"?client_id={Uri.EscapeDataString(_clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}&response_type=code" +
            $"&scope={Uri.EscapeDataString(Scope)}&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
            "&access_type=offline&prompt=consent") { UseShellExecute = true });

        var ctxTask = listener.GetContextAsync();
        if (await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(3))) != ctxTask) throw new TimeoutException();
        var ctx = await ctxTask;
        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
        bool ok = q["state"] == state && q["code"] is not null;
        var html = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><body style=\"font-family:system-ui;background:#000;color:#fff;display:grid;place-items:center;height:100vh;margin:0\"><h2>" +
            (ok ? (Core.Loc.German ? "Google Kalender verbunden. Du kannst das Fenster schließen." : "Google Calendar connected. You can close this window.")
                : (Core.Loc.German ? "Anmeldung abgebrochen." : "Sign-in cancelled.")) + "</h2>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException(q["error"] ?? "denied");

        await TokenAsync(new()
        {
            ["code"] = q["code"]!, ["client_id"] = _clientId, ["client_secret"] = _clientSecret,
            ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
        });
    }

    private async Task TokenAsync(Dictionary<string, string> form)
    {
        using var http = new HttpClient();
        var res = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(json?["error_description"]?.ToString() ?? json?["error"]?.ToString() ?? res.StatusCode.ToString());
        _accessToken = json!["access_token"]!.ToString();
        _expires = DateTime.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>() - 60);
        if (json["refresh_token"] is JsonNode rt) RefreshToken = rt.ToString();
    }

    private async Task EnsureTokenAsync()
    {
        if (_accessToken is not null && DateTime.UtcNow < _expires) return;
        if (RefreshToken is null) throw new InvalidOperationException("not signed in");
        await TokenAsync(new()
        {
            ["refresh_token"] = RefreshToken, ["client_id"] = _clientId, ["client_secret"] = _clientSecret, ["grant_type"] = "refresh_token",
        });
    }

    private static readonly HttpClient Api = new() { BaseAddress = new Uri("https://www.googleapis.com/calendar/v3/"), Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>One shared HttpClient (no new connection pool per refresh); the token goes on each request.</summary>
    private async Task<string> GetAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var resp = await Api.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public async Task<List<CalendarEvent>> FetchAsync(DateTime from, DateTime to)
    {
        await EnsureTokenAsync();
        var list = JsonNode.Parse(await GetAsync("users/me/calendarList?minAccessRole=reader"));
        var events = new List<CalendarEvent>();
        var calendars = (list?["items"]?.AsArray() ?? new JsonArray()).Where(c => c is not null && c["selected"]?.GetValue<bool>() != false).ToList();
        // All calendars at once instead of one after another.
        var pages = await Task.WhenAll(calendars.Select(async cal =>
        {
            string id = Uri.EscapeDataString(cal!["id"]!.ToString());
            string url = $"calendars/{id}/events?singleEvents=true&orderBy=startTime&maxResults=50" +
                         $"&timeMin={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&timeMax={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}";
            try { return (cal, data: JsonNode.Parse(await GetAsync(url))); } catch { return (cal, data: (JsonNode?)null); }
        }));
        foreach (var (cal, data) in pages)
        {
            foreach (var ev in data?["items"]?.AsArray() ?? new JsonArray())
            {
                if (ev?["status"]?.ToString() == "cancelled") continue;
                var start = ev!["start"]; var end = ev["end"];
                bool allDay = start?["date"] is not null;
                DateTime s = allDay ? DateTime.Parse(start!["date"]!.ToString()) : DateTime.Parse(start!["dateTime"]!.ToString()).ToLocalTime();
                DateTime e = allDay ? DateTime.Parse(end!["date"]!.ToString()) : DateTime.Parse(end!["dateTime"]!.ToString()).ToLocalTime();
                events.Add(new CalendarEvent
                {
                    Title = ev["summary"]?.ToString() ?? "(ohne Titel)", Start = s, End = e, AllDay = allDay,
                    Location = ev["location"]?.ToString(), Color = cal["backgroundColor"]?.ToString(),
                });
            }
        }
        return events;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
