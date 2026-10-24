using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Server;

/// <summary>A shared setting as the server holds it: any JSON, when it changed (Unix ms) and on which device.</summary>
public sealed record SharedValue(JsonNode? Value, long Changed, string From);

/// <summary>The iPhone focus the server knows, with its end (it ends by itself 16 h after it began).</summary>
public sealed record ServerFocus(string Name, long Since, long Until);

public sealed record ServerDevice(string Id, string Name, string Kind, bool Online, long LastSeen);

/// <summary><c>zustand</c>: focus, "Nicht stören" (the newer change wins) and the Anzeigetafel's panel.</summary>
public sealed record ServerState(ServerFocus? Focus, bool QuietOn, long QuietChanged, PanelState? Panel);

/// <summary><c>willkommen</c>: the whole picture after <c>hallo</c>.</summary>
public sealed record ServerWelcome(string Version, Dictionary<string, SharedValue> Shared, Dictionary<string, SharedValue> Overrides,
    ServerState State, List<ServerDevice> Devices, string IphoneAddress, string IphoneKey);

/// <summary>
/// The DynamicBay server (Rubus project dynamicbay-server; contract in Fixtures/Api/Server): addresses, pairing,
/// the WebSocket messages and the shared settings. Tolerant like every external JSON here. No network, no WPF.
/// </summary>
public static class ServerProtocol
{
    public const int Version = 1;
    public const int MaxMessage = 256 * 1024;

    /// <summary>Close codes of the server: the key is gone (removed, paired again), the first message was not
    /// <c>hallo</c>, the same device connected again elsewhere.</summary>
    public const int CloseUnpaired = 4401, CloseProtocol = 4400, CloseReplaced = 4409;

    /// <summary>The server's base address for what the user typed ("homeserver.x.ts.net:20009" is enough).</summary>
    public static Uri? Base(string address)
    {
        string a = address.Trim().TrimEnd('/');
        if (a.Length == 0) return null;
        if (!a.Contains("://", StringComparison.Ordinal)) a = "https://" + a;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        return new Uri(u.GetLeftPart(UriPartial.Authority));
    }

    public static Uri PairUri(Uri b) => new(b, "/api/v1/koppeln");
    public static Uri SocketUri(Uri b) => new UriBuilder(b) { Scheme = b.Scheme == "https" ? "wss" : "ws", Path = "/api/v1/verbindung" }.Uri;

    /// <summary>Six digits, spaces and dashes typed along are fine.</summary>
    public static string? CleanCode(string typed)
    {
        var digits = new string(typed.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 6 ? digits : null;
    }

    public static JsonObject PairRequest(string code, string device, string name, string kind, string version) => new()
    {
        ["v"] = Version, ["code"] = code, ["geraet"] = device, ["name"] = name, ["art"] = kind, ["version"] = version,
    };

    /// <summary>The device key from the answer to <c>POST /api/v1/koppeln</c>, or null.</summary>
    public static string? PairKey(string json) =>
        Obj(json) is { } o && Text(o, "geraete_schluessel") is { Length: > 0 } key ? key : null;

    public static JsonObject Hello(string device, string name, string kind, string version, IEnumerable<(string Key, SharedValue Value)> open)
    {
        var list = new JsonArray();
        foreach (var (key, v) in open)
            list.Add(new JsonObject { ["schluessel"] = key, ["wert"] = v.Value?.DeepClone(), ["geaendert"] = v.Changed, ["nur_hier"] = false });
        return new JsonObject
        {
            ["art"] = "hallo", ["v"] = Version, ["geraet"] = device, ["name"] = name, ["art_geraet"] = kind, ["version"] = version, ["offen"] = list,
        };
    }

    public static JsonObject Setting(string key, JsonNode? value, long changed, bool onlyHere = false, bool remove = false)
    {
        var o = new JsonObject { ["art"] = "einstellung", ["schluessel"] = key };
        if (!remove) o["wert"] = value?.DeepClone();
        o["geaendert"] = changed;
        o["nur_hier"] = onlyHere;
        if (remove) o["entfernen"] = true;
        return o;
    }

    public static JsonObject Quiet(bool on, long changed) => new() { ["art"] = "nicht_stoeren", ["an"] = on, ["geaendert"] = changed };
    public static JsonObject EndFocus(string name) => new() { ["art"] = "fokus_beenden", ["name"] = name };
    public static JsonObject RequestCode() => new() { ["art"] = "code_anfordern" };
    public static JsonObject RemoveDevice(string id) => new() { ["art"] = "geraet_entfernen", ["geraet"] = id };

    /// <summary>A widget command for the Anzeigetafel: the server adds who sent it and why (<c>grund</c> "Insel").</summary>
    public static JsonObject Panel(JsonObject command)
    {
        var o = new JsonObject { ["art"] = "panel" };
        foreach (var (k, v) in command)
            if (k is not ("quelle" or "grund")) o[k] = v?.DeepClone();
        return o;
    }

    // ---- reading ----

    public static JsonObject? Obj(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; } catch (JsonException) { return null; }
    }

    public static string Kind(JsonObject message) => Text(message, "art") ?? "";

    public static ServerWelcome? Welcome(JsonObject o)
    {
        if (Kind(o) != "willkommen") return null;
        var devices = new List<ServerDevice>();
        if (o["geraete"] is JsonArray list) devices.AddRange(Devices(list));
        var iphone = o["iphone"] as JsonObject;
        return new ServerWelcome(Text(o, "server_version") ?? "", Values(o["einstellungen"] as JsonObject), Values(o["ueberschreibungen"] as JsonObject),
            State(o["zustand"] as JsonObject), devices, Text(iphone, "adresse") ?? "", Text(iphone, "schluessel") ?? "");
    }

