using System.Runtime.InteropServices;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

public class FullscreenGamesTests
{
    private static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact]
    public void Adjusts_only_windows_that_cover_the_monitor_exactly()
    {
        var mon = R(0, 0, 2560, 1440);
        Assert.Equal(1441, FullscreenGames.Adjusted(mon, mon, FullscreenFix.Taller)!.Value.Bottom);
        Assert.Equal(1439, FullscreenGames.Adjusted(mon, mon, FullscreenFix.Shorter)!.Value.Bottom);
        Assert.Null(FullscreenGames.Adjusted(mon, mon, FullscreenFix.Off));
        Assert.Null(FullscreenGames.Adjusted(mon, mon, FullscreenFix.Nvidia));
        Assert.Null(FullscreenGames.Adjusted(R(0, 0, 2560, 1441), mon, FullscreenFix.Taller)); // already ours
        Assert.Null(FullscreenGames.Adjusted(R(100, 100, 900, 700), mon, FullscreenFix.Taller));
    }

    [Fact]
    public void Adjusts_on_a_second_monitor_too()
    {
        var mon = R(2560, -200, 4480, 880);
        var t = FullscreenGames.Adjusted(mon, mon, FullscreenFix.Taller)!.Value;
        Assert.Equal((2560, -200, 4480, 881), (t.Left, t.Top, t.Right, t.Bottom));
    }

    [Theory]
    [InlineData("GLFW30", 5, true)]          // Minecraft Java
    [InlineData("SDL_app", 5, true)]          // Minecraft 26.x
    [InlineData("UnityWndClass", 5, true)]
    [InlineData("Chrome_WidgetWin_1", 3, true)] // exclusive Direct3D says it is a game
    [InlineData("Chrome_WidgetWin_1", 5, false)] // a video in the browser
    [InlineData("MozillaWindowClass", 2, false)]
    public void Recognises_games(string cls, int state, bool game) =>
        Assert.Equal(game, FullscreenGames.IsGame(cls, state));

    [Theory]
    [InlineData("XamlExplorerHostIslandWindow", "explorer", true)] // Start and search: seen in a test run
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost", true)]
    [InlineData("SDL_app", "javaw", false)]
    public void Never_touches_the_shell(string cls, string exe, bool shell) =>
        Assert.Equal(shell, FullscreenGames.IsShell(cls, exe));
}

/// <summary>
/// The NVAPI structures rebuilt field by field from nvapi.h (x64): the runtime computes the C layout, which must match the
/// sizes and offsets NvidiaProfile writes by hand. A wrong size makes the driver reject the call (version mismatch).
/// </summary>
public class NvidiaProfileLayoutTests
{
    private const int Str = 4096; // NvAPI_UnicodeString: 2048 x NvU16

    [StructLayout(LayoutKind.Explicit, Size = 4104)] // union { NvU32; NVDRS_BINARY_SETTING (4 + 4096); NvU16[2048]; NvU64 }
    private struct SettingUnion { [FieldOffset(0)] public ulong U64; [FieldOffset(4100)] public uint Pad; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Setting
    {
        public uint Version;
        public fixed ushort Name[2048];
        public uint Id, Type, Location, IsCurrentPredefined, IsPredefinedValid;
        public SettingUnion Predefined, Current;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Application
    {
        public uint Version, IsPredefined;
        public fixed ushort AppName[2048], FriendlyName[2048], Launcher[2048], FileInFolder[2048];
        public uint Flags;
        public fixed ushort CommandLine[2048];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Profile
    {
        public uint Version;
        public fixed ushort Name[2048];
        public uint GpuSupport, IsPredefined, NumOfApps, NumOfSettings;
    }

    [Fact]
    public void Setting_layout_matches()
    {
        Assert.Equal(NvidiaProfile.SettingSize, Marshal.SizeOf<Setting>());
        Assert.Equal(NvidiaProfile.SettingIdOffset, (int)Marshal.OffsetOf<Setting>(nameof(Setting.Id)));
        Assert.Equal(NvidiaProfile.SettingTypeOffset, (int)Marshal.OffsetOf<Setting>(nameof(Setting.Type)));
        Assert.Equal(NvidiaProfile.SettingCurrentOffset, (int)Marshal.OffsetOf<Setting>(nameof(Setting.Current)));
    }

    [Fact]
    public void Application_and_profile_layout_matches()
    {
        Assert.Equal(NvidiaProfile.AppSize, Marshal.SizeOf<Application>());
        Assert.Equal(NvidiaProfile.AppNameOffset, (int)Marshal.OffsetOf<Application>(nameof(Application.AppName)));
        Assert.Equal(NvidiaProfile.ProfileSize, Marshal.SizeOf<Profile>());
        Assert.Equal(NvidiaProfile.ProfileNameOffset, (int)Marshal.OffsetOf<Profile>(nameof(Profile.Name)));
        Assert.Equal(Str, NvidiaProfile.StringBytes);
    }

    [Fact]
    public void Versions_carry_size_and_number() =>
        Assert.Equal(0x00013028u, NvidiaProfile.Version(NvidiaProfile.SettingSize, 1)); // MAKE_NVAPI_VERSION(NVDRS_SETTING_V1, 1)
}
