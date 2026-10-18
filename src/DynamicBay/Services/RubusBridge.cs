using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicBay.Services;

public enum RubusZustand { Unbekannt, Arbeitet, Wartet, Bereit }

/// <summary>
/// A Claude session Rubus reports (it may run on another host). Grund: freigabe, erlaubnis or eingabe while it waits,
/// fehler or unterbrochen (Esc) when it is ready, else null. Seit: start of the state, Aktualisiert: Claude's last event.
/// </summary>
public sealed record RubusSitzung(string Id, string Titel, string Host, string Projekt, RubusZustand Zustand, string? Grund, string Text,
    DateTime? Seit, DateTime? Aktualisiert = null);

/// <summary>An open approval in Rubus. NurImFenster: it can only be granted in the Rubus window (prod needs the password).</summary>
public sealed record RubusAnfrage(string Id, string Sitzung, string Titel, string Projekt, string Host, bool NurImFenster);

/// <summary>The whole state of one channel; every "stand" replaces the previous one.</summary>
public sealed record RubusStand(IReadOnlyList<RubusSitzung> Sitzungen, IReadOnlyList<RubusAnfrage> Anfragen)
{
    public static readonly RubusStand Leer = new(Array.Empty<RubusSitzung>(), Array.Empty<RubusAnfrage>());
}

public enum RubusArt { Stand, Abholen, Ende, Unbekannt }

public sealed record RubusNachricht(string Kanal, RubusArt Art, RubusStand? Stand);

/// <summary>A click for Rubus: erlauben, ablehnen, oeffnen (with Anfrage) or sitzung_oeffnen (with Sitzung).</summary>
public sealed record RubusAktion(string Art, string? Anfrage = null, string? Sitzung = null);

/// <summary>What changed between two states of a channel; the app turns these into peeks.</summary>
public abstract record RubusEreignis(string Kanal);
public sealed record RubusNeueAnfrage(string Kanal, RubusAnfrage Anfrage) : RubusEreignis(Kanal);
public sealed record RubusAnfrageWeg(string Kanal, string Anfrage) : RubusEreignis(Kanal);
public sealed record RubusBrauchtDich(string Kanal, RubusSitzung Sitzung) : RubusEreignis(Kanal);
public sealed record RubusFertig(string Kanal, RubusSitzung Sitzung) : RubusEreignis(Kanal);
public sealed record RubusFehler(string Kanal, RubusSitzung Sitzung) : RubusEreignis(Kanal);

/// <summary>
/// Protocol version 1 between the Rubus client and DynamicBay (POST /rubus/, see docs/INTEGRATIONS.md#rubus):
/// parsing (tolerant, unknown fields are skipped) and the transitions between two states. No WPF, no network.
/// </summary>
public static class RubusProtocol
{
    public const int MaxBody = 64 * 1024;
    public const int MaxSitzungen = 50, MaxAnfragen = 50;
    public static readonly TimeSpan Ablauf = TimeSpan.FromSeconds(90);

    private static readonly Regex KanalPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    internal static bool IsKanal(string? s) => s is not null && KanalPattern.IsMatch(s);
    internal static bool IsId(string? s) => s is not null && IdPattern.IsMatch(s);

    /// <summary>The message, or null when it is not version 1 or has no valid channel.</summary>
    public static RubusNachricht? Parse(JsonNode? json)
    {
        if (json is not JsonObject o) return null;
        if (Number(o["v"]) != 1) return null;
        string kanal = Text(o["kanal"]);
        if (!IsKanal(kanal)) return null;
        kanal = kanal.ToLowerInvariant();
        var art = Text(o["art"]) switch
        {
            "stand" => RubusArt.Stand,
            "abholen" => RubusArt.Abholen,
            "ende" => RubusArt.Ende,
            _ => RubusArt.Unbekannt,
        };
        if (art != RubusArt.Stand) return new(kanal, art, null);

        var sitzungen = new List<RubusSitzung>();
        foreach (var n in Items(o["sitzungen"]))
        {
            if (sitzungen.Count >= MaxSitzungen) break;
            string id = Text(n["id"]);
            if (!IsId(id) || sitzungen.Any(x => x.Id == id)) continue;
            var zustand = Text(n["zustand"]) switch
            {
                "arbeitet" => RubusZustand.Arbeitet,
                "wartet" => RubusZustand.Wartet,
                "bereit" => RubusZustand.Bereit,
                _ => RubusZustand.Unbekannt,
            };
            string g = Text(n["grund"]);
            string? grund = g is "freigabe" or "erlaubnis" or "eingabe" or "fehler" or "unterbrochen" ? g : null;
            sitzungen.Add(new RubusSitzung(id, Clip(Text(n["titel"]), 200), Clip(Text(n["host"]), 64), Clip(Text(n["projekt"]), 100),
                zustand, grund, Clip(Text(n["text"]), 300), Time(n["seit"]), Time(n["aktualisiert"])));
        }
        var anfragen = new List<RubusAnfrage>();
        foreach (var n in Items(o["anfragen"]))
        {
            if (anfragen.Count >= MaxAnfragen) break;
            string id = Text(n["id"]);
            if (!IsId(id) || anfragen.Any(x => x.Id == id)) continue;
            anfragen.Add(new RubusAnfrage(id, Text(n["sitzung"]) is var s && IsId(s) ? s : "", Clip(Text(n["titel"]), 200),
                Clip(Text(n["projekt"]), 100), Clip(Text(n["host"]), 64), n["nur_im_fenster"] is JsonValue v && v.TryGetValue<bool>(out var b) && b));
        }
        return new(kanal, art, new RubusStand(sitzungen, anfragen));
    }

