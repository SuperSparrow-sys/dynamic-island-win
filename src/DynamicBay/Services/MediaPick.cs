namespace DynamicBay.Services;

/// <summary>One media session as the picker sees it.</summary>
public readonly record struct MediaCandidate(string Key, bool Playing, bool Spotify);

/// <summary>
/// Which player the media card shows when several run (Spotify and YouTube in the browser). The card is a stack of
/// pages, one per session: scrolling picks a page, and the pick holds while that session exists. A player that starts
/// playing comes to the front; a short stop while skipping does not count as a start, so two players never take turns.
/// </summary>
public static class MediaPick
{
    /// <summary>A stop shorter than this is a skip or a buffering blip, not a pause.</summary>
    public static readonly TimeSpan StartGap = TimeSpan.FromSeconds(3);

    /// <summary>Stable keys for sessions: the app id, numbered when an app has several ("chrome#0", "chrome#1").</summary>
    public static List<string> Keys(IReadOnlyList<string> appIds)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>(appIds.Count);
        foreach (var id in appIds)
        {
            seen.TryGetValue(id, out int n);
            seen[id] = n + 1;
            keys.Add($"{id}#{n}");
        }
        return keys;
    }

    /// <summary>
    /// The page to show: the chosen one while it exists, else a playing Spotify, any playing session, Spotify, the session
    /// Windows calls current (<paramref name="current"/>, -1 for none). -1 when there are no sessions.
    /// </summary>
    public static int Choose(IReadOnlyList<MediaCandidate> sessions, string? pinned, int current)
    {
        if (sessions.Count == 0) return -1;
        if (pinned is not null)
            for (int i = 0; i < sessions.Count; i++)
                if (sessions[i].Key == pinned) return i;
        int Find(Func<MediaCandidate, bool> f)
        {
            for (int i = 0; i < sessions.Count; i++) if (f(sessions[i])) return i;
            return -1;
        }
        int pick = Find(s => s.Playing && s.Spotify);
        if (pick < 0) pick = Find(s => s.Playing);
        if (pick < 0) pick = Find(s => s.Spotify);
        if (pick < 0) pick = current >= 0 && current < sessions.Count ? current : 0;
        return pick;
    }
}

/// <summary>Notices sessions that began to play since the last look (a new video, Spotify resumed after a pause).</summary>
public sealed class MediaStarts
{
    // Per session: null while it played at the last look, else when it was first seen not playing.
    private readonly Dictionary<string, DateTime?> _stoppedAt = new();
    private bool _baseline;

    public List<string> Update(IReadOnlyList<MediaCandidate> sessions, DateTime now)
    {
        var started = new List<string>();
        var present = new HashSet<string>();
        foreach (var s in sessions)
        {
            present.Add(s.Key);
            bool known = _stoppedAt.TryGetValue(s.Key, out var stopped);
            if (s.Playing)
            {
                // The first look after start is the baseline: what already plays did not just start.
                if (_baseline && (!known || stopped is { } t && now - t >= MediaPick.StartGap)) started.Add(s.Key);
                _stoppedAt[s.Key] = null;
            }
            else if (!known || stopped is null) _stoppedAt[s.Key] = now;
        }
        foreach (var gone in _stoppedAt.Keys.Where(k => !present.Contains(k)).ToList()) _stoppedAt.Remove(gone);
        _baseline = true;
        return started;
    }
}
