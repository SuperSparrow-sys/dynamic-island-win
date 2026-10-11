using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>A peek with two buttons as it goes to the main device. Color: "#RRGGBB" of the symbol (optional).</summary>
public sealed record LinkQuestion(string Title, string Subtitle, string? IconKey, string? Color, string ActionText, string DismissText,
    double Seconds, bool Important, bool ShowInDnd);

/// <summary>A question another device handed over; answer it with <see cref="LinkQuestionFeature.AnswerAsync"/>.</summary>
public sealed record LinkQuestionRequest(string PeerId, string PeerName, string Id, LinkQuestion Question);

/// <summary>How a handed-over question ended. Lost: the main device went away before it was answered.</summary>
public enum LinkAnswer { Yes, No, Timeout, Lost }

/// <summary>
/// Fragen (Einstellungen → Geräte): on the quiet device a peek with buttons (microphone on - mute?, join the meeting,
/// incoming call …) goes to the main device. Its buttons work there; the answer comes back and runs here, where the
/// question came from. Unanswered in time: a timeout, handled here like a dismissed peek. If the main device goes away
/// first, the question shows here after all. Answers always go back, also when the switch went off meanwhile.
/// </summary>
public sealed class LinkQuestionFeature : LinkFeature
{
    public const string Type = "ask", AnswerType = "ask.answer";
    /// <summary>How long after its own time a question waits here for the answer (the main device may have shown it late).</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    internal const int MaxOpen = 16;

