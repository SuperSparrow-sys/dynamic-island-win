using System.IO;
using DynamicBay.Core;

namespace DynamicBay.Tests;

/// <summary>Work that runs only when something changed (performance review of 15.10.2026, docs/performance/README.md).</summary>
public class PerformanceTests
{
    [Fact]
    public async Task Changes_during_a_read_lead_to_exactly_one_more_read()
    {
        int runs = 0;
        var release = new TaskCompletionSource();
        var gate = new CoalescedRun(async () => { runs++; if (runs == 1) await release.Task; });

        var first = gate.RequestAsync();
        Assert.Equal(1, runs);
        // Three notifications arrive while Windows is still being read: none may be lost, one read covers them all.
        await gate.RequestAsync();
        await gate.RequestAsync();
        await gate.RequestAsync();
        Assert.Equal(1, runs);
        release.SetResult();
        await first;
        Assert.Equal(2, runs);

        await gate.RequestAsync(); // idle again: the next change reads at once
        Assert.Equal(3, runs);
    }

    /// <summary>The end of a Claude Code transcript as it is written: big tool results between the small meta lines.</summary>
    private static byte[] Transcript(int bigLines)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"type\":\"user\",\"cwd\":\"C:\\\\old\",\"message\":\"cut off in the mid");
        sb.Append("dle\"}\n");
        sb.Append("{\"type\":\"ai-title\",\"aiTitle\":\"Alter Titel\"}\n");
        sb.Append("{\"type\":\"last-prompt\",\"lastPrompt\":\"erste\\nFrage\"}\n");
        string big = new('x', 200_000);
        for (int i = 0; i < bigLines; i++) sb.Append("{\"type\":\"tool_result\",\"cwd\":\"C:\\\\Projekt\",\"content\":\"").Append(big).Append("\"}\n");
        sb.Append("{\"type\":\"custom-title\",\"customTitle\":\"Leistung prüfen\"}\n");
        sb.Append("{\"type\":\"bridge-session\",\"bridgeSessionId\":\"cse_abc\"}\n");
        sb.Append("{\"type\":\"last-prompt\",\"lastPrompt\":\"gib mir die offenen punkte\"}\n");
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Fact]
    public void The_transcript_tail_gives_the_newest_title_prompt_and_link()
    {
        var meta = DynamicBay.Services.ClaudeService.ParseTail(Transcript(2), startsMidLine: true);
        Assert.Equal("Leistung prüfen", meta.Title);
        Assert.Equal("gib mir die offenen punkte", meta.Prompt);
        Assert.Equal("cse_abc", meta.Bridge);
        Assert.Equal("C:\\Projekt", meta.Cwd);
    }

    [Fact]
    public void Reading_the_transcript_tail_makes_no_large_strings()
    {
        // Every 5 s while a session works: a 1 MB string of the last 512 KB and its lines landed on the large object
        // heap and left 26 MB of holes there (15.10.2026). Only the small meta lines may become strings.
        var tail = Transcript(2);
        DynamicBay.Services.ClaudeService.ParseTail(tail, startsMidLine: true); // JIT and first-use allocations
        long before = GC.GetAllocatedBytesForCurrentThread();
        DynamicBay.Services.ClaudeService.ParseTail(tail, startsMidLine: true);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024);
    }

    [Fact]
    public void Nobody_sees_the_island_while_locked_or_with_the_display_off()
    {
        var a = new ActivityState();
        int changes = 0;
        a.Changed += () => changes++;
        Assert.False(a.Away);

        a.Set(locked: true);
        Assert.True(a.Away);
        a.Set(displayOff: true); // still away: no second change
        a.Set(locked: false);
        Assert.True(a.Away); // the display is still off
        Assert.Equal(1, changes);
        a.Set(displayOff: false);
        Assert.False(a.Away);
        Assert.Equal(2, changes);

        // Endless animations run only while someone can see them
        Assert.True(ActivityState.ShouldAnimate(visible: true, loaded: true, away: false));
        Assert.False(ActivityState.ShouldAnimate(visible: true, loaded: true, away: true));
        Assert.False(ActivityState.ShouldAnimate(visible: false, loaded: true, away: false));
    }

    [Theory]
    [InlineData(0, true)]   // off
    [InlineData(1, false)]  // on
    [InlineData(2, false)]  // dimmed: still visible
    public void The_display_state_comes_from_the_power_setting_message(int state, bool off)
    {
        // POWERBROADCAST_SETTING: GUID PowerSetting, DWORD DataLength, BYTE Data[]
        var data = new byte[24];
        ActivityState.ConsoleDisplayState.ToByteArray().CopyTo(data, 0);
        BitConverter.GetBytes(4).CopyTo(data, 16);
        BitConverter.GetBytes(state).CopyTo(data, 20);
        var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data, 0, p, data.Length);
            Assert.Equal(off, ActivityState.DisplayOff(p));
            Guid.NewGuid().ToByteArray().CopyTo(data, 0); // another power setting
            System.Runtime.InteropServices.Marshal.Copy(data, 0, p, data.Length);
            Assert.Null(ActivityState.DisplayOff(p));
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public async Task Clipboard_work_runs_on_its_own_sta_thread_one_after_the_other()
    {
        // Reading the clipboard waits for the app that copied (it renders the data on request), and a 4K screenshot
        // takes about 450 ms to save as PNG: on the UI thread that stalled the island for up to 1.9 s (15.10.2026).
        using var worker = new StaWorker("Test");
        int caller = Environment.CurrentManagedThreadId;
        var order = new List<int>();
        var first = worker.Run(() => { Thread.Sleep(50); lock (order) order.Add(1); return Thread.CurrentThread.GetApartmentState(); });
        var second = worker.Run(() => { lock (order) order.Add(2); return Environment.CurrentManagedThreadId; });
        Assert.Equal(ApartmentState.STA, await first);
        Assert.NotEqual(caller, await second);
        Assert.Equal(new[] { 1, 2 }, order);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Run<int>(() => throw new InvalidOperationException()));
        Assert.Equal(3, await worker.Run(() => 3)); // an exception does not stop the thread
    }

    [Fact]
    public async Task Launcher_icons_load_off_the_calling_thread_and_the_newest_list_wins()
    {
        // 12 shell icons on the UI thread held the start for up to 2.1 s (InstalledApps.Ready → Rebuild).
        var dir = Directory.CreateTempSubdirectory("dynamicbay-shortcuts").FullName;
        try
        {
            string a = Path.Combine(dir, "a.txt"), b = Path.Combine(dir, "b.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            var settings = new AppSettings();
            var svc = new DynamicBay.Services.ShortcutsService(settings);
            settings.Shortcuts.Add(a);
            svc.Rebuild();
            Assert.Empty(svc.Items); // returned at once: the shell icon is still loading elsewhere
            settings.Shortcuts.Clear();
            settings.Shortcuts.Add(b);
            svc.Rebuild(); // changed while the first list was still loading
            for (int i = 0; i < 100 && svc.Items.Count == 0; i++) await Task.Delay(50);
            await Task.Delay(200);
            Assert.Equal(new[] { b }, svc.Items.Select(x => x.Path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_failed_read_does_not_block_the_next_one()
    {
        int runs = 0;
        var gate = new CoalescedRun(() => { runs++; throw new InvalidOperationException(); });
        await Assert.ThrowsAsync<InvalidOperationException>(gate.RequestAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(gate.RequestAsync);
        Assert.Equal(2, runs);
    }
}
