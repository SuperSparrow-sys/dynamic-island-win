using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;

namespace DynamicBay.Services.M365;

/// <summary>
/// One Microsoft sign-in for Outlook calendar, Microsoft To Do and the Teams status (Microsoft Graph).
/// Works for work/school accounts and personal accounts ("common" endpoint), OAuth 2.0 with PKCE and a loopback redirect.
/// Each user brings their own app registration (client id), see docs/INTEGRATIONS.md. The refresh token is kept
/// encrypted in <see cref="SecretStore"/> ("ms:refresh").
/// </summary>
public sealed partial class MicrosoftAccount : ObservableObject
{
    /// <summary>Calendar, To Do and profile; the Teams status is asked for separately (only work accounts have it).</summary>
    public const string BaseScopes = "offline_access User.Read Calendars.Read Tasks.ReadWrite People.Read Contacts.Read";
    public const string PresenceScopes = "Presence.ReadWrite";
    private const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private readonly AppSettings _settings;
    private string? _accessToken;
    private DateTime _expires;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    /// <summary>Work or school account (has Teams presence); false for personal Microsoft accounts.</summary>
    [ObservableProperty] private bool _isWorkAccount;

    public event Action? Connected;

    public MicrosoftAccount(AppSettings settings)
    {
        _settings = settings;
        IsConnected = SecretStore.Get("ms:refresh") is not null && settings.MicrosoftClientId.Length > 0;
        IsWorkAccount = settings.MicrosoftWorkAccount;
    }

    private string Tenant => string.IsNullOrWhiteSpace(_settings.MicrosoftTenant) ? "common" : _settings.MicrosoftTenant.Trim();
    private string Authority => $"https://login.microsoftonline.com/{Uri.EscapeDataString(Tenant)}/oauth2/v2.0";

    // ---------- sign-in ----------