    /// <summary>
    /// Peeks come from the transitions: a new request, a session that starts waiting for permission or input, working
    /// to ready (not when interrupted with Esc), and a known session that stops with an error.
    /// </summary>
    public static List<RubusEreignis> Between(string kanal, RubusStand vorher, RubusStand nachher)
    {
        var list = new List<RubusEreignis>();
        foreach (var a in nachher.Anfragen)
            if (!vorher.Anfragen.Any(x => x.Id == a.Id)) list.Add(new RubusNeueAnfrage(kanal, a));
        foreach (var a in vorher.Anfragen)
            if (!nachher.Anfragen.Any(x => x.Id == a.Id)) list.Add(new RubusAnfrageWeg(kanal, a.Id));
        foreach (var s in nachher.Sitzungen)
        {
            var alt = vorher.Sitzungen.FirstOrDefault(x => x.Id == s.Id);
            // "freigabe" has its own question peek (the request). A new start (Seit) is a new question of the same
            // kind, e.g. the next answer that ends with a question.
            if (s.Zustand == RubusZustand.Wartet && s.Grund is "erlaubnis" or "eingabe"
                && !(alt is { Zustand: RubusZustand.Wartet } && alt.Grund == s.Grund && alt.Seit == s.Seit))
                list.Add(new RubusBrauchtDich(kanal, s));
            if (alt?.Zustand == RubusZustand.Arbeitet && s.Zustand == RubusZustand.Bereit && s.Grund is null) list.Add(new RubusFertig(kanal, s));
            // Only for sessions seen before: an old error is not news when a channel (re)appears.
            if (alt is not null && s.Zustand == RubusZustand.Bereit && s.Grund == "fehler" && !(alt is { Zustand: RubusZustand.Bereit, Grund: "fehler" }))
                list.Add(new RubusFehler(kanal, s));
        }
        return list;
    }

    public static string ActionsJson(IEnumerable<RubusAktion> aktionen) => new JsonObject
    {
        ["aktionen"] = new JsonArray(aktionen.Select(a =>
        {
            var o = new JsonObject { ["art"] = a.Art };
            if (a.Anfrage is not null) o["anfrage"] = a.Anfrage;
            if (a.Sitzung is not null) o["sitzung"] = a.Sitzung;
            return (JsonNode)o;
        }).ToArray()),
    }.ToJsonString();

    private static IEnumerable<JsonObject> Items(JsonNode? n) => n is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

    private static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    // Parsed values convert to any number type, built ones (tests, JsonObject literals) only to their own.
    private static double? Number(JsonNode? n) =>
        n is not JsonValue v ? null : v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? l : null;

    /// <summary>Unix seconds; 0, missing or implausible is unknown.</summary>
    private static DateTime? Time(JsonNode? n) =>
        Number(n) is > 0 and < 32503680000 and var t ? DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime : null;

    private static string Clip(string s, int max)
    {
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }
}

/// <summary>
/// The channels Rubus reports on, with their last state and the clicks waiting for pickup. Thread-safe: the listener
/// calls <see cref="Handle"/>, the UI enqueues clicks and expires silent channels.
/// </summary>
public sealed class RubusBridge
{
    public const int MaxKanaele = 8, MaxAktionen = 32;

    private sealed class Kanal
    {
        public RubusStand Stand = RubusStand.Leer;
        public DateTime Zuletzt;
        public readonly List<RubusAktion> Aktionen = new();
        public readonly HashSet<string> Beantwortet = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Kanal> _kanaele = new();

