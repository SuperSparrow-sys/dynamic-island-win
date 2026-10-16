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