    /// <summary>Opens the browser for sign-in and consent (scopes: base, or base + Teams status).</summary>
    public async Task SignInAsync(bool withPresence = false)
    {
        string clientId = _settings.MicrosoftClientId.Trim();
        if (clientId.Length == 0)
        {
            Status = Loc.German ? "Bitte zuerst die Anwendungs-ID (Client-ID) eintragen." : "Enter the application (client) ID first.";
            return;
        }
        IsBusy = true;
        Status = Loc.German ? "Warte auf Anmeldung im Browser…" : "Waiting for sign-in in the browser…";
        try
        {
            string scopes = withPresence ? BaseScopes + " " + PresenceScopes : BaseScopes;
            int port = FreePort();
            string redirect = $"http://localhost:{port}/";
            string verifier = B64(RandomNumberGenerator.GetBytes(48));
            string challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = B64(RandomNumberGenerator.GetBytes(12));

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();
            // select_account: pick the work account (or the personal one) instead of whatever the browser is signed in with.
            Process.Start(new ProcessStartInfo($"{Authority}/authorize?client_id={Uri.EscapeDataString(clientId)}" +
                $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}&response_mode=query" +
                $"&scope={Uri.EscapeDataString(scopes)}&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
                "&prompt=select_account") { UseShellExecute = true });

            var ctxTask = listener.GetContextAsync();
            if (await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5))) != ctxTask) throw new TimeoutException();
            var ctx = await ctxTask;
            var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
            bool ok = q["state"] == state && q["code"] is not null;
            string message = ok
                ? (Loc.German ? "Microsoft verbunden. Du kannst das Fenster schließen." : "Microsoft connected. You can close this window.")
                : (Loc.German ? "Anmeldung abgebrochen: " : "Sign-in cancelled: ") + WebUtility.HtmlEncode(q["error_description"] ?? q["error"] ?? "");
            var html = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><body style=\"font-family:system-ui;background:#000;color:#fff;display:grid;place-items:center;height:100vh;margin:0;padding:24px;text-align:center\"><h2>" + message + "</h2>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(html);
            ctx.Response.Close();
            if (!ok) throw new InvalidOperationException(q["error_description"] ?? q["error"] ?? "denied");

            await TokenAsync(new()
            {
                ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = q["code"]!,
                ["redirect_uri"] = redirect, ["code_verifier"] = verifier, ["scope"] = scopes,
            });
            if (withPresence) _settings.MicrosoftPresence = true;
            await LoadProfileAsync();
            IsConnected = true;
            Status = "";
            Connected?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Microsoft sign-in", ex);
            Status = ex.Message.Contains("AADSTS50194")
                // App registered for one organisation only: "common" is not allowed, the directory must be named.
                ? (Loc.German
                    ? "Die App ist nur für deine Firma registriert. Trag unter „Verzeichnis“ die Verzeichnis-ID (Mandant) von der Übersichtsseite der App ein – oder deine Firmen-Domain, z. B. firma.de."
                    : "The app is registered for your organisation only. Enter the directory (tenant) ID from the app's overview page under Directory - or your company domain, e.g. contoso.com.")
                : (Loc.German ? "Anmeldung fehlgeschlagen: " : "Sign-in failed: ") + ex.Message;
        }
        finally { IsBusy = false; }
    }

    public void SignOut()
    {
        SecretStore.Set("ms:refresh", null);
        _accessToken = null;
        IsConnected = false;
        _settings.MicrosoftUser = "";
        _settings.MicrosoftPresence = false;
        Status = "";
    }

    private async Task LoadProfileAsync()
    {
        var me = await GetAsync("me?$select=displayName,userPrincipalName,mail");
        string name = me?["displayName"]?.ToString() ?? "";
        string mail = me?["mail"]?.ToString() ?? me?["userPrincipalName"]?.ToString() ?? "";
        _settings.MicrosoftUser = name.Length > 0 && mail.Length > 0 ? $"{name} ({mail})" : name + mail;
    }

    // ---------- tokens ----------

    private async Task TokenAsync(Dictionary<string, string> form)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var res = await http.PostAsync($"{Authority}/token", new FormUrlEncodedContent(form));
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(FirstLine(json?["error_description"]?.ToString()) ?? json?["error"]?.ToString() ?? res.StatusCode.ToString());
        _accessToken = json!["access_token"]!.ToString();
        _expires = DateTime.UtcNow.AddSeconds((json["expires_in"]?.GetValue<int>() ?? 3600) - 120);
        if (json["refresh_token"]?.ToString() is { Length: > 0 } rt) SecretStore.Set("ms:refresh", rt);
        // Personal accounts have the consumer tenant id in their token; everything else is a work or school account.
        if (json["id_token"]?.ToString() is { } idt && TenantOf(idt) is { } tid)
        {
            IsWorkAccount = tid != ConsumerTenantId;
            _settings.MicrosoftWorkAccount = IsWorkAccount;
        }
        else if (Payload(_accessToken)?["tid"]?.ToString() is { } tid2)
        {
            IsWorkAccount = tid2 != ConsumerTenantId;
            _settings.MicrosoftWorkAccount = IsWorkAccount;
        }
    }

    private static string? FirstLine(string? s) => s?.Split('\n', '\r')[0];

    private static string? TenantOf(string jwt) => Payload(jwt)?["tid"]?.ToString();

    private static JsonNode? Payload(string jwt)
    {
        try
        {
            var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(part));
        }
        catch { return null; } // personal-account access tokens are not readable JWTs
    }

    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private async Task<bool> EnsureTokenAsync()
    {
        if (_accessToken is not null && DateTime.UtcNow < _expires) return true;
        await _tokenGate.WaitAsync();
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _expires) return true;
            var rt = SecretStore.Get("ms:refresh");
            if (rt is null || _settings.MicrosoftClientId.Length == 0) { IsConnected = false; return false; }
            string scopes = _settings.MicrosoftPresence ? BaseScopes + " " + PresenceScopes : BaseScopes;
            try
            {
                await TokenAsync(new()
                {
                    ["client_id"] = _settings.MicrosoftClientId.Trim(), ["grant_type"] = "refresh_token",
                    ["refresh_token"] = rt, ["scope"] = scopes,
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Microsoft token", ex);
                Status = Loc.German ? "Microsoft bitte neu verbinden" : "Please reconnect Microsoft";
                return false;
            }
        }
        finally { _tokenGate.Release(); }
    }

    // ---------- Graph ----------

    private static readonly HttpClient Graph = new() { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"), Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>GET a Graph resource (null if not signed in). Throws with Graph's message on errors.</summary>
    public async Task<JsonNode?> GetAsync(string path, string? prefer = null) => await SendAsync(HttpMethod.Get, path, null, prefer);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null, string? prefer = null)
    {
        if (!await EnsureTokenAsync()) return null;
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (prefer is not null) req.Headers.Add("Prefer", prefer);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await Graph.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
        if (!res.IsSuccessStatusCode)
        {
            string msg = "";
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? ""; } catch { }
            throw new GraphException(res.StatusCode, msg.Length > 0 ? msg : res.StatusCode.ToString());
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    // ---------- helpers ----------

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class GraphException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
