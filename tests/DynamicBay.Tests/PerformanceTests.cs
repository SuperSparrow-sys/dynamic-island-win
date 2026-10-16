using DynamicBay.Core;

namespace DynamicBay.Tests;

/// <summary>Work that runs only when something changed (performance review of 15.10.2026, docs/plans/leistung-2026-10.md).</summary>
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
    public async Task A_failed_read_does_not_block_the_next_one()
    {
        int runs = 0;
        var gate = new CoalescedRun(() => { runs++; throw new InvalidOperationException(); });
        await Assert.ThrowsAsync<InvalidOperationException>(gate.RequestAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(gate.RequestAsync);
        Assert.Equal(2, runs);
    }
}
