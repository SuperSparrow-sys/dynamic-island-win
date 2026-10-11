using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>An app that crashed while recording leaves its privacy entry "in use"; only running apps may count.</summary>
public class MicUsageTests
{
    private static readonly string[] Running =
    {
        @"C:\Program Files\Discord\app-1.0\Discord.exe",
        @"C:\Program Files\WindowsApps\Microsoft.WindowsSoundRecorder_11.2405.10.0_x64__8wekyb3d8bbwe\VoiceRecorder.exe",
    };

    [Theory]
    [InlineData(@"C:#Program Files#Discord#app-1.0#Discord.exe", true)]
    [InlineData(@"c:#program files#discord#app-1.0#discord.exe", true)]
    [InlineData(@"C:#Program Files#Zoom#bin#Zoom.exe", false)] // crashed, entry left behind
    public void Desktop_apps_count_only_while_running(string key, bool expected) =>
        Assert.Equal(expected, AudioService.StillRunning(key, packaged: false, Running));

    [Theory]
    [InlineData("Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe", true)]
    [InlineData("MSTeams_8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.WindowsSoundRecorder_otherpublisher", false)]
    public void Store_apps_count_only_while_running(string family, bool expected) =>
        Assert.Equal(expected, AudioService.StillRunning(family, packaged: true, Running));

    [Fact]
    public void Without_a_process_list_everything_counts_as_before() =>
        Assert.True(AudioService.StillRunning(@"C:#x.exe", packaged: false, Array.Empty<string>()));
}
