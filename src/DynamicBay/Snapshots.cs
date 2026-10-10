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
        // Live: DYNAMICBAY_SNAPSHOT=live - the island runs normally (throwaway settings) for 30 s, so a script can drive the mouse.
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "live")
        {
            // Frame pacing while the mouse button is down (dragging), summarised on every release.
            var gaps = new List<double>();
            double last = -1;
            bool down = false;
            EventHandler onFrame = (_, e) =>
            {
                double t = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
                bool now = System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
                if (now && down && last > 0) gaps.Add(t - last);
                if (!now && down && gaps.Count > 0)
                {
                    var sorted = gaps.OrderBy(g => g).ToList();
                    Log.Info($"LIVE drag: {gaps.Count} frames, median {sorted[sorted.Count / 2]:0.0} ms, over 25 ms: {gaps.Count(g => g > 25)}, max {sorted[^1]:0.0} ms, big: {string.Join(" ", gaps.Select((g, i) => (g, i)).Where(x => x.g > 25).Select(x => $"#{x.i}={x.g:0}"))}");
                    gaps.Clear();
                }
                down = now; last = t;
            };
            System.Windows.Media.CompositionTarget.Rendering += onFrame;
            // DYNAMICBAY_LIVE_CLOCK=1: the open island with only the analog clock (to watch the second hand).
            if (Environment.GetEnvironmentVariable("DYNAMICBAY_LIVE_CLOCK") == "1")
            {
                settings.ClockAnalog = true;
                settings.HomeWidgets.Clear();
                settings.HomeWidgets.Add(Widgets.Clock);
                island.ForceState(IslandMode.Expanded);
            }
            // DYNAMICBAY_LIVE_PEEK=1: a clickable peek with a file (like a Taildrop arrival); logs clicks and opening.
            if (Environment.GetEnvironmentVariable("DYNAMICBAY_LIVE_PEEK") == "1")
            {
                var file = Path.Combine(Path.GetTempPath(), "DynamicBay-Peek-Test.txt");
                File.WriteAllText(file, "test");
                island.ShowPeek(new PeekItem { Title = "DynamicBay-Peek-Test.txt", Subtitle = "Test", Seconds = 20, DragPayload = new[] { file },
                                               OnClick = () => Log.Info("LIVE peek clicked") });
                bool wasOpen = false;
                for (int i = 0; i < 280; i++)
                {
                    await Task.Delay(100);
                    if (island.IsExpandedForTest != wasOpen) { wasOpen = island.IsExpandedForTest; Log.Info($"LIVE island {(wasOpen ? "opened" : "closed")}"); }
                }
            }
            else await Task.Delay(30000);
            System.Windows.Media.CompositionTarget.Rendering -= onFrame;
            return;
        }
        // Leak check: DYNAMICBAY_SNAPSHOT=leak - opens and closes the settings window five times; memory must not grow.
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "leak")
        {
            static long Live() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true) / 1048576; }
            var w0 = new Settings.SettingsWindow(settings, vm); w0.Show(); await Task.Delay(800); w0.Close(); await Task.Delay(500);
            long before = Live();
            for (int i = 0; i < 5; i++)
            {
                var w = new Settings.SettingsWindow(settings, vm);
                w.Show(); await Task.Delay(800); w.Close(); await Task.Delay(500);
            }
            Log.Info($"LEAK settings window x5: {before} MB -> {Live()} MB managed");
            return;
        }
        // CPU per state: DYNAMICBAY_SNAPSHOT=perf - measures this process in each island state for 15 s.
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "perf")
        {
            settings.CompactItems.Clear();
            foreach (var w in new[] { Widgets.Media }) settings.CompactItems.Add(w);
            // DYNAMICBAY_SNAPSHOT_FPS=60 measures with the frame rate setting of that value.
            if (int.TryParse(Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_FPS"), out int fps)) Motion.FrameRate.Limit = fps;
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            async Task Measure(string label, IslandMode mode, bool playing)
            {
                vm.Media.IsPlaying = playing;
                vm.Timer.IsActive = false;
                island.ForceState(mode);
                await Task.Delay(3000);
                me.Refresh();
                var c0 = me.TotalProcessorTime;
                await Task.Delay(15000);
                me.Refresh();
                Log.Info($"PERF {label}: {(me.TotalProcessorTime - c0).TotalSeconds / 15 * 100:0.0} % of one core");
            }
            await Measure("idle bar, nothing playing", IslandMode.Idle, false);
            await Measure("compact, music playing (waveform)", IslandMode.Compact, true);
            await Measure("compact, music paused", IslandMode.Compact, false);
            await Measure("expanded, music playing", IslandMode.Expanded, true);
            // Open/close transitions: frame pacing while the island morphs (blur, scale, springs).
            var gaps = new List<double>();
            var open = new List<double>();
            var close = new List<double>();
            List<double> current = open;
            double lastFrame = -1;
            bool recording = false;
            EventHandler onFrame = (_, e) =>
            {
                double t = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
                if (recording && lastFrame > 0) { gaps.Add(t - lastFrame); current.Add(t - lastFrame); }
                lastFrame = t;
            };
            System.Windows.Media.CompositionTarget.Rendering += onFrame;
            me.Refresh();
            var t0 = me.TotalProcessorTime;
            for (int i = 0; i < 8; i++)
            {
                foreach (var mode in new[] { IslandMode.Expanded, IslandMode.Compact })
                {
                    lastFrame = -1; recording = true;
                    current = mode == IslandMode.Expanded ? open : close;
                    island.ForceState(mode);
                    await Task.Delay(650);
                    recording = false;
                    await Task.Delay(250);
                }
            }
            me.Refresh();
            System.Windows.Media.CompositionTarget.Rendering -= onFrame;
            gaps.Sort();
            static string Stat(List<double> g) => $"{g.Count} frames, over 25 ms: {g.Count(x => x > 25)}, max {(g.Count > 0 ? g.Max() : 0):0} ms";
            Log.Info($"PERF opening: {Stat(open)}; closing: {Stat(close)}");
            Log.Info($"PERF gen0 collections so far: {GC.CollectionCount(0)}, gen2: {GC.CollectionCount(2)}");
            Log.Info($"PERF transitions: {(me.TotalProcessorTime - t0).TotalSeconds / 14.4 * 100:0.0} % of one core, {gaps.Count} frames, "
                     + $"median {gaps[gaps.Count / 2]:0.0} ms, 95th {gaps[(int)(gaps.Count * 0.95)]:0.0} ms, max {gaps[^1]:0.0} ms, over 25 ms: {gaps.Count(g => g > 25)}");
            return;
        }

        // Devices widget with the real Bluetooth devices: DYNAMICBAY_SNAPSHOT_DEVICES=1
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_DEVICES") == "1")
        {
            vm.Bluetooth?.Start();
            settings.HomeWidgets.Clear();
            foreach (var w in new[] { Widgets.Devices, Widgets.Media, Widgets.Calendar }) settings.HomeWidgets.Add(w);
            await Task.Delay(5000);
            foreach (var (name, edge, align, along) in placements.Where(p => p.name is "top" or "left"))
            {
                settings.Edge = edge; settings.Align = align; settings.Inset = 8; settings.Along = along;
                await Task.Delay(900);
                vm.Tab = 0;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-devices.png"), compact: false);
            }
            Log.Info("Devices snapshot: " + string.Join(", ", vm.Bluetooth?.Devices.Select(d => $"{d.Name}={d.Kind}/{d.Battery}") ?? Array.Empty<string>()));
            return;
        }

        // Script widgets: DYNAMICBAY_SNAPSHOT_SCRIPT=<file.js> - the script as large and small card and as Mini line.
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_SCRIPT") is { Length: > 0 } scriptFile && Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") != "settings")
        {
            var large = new ScriptWidgetConfig { Id = "snapL", Name = Path.GetFileNameWithoutExtension(scriptFile), File = Path.GetFullPath(scriptFile), Size = ScriptSize.Large, ShowInCompact = true };
            var small = new ScriptWidgetConfig { Id = "snapS", Name = large.Name, File = large.File, Size = ScriptSize.Small };
            settings.ScriptWidgets.Add(large);
            settings.ScriptWidgets.Add(small);
            settings.HomeWidgets.Clear();
            foreach (var w in new[] { large.HomeId, small.HomeId, Widgets.Calendar }) settings.HomeWidgets.Add(w);
            settings.CompactItems.Clear();
            vm.Scripts.Start();
            // network + script runs: wait until every size has a result
            for (int i = 0; i < 100 && (vm.Scripts.Get(large.Id, "accessoryInline") is null || vm.Scripts.Get(large.Id, "medium") is null || vm.Scripts.Get(small.Id, "small") is null); i++)
                await Task.Delay(200);
            await Task.Delay(1500);
            foreach (var (name, edge, align, along) in placements.Where(p => p.name is "top" or "left"))
            {
                settings.Edge = edge; settings.Align = align; settings.Inset = 8; settings.Along = along;
                await Task.Delay(900);
                vm.Tab = 0;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-script-cards.png"), compact: false);
                await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, $"{name}-script-mini.png"), compact: false);
            }
            Log.Info("Script snapshot: " + vm.Scripts.StatusOf(large) + " | " + vm.Scripts.StatusOf(small) + $" | compact ids={vm.Scripts.CompactIds.Count} show={vm.ShowScripts} has={vm.HasCompact} mini={(vm.Scripts.Get(large.Id, "accessoryInline")?.Widget is not null)}");
            return;
        }
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "settings")
        {
            if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_SCRIPT") is { Length: > 0 } sf)
            {
                var cfg = new ScriptWidgetConfig { Id = "snapL", Name = Path.GetFileNameWithoutExtension(sf), File = Path.GetFullPath(sf), ShowInCompact = true };
                settings.ScriptWidgets.Add(cfg);
                settings.HomeWidgets.Add(cfg.HomeId);
                vm.Scripts.Start();
                await Task.Delay(4000);
            }
            foreach (var app in InstalledApps.All().Take(3)) settings.Shortcuts.Add(app.LaunchPath); // launcher list with tiles
            // Time tracking window with a demo day (the user's file is not touched).
            {
                var d = DateTime.Today;
                settings.TimeProjects.Clear();
                foreach (var p in new[] { "Kunde Müller", "Intern", "Angebot Berlin" }) settings.TimeProjects.Add(p);
                vm.Time.LoadDemo(new[]
                {
                    new TimeEntry { Project = "Intern", Start = d.AddHours(8), End = d.AddHours(9), Description = "Mails und Planung" },
                    new TimeEntry { Project = "Kunde Müller", Start = d.AddHours(9), End = d.AddHours(11.5), Description = "Workshop Speicherauslegung" },
                    new TimeEntry { Project = "Angebot Berlin", Start = d.AddHours(13), End = d.AddHours(14.25) },
                    new TimeEntry { Project = "Kunde Müller", Start = d.AddDays(-1).AddHours(9), End = d.AddDays(-1).AddHours(12) },
                });
                var tw = new Settings.TimeTrackingWindow(vm.Time, settings);
                tw.Show();
                await Task.Delay(900);
                RenderWindow(tw, Path.Combine(dir, "time-window.png"));
                tw.Close();
            }
            var win = new Settings.SettingsWindow(settings, vm);
            win.Show();
            for (int page = 0; page <= 11; page++)
            {
                win.ShowPage(page);
                await Task.Delay(700);
                RenderWindow(win, Path.Combine(dir, $"settings-{page:00}.png"));
            }
            // Calendar page with the CalDAV form open
            win.ShowPage(9);
            if (win.FindName("ICloudForm") is FrameworkElement form) { form.Visibility = Visibility.Visible; await Task.Delay(300); form.BringIntoView(); }
            await Task.Delay(700);
            RenderWindow(win, Path.Combine(dir, "settings-10b-caldav.png"));
            // Widgets page, scrolled to the script widgets
            win.ShowPage(11);
            await Task.Delay(500);
            if (win.FindName("ScriptRows") is FrameworkElement rows) rows.BringIntoView();
            await Task.Delay(500);
            RenderWindow(win, Path.Combine(dir, "settings-11b-scripts.png"));
            if (win.FindName("PageScroll") is System.Windows.Controls.ScrollViewer ps) { ps.ScrollToVerticalOffset(ps.VerticalOffset + 650); await Task.Delay(500); RenderWindow(win, Path.Combine(dir, "settings-11c-launcher.png")); }
            // Behaviour page, scrolled down to "hide while these apps are active"
            win.ShowPage(3);
            await Task.Delay(500);
            (win.FindName("PageScroll") as System.Windows.Controls.ScrollViewer)?.ScrollToEnd();
            await Task.Delay(500);
            RenderWindow(win, Path.Combine(dir, "settings-03b-excluded.png"));
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
            vm.Spotify.IsConnected = true; // shows shuffle, like, repeat and device buttons next to the transport
            vm.Spotify.Shuffle = true;
            vm.Tab = 0;
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-4-home.png"), compact: true);
            vm.Calendar.SelectDayCommand.Execute(vm.Calendar.Week.First(d => d.Date == DateTime.Today.AddDays(1)));
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-4b-calendar-tomorrow.png"), compact: true);
            vm.Calendar.SelectDayCommand.Execute(vm.Calendar.Week.First(d => d.IsToday));
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
                // Office widgets: Microsoft To Do and the Teams status.
                if (vm.Todo is not null && vm.Presence is not null)
                {
                    vm.Todo.LoadDemo();
                    vm.Presence.LoadDemo();
                    vm.Contacts?.LoadDemo();
                    settings.HomeWidgets.Clear();
                    settings.ClockAnalog = true;
                    foreach (var w in new[] { Widgets.Calendar, Widgets.Contacts, Widgets.TimeTrack, Widgets.Clock, Widgets.Teams }) settings.HomeWidgets.Add(w);
                    await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8c-office.png"), compact: true);
                }
                // Audio widget: outputs, microphone, Bluetooth headsets.
                vm.AudioDevices.LoadDemo();
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.Media, Widgets.Audio, Widgets.Devices }) settings.HomeWidgets.Add(w);
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8d-audio.png"), compact: true);
                // Second page "System": sound and devices live there
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.Media, Widgets.Calendar }) settings.HomeWidgets.Add(w);
                foreach (var w in new[] { Widgets.Audio, Widgets.Devices }) settings.SystemWidgets.Add(w);
                vm.Tab = 3;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8g-system-tab.png"), compact: true);
                vm.Tab = 0;
                settings.SystemWidgets.Clear();
                // Work widgets side by side: headers, rows and radii must line up.
                vm.Notes.LoadDemo();
                settings.TimeProjects.Clear();
                foreach (var p in new[] { "Allgemein", "automation-frank", "Angebot Berlin" }) settings.TimeProjects.Add(p);
                vm.Time.LoadDemo(new[] { new TimeEntry { Project = "automation-frank", Start = DateTime.Today.AddHours(8), End = DateTime.Today.AddHours(9) } });
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.TimeTrack, Widgets.Notes, Widgets.Audio, Widgets.Todo, Widgets.Devices }) settings.HomeWidgets.Add(w);
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8e-work.png"), compact: true);
                // Spotify: up next / playlists / devices over the player
                vm.Spotify.LoadDemoQueue();
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.Media, Widgets.Calendar }) settings.HomeWidgets.Add(w);
                island.ExpandedLayer.ShowMusicOverlayForSnapshot();
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8f-upnext.png"), compact: true);
                island.ExpandedLayer.DevicesOverlay.Visibility = Visibility.Collapsed;
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

    /// <summary>Renders the window content itself (not the screen), so other windows cannot cover it.</summary>
    private static void RenderWindow(Window w, string file)
    {
        var root = (FrameworkElement)w.Content;
        double k = VisualTreeHelper.GetDpi(w).DpiScaleX;
        int pw = (int)(root.ActualWidth * k), ph = (int)(root.ActualHeight * k);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = w.Background is SolidColorBrush b && b.Color.A > 0 ? b : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            dc.DrawRectangle(bg, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        var bmp = new RenderTargetBitmap(pw, ph, 96 * k, 96 * k, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
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
        // DYNAMICBAY_SNAPSHOT_TITLE: check how a long track title fits.
        vm.Media.LoadDemo(DemoCover(), Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_TITLE") ?? "Midnight Drive", "Neon Harbor");
        vm.MusicVolume.LoadDemo(65);
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