    public static IEnumerable<ServerDevice> Devices(JsonArray list) =>
        list.OfType<JsonObject>().Where(d => Text(d, "geraet") is { Length: > 0 })
            .Select(d => new ServerDevice(Text(d, "geraet")!, Text(d, "name") ?? "", Text(d, "art") ?? "", Bool(d, "online") ?? false, Long(d, "zuletzt") ?? 0));

    /// <summary>The state from <c>zustand</c> or from <c>willkommen.zustand</c>; missing parts mean "none".</summary>
    public static ServerState State(JsonObject? o)
    {
        var f = o?["fokus"] as JsonObject;
        ServerFocus? focus = Text(f, "name") is { Length: > 0 } name ? new ServerFocus(PhoneFocusProtocol.Clean(name), Long(f!, "seit") ?? 0, Long(f!, "bis") ?? 0) : null;
        var q = o?["nicht_stoeren"] as JsonObject;
        var panel = o?["panel"] is JsonObject p ? PanelProtocol.Parse(p.ToJsonString()) : null;
        return new ServerState(focus, q is not null && (Bool(q, "an") ?? false), q is null ? 0 : Long(q, "geaendert") ?? 0, panel);
    }

    /// <summary>One <c>einstellung</c>: key, value, time, only for this device, removed.</summary>
    public static (string Key, SharedValue Value, bool OnlyHere, bool Remove)? SettingOf(JsonObject o)
    {
        if (Text(o, "schluessel") is not { Length: > 0 } key) return null;
        return (key, new SharedValue(o["wert"]?.DeepClone(), Long(o, "geaendert") ?? 0, Text(o, "von") ?? ""), Bool(o, "nur_hier") ?? false, Bool(o, "entfernen") ?? false);
    }

    /// <summary><c>code</c>: the six digits for the next device and until when (Unix seconds).</summary>
    public static (string Code, long Until)? CodeOf(JsonObject o) =>
        Text(o, "code") is { Length: > 0 } c ? (c, Long(o, "bis") ?? 0) : null;

    /// <summary><c>fehler</c> and <c>panel_fehler</c>: the server's sentence (shown as is, it is German like the server).</summary>
    public static string ErrorOf(JsonObject o) => Text(o, "text") ?? Text(o, "fehler") ?? "";

    private static Dictionary<string, SharedValue> Values(JsonObject? o)
    {
        var map = new Dictionary<string, SharedValue>(StringComparer.Ordinal);
        if (o is null) return map;
        foreach (var (k, v) in o)
            if (v is JsonObject e) map[k] = new SharedValue(e["wert"]?.DeepClone(), Long(e, "geaendert") ?? 0, Text(e, "von") ?? "");
        return map;
    }

    private static string? Text(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    private static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
    private static long? Long(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out double d)) return (long)d;
        return null;
    }
}

/// <summary>
/// Which settings are shared through the server, and how a value travels (owner's list of 24.10.2026: focus
/// profiles, muted apps, and what each focus does to the panel; per device stay placement, look, audio …).
/// </summary>
public static class SharedSettings
{
    public const string FocusProfiles = "fokus.profile", MutedApps = "stumme_apps", PanelFocus = "anzeigetafel.fokus";
    public static readonly string[] Keys = { FocusProfiles, MutedApps, PanelFocus };

    /// <summary>The value of a key as it is here now.</summary>
    public static JsonNode? Read(AppSettings s, string key) => key switch
    {
        FocusProfiles => JsonSerializer.SerializeToNode(s.FocusProfiles.ToList()),
        MutedApps => JsonSerializer.SerializeToNode(s.MutedApps.ToList()),
        PanelFocus => PanelFocusOf(s.FocusProfiles),
        _ => null,
    };

    /// <summary>
    /// Takes a value from the server; false when it does not fit (an older server or a wrong shape: nothing changes).
    /// <see cref="PanelFocus"/> is derived from the profiles and never taken back.
    /// </summary>
    public static bool Write(AppSettings s, string key, JsonNode? value)
    {
        try
        {
            switch (key)
            {
                case FocusProfiles when value is JsonArray:
                    var profiles = value.Deserialize<List<FocusProfile>>() ?? new();
                    var clean = profiles.Where(p => PhoneFocusProtocol.Clean(p.Name).Length > 0)
                        .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                        .Take(PhoneFocusProtocol.MaxProfiles).ToList();
                    s.FocusProfiles.Clear();
                    foreach (var p in clean) s.FocusProfiles.Add(p);
                    s.FocusProfilesChanged();
                    return true;
                case MutedApps when value is JsonArray:
                    var apps = value.Deserialize<List<string>>() ?? new();
                    s.MutedApps.Clear();
                    foreach (var a in apps.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) s.MutedApps.Add(a);
                    s.SaveSoon();
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException) { return false; }
    }

    /// <summary><c>{"Arbeit": {"panel": "dimmen", "prozent": 15, "am_ende": "plan"}, …}</c>: what the server sends the Anzeigetafel per focus.</summary>
    public static JsonObject PanelFocusOf(IEnumerable<FocusProfile> profiles)
    {
        var o = new JsonObject();
        foreach (var p in profiles)
            o[p.Name] = new JsonObject
            {
                ["panel"] = p.Panel is "dimmen" or "aus" or "plan" ? p.Panel : "nichts", ["prozent"] = Math.Clamp(p.PanelPercent, 5, 100),
                ["am_ende"] = p.PanelEnd == "aus" ? "aus" : "plan",
            };
        return o;
    }
}
