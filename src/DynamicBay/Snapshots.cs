using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicBay.Core;
using DynamicBay.Island;
using DynamicBay.Services;

namespace DynamicBay;

/// <summary>
/// Design review helper: `DynamicBay.exe --snapshot &lt;dir&gt;` fills the island with demo data, walks through
/// every state and orientation and saves real screen captures (wallpaper included) as PNGs.
/// </summary>
public static class Snapshots
{
    public static async Task RunAsync(IslandWindow island, IslandViewModel vm, AppSettings settings, string dir)
    {
        Directory.CreateDirectory(dir);
        LoadDemo(vm);
        await Task.Delay(800);

        var placements = new (string name, IslandEdge edge, IslandAlign align, double along)[]
        {
            ("top", IslandEdge.Top, IslandAlign.Center, 0.5),
            ("left", IslandEdge.Left, IslandAlign.Center, 0.5),
            ("bottom", IslandEdge.Bottom, IslandAlign.Center, 0.5),
            ("topright", IslandEdge.Top, IslandAlign.End, 1),
        };
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "settings")
        {
            var win = new Settings.SettingsWindow(settings, vm);
            win.Show();
            for (int page = 0; page <= 10; page++)
            {
                win.ShowPage(page);
                await Task.Delay(700);
                Capture(win, Path.Combine(dir, $"settings-{page:00}.png"));
            }
            win.Close();
            return;
        }

        foreach (var (name, edge, align, along) in placements)
        {
            settings.Edge = edge;
            settings.Align = align;
            settings.Inset = 8;
            settings.Along = along == 1 ? 1 - 8.0 / SystemParameters.PrimaryScreenWidth : along;
            await Task.Delay(900);

            await Shot(island, vm, IslandMode.Idle, Path.Combine(dir, $"{name}-1-idle.png"), compact: false);
            await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, $"{name}-2-compact.png"), compact: true);
            island.SetPeekForSnapshot(new PeekItem
            {
                Image = vm.Clipboard.Items.FirstOrDefault(i => i.IsImage)?.Thumbnail,
                Title = Loc.T("Peek.Screenshot"),
                Subtitle = Loc.T("Peek.ScreenshotHint"),
                Trailing = "1920×1080",
                TrailingBrush = (Brush)Application.Current.FindResource("B.Text3"),
                Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-3-peek.png"), compact: true);
            vm.Tab = 0;
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-4-home.png"), compact: true);
            if (name is "top" or "left")
            {
                vm.Tab = 1;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-5-tray.png"), compact: true);
                vm.Tab = 2;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-6-notifications.png"), compact: true);
                await Shot(island, vm, IslandMode.Drop, Path.Combine(dir, $"{name}-7-drop.png"), compact: true);
            }
        }
    }

    private static async Task Shot(IslandWindow island, IslandViewModel vm, IslandMode mode, string file, bool compact)
    {
        vm.Timer.IsActive = compact;
        island.ForceState(mode);
        await Task.Delay(1300); // let springs settle
        Capture(island, file);
    }

    private static void Capture(Window w, string file)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        Native.GetWindowRect(hwnd, out var r);
        using var bmp = new System.Drawing.Bitmap(r.Width, r.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            // CAPTUREBLT is required, otherwise layered (transparent) windows like the island are skipped.
            IntPtr dst = g.GetHdc(), src = GetDC(IntPtr.Zero);
            BitBlt(dst, 0, 0, r.Width, r.Height, src, r.Left, r.Top, 0x00CC0020 | 0x40000000);
            ReleaseDC(IntPtr.Zero, src);
            g.ReleaseHdc(dst);
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private static void LoadDemo(IslandViewModel vm)
    {
        vm.Media.LoadDemo(DemoCover(), "Midnight Drive", "Neon Harbor");
        vm.Timer.LoadDemo();
        vm.Calendar.LoadDemo();
        vm.Battery.LoadDemo(84, true);

        var shot = DemoScreenshot();
        vm.Clipboard.AddDemo(new ClipItem { Kind = ClipKind.Image, IsScreenshot = true, Thumbnail = shot, PixelWidth = 1920, PixelHeight = 1080 });
        vm.Clipboard.AddDemo(new ClipItem { Kind = ClipKind.Text, Text = "https://github.com/SuperSparrow-sys/dynamic-island-win", Created = DateTime.Now.AddMinutes(-3) });
        vm.Clipboard.AddDemo(new ClipItem { Kind = ClipKind.Text, Text = "Treffen am Freitag um 14 Uhr im Studio, bitte Entwürfe mitbringen.", Created = DateTime.Now.AddMinutes(-12), Pinned = true });

        var demoDir = Path.Combine(Path.GetTempPath(), "DynamicBayDemo");
        Directory.CreateDirectory(demoDir);
        foreach (var name in new[] { "Präsentation.pptx", "Rechnung_Oktober.pdf", "Notizen.txt" })
        {
            var p = Path.Combine(demoDir, name);
            if (!File.Exists(p)) File.WriteAllText(p, "");
            vm.Shelf.AddDemo(new ShelfItem { Path = p, Icon = ShellThumbnail.Get(p, 96, iconOnly: true) });
        }

        vm.Notifications.Access = NotificationAccess.Allowed;
        vm.Notifications.AddDemo(new NotificationItem { App = "WhatsApp", Title = "Lena", Body = "Bist du heute Abend dabei?", Time = DateTime.Now });
        vm.Notifications.AddDemo(new NotificationItem { App = "Outlook", Title = "Design Review", Body = "Beginnt in 10 Minuten · Raum 2", Time = DateTime.Now.AddMinutes(-4) });
    }

    private static BitmapSource DemoCover()
    {
        // Abstract gradient art (no copyrighted cover art in the repo).
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(255, 94, 98), Color.FromRgb(120, 40, 200), 45), null, new Rect(0, 0, 300, 300));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(220, 255, 200, 80), Color.FromArgb(0, 255, 200, 80)), null, new Point(210, 90), 120, 120);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, new Rect(0, 210, 300, 90));
        }
        var rtb = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource DemoScreenshot()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 242, 247)), null, new Rect(0, 0, 320, 180));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 36)), null, new Rect(0, 0, 320, 22));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(10, 132, 255)), null, new Rect(20, 40, 130, 70), 8, 8);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(200, 205, 215)), null, new Rect(170, 40, 130, 12), 6, 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(215, 220, 228)), null, new Rect(170, 62, 100, 12), 6, 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(48, 209, 88)), null, new Rect(20, 125, 280, 36), 8, 8);
        }
        var rtb = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
