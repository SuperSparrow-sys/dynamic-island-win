using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DynamicBay.Services;

public sealed record MeetingLink(string Service, string Url);

/// <summary>
/// Finds the Teams or Zoom meeting in a calendar event (location, description or the provider's conference data)
/// and opens it in the meeting app.
/// </summary>
public static partial class MeetingLinks
{
    [GeneratedRegex(@"https://teams\.(microsoft|live)\.com/(l/meetup-join|meet)/[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex TeamsRx();

    [GeneratedRegex(@"https://([\w-]+\.)?zoom\.us/(j|my|w|s)/[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex ZoomRx();

    /// <summary>The first Teams or Zoom link in the given texts (most specific first: conference data, location, description).</summary>
    public static MeetingLink? Find(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            // Descriptions come as HTML or with escaped characters in some calendars.
            var t = System.Net.WebUtility.HtmlDecode(text).Replace("\\n", " ").Replace("\\,", ",");
            if (TeamsRx().Match(t) is { Success: true } teams) return new("Teams", Clean(teams.Value));
            if (ZoomRx().Match(t) is { Success: true } zoom) return new("Zoom", Clean(zoom.Value));
        }
        return null;
    }

    /// <summary>Trailing punctuation from the surrounding text is not part of the link.</summary>
    private static string Clean(string url) => url.TrimEnd('.', ',', ';', ')', ']', '>', '\\');

    /// <summary>Opens the meeting: Teams links go straight to the Teams app (msteams:), Zoom links to Zoom's launcher.</summary>
    public static void Open(MeetingLink link)
    {
        string target = link.Url;
        if (link.Service == "Teams" && link.Url.StartsWith("https://teams.microsoft.com/", StringComparison.OrdinalIgnoreCase))
            target = "msteams:" + link.Url["https://teams.microsoft.com".Length..];
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch
        {
            // msteams: not registered (Teams not installed): the browser offers to open or join on the web.
            try { Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true }); } catch { }
        }
    }
}
