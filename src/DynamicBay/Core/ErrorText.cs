using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml;

namespace DynamicBay.Core;

/// <summary>
/// One short, plain sentence (DE/EN) for a failure the user sees: no status codes, no exception messages.
/// The details belong in the log (<see cref="Log.Error"/>), never in the UI.
/// </summary>
public static class ErrorText
{
    /// <summary>The sentence for <paramref name="ex"/>; <paramref name="unknown"/> replaces the generic one when nothing fits.</summary>
    public static string For(Exception ex, string? unknown = null)
    {
        if (Find<UserMessageException>(ex) is { } plain) return plain.Message;
        return KeyFor(ex) is { } key ? Loc.T(key) : unknown ?? Loc.T("Err.Unknown");
    }

    /// <summary>The sentence for an HTTP status a service answered with.</summary>
    public static string For(HttpStatusCode status) => Loc.T(KeyFor(status) ?? "Err.Unknown");

    /// <summary>Loc key for an exception, null if nothing specific fits (then the generic sentence is used).</summary>
    internal static string? KeyFor(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0) return KeyFor(agg.InnerExceptions[0]);
        // OAuth: the sign-in is gone (revoked, expired) or the own app registration is wrong.
        if (Find<IOAuthError>(ex)?.Error is { } oauth)
        {
            if (oauth is "invalid_grant" or "invalid_token" or "interaction_required" or "consent_required") return "Err.SignIn";
            if (oauth is "invalid_client" or "unauthorized_client") return "Err.ClientId";
        }
        if (StatusOf(ex) is { } status && KeyFor(status) is { } byStatus) return byStatus;
        // HttpClient reports its own timeout as a cancellation (inner TimeoutException).
        if (Find<TimeoutException>(ex) is not null || Find<OperationCanceledException>(ex) is not null) return "Err.Timeout";
        if (Find<SocketException>(ex) is not null || Find<WebException>(ex) is not null) return "Err.Network";
        if (Find<HttpRequestException>(ex) is { StatusCode: null }) return "Err.Network";
        if (Find<UriFormatException>(ex) is not null) return "Err.Address";
        if (Find<JsonException>(ex) is not null || Find<XmlException>(ex) is not null) return "Err.BadAnswer";
        return null;
    }

    internal static string? KeyFor(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => "Err.SignIn",
        404 or 410 => "Err.NotFound",
        408 => "Err.Timeout",
        429 => "Err.TooMany",
        >= 500 and <= 599 => "Err.Service",
        _ => null,
    };

    /// <summary>The HTTP status carried by the exception (or one inside it), if any.</summary>
    private static HttpStatusCode? StatusOf(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: { } s }) return s;
            if (e is IHttpStatusError h) return h.Status;
        }
        return null;
    }

    private static T? Find<T>(Exception ex) where T : class
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is T t) return t;
        return null;
    }
}

/// <summary>A failure whose message is already a short, localized sentence for the user (shown as it is).</summary>
public sealed class UserMessageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A service answered with an HTTP error status (Graph, token endpoints).</summary>
public interface IHttpStatusError
{
    HttpStatusCode Status { get; }
}

/// <summary>An OAuth token endpoint refused; <see cref="Error"/> is its error code ("invalid_grant", ...).</summary>
public interface IOAuthError
{
    string? Error { get; }
}