    /// <summary>Answer status and body (200 with the collected clicks, else 204) and what changed.</summary>
    public (int Status, string? Body, List<RubusEreignis> Ereignisse) Handle(RubusNachricht msg, DateTime now)
    {
        lock (_lock)
        {
            var ereignisse = new List<RubusEreignis>();
            _kanaele.TryGetValue(msg.Kanal, out var k);
            switch (msg.Art)
            {
                case RubusArt.Ende:
                    if (k is not null) ereignisse.AddRange(Remove(msg.Kanal));
                    return (204, null, ereignisse);
                case RubusArt.Stand when msg.Stand is not null:
                    if (k is null)
                    {
                        if (_kanaele.Count >= MaxKanaele) return (429, null, ereignisse);
                        k = _kanaele[msg.Kanal] = new Kanal();
                    }
                    ereignisse.AddRange(RubusProtocol.Between(msg.Kanal, k.Stand, msg.Stand));
                    k.Stand = msg.Stand;
                    // Answered requests that left the state are done.
                    k.Beantwortet.RemoveWhere(id => !msg.Stand.Anfragen.Any(a => a.Id == id));
                    break;
                case RubusArt.Unbekannt:
                    if (k is not null) k.Zuletzt = now;
                    return (204, null, ereignisse);
            }
            if (k is null) return (204, null, ereignisse);
            k.Zuletzt = now;
            if (k.Aktionen.Count == 0) return (204, null, ereignisse);
            string body = RubusProtocol.ActionsJson(k.Aktionen);
            k.Aktionen.Clear();
            return (200, body, ereignisse);
        }
    }

    /// <summary>Channels nothing came from for 90 s disappear with their sessions and requests.</summary>
    public List<RubusEreignis> Expire(DateTime now)
    {
        lock (_lock)
        {
            var list = new List<RubusEreignis>();
            foreach (var kanal in _kanaele.Where(kv => now - kv.Value.Zuletzt >= RubusProtocol.Ablauf).Select(kv => kv.Key).ToList())
                list.AddRange(Remove(kanal));
            return list;
        }
    }

    /// <summary>Everything goes (the switch was turned off).</summary>
    public List<RubusEreignis> Clear()
    {
        lock (_lock)
        {
            var list = new List<RubusEreignis>();
            foreach (var kanal in _kanaele.Keys.ToList()) list.AddRange(Remove(kanal));
            return list;
        }
    }

    /// <summary>
    /// Queues a click for the channel that reported the request or session. A request is answered once; unknown
    /// requests and sessions are refused. False when nothing was queued.
    /// </summary>
    public bool Enqueue(string kanal, RubusAktion aktion)
    {
        lock (_lock)
        {
            if (!_kanaele.TryGetValue(kanal, out var k) || k.Aktionen.Count >= MaxAktionen) return false;
            if (aktion.Art == "sitzung_oeffnen")
            {
                if (!k.Stand.Sitzungen.Any(s => s.Id == aktion.Sitzung)) return false;
            }
            else if (aktion.Art is "erlauben" or "ablehnen" or "oeffnen")
            {
                if (aktion.Anfrage is null || !k.Stand.Anfragen.Any(a => a.Id == aktion.Anfrage) || !k.Beantwortet.Add(aktion.Anfrage)) return false;
            }
            else return false;
            k.Aktionen.Add(aktion);
            return true;
        }
    }

    public List<(string Kanal, RubusStand Stand)> Snapshot()
    {
        lock (_lock) return _kanaele.Select(kv => (kv.Key, kv.Value.Stand)).ToList();
    }

    private List<RubusEreignis> Remove(string kanal)
    {
        var k = _kanaele[kanal];
        _kanaele.Remove(kanal);
        return k.Stand.Anfragen.Select(a => (RubusEreignis)new RubusAnfrageWeg(kanal, a.Id)).ToList();
    }
}

/// <summary>Which requests the loopback listener refuses, and with what (pure, so it can be tested without a socket).</summary>
public static class ListenerGate
{
    public const int MaxHookBody = 1024 * 1024; // PreToolUse carries the tool input (a whole file for Write)

    public enum Route { None, Claude, Rubus }

    public static Route RouteOf(string path) =>
        path.StartsWith("/rubus/", StringComparison.OrdinalIgnoreCase) ? Route.Rubus
        : path.StartsWith("/claude/", StringComparison.OrdinalIgnoreCase) ? Route.Claude : Route.None;

    public static int MaxBody(Route route) => route == Route.Rubus ? RubusProtocol.MaxBody : MaxHookBody;

    /// <summary>
    /// Null when the request may be read, else the status: 404 for a path that is switched off, 403 when a browser
    /// sent it (Origin or Sec-Fetch-Site), 405 for anything but POST, 413 when the announced body is too large.
    /// </summary>
    public static int? Refuse(Route route, string method, bool hasOrigin, bool hasFetchSite, long contentLength, bool hooksOn, bool rubusOn)
    {
        if (route == Route.None || (route == Route.Claude && !hooksOn) || (route == Route.Rubus && !rubusOn)) return 404;
        if (hasOrigin || hasFetchSite) return 403;
        if (method != "POST") return 405;
        if (contentLength > MaxBody(route)) return 413;
        return null;
    }
}
