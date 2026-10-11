using DynamicBay.Services.Link;

namespace DynamicBay.Tests;

/// <summary>A peek from the other device must never travel back (it did, endlessly, when neither device was main).</summary>
public class PeekLoopTests
{
    [Fact]
    public void A_received_peek_is_not_sent_back_within_15_seconds()
    {
        var recent = new LinkRoleFeature.RecentPeeks();
        var t = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc);
        recent.Add("Akku schwach", "15 %", t);
        Assert.True(recent.CameBack("Akku schwach", "15 %", t.AddSeconds(1)));
        Assert.True(recent.CameBack("Akku schwach", "15 %", t.AddSeconds(15)));
        Assert.False(recent.CameBack("Akku schwach", "15 %", t.AddSeconds(16)));
        Assert.False(recent.CameBack("Akku schwach", "14 %", t.AddSeconds(1)));
    }
}
