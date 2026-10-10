using DynamicBay.Services;

namespace DynamicBay.Tests;

public class PcMicTests
{
    private static AudioEndpointInfo Mic(string id, string name, bool bt = false, bool active = true) =>
        new(id, AudioFlow.Input, name, name, active, bt, bt, false);

    [Fact]
    public void Prefers_the_last_default_microphone()
    {
        var list = new[] { Mic("a", "Mikrofonarray (Realtek)"), Mic("b", "Mikrofon (Yeti USB)"), Mic("c", "Headset (AirPods)", bt: true) };
        Assert.Equal("b", AudioDevicesService.PcMic(list, "b")?.Id);
    }

    [Fact]
    public void Last_microphone_gone_falls_back_to_a_real_one()
    {
        var list = new[] { Mic("x", "Stereomix (Realtek)"), Mic("y", "Steam Streaming Microphone"), Mic("z", "CABLE Output (VB-Audio Virtual Cable)"), Mic("a", "Mikrofonarray (Realtek)") };
        Assert.Equal("a", AudioDevicesService.PcMic(list, "gone")?.Id);
    }

    [Fact]
    public void Only_virtual_or_bluetooth_microphones_switch_nothing()
    {
        var list = new[] { Mic("x", "Stereo Mix (Realtek)"), Mic("v", "Voicemeeter Out B1"), Mic("n", "Mikrofon (NVIDIA Broadcast)"), Mic("c", "Headset (AirPods)", bt: true) };
        Assert.Null(AudioDevicesService.PcMic(list, null));
    }

    [Fact]
    public void Ignores_inactive_and_bluetooth_even_as_last()
    {
        var list = new[] { Mic("c", "Headset (AirPods)", bt: true), Mic("o", "Mikrofon (USB)", active: false), Mic("a", "Mikrofonarray") };
        Assert.Equal("a", AudioDevicesService.PcMic(list, "c")?.Id);
        Assert.Equal("a", AudioDevicesService.PcMic(list, "o")?.Id);
    }

    [Fact]
    public void A_virtual_microphone_chosen_by_hand_is_kept()
    {
        var list = new[] { Mic("a", "Mikrofonarray"), Mic("v", "Voicemeeter Out B1") };
        Assert.Equal("v", AudioDevicesService.PcMic(list, "v")?.Id);
    }
}
