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
            for (int page = 0; page <= 11; page++)
            {
                win.ShowPage(page);
                await Task.Delay(700);
                Capture(win, Path.Combine(dir, $"settings-{page:00}.png"));
            }
            // Calendar page with the CalDAV form open
            win.ShowPage(9);
            if (win.FindName("ICloudForm") is FrameworkElement form) { form.Visibility = Visibility.Visible; await Task.Delay(300); form.BringIntoView(); }
            await Task.Delay(700);
            Capture(win, Path.Combine(dir, "settings-10b-caldav.png"));
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

                // Customized layout: different widgets and the clock as a live activity.
                var saved = settings.HomeWidgets.ToList();
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.Clock, Widgets.Media, Widgets.System, Widgets.Shortcuts }) settings.HomeWidgets.Add(w);
                settings.Shortcuts.Clear();
                var installed = InstalledApps.All();
                foreach (var want in new[] { "WhatsApp", "Discord", "Google Chrome", "Obsidian", "Rechner", "Calculator", "Spotify", "Zotero", "Opera-Browser" })
                {
                    var app = installed.FirstOrDefault(a => a.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
                    if (app is not null && settings.Shortcuts.Count < 8) settings.Shortcuts.Add(app.LaunchPath);
                }
                settings.CompactItems.Add(Widgets.Clock);
                vm.Tab = 0;
                settings.AppIcons = AppIconStyle.Mono;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8-widgets.png"), compact: true);
                settings.AppIcons = AppIconStyle.Dark;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8b-widgets-dark.png"), compact: true);
                settings.AppIcons = AppIconStyle.Mono;
                await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, $"{name}-9-compact-clock.png"), compact: true);

                // Audio: real readings first (logged), then demo states for the images.
                vm.Audio.Start();
                await Task.Delay(700);
                Log.Info($"Audio check: speakerMuted={vm.Audio.SpeakerMuted} volume={vm.Audio.Volume} micMuted={vm.Audio.MicMuted} micInUse={vm.Audio.MicInUse} '{vm.Audio.MicApp}' camera={vm.Audio.CameraInUse}");
                vm.Audio.LoadDemo(micInUse: true, app: "Teams", micMuted: false, speakerMuted: true);
                await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, $"{name}-11-mic.png"), compact: false);
                vm.Audio.LoadDemo(micInUse: true, app: "Teams", micMuted: true, speakerMuted: false);
                await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, $"{name}-12-mic-muted.png"), compact: false);
                island.SetPeekForSnapshot(new PeekItem
                {
                    Icon = (Geometry)Application.Current.FindResource("Icon.Mic"),
                    IconBrush = (Brush)Application.Current.FindResource("B.Orange"),
                    IconBackground = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0x9F, 0x0A)),
                    Title = "Mikrofon aktiv", Subtitle = "Teams", ActionText = "Stumm", DismissText = "OK", Seconds = 60,
                });
                await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-13-mic-question.png"), compact: false);
                vm.Audio.LoadDemo(micInUse: false, app: "", micMuted: false, speakerMuted: false);
                vm.Tab = 0;
                vm.AltHeld = true; // hidden quit button revealed
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-14-alt-quit.png"), compact: true);
                vm.AltHeld = false;

                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.Media, Widgets.Claude, Widgets.Calendar, Widgets.Timer, Widgets.Shortcuts, Widgets.Clock }) settings.HomeWidgets.Add(w);
                vm.Claude.Start();
                await Task.Delay(800);
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-10-claude.png"), compact: true);
                settings.CompactItems.Remove(Widgets.Clock);
                settings.HomeWidgets.Clear();
                foreach (var w in saved) settings.HomeWidgets.Add(w);
            }
        }
    }

    private static async Task Shot(IslandWindow island, IslandViewModel vm, IslandMode mode, string file, bool compact)
    {
        vm.Timer.IsActive = compact;
        island.ForceState(mode);
        await Task.Delay(1300); // let springs settle
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "readme") RenderClean(island, file);
        else Capture(island, file);
    }

    /// <summary>
    /// Renders the island itself (not the screen) over a neutral gradient at 2x - for README images without
    /// anything private from the desktop behind it.
    /// </summary>
    private static void RenderClean(Window island, string file)
    {
        var root = (FrameworkElement)island.Content;
        var shape = (FrameworkElement)island.FindName("Shape");
        // Crop: the island's own bounds plus a margin for the shadow (in the root's coordinate space).
        var b = shape.TransformToAncestor(root).TransformBounds(new Rect(0, 0, shape.ActualWidth, shape.ActualHeight));
        var crop = Rect.Inflate(b, 36, 30);
        double w = crop.Width, h = crop.Height, k = 2;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x1E, 0x2A, 0x4A), Color.FromRgb(0x6B, 0x4E, 0x9B), 35);
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(120, 0xFF, 0x9F, 0x6B), Color.FromArgb(0, 0xFF, 0x9F, 0x6B)), null, new Point(w * 0.8, h * 0.9), w * 0.5, h * 0.8);
            // Absolute viewbox = exact 1:1 mapping (a default VisualBrush would stretch the content bounds and distort).
            var brush = new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = crop, Stretch = Stretch.None };
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)(w * k), (int)(h * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        rtb.Render(dv);
        ImageTools.SavePng(rtb, file);
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
        vm.Notifications.AddDemo(new NotificationItem { App = "Outlook", Title = "Design Review", Body = "Beginnt in 10 Minuten Â· Raum 2", Time = DateTime.Now.AddMinutes(-4) });
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

