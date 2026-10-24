using DynamicBay.Services;

namespace DynamicBay.Tests;

public class MicSessionsTests
{
    [Fact]
    public void Voice_input_with_pauses_stays_one_recording_and_ends_after_five_seconds()
    {
        // Seen on 24.10.2026 at 22:08: the session of "claude" disappears for 1-2 s in every pause.
        var t0 = new DateTime(2026, 10, 24, 22, 8, 43, DateTimeKind.Utc);
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var hold = TimeSpan.FromSeconds(5);
        string[] claude = ["claude"], none = [];

        Assert.Equal(claude, MicSessions.Held(seen, claude, t0, hold));
        Assert.Equal(claude, MicSessions.Held(seen, claude, t0.AddSeconds(11), hold));
        // A pause of 2 s: still recording.
        Assert.Equal(claude, MicSessions.Held(seen, none, t0.AddSeconds(12.5), hold));
        Assert.Equal(claude, MicSessions.Held(seen, claude, t0.AddSeconds(13), hold));
        // Stopped at 13 s: counts until 18 s, then no longer.
        Assert.Equal(claude, MicSessions.Held(seen, none, t0.AddSeconds(17.9), hold));
        Assert.Empty(MicSessions.Held(seen, none, t0.AddSeconds(18), hold));
    }
}
