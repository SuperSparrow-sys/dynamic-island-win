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

    public sealed record RemoteCalendar(Uri Url, string Name, string? Color);

    public CalDavClient(string server, string user, string password)
    {
        _server = new Uri(server);
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicBay/" + App.Version);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// iCloud answers 503/429 when it is busy or gets too many requests at once: wait (as long as the server asks,
    /// at most 10 s) and try again, twice.
    /// </summary>
    private async Task<XDocument> SendAsync(string method, Uri url, string body, int depth)
    {
        HttpResponseMessage res;
        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            req.Headers.Add("Depth", depth.ToString());
            res = await _http.SendAsync(req);
            bool busy = res.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
            if (!busy || attempt >= 2) break;
            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt == 0 ? 2 : 5);
            await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait);
        }
        if (res.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException(Core.Loc.German ? $"iCloud ist gerade überlastet ({(int)res.StatusCode}), neuer Versuch beim nächsten Abgleich" : $"iCloud is busy right now ({(int)res.StatusCode}), retrying at the next sync");
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException(Core.Loc.German ? "Anmeldung abgelehnt (Apple-ID oder app-spezifisches Passwort falsch)" : "Login rejected (wrong Apple ID or app-specific password)");
        if ((int)res.StatusCode >= 400) throw new HttpRequestException($"{method} {url} -> {(int)res.StatusCode}");
        return XDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static string? Href(XDocument doc, XName prop) =>
        doc.Descendants(prop).Descendants(D + "href").FirstOrDefault()?.Value;

    public async Task<List<RemoteCalendar>> DiscoverAsync()
    {
        var principalDoc = await SendAsync("PROPFIND", _server,
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:current-user-principal/></d:prop></d:propfind>", 0);
        var principal = new Uri(_server, Href(principalDoc, D + "current-user-principal") ?? throw new InvalidOperationException("no principal"));

        var homeDoc = await SendAsync("PROPFIND", principal,
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-home-set/></d:prop></d:propfind>", 0);
        var home = new Uri(principal, Href(homeDoc, C + "calendar-home-set") ?? throw new InvalidOperationException("no calendar home"));

        var listDoc = await SendAsync("PROPFIND", home,
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:a=\"http://apple.com/ns/ical/\">" +
            "<d:prop><d:resourcetype/><d:displayname/><c:supported-calendar-component-set/><a:calendar-color/></d:prop></d:propfind>", 1);

        var result = new List<RemoteCalendar>();
        foreach (var r in listDoc.Descendants(D + "response"))
        {
            bool isCalendar = r.Descendants(D + "resourcetype").Descendants(C + "calendar").Any();
            var comps = r.Descendants(C + "comp").Select(c => (string?)c.Attribute("name")).ToList();
            if (!isCalendar || (comps.Count > 0 && !comps.Contains("VEVENT"))) continue;
            var href = r.Element(D + "href")?.Value;
            if (href is null) continue;
            string name = r.Descendants(D + "displayname").FirstOrDefault()?.Value ?? "Kalender";
            string? color = r.Descendants(A + "calendar-color").FirstOrDefault()?.Value;
            result.Add(new RemoteCalendar(new Uri(home, href), name, color));
        }
        return result;
    }

    /// <summary>Raw iCalendar blobs of all events overlapping [from, to).</summary>
    public async Task<List<string>> FetchAsync(Uri calendar, DateTime from, DateTime to)
    {
        string f = from.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"), t = to.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        var doc = await SendAsync("REPORT", calendar,
            "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-data/></d:prop>" +
            $"<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\"><c:time-range start=\"{f}\" end=\"{t}\"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>", 1);
        return doc.Descendants(C + "calendar-data").Select(e => e.Value).Where(v => v.Length > 0).ToList();
    }
}
