using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public sealed class SpotifyDevice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public bool IsActive { get; init; }
}

public sealed class SpotifyQueueItem
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string? ImageUrl { get; init; }
}

/// <summary>
/// Optional Spotify Web API connection (Authorization Code + PKCE, loopback redirect).
/// Each user brings their own Client ID (Spotify Development Mode limits). Tokens are stored with DPAPI
/// and refreshed silently, so a login lasts until the user disconnects or revokes access.
/// </summary>
public sealed partial class SpotifyService : ObservableObject
{
    public const int RedirectPort = 43821;
    public static string RedirectUri => $"http://127.0.0.1:{RedirectPort}/callback";
    private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing " +
                                  "user-library-read user-library-modify user-read-private";

    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.spotify.com/v1/") };
    private readonly string _tokenFile = Path.Combine(AppSettings.Folder, "spotify.bin");
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _expiresAt;
    private string? _trackUri;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLiked;
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private string _repeat = "off"; // off, context, track
    /// <summary>Short message in the player when a Spotify action failed (cleared after a few seconds).</summary>
    [ObservableProperty] private string _playerError = "";
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private int _volume = 50;
    [ObservableProperty] private bool _supportsVolume;
    public ObservableCollection<SpotifyDevice> Devices { get; } = new();
    public ObservableCollection<SpotifyQueueItem> Queue { get; } = new();

    public SpotifyService(AppSettings settings)
    {
        _settings = settings;
        _poll.Tick += async (_, _) => await PollAsync();
    }

    private bool _panelOpen;

    /// <summary>
    /// Player state (like, device, shuffle) is only shown in the open panel, so the Web API is only asked while it is
    /// open (every 3 s) - not around the clock in the background.
    /// </summary>
    public void SetPanelOpen(bool open)
    {
        if (_panelOpen == open) return;
        _panelOpen = open;
        UpdatePolling();
        if (open && IsConnected) _ = PollAsync();
    }

    private void UpdatePolling()
    {
        if (IsConnected && _panelOpen) _poll.Start();
        else _poll.Stop();
    }

    public async Task InitAsync()
    {
        LoadTokens();
        if (_refreshToken is null || string.IsNullOrWhiteSpace(_settings.SpotifyClientId)) return;
        if (await EnsureTokenAsync())
        {
            IsConnected = true;
            await LoadProfileAsync();
            UpdatePolling();
        }
    }

    // ---------- auth ----------

    [RelayCommand]
    public async Task Connect()
    {
        string clientId = _settings.SpotifyClientId.Trim();
        if (clientId.Length < 16)
        {
            Status = Loc.German ? "Bitte zuerst deine Client-ID eintragen." : "Enter your Client ID first.";
            return;
        }
        IsBusy = true;
        Status = Loc.German ? "Warte auf Anmeldung im Browser…" : "Waiting for browser login…";
        try
        {
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{RedirectPort}/callback/");
            listener.Start();

            var url = "https://accounts.spotify.com/authorize" +
                      $"?client_id={Uri.EscapeDataString(clientId)}&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&code_challenge_method=S256&code_challenge={challenge}" +
                      $"&state={state}&scope={Uri.EscapeDataString(Scopes)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            var ctxTask = listener.GetContextAsync();
            if (await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(3))) != ctxTask)
                throw new TimeoutException();
            var ctx = await ctxTask;
            var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
            bool ok = q["state"] == state && q["code"] is not null;
            await RespondAsync(ctx, ok);
            if (!ok) throw new InvalidOperationException(q["error"] ?? "denied");

