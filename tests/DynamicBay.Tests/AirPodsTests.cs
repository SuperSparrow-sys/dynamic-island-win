using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>The AirPods widget: battery from Apple's proximity pairing advertisement, and whose AirPods it listens to.</summary>
public class AirPodsTests
{
    // Received on 15.10.2026 from AirPods 4 (ANC) in both ears, music playing, case closed (manufacturer data of 0x004C).
    private const string InEars = "0719011B200BAA8F100005D785C1E1D153FFE5A8A16567AFECBC47";

    [Fact]
    public void Real_advertisement_gives_both_earbuds_full_and_the_closed_case_unknown()
    {
        var r = AirPodsAdvert.Parse(Convert.FromHexString(InEars))!;
        Assert.Equal(0x201B, r.Model);
        Assert.Equal(100, r.Left);
        Assert.Equal(100, r.Right);
        Assert.Null(r.Case);
        Assert.False(r.LeftCharging || r.RightCharging || r.CaseCharging);
    }

    [Fact]
    public void Earbud_levels_follow_the_sending_side()
    {
        // Right earbud sends (status bit 0x20 clear): its level is the low nibble, the left one's the high nibble.
        var right = AirPodsAdvert.Parse(Convert.FromHexString("071901142001" + "83" + "26" + "10"))!;
        Assert.Equal((80, 30), (right.Left, right.Right));
        Assert.Equal(60, right.Case);
        Assert.True(right.LeftCharging);   // flags 0x2: the other earbud
        Assert.False(right.RightCharging);
        Assert.False(right.CaseCharging);

        // Left earbud sends (0x20 set): the same bytes mean the other way round.
        var left = AirPodsAdvert.Parse(Convert.FromHexString("071901142021" + "83" + "56" + "10"))!;
        Assert.Equal((30, 80), (left.Left, left.Right));
        Assert.True(left.LeftCharging);    // flags 0x1: the sending earbud
        Assert.False(left.RightCharging);
        Assert.True(left.CaseCharging);    // flags 0x4
    }

    [Theory]
    [InlineData("0710")]                         // too short
    [InlineData("1005011B200BAA8F10")]           // another Apple message (nearby info)
    [InlineData("071901FFFF0BAA8F10")]           // not a headphone model
    public void Other_messages_are_ignored(string hex) => Assert.Null(AirPodsAdvert.Parse(Convert.FromHexString(hex)));

    [Fact]
    public void Unknown_and_invalid_levels_are_null()
    {
        var r = AirPodsAdvert.Parse(Convert.FromHexString("071901142001" + "FB" + "0F" + "10"))!;
        Assert.Null(r.Left);   // 15 = unknown
        Assert.Null(r.Right);  // 11 = no level
        Assert.Null(r.Case);
    }

    [Fact]
    public void Stays_with_its_AirPods_unless_another_sender_is_much_closer_or_ours_went_quiet()
    {
        var t = new DateTime(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);
        Assert.True(AirPodsAdvert.TakeSender(0, 0, default, 0xA, -70, t));           // the first one
        Assert.False(AirPodsAdvert.TakeSender(0, 0, default, 0xA, -90, t));          // too far away
        Assert.True(AirPodsAdvert.TakeSender(0xA, -60, t, 0xA, -72, t));             // ours, weaker this time
        Assert.False(AirPodsAdvert.TakeSender(0xA, -60, t, 0xB, -65, t));            // somebody else's in the room
        Assert.True(AirPodsAdvert.TakeSender(0xA, -70, t, 0xB, -55, t));             // clearly closer
        Assert.True(AirPodsAdvert.TakeSender(0xA, -60, t, 0xB, -66, t.AddSeconds(16))); // ours changed its address
    }

    [Fact]
    public void Devices_widget_shows_the_emptier_earbud()
    {
        Assert.Equal(40, AirPodsAdvert.Lowest(90, 40));
        Assert.Equal(90, AirPodsAdvert.Lowest(90, null)); // one earbud in the case with the lid closed
        Assert.Null(AirPodsAdvert.Lowest(null, null));
    }
}
