using DynamicBay.Services;
using NAudio.CoreAudioApi;

namespace DynamicBay.Tests;

/// <summary>Our enumerator and NAudio's live side by side (Windows hands out one per process).</summary>
public class CoreAudioEnumeratorTests
{
    [Fact]
    public async Task NAudioEnumeratorWorksWhileOursIsHeld()
    {
        // Same order as in the app: the audio services hold theirs, then a received stream starts the NAudio sink.
        object ours = await Task.Run(AudioEndpoints.CreateEnumerator);
        await Task.Run(() =>
        {
            using var en = new MMDeviceEnumerator(); // threw InvalidCastException before
            en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
        });
        await Task.Run(() =>
        {
            var again = (AudioEndpoints.IMMDeviceEnumerator)AudioEndpoints.CreateEnumerator();
            again.GetDefaultAudioEndpoint((int)AudioFlow.Output, 1, out _); // no output (CI) only returns an error code
        });
        GC.KeepAlive(ours);
    }
}
