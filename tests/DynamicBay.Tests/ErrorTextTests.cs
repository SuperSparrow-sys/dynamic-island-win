using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using DynamicBay.Core;
using DynamicBay.Services.M365;

namespace DynamicBay.Tests;

/// <summary>Failures become one short, plain sentence in the user's language - never a status code or exception text.</summary>
[Collection("Loc")]
public class ErrorTextTests
{
    private sealed class OAuthFailure(string error) : InvalidOperationException("AADSTS70008: The refresh token has expired"), IOAuthError
    {
        public string? Error { get; } = error;
    }

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { new HttpRequestException("No such host is known. (graph.microsoft.com:443)", new SocketException((int)SocketError.HostNotFound)), "Err.Network" },
        new object[] { new HttpRequestException("Connection refused"), "Err.Network" },
        new object[] { new SocketException((int)SocketError.NetworkUnreachable), "Err.Network" },
        new object[] { new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing.", new TimeoutException()), "Err.Timeout" },
        new object[] { new TimeoutException(), "Err.Timeout" },
        new object[] { new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized), "Err.SignIn" },
        new object[] { new GraphException(HttpStatusCode.Forbidden, "Access is denied. Check credentials and try again."), "Err.SignIn" },
        new object[] { new HttpRequestException("404", null, HttpStatusCode.NotFound), "Err.NotFound" },
        new object[] { new HttpRequestException("429", null, HttpStatusCode.TooManyRequests), "Err.TooMany" },
        new object[] { new GraphException(HttpStatusCode.ServiceUnavailable, "Service unavailable"), "Err.Service" },
        new object[] { new HttpRequestException("500", null, HttpStatusCode.InternalServerError), "Err.Service" },
        new object[] { new HttpRequestException("502", null, HttpStatusCode.BadGateway), "Err.Service" },
        new object[] { new OAuthFailure("invalid_grant"), "Err.SignIn" },
        new object[] { new OAuthFailure("invalid_client"), "Err.ClientId" },
        new object[] { new AggregateException(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable)), "Err.Service" },
        new object[] { new UriFormatException("Invalid URI: The hostname could not be parsed."), "Err.Address" },
        new object[] { new JsonException("'<' is an invalid start of a value."), "Err.BadAnswer" },
        new object[] { new System.Xml.XmlException("Root element is missing."), "Err.BadAnswer" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Maps_failures_to_the_right_sentence(Exception ex, string key) => Assert.Equal(key, ErrorText.KeyFor(ex));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Err.SignIn")]
    [InlineData(HttpStatusCode.Forbidden, "Err.SignIn")]
    [InlineData(HttpStatusCode.NotFound, "Err.NotFound")]
    [InlineData(HttpStatusCode.Gone, "Err.NotFound")]
    [InlineData(HttpStatusCode.RequestTimeout, "Err.Timeout")]
    [InlineData(HttpStatusCode.TooManyRequests, "Err.TooMany")]
    [InlineData(HttpStatusCode.InternalServerError, "Err.Service")]
    [InlineData(HttpStatusCode.GatewayTimeout, "Err.Service")]
    [InlineData(HttpStatusCode.BadRequest, null)]
    [InlineData(HttpStatusCode.Conflict, null)]
    public void Maps_status_codes(HttpStatusCode status, string? key) => Assert.Equal(key, ErrorText.KeyFor(status));

    [Fact]
    public void Unknown_failures_get_the_generic_sentence_or_the_given_one()
    {
        try
        {
            Loc.Init("de");
            Assert.Null(ErrorText.KeyFor(new InvalidOperationException("access_denied")));
            Assert.Equal(Loc.T("Err.Unknown"), ErrorText.For(new InvalidOperationException("access_denied")));
            Assert.Equal("Anmeldung fehlgeschlagen, bitte nochmal versuchen.", ErrorText.For(new InvalidOperationException("zzq"), Loc.T("Err.SignInFailed")));
            Assert.Equal(Loc.T("Err.Unknown"), ErrorText.For(HttpStatusCode.BadRequest));
        }
        finally { Loc.Init("de"); }
    }

    [Fact]
    public void A_ready_user_message_is_shown_as_it_is_even_when_wrapped()
    {
        var inner = new UserMessageException("Adressen in diesem PC oder im lokalen Netz sind gesperrt");
        Assert.Equal(inner.Message, ErrorText.For(new HttpRequestException("An error occurred while sending the request.", inner)));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Sentences_exist_in_both_languages_and_are_short_and_plain(string lang)
    {
        try
        {
            Loc.Init(lang);
            var all = new Exception[]
            {
                new HttpRequestException("zzq"), new TimeoutException(), new HttpRequestException("zzq", null, HttpStatusCode.Unauthorized),
                new HttpRequestException("zzq", null, HttpStatusCode.NotFound), new HttpRequestException("zzq", null, HttpStatusCode.TooManyRequests),
                new HttpRequestException("zzq", null, HttpStatusCode.InternalServerError), new OAuthFailure("invalid_client"),
                new UriFormatException(), new JsonException(), new InvalidOperationException("Something technical: 0x80004005"),
            };
            foreach (var ex in all)
            {
                string text = ErrorText.For(ex);
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.True(text.Length <= 70, text);
                Assert.DoesNotContain("HTTP", text);
                Assert.DoesNotMatch(@"\d", text);            // no status codes
                Assert.DoesNotContain(ex.Message, text);     // never the exception text
                Assert.Equal(1, text.Count(c => c is '.' or '!' or '?')); // one sentence
                Assert.EndsWith(".", text);
            }
            // German and English really differ.
            Assert.NotEqual(Loc.TableForTests["Err.Network"].de, Loc.TableForTests["Err.Network"].en);
            Assert.Equal(lang == "de" ? Loc.TableForTests["Err.TooMany"].de : Loc.TableForTests["Err.TooMany"].en,
                ErrorText.For(HttpStatusCode.TooManyRequests));
        }
        finally { Loc.Init("de"); }
    }
}
