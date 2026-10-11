using System.IO;
using DynamicBay.Services;

namespace DynamicBay.Tests;

public class CursorHopFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dynamicbay-hop-" + Guid.NewGuid().ToString("N"));

    public CursorHopFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Finalizing = "2026-10-11T12:49:05.871307Z  INFO cursorhop::injection: [CLIP_TRANSFER] finalizing transfer transfer_id=9f5ef6c0-a12d-46b8-9332-84ddb6d05588 received=35920 expected=35920 elapsed_ms=10 paste_dir=Some(\"C:\\Users\\<user>\\OneDrive\\Desktop\")";
    private const string Delivered = "2026-10-11T12:49:05.871961Z  INFO cursorhop::injection: [CLIP_TRANSFER] files delivered to paste directory delivered=1";

    [Fact]
    public void Folder_from_the_finalizing_line_goes_with_the_delivered_files()
    {
        var fin = CursorHopLog.Parse(Finalizing);
        Assert.Equal(CursorHopEventKind.TransferFolder, fin!.Kind);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, "OneDrive", "Desktop"), fin.Folder);

        var state = new CursorHopState();
        Assert.Empty(state.Apply(fin));
        var files = Assert.Single(state.Apply(CursorHopLog.Parse(Delivered)!));
        Assert.Equal(CursorHopEventKind.FilesReceived, files.Kind);
        Assert.Equal(1, files.Count);
        Assert.Equal(fin.Folder, files.Folder);
    }

    [Theory]
    [InlineData(@"C:\Users\<user>\OneDrive\Desktop", @"D:\Profile\OneDrive\Desktop")]
    [InlineData(@"D:\Shared\Drop", @"D:\Shared\Drop")]
    public void Logged_paths_are_unescaped_and_unmasked(string logged, string expected) =>
        Assert.Equal(expected, CursorHopLog.UnmaskFolder(logged, @"D:\Profile"));

    [Fact]
    public void Recent_files_are_the_newest_written_ones()
    {
        var now = DateTime.Now;
        void Make(string name, TimeSpan age, bool hidden = false)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllText(p, name);
            File.SetCreationTime(p, now - age);
            File.SetLastWriteTime(p, now - age);
            if (hidden) File.SetAttributes(p, FileAttributes.Hidden);
        }
        Make("old.png", TimeSpan.FromHours(2));
        Make("desktop.ini", TimeSpan.FromSeconds(1), hidden: true);
        Make("a.png", TimeSpan.FromSeconds(5));
        Make("b.png", TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "b.png" }, CursorHopService.RecentFiles(_dir, 1, TimeSpan.FromSeconds(30), now).Select(Path.GetFileName));
        Assert.Equal(new[] { "b.png", "a.png" }, CursorHopService.RecentFiles(_dir, 5, TimeSpan.FromSeconds(30), now).Select(Path.GetFileName));
        Assert.Empty(CursorHopService.RecentFiles(Path.Combine(_dir, "missing"), 1, TimeSpan.FromSeconds(30), now));
        Assert.Empty(CursorHopService.RecentFiles(null, 1, TimeSpan.FromSeconds(30), now));
    }

    [Theory]
    [InlineData("CursorHop-App", "cursorhop-ui", "Chrome_WidgetWin_1", 0x8280028, 110, 39, true)]   // "Receiving" next to the cursor
    [InlineData("CursorHop-App", "cursorhop-ui", "Chrome_WidgetWin_1", 0x200008, 344, 96, true)]    // "Transfer complete"
    [InlineData("CursorHop-App", "cursorhop-ui", "Chrome_WidgetWin_1", 0x100, 1100, 760, false)]     // its settings window
    [InlineData("CursorHop-App", "CursorHop", "Chrome_WidgetWin_1", 0x8, 344, 96, false)]
    [InlineData("Spotify", "cursorhop-ui", "Chrome_WidgetWin_1", 0x8, 344, 96, false)]
    [InlineData("CursorHop-App", "cursorhop-ui", "Chrome_WidgetWin_1", 0x0, 344, 96, false)]         // not top-most
    public void Only_cursorhops_small_top_most_popups_are_hidden(string process, string title, string cls, int ex, int w, int h, bool hide) =>
        Assert.Equal(hide, CursorHopOverlay.IsPopup(process, title, cls, ex, w, h));
}

public class LinkAddressOrderTests
{
    [Fact]
    public void Tailscale_addresses_come_first() =>
        Assert.Equal(new[] { "100.105.33.4", "192.168.10.25" },
            DynamicBay.Services.Link.LinkHub.OrderAddresses(new[] { "192.168.10.25", "100.105.33.4", "192.168.10.25" }));

    [Theory]
    [InlineData("100.105.33.4", true)]
    [InlineData("100.105.33.4:43823", true)]
    [InlineData("192.168.10.25", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("pc-jonathan", false)]
    public void Tailscale_ranges_are_recognised(string address, bool expected) =>
        Assert.Equal(expected, DynamicBay.Services.Link.LinkProtocol.IsTailscaleAddress(address));
}
