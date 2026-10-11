using System.Net;
using System.Net.Http;
using System.Text;
using DynamicBay.Services.Calendars;

namespace DynamicBay.Tests;

/// <summary>GMX and web.de redirect the server address; the sign-in must survive that (it didn't with auto redirects).</summary>
public class CalDavRedirectTests
{
    private const string Multistatus = "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/begenda/dav/users/</d:href><d:propstat><d:prop>" +
        "<d:current-user-principal><d:href>/begenda/dav/users/me@gmx.de/</d:href></d:current-user-principal></d:prop></d:propstat></d:response></d:multistatus>";

    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly List<(string Method, Uri Url, bool Auth)> Seen = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Seen.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization is not null));
            return Task.FromResult(Answer(request));
        }
    }

    private static HttpResponseMessage Redirect(HttpStatusCode code, string to)
    {
        var res = new HttpResponseMessage(code);
        res.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return res;
    }

    private static HttpResponseMessage Ok(HttpRequestMessage req) => req.Headers.Authorization is null
        ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
        : new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(Multistatus, Encoding.UTF8, "application/xml") };

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task Redirect_on_the_same_server_keeps_the_sign_in(HttpStatusCode code)
    {
        var server = new FakeServer();
        server.Answer = req => req.RequestUri!.AbsolutePath == "/" ? Redirect(code, "https://caldav.gmx.net/begenda/dav/users/") : Ok(req);
        using var client = new CalDavClient("https://caldav.gmx.net/", "me@gmx.de", "secret", server);
        var doc = await client.SendAsync("PROPFIND", new Uri("https://caldav.gmx.net/"), "<x/>", 0);
        Assert.Equal("/begenda/dav/users/me@gmx.de/", CalDavClient.Href(doc, CalDavClient.PrincipalProp));
        Assert.Equal(2, server.Seen.Count);
        Assert.All(server.Seen, s => Assert.Equal("PROPFIND", s.Method));
        Assert.All(server.Seen, s => Assert.True(s.Auth));
    }

    [Fact]
    public async Task Relative_location_is_resolved()
    {
        var server = new FakeServer();
        server.Answer = req => req.RequestUri!.AbsolutePath == "/" ? Redirect(HttpStatusCode.Found, "/begenda/dav/users/") : Ok(req);
        using var client = new CalDavClient("https://caldav.gmx.net/", "me@gmx.de", "secret", server);
        await client.SendAsync("PROPFIND", new Uri("https://caldav.gmx.net/"), "<x/>", 0);
        Assert.Equal("https://caldav.gmx.net/begenda/dav/users/", server.Seen[1].Url.AbsoluteUri);
    }

    [Fact]
    public async Task Redirect_to_a_foreign_host_gets_no_password()
    {
        var server = new FakeServer();
        server.Answer = req => req.RequestUri!.Host == "caldav.gmx.net" ? Redirect(HttpStatusCode.Found, "https://evil.example.com/dav/") : Ok(req);
        using var client = new CalDavClient("https://caldav.gmx.net/", "me@gmx.de", "secret", server);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync("PROPFIND", new Uri("https://caldav.gmx.net/"), "<x/>", 0));
        Assert.False(server.Seen[1].Auth);
    }

    [Fact]
    public async Task Redirect_loops_end()
    {
        var server = new FakeServer();
        server.Answer = req => Redirect(HttpStatusCode.Found, "https://caldav.gmx.net/loop" + Guid.NewGuid().ToString("N")[..4]);
        using var client = new CalDavClient("https://caldav.gmx.net/", "me@gmx.de", "secret", server);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync("PROPFIND", new Uri("https://caldav.gmx.net/"), "<x/>", 0));
        Assert.True(server.Seen.Count <= 6);
    }
}
