using DynamicBay.Core;

namespace DynamicBay.Tests;

public class RunningAppsTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Claude = @"C:\Users\x\AppData\Local\AnthropicClaude\app-1.0\claude.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private static OpenWindow W(int hwnd, string? exe, string? appId = null) => new(hwnd, appId, exe);

    [Fact]
    public void Windows_group_per_app_and_keep_the_z_order()
    {
        // Z-order: Chrome 1 in front, Claude, Chrome 2, Notepad, Chrome 3, Claude 2
        var windows = new[] { W(1, Chrome, "Chrome"), W(2, Claude), W(3, Chrome, "Chrome"), W(4, Notepad), W(5, Chrome, "chrome"), W(6, Claude) };
        var (pinned, others) = RunningApps.Group(windows, 0, (_, _) => false);
        Assert.Empty(pinned);
        Assert.Equal(new[] { "chrome", Claude.ToLowerInvariant(), Notepad.ToLowerInvariant() }, others.Select(o => o.Key));
        Assert.Equal(new IntPtr[] { 1, 3, 5 }, others[0].Windows.Select(w => w.Hwnd)); // app id compared without case
        Assert.Equal(new IntPtr[] { 2, 6 }, others[1].Windows.Select(w => w.Hwnd));
    }

    [Fact]
    public void Pinned_apps_take_their_windows_and_the_rest_follows()
    {
        var windows = new[] { W(1, Notepad), W(2, Chrome, "Chrome"), W(3, Chrome, "Chrome") };
        var (pinned, others) = RunningApps.Group(windows, 2, (i, w) => i == 1 && w.ExePath == Chrome);
        Assert.Empty(pinned[0]); // pinned but not running
        Assert.Equal(new IntPtr[] { 2, 3 }, pinned[1].Select(w => w.Hwnd));
        Assert.Equal(Notepad.ToLowerInvariant(), Assert.Single(others).Key);
    }

    [Fact]
    public void Key_falls_back_to_the_program_then_the_window()
    {
        Assert.Equal("chrome", W(1, Chrome, "Chrome").Key);
        Assert.Equal(Notepad.ToLowerInvariant(), W(1, Notepad, "").Key);
        Assert.Equal("hwnd:7", W(7, null).Key);
    }

    [Fact]
    public void A_late_app_id_moves_the_window_to_its_own_app()
    {
        // A Chrome app (PWA) opens as chrome.exe and sets its own id a moment later.
        var first = W(1, Chrome);
        Assert.True(RunningApps.IsYoung(seenMs: 1000, nowMs: 1000 + RunningApps.YoungMs - 1));
        Assert.False(RunningApps.IsYoung(seenMs: 1000, nowMs: 1000 + RunningApps.YoungMs));
        var later = RunningApps.Reread(first, "Chrome._crx_teams");
        Assert.Equal("chrome._crx_teams", later.Key);
        var (_, others) = RunningApps.Group(new[] { later, W(2, Chrome, null) }, 0, (_, _) => false);
        Assert.Equal(2, others.Count); // the app and the browser are two symbols
        Assert.Same(later, RunningApps.Reread(later, "Chrome._crx_teams")); // unchanged: nothing to regroup
        Assert.Same(first, RunningApps.Reread(first, null));
    }

    [Fact]
    public void Order_keeps_places_and_puts_new_apps_last()
    {
        var order = RunningApps.Order(new[] { "a", "b", "c" }, new[] { "c", "d", "a" }); // b closed, d new, c came to the front
        Assert.Equal(new[] { "a", "c", "d" }, order);
        Assert.Equal(new[] { "x", "y" }, RunningApps.Order(Array.Empty<string>(), new[] { "x", "y" }));
    }

    [Fact]
    public void Everything_fits_without_more()
    {
        var (shown, hidden) = RunningApps.Fit(5, 2, 4, 32, 32, 160);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, shown);
        Assert.Empty(hidden);
    }

    [Fact]
    public void Overflow_hides_the_last_ones_but_keeps_front_and_pinned()
    {
        // 8 symbols, 3 pinned, the app in front is the last one; room for 5 symbols including "more": 4 stay.
        var (shown, hidden) = RunningApps.Fit(8, 3, 7, 32, 32, 160);
        Assert.Equal(new[] { 0, 1, 2, 7 }, shown);
        Assert.Equal(new[] { 3, 4, 5, 6 }, hidden);
    }

    [Fact]
    public void Overflow_without_front_keeps_from_the_left()
    {
        var (shown, hidden) = RunningApps.Fit(6, 1, -1, 32, 32, 130);
        Assert.Equal(new[] { 0, 1, 2 }, shown);
        Assert.Equal(new[] { 3, 4, 5 }, hidden);
    }

    [Fact]
    public void Front_wins_over_pinned_when_room_is_tiny()
    {
        var (shown, hidden) = RunningApps.Fit(4, 3, 3, 32, 32, 70);
        Assert.Equal(new[] { 3 }, shown);
        Assert.Equal(new[] { 0, 1, 2 }, hidden);
        Assert.Empty(RunningApps.Fit(3, 0, -1, 32, 32, 10).Shown); // no room: all behind "more"
    }

    [Theory]
    [InlineData(0, false, AppClick.Launch)]
    [InlineData(0, true, AppClick.Launch)]
    [InlineData(1, false, AppClick.Toggle)]
    [InlineData(1, true, AppClick.Toggle)]   // a single window minimizes like the taskbar
    [InlineData(3, false, AppClick.Activate)] // three Chrome windows: the one used last comes to the front
    [InlineData(2, true, AppClick.Menu)]      // two Claude windows, Claude in front: pick one
    public void Click_decides_by_windows_and_front(int windows, bool front, AppClick expected) =>
        Assert.Equal(expected, RunningApps.Click(windows, front));

    [Theory]
    [InlineData(false, false, false, AppIndicator.None)]
    [InlineData(true, false, false, AppIndicator.Running)]
    [InlineData(true, true, false, AppIndicator.Front)]
    [InlineData(true, false, true, AppIndicator.Attention)]
    [InlineData(true, true, true, AppIndicator.Front)] // in front: seen, no colour
    public void Indicator_like_the_taskbar(bool running, bool front, bool flashing, AppIndicator expected) =>
        Assert.Equal(expected, RunningApps.Indicator(running, front, flashing));
}
