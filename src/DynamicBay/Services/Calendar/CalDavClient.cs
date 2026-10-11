using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace DynamicBay.Services.Calendars;

/// <summary>
/// Minimal CalDAV reader (RFC 4791): discovers the user's calendars and fetches events in a time range.
/// Used for iCloud (caldav.icloud.com, Apple ID + app-specific password) but works with any CalDAV server.
/// </summary>
public sealed class CalDavClient : IDisposable
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace A = "http://apple.com/ns/ical/";
    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly AuthenticationHeaderValue _auth;

    public sealed record RemoteCalendar(Uri Url, string Name, string? Color);

    public CalDavClient(string server, string user, string password)
        // Redirects are followed by hand (SendAsync): HttpClient drops the Authorization header on every redirect,
        // and GMX/web.de redirect the server address (caldav.gmx.net -> /begenda/dav/users/), so sign-in failed there.
        : this(server, user, password, new HttpClientHandler { AllowAutoRedirect = false }) { }

    internal CalDavClient(string server, string user, string password, HttpMessageHandler handler)
    {
        _server = new Uri(server);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        // The password goes on each request to the server itself (see MaySendCredentials), not on every request.
        _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicBay/" + App.Version);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Credentials only over https and only to the server's own host (and hosts below it). Sibling hosts only for
    /// providers known to hand out partitions (caldav.icloud.com -> p42-caldav.icloud.com): for a server like
    /// name.github.io or dav.example.co.uk the parent domain belongs to strangers. An href anywhere else gets none.
    /// </summary>
    internal static bool MaySendCredentials(Uri server, Uri target)
    {
        if (target.Scheme != Uri.UriSchemeHttps || server.Scheme != Uri.UriSchemeHttps) return false;
        string host = target.IdnHost, own = server.IdnHost;
        if (host.Equals(own, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + own, StringComparison.OrdinalIgnoreCase)) return true;
        int dot = own.IndexOf('.');
        string parent = dot < 0 ? "" : own[(dot + 1)..];
        return SiblingDomains.Contains(parent) && host.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> SiblingDomains = new(StringComparer.OrdinalIgnoreCase) { "icloud.com", "me.com", "gmx.net", "gmx.de", "web.de" };

    /// <summary>
    /// iCloud answers 503/429 when it is busy or gets too many requests at once: wait (as long as the server asks,
    /// at most 10 s) and try again, twice.
    /// </summary>
    internal async Task<XDocument> SendAsync(string method, Uri url, string body, int depth)
    {
        if (_server.Scheme != Uri.UriSchemeHttps)
            throw new Core.UserMessageException(Core.Loc.German ? "Der Kalender-Server braucht eine https-Adresse" : "The calendar server needs an https address");
        HttpResponseMessage res;
        int redirects = 0;
        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            if (MaySendCredentials(_server, url)) req.Headers.Authorization = _auth;
            req.Headers.Add("Depth", depth.ToString());
            res = await _http.SendAsync(req);
            if (IsRedirect(res.StatusCode) && res.Headers.Location is { } location && redirects < 5)
            {
                // Same method and body to the new address; the password only goes along if the target may have it.
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                res.Dispose();
                redirects++;
                attempt--;
                continue;
            }
            bool busy = res.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
            if (!busy || attempt >= 2) break;
            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt == 0 ? 2 : 5);
            await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait);
        }
        // The status goes with the exception: ErrorText turns it into a sentence, the log keeps the details.
        if (res.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"{method} {url} -> {(int)res.StatusCode}, busy (retried twice)", null, res.StatusCode);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new Core.UserMessageException(Core.Loc.German ? "Anmeldung abgelehnt, bitte Benutzername und App-Passwort prüfen." : "Sign-in rejected, please check the user name and app password.");
        if ((int)res.StatusCode >= 400) throw new HttpRequestException($"{method} {url} -> {(int)res.StatusCode}", null, res.StatusCode);
        return XDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>The href inside a property (current-user-principal, calendar-home-set); null if the server left it out.</summary>
    internal static string? Href(XDocument doc, XName prop) =>
        doc.Descendants(prop).Descendants(D + "href").FirstOrDefault()?.Value is { Length: > 0 } h ? h.Trim() : null;

    internal static XName PrincipalProp => D + "current-user-principal";
    internal static XName HomeSetProp => C + "calendar-home-set";

    public async Task<List<RemoteCalendar>> DiscoverAsync()
    {
        var principalDoc = await SendAsync("PROPFIND", _server,
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:current-user-principal/></d:prop></d:propfind>", 0);
        var principal = new Uri(_server, Href(principalDoc, PrincipalProp) ?? throw new InvalidOperationException("no principal"));

        var homeDoc = await SendAsync("PROPFIND", principal,
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-home-set/></d:prop></d:propfind>", 0);
        var home = new Uri(principal, Href(homeDoc, HomeSetProp) ?? throw new InvalidOperationException("no calendar home"));

        var listDoc = await SendAsync("PROPFIND", home,
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:a=\"http://apple.com/ns/ical/\">" +
            "<d:prop><d:resourcetype/><d:displayname/><c:supported-calendar-component-set/><a:calendar-color/></d:prop></d:propfind>", 1);
        return ParseCalendars(listDoc, home);
    }

    /// <summary>
    /// The PROPFIND answer on the calendar home: only real calendars with events (not the inbox/outbox, reminders
    /// or the home itself). A calendar without a name is "Kalender".
    /// </summary>
    internal static List<RemoteCalendar> ParseCalendars(XDocument listDoc, Uri home)
    {
        var result = new List<RemoteCalendar>();
        foreach (var r in listDoc.Descendants(D + "response"))
        {
            bool isCalendar = r.Descendants(D + "resourcetype").Descendants(C + "calendar").Any();
            var comps = r.Descendants(C + "comp").Select(c => (string?)c.Attribute("name")).ToList();
            if (!isCalendar || (comps.Count > 0 && !comps.Contains("VEVENT"))) continue;
            var href = r.Element(D + "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href) || !Uri.TryCreate(home, href, out var url)) continue;
            string name = r.Descendants(D + "displayname").FirstOrDefault()?.Value is { Length: > 0 } n ? n : "Kalender";
            string? color = r.Descendants(A + "calendar-color").FirstOrDefault()?.Value is { Length: > 0 } c ? c : null;
            result.Add(new RemoteCalendar(url, name, color));
        }
        return result;
    }

    /// <summary>The iCalendar blobs of a calendar-query REPORT (empty calendar-data, e.g. for a deleted event, is skipped).</summary>
    internal static List<string> ParseCalendarData(XDocument doc) =>
        doc.Descendants(C + "calendar-data").Select(e => e.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

    /// <summary>Raw iCalendar blobs of all events overlapping [from, to).</summary>
    public async Task<List<string>> FetchAsync(Uri calendar, DateTime from, DateTime to)
    {
        string f = from.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"), t = to.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        var doc = await SendAsync("REPORT", calendar,
            "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-data/></d:prop>" +
            $"<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\"><c:time-range start=\"{f}\" end=\"{t}\"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>", 1);
        return ParseCalendarData(doc);
    }
}
