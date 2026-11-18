namespace DynamicBay.Core;

/// <summary>An open window as the menu bar groups it. <see cref="Title"/> is only a fallback: menus read it live.</summary>
public sealed record OpenWindow(IntPtr Hwnd, string? AppId, string? ExePath, string Title = "")
{
    /// <summary>One symbol per app: its app id (Chrome profiles, Store and Electron apps), otherwise its program.</summary>
    public string Key => !string.IsNullOrEmpty(AppId) ? AppId.ToLowerInvariant()
        : !string.IsNullOrEmpty(ExePath) ? ExePath.ToLowerInvariant() : "hwnd:" + Hwnd;
}

/// <summary>What a left click on an app symbol in the menu bar does.</summary>
public enum AppClick { Launch, Toggle, Activate, Menu }

/// <summary>The line under an app symbol, as in the Windows taskbar.</summary>
public enum AppIndicator { None, Running, Front, Attention }

/// <summary>
/// Pure rules of the open apps in the menu bar (grouping, order, overflow, clicks), testable without a window.
/// </summary>
public static class RunningApps
{
    /// <summary>
    /// Sorts windows (in z-order, front first) to the pinned entries (<paramref name="isPinned"/>: entry index, window)
    /// and groups the rest by <see cref="OpenWindow.Key"/>. Every list keeps the z-order, so its first window is the one
    /// used last.
    /// </summary>
    public static (List<OpenWindow>[] Pinned, List<(string Key, List<OpenWindow> Windows)> Others) Group(
        IReadOnlyList<OpenWindow> windows, int pinnedCount, Func<int, OpenWindow, bool> isPinned)
    {
        var pinned = Enumerable.Range(0, pinnedCount).Select(_ => new List<OpenWindow>()).ToArray();
        var others = new List<(string Key, List<OpenWindow> Windows)>();
        foreach (var w in windows)
        {
            int p = Enumerable.Range(0, pinnedCount).FirstOrDefault(i => isPinned(i, w), -1);
            if (p >= 0) { pinned[p].Add(w); continue; }
            int at = others.FindIndex(o => o.Key == w.Key);
            if (at >= 0) others[at].Windows.Add(w);
            else others.Add((w.Key, new List<OpenWindow> { w }));
        }
        return (pinned, others);
    }

    /// <summary>
    /// Order of the unpinned apps: as before (symbols must not jump when another window comes to the front), new apps
    /// at the end like on the taskbar.
    /// </summary>
    public static List<string> Order(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var now = new HashSet<string>(current);
        var order = previous.Where(now.Contains).ToList();
        var kept = new HashSet<string>(order);
        order.AddRange(current.Where(k => kept.Add(k)));
        return order;
    }

    /// <summary>
    /// Which of <paramref name="count"/> symbols (each <paramref name="item"/> wide) fit into <paramref name="room"/>;
    /// the rest go behind a "more" button (<paramref name="more"/> wide). The app in front (<paramref name="front"/>,
    /// -1 for none) stays first, then the pinned ones (the first <paramref name="pinned"/>), then the others from the
    /// left: the last symbols go first. Both lists keep the order of the bar.
    /// </summary>
    public static (List<int> Shown, List<int> Hidden) Fit(int count, int pinned, int front, double item, double more, double room)
    {
        var all = Enumerable.Range(0, count).ToList();
        if (count * item <= room) return (all, new List<int>());
        int fits = Math.Max(0, (int)Math.Floor((room - more) / item));
        var keep = new HashSet<int>(all.OrderBy(i => i == front ? 0 : i < pinned ? 1 : 2).ThenBy(i => i).Take(fits));
        return (all.Where(keep.Contains).ToList(), all.Where(i => !keep.Contains(i)).ToList());
    }

    /// <summary>
    /// Left click: start the app without a window, toggle a single window like the taskbar; with several windows bring
    /// the one used last to the front, or list them all when the app is already in front.
    /// </summary>
    public static AppClick Click(int windows, bool appInFront) =>
        windows == 0 ? AppClick.Launch : windows == 1 ? AppClick.Toggle : appInFront ? AppClick.Menu : AppClick.Activate;

    /// <summary>
    /// How long a new window's app id is read again (ms): Chrome apps, Electron apps and Teams set theirs after the
    /// window opens. Until then they would sit under their program (chrome.exe) and miss their pinned entry.
    /// </summary>
    public const long YoungMs = 10_000;

    public static bool IsYoung(long seenMs, long nowMs) => nowMs - seenMs < YoungMs;

    /// <summary>The window with its app id read again; the same instance when it did not change (nothing to regroup).</summary>
    public static OpenWindow Reread(OpenWindow window, string? appId) =>
        string.Equals(window.AppId ?? "", appId ?? "", StringComparison.Ordinal) ? window : window with { AppId = appId };

    /// <summary>
    /// A window whose app id has its own Start menu entry is that app (a Chrome app such as Spotify or Gemini), even
    /// though it runs in the program of a pinned entry (chrome.exe): it must not join the pinned browser. Without an
    /// own entry the program still decides (Electron apps do not always give their windows the entry's id).
    /// </summary>
    public static bool IsOwnApp(string? windowAppId, string pinPath, Func<string, bool> hasEntry) =>
        !string.IsNullOrEmpty(windowAppId)
        && !pinPath.Equals(@"shell:AppsFolder\" + windowAppId, StringComparison.OrdinalIgnoreCase)
        && hasEntry(@"shell:AppsFolder\" + windowAppId);

    /// <summary>In front: a longer line. Wants attention (a blinking window, a call): coloured. Running: a dot.</summary>
    public static AppIndicator Indicator(bool running, bool front, bool flashing) =>
        !running ? AppIndicator.None : front ? AppIndicator.Front : flashing ? AppIndicator.Attention : AppIndicator.Running;
}
