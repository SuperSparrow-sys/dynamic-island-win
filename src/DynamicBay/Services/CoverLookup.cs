using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Finds a cover for a song when the player sends none (Apple Music for Windows, some browsers): the public iTunes
/// Search API, no account. Only title and artist go out. Results are cached for the session.
/// </summary>
public static class CoverLookup
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, byte[]?> Cache = new();
    private const int Size = 600;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DynamicBay/{App.Version}");
        return http;
    }

    public static string SearchUrl(string title, string artist)
    {
        string country = "us";
        try { country = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant(); } catch { }
        string term = Uri.EscapeDataString($"{MainArtist(artist)} {Clean(title)}".Trim());
        return $"https://itunes.apple.com/search?term={term}&entity=song&limit=10&country={country}";
    }

    /// <summary>The best matching artwork URL in <see cref="Size"/> px, or null. Tolerant of missing fields.</summary>
    internal static string? PickArtwork(string json, string title, string artist)
    {
        JsonArray? results;
        try { results = JsonNode.Parse(json)?["results"] as JsonArray; }
        catch (System.Text.Json.JsonException) { return null; }
        if (results is null) return null;
        string wantTitle = Norm(Clean(title)), wantArtist = Norm(MainArtist(artist));
        if (wantTitle.Length == 0) return null;
        string? best = null;
        int bestScore = 0;
        foreach (var r in results)
        {
            if (r is not JsonObject o || Str(o, "artworkUrl100") is not { Length: > 0 } art) continue;
            string t = Norm(Clean(Str(o, "trackName") ?? ""));
            string a = Norm(Str(o, "artistName") ?? "");
            int score = 0;
            if (t == wantTitle) score += 4;
            else if (t.Length > 0 && (t.Contains(wantTitle) || wantTitle.Contains(t))) score += 2;
            if (wantArtist.Length > 0 && a.Length > 0 && (a.Contains(wantArtist) || wantArtist.Contains(a))) score += 3;
            if (score > bestScore) { bestScore = score; best = art; }
        }
        // Title and artist must both fit: a wrong cover is worse than none
        if (best is null || bestScore < 5) return null;
        return best.Replace("100x100bb", $"{Size}x{Size}bb", StringComparison.Ordinal);
    }

    public static async Task<byte[]?> FetchAsync(string title, string artist)
    {
        if (title.Length == 0) return null;
        string key = $"{title}|{artist}";
        lock (Cache) if (Cache.TryGetValue(key, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            string json = await Http.GetStringAsync(SearchUrl(title, artist));
            if (PickArtwork(json, title, artist) is { } url) bytes = await Http.GetByteArrayAsync(url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"Cover lookup failed: {ex.Message}");
            return null; // not cached: tried again with the next track
        }
        lock (Cache)
        {
            if (Cache.Count > 200) Cache.Clear();
            Cache[key] = bytes;
        }
        return bytes;
    }

    /// <summary>"Song (Radio Edit)" or "Song - Remastered 2011" → "Song": players and the store spell versions differently.</summary>
    internal static string Clean(string title)
    {
        int cut = title.Length;
        foreach (var mark in new[] { " (", " [", " - " })
        {
            int i = title.IndexOf(mark, StringComparison.Ordinal);
            if (i > 0 && i < cut) cut = i;
        }
        return title[..cut].Trim();
    }

    /// <summary>Apple Music for Windows names the artist as "Artist — Album": the store knows only the artist.</summary>
    internal static string MainArtist(string artist)
    {
        int i = artist.IndexOf(" — ", StringComparison.Ordinal);
        return (i > 0 ? artist[..i] : artist).Trim();
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