            await TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = q["code"]!,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            });
            IsConnected = true;
            Status = "";
            await LoadProfileAsync();
            UpdatePolling();
            await PollAsync();
        }
        catch (Exception ex)
        {
            Status = (Loc.German ? "Anmeldung fehlgeschlagen: " : "Login failed: ") + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private static async Task RespondAsync(HttpListenerContext ctx, bool ok)
    {
        string title = ok ? (Loc.German ? "Verbunden" : "Connected") : (Loc.German ? "Fehlgeschlagen" : "Failed");
        string body = ok
            ? (Loc.German ? "DynamicBay ist jetzt mit Spotify verbunden. Du kannst dieses Fenster schließen." : "DynamicBay is now connected to Spotify. You can close this window.")
            : (Loc.German ? "Die Anmeldung wurde abgebrochen." : "The login was cancelled.");
        string html = $"<!doctype html><meta charset=utf-8><title>DynamicBay</title><body style=\"font-family:system-ui;background:#000;color:#fff;display:grid;place-items:center;height:100vh;margin:0\"><div style=\"text-align:center\"><h1 style=\"font-weight:600\">{title}</h1><p style=\"color:#aaa\">{body}</p></div>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    [RelayCommand]
    public void Disconnect()
    {
        _poll.Stop();
        _accessToken = _refreshToken = null;
        IsConnected = false;
        UserName = "";
        try { File.Delete(_tokenFile); } catch { }
    }

    private async Task TokenRequestAsync(Dictionary<string, string> form)
    {
        using var auth = new HttpClient();
        var res = await auth.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(json?["error_description"]?.ToString() ?? res.StatusCode.ToString());
        _accessToken = json!["access_token"]!.ToString();
        _expiresAt = DateTime.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>() - 60);
        // Spotify may rotate the refresh token; keep the old one if none is returned.
        if (json["refresh_token"] is JsonNode rt) _refreshToken = rt.ToString();
        SaveTokens();
    }

    private async Task<bool> EnsureTokenAsync()
    {
        if (_accessToken is not null && DateTime.UtcNow < _expiresAt) return true;
        if (_refreshToken is null) return false;
        try
        {
            await TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = _settings.SpotifyClientId.Trim(),
            });
            return true;
        }
        catch (HttpRequestException) { return false; } // offline: try again later, keep login
        catch
        {
            Disconnect(); // refresh token revoked
            return false;
        }
    }

    private void SaveTokens()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var raw = JsonSerializer.SerializeToUtf8Bytes(new { r = _refreshToken });
            File.WriteAllBytes(_tokenFile, ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
        }
        catch { }
    }

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(_tokenFile)) return;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(_tokenFile), null, DataProtectionScope.CurrentUser);
            _refreshToken = JsonNode.Parse(raw)?["r"]?.ToString();
        }
        catch { }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------- API ----------

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null)
    {
        if (!await EnsureTokenAsync()) return null;
        try
        {
            var req = new HttpRequestMessage(method, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            if (body is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var res = await _http.SendAsync(req);
            if (res.StatusCode == HttpStatusCode.Unauthorized) { _accessToken = null; return null; }
            var text = await res.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch { return null; }
    }

    /// <summary>A write request (like, shuffle, repeat): true on success; failures are logged and shown in the player.</summary>
    private async Task<bool> SendActionAsync(HttpMethod method, string path)
    {
        if (!await EnsureTokenAsync()) { ShowPlayerError(Loc.German ? "Spotify ist nicht verbunden" : "Spotify is not connected"); return false; }
        try
        {
            var req = new HttpRequestMessage(method, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            var res = await _http.SendAsync(req);
            if (res.IsSuccessStatusCode) return true;
            var text = await res.Content.ReadAsStringAsync();
            Log.Info($"Spotify {method} {path.Split('?')[0]} -> {(int)res.StatusCode} {text[..Math.Min(text.Length, 200)]}");
            if (res.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
            ShowPlayerError(res.StatusCode switch
            {
                HttpStatusCode.Forbidden => Loc.German ? "Spotify erlaubt das nur mit Premium" : "Spotify allows this with Premium only",
                HttpStatusCode.NotFound => Loc.German ? "Kein aktives Spotify-Gerät" : "No active Spotify device",
                HttpStatusCode.Unauthorized => Loc.German ? "Spotify bitte neu verbinden" : "Please reconnect Spotify",
                _ => (Loc.German ? "Spotify-Fehler " : "Spotify error ") + (int)res.StatusCode,
            });
        }
        catch (Exception ex) { Log.Info($"Spotify {method} {path.Split('?')[0]} failed: {ex.Message}"); ShowPlayerError(Loc.German ? "Spotify nicht erreichbar" : "Spotify unreachable"); }
        return false;
    }

    private System.Windows.Threading.DispatcherTimer? _errorTimer;

    private void ShowPlayerError(string message)
    {
        PlayerError = message;
        _errorTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(4), System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => { PlayerError = ""; _errorTimer!.Stop(); }, System.Windows.Application.Current.Dispatcher);
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    private async Task LoadProfileAsync()
    {
        var me = await SendAsync(HttpMethod.Get, "me");
        UserName = me?["display_name"]?.ToString() ?? me?["id"]?.ToString() ?? "Spotify";
    }

    private async Task PollAsync()
    {
        if (!IsConnected) return;
        var st = await SendAsync(HttpMethod.Get, "me/player");
        if (st is null) return;
        Shuffle = st["shuffle_state"]?.GetValue<bool>() ?? false;
        Repeat = st["repeat_state"]?.ToString() ?? "off";
        DeviceName = st["device"]?["name"]?.ToString() ?? "";
        SupportsVolume = st["device"]?["supports_volume"]?.GetValue<bool>() ?? false;
        Volume = st["device"]?["volume_percent"]?.GetValue<int>() ?? Volume;
        string? uri = st["item"]?["uri"]?.ToString();
        if (uri is not null && uri != _trackUri)
        {
            _trackUri = uri;
            var contains = await SendAsync(HttpMethod.Get, $"me/library/contains?uris={Uri.EscapeDataString(uri)}");
            IsLiked = contains is JsonArray arr && arr.Count > 0 && arr[0]!.GetValue<bool>();
        }
    }

    [RelayCommand]
    public async Task ToggleLike()
    {
        if (_trackUri is null) await PollAsync();
        if (_trackUri is null) return;
        // Saves the song in "Lieblingssongs" (Spotify's library endpoint since February 2026 takes URIs).
        bool like = !IsLiked;
        IsLiked = like;
        if (!await SendActionAsync(like ? HttpMethod.Put : HttpMethod.Delete, $"me/library?uris={Uri.EscapeDataString(_trackUri)}"))
            IsLiked = !like;
    }

    [RelayCommand]
    public async Task ToggleShuffle()
    {
        bool on = !Shuffle;
        Shuffle = on;
        if (!await SendActionAsync(HttpMethod.Put, $"me/player/shuffle?state={(on ? "true" : "false")}")) Shuffle = !on;
    }

    [RelayCommand]
    public async Task CycleRepeat()
    {
        string before = Repeat;
        Repeat = Repeat switch { "off" => "context", "context" => "track", _ => "off" };
        if (!await SendActionAsync(HttpMethod.Put, $"me/player/repeat?state={Repeat}")) Repeat = before;
    }

    public async Task SetVolumeAsync(int percent)
    {
        Volume = Math.Clamp(percent, 0, 100);
        await SendAsync(HttpMethod.Put, $"me/player/volume?volume_percent={Volume}");
    }

    [RelayCommand]
    public async Task LoadDevices()
    {
        var res = await SendAsync(HttpMethod.Get, "me/player/devices");
        Devices.Clear();
        if (res?["devices"] is JsonArray arr)
            foreach (var d in arr)
                Devices.Add(new SpotifyDevice
                {
                    Id = d!["id"]?.ToString() ?? "",
                    Name = d["name"]?.ToString() ?? "",
                    Type = d["type"]?.ToString() ?? "",
                    IsActive = d["is_active"]?.GetValue<bool>() ?? false,
                });
    }

    [RelayCommand]
    public async Task TransferTo(SpotifyDevice? device)
    {
        if (device is null) return;
        await SendAsync(HttpMethod.Put, "me/player", new { device_ids = new[] { device.Id }, play = true });
        await LoadDevices();
        await PollAsync();
    }

    [RelayCommand]
    public async Task LoadQueue()
    {
        var res = await SendAsync(HttpMethod.Get, "me/player/queue");
        Queue.Clear();
        if (res?["queue"] is JsonArray arr)
            foreach (var t in arr.Take(8))
                Queue.Add(new SpotifyQueueItem
                {
                    Title = t!["name"]?.ToString() ?? "",
                    Artist = string.Join(", ", (t["artists"] as JsonArray)?.Select(a => a!["name"]?.ToString()) ?? Array.Empty<string>()),
                    ImageUrl = (t["album"]?["images"] as JsonArray)?.LastOrDefault()?["url"]?.ToString(),
                });
    }
}