    private sealed record Pending(string PeerId, Action<LinkAnswer> Done, DateTime Deadline);
    private readonly Dictionary<string, Pending> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PeerId, DateTime Deadline)> _shown = new(StringComparer.Ordinal);
    private readonly IDisposable _answers;

    /// <summary>The main device's switch: only then are questions handed over.</summary>
    public LinkPeerSwitch Peer { get; }

    /// <summary>Another device asks (show it with working buttons).</summary>
    public event Action<LinkQuestionRequest>? Asked;
    /// <summary>A question shown here ran out or its device went away: take the peek away (id).</summary>
    public event Action<string>? Expired;

    public LinkQuestionFeature(LinkService link, AppSettings settings) : base(link, settings)
    {
        Peer = new LinkPeerSwitch(link, Type, () => Enabled);
        Handle(Type, OnAsk);
        // Answers always count: a question that went out must end here, whatever the switch says now.
        _answers = link.RegisterHandler(AnswerType, OnAnswer);
        link.PeerOffline += OnPeerOffline;
    }

    public override bool Enabled => Settings.LinkShareQuestions;

    /// <summary>Questions can go to this device now (switch on here and there, connected).</summary>
    public bool CanAsk(string peerId) => Enabled && Link.IsOnline(peerId) && Peer.IsOn(peerId);

    /// <summary>
    /// Hands a question to <paramref name="peerId"/>. <paramref name="done"/> runs exactly once, with the answer, a
    /// timeout or "lost". False: not sent (then <paramref name="done"/> never runs; show the question here).
    /// </summary>
    public async Task<bool> AskAsync(string peerId, LinkQuestion q, Action<LinkAnswer> done, DateTime? now = null)
    {
        if (!CanAsk(peerId)) return false;
        string id = Guid.NewGuid().ToString("N")[..16];
        var deadline = (now ?? DateTime.UtcNow) + TimeSpan.FromSeconds(Math.Clamp(q.Seconds, 1, 900)) + Grace;
        lock (_asked)
        {
            if (_asked.Count >= MaxOpen) return false;
            _asked[id] = new Pending(peerId, done, deadline);
        }
        bool sent = await SendAsync(peerId, Type, new JsonObject
        {
            ["id"] = id, ["title"] = q.Title, ["subtitle"] = q.Subtitle, ["icon"] = q.IconKey, ["color"] = q.Color,
            ["yes"] = q.ActionText, ["no"] = q.DismissText, ["seconds"] = Math.Round(q.Seconds, 1), ["important"] = q.Important, ["dnd"] = q.ShowInDnd,
        }).ConfigureAwait(false);
        if (!sent) lock (_asked) _asked.Remove(id);
        else Log.Info($"Link: question handed to the main device ({q.Seconds:0} s)");
        return sent;
    }

    private void OnAsk(LinkMessage m)
    {
        var id = Str(m.Payload, "id", 32);
        var title = Str(m.Payload, "title", 300);
        var yes = Str(m.Payload, "yes", 40);
        if (id.Length == 0 || title.Length == 0 || yes.Length == 0) return;
        double seconds = Math.Clamp(Num(m.Payload, "seconds"), 1, 900);
        var icon = Str(m.Payload, "icon", 40);
        var color = Str(m.Payload, "color", 9);
        var no = Str(m.Payload, "no", 40);
        var q = new LinkQuestion(title, Str(m.Payload, "subtitle", 300), icon.StartsWith("Icon.", StringComparison.Ordinal) ? icon : null,
            IsColor(color) ? color : null, yes, no.Length > 0 ? no : "OK", seconds, Bool(m.Payload, "important"), Bool(m.Payload, "dnd"));
        lock (_shown)
        {
            if (_shown.Count >= MaxOpen || _shown.ContainsKey(id)) return;
            _shown[id] = (m.PeerId, DateTime.UtcNow + TimeSpan.FromSeconds(seconds));
        }
        Log.Info($"Link: question from {m.PeerName}");
        Asked?.Invoke(new LinkQuestionRequest(m.PeerId, m.PeerName, id, q));
    }

    /// <summary>The user answered a question from another device (true: the first button). False when it was over already.</summary>
    public Task<bool> AnswerAsync(string id, bool yes) => Reply(id, yes ? "yes" : "no");

    private async Task<bool> Reply(string id, string answer)
    {
        string peerId;
        lock (_shown)
        {
            if (!_shown.Remove(id, out var s)) return false;
            peerId = s.PeerId;
        }
        try { return await Link.SendAsync(peerId, AnswerType, new JsonObject { ["id"] = id, ["answer"] = answer }).ConfigureAwait(false); }
        catch (ArgumentException) { return false; }
    }

    private void OnAnswer(LinkMessage m)
    {
        var id = Str(m.Payload, "id", 32);
        Pending? p;
        lock (_asked)
        {
            if (!_asked.TryGetValue(id, out p) || p.PeerId != m.PeerId) return; // only the device that was asked answers
            _asked.Remove(id);
        }
        var answer = Str(m.Payload, "answer", 16) switch { "yes" => LinkAnswer.Yes, "timeout" => LinkAnswer.Timeout, _ => LinkAnswer.No };
        Log.Info($"Link: question answered on {m.PeerName}: {answer}");
        p.Done(answer);
    }

    /// <summary>
    /// Ends what ran out (call it every second): questions sent from here without an answer end as a timeout; questions
    /// shown here that nobody answered go back as a timeout and their peek goes away.
    /// </summary>
    public void Expire(DateTime now)
    {
        List<Pending> late;
        lock (_asked)
        {
            late = _asked.Where(kv => now > kv.Value.Deadline).Select(kv => kv.Value).ToList();
            foreach (var key in _asked.Where(kv => now > kv.Value.Deadline).Select(kv => kv.Key).ToList()) _asked.Remove(key);
        }
        foreach (var p in late) p.Done(LinkAnswer.Timeout);

        List<string> over;
        lock (_shown) over = _shown.Where(kv => now > kv.Value.Deadline).Select(kv => kv.Key).ToList();
        foreach (var id in over)
        {
            _ = Reply(id, "timeout");
            Expired?.Invoke(id);
        }
    }

    private void OnPeerOffline(PairedDevice peer)
    {
        List<Pending> lost;
        lock (_asked)
        {
            lost = _asked.Values.Where(p => p.PeerId == peer.Id).ToList();
            foreach (var key in _asked.Where(kv => kv.Value.PeerId == peer.Id).Select(kv => kv.Key).ToList()) _asked.Remove(key);
        }
        foreach (var p in lost) p.Done(LinkAnswer.Lost);
        List<string> gone;
        lock (_shown)
        {
            gone = _shown.Where(kv => kv.Value.PeerId == peer.Id).Select(kv => kv.Key).ToList();
            foreach (var id in gone) _shown.Remove(id);
        }
        foreach (var id in gone) Expired?.Invoke(id);
    }

    private static bool IsColor(string s) => s.Length is 7 or 9 && s[0] == '#' && s.Skip(1).All(char.IsAsciiHexDigit);

    public override void Dispose()
    {
        Link.PeerOffline -= OnPeerOffline;
        _answers.Dispose();
        Peer.Dispose();
        base.Dispose();
        // Nothing stays open: questions from here end as lost (they show here), shown ones go away.
        List<Pending> open;
        lock (_asked) { open = _asked.Values.ToList(); _asked.Clear(); }
        foreach (var p in open) p.Done(LinkAnswer.Lost);
        List<string> shown;
        lock (_shown) { shown = _shown.Keys.ToList(); _shown.Clear(); }
        foreach (var id in shown) Expired?.Invoke(id);
    }
}
