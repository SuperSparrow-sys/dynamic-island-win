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
        // Compact symbols: DYNAMICBAY_SNAPSHOT=compact - sound on the laptop, time tracking and Claude with long names,
        // horizontal and vertical (long names belong in the open island, not in the small one).
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "compact")
        {
            foreach (var (vertical, working) in new[] { (false, 1), (false, 2), (true, 1) })
            {
                var view = new Island.Views.CompactView { DataContext = new CompactDemo(working) };
                view.SetVertical(vertical);
                var w = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
                    Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), Left = 100, Top = 100,
                    Content = new System.Windows.Controls.Border
                    {
                        Background = Brushes.Black, CornerRadius = new CornerRadius(18), Margin = new Thickness(16),
                        Width = vertical ? 40 : double.NaN, Height = vertical ? double.NaN : 36, Child = view,
                    },
                };
                w.Show();
                await Task.Delay(600);
                RenderWindow(w, Path.Combine(dir, $"compact-{(vertical ? "vertical" : "horizontal")}-{working}.png"));
                w.Close();
            }
            return;
        }
        // Morph frames: DYNAMICBAY_SNAPSHOT=frames - the minimal idle line opening and closing, every rendered frame as a
        // PNG at DYNAMICBAY_SNAPSHOT_SCALE (default 1.5 = 150 %), to check that the line does not jitter.
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "frames")
        {
            await MorphFramesAsync(island, vm, settings, dir);
            return;
        }
        // Leak check: DYNAMICBAY_SNAPSHOT=leak - cycles settings window, island states, widgets, scripts, covers and a
        // mirror island; memory must not grow (see docs/performance/README.md).
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") == "leak")
        {
            await LeakCheck.RunAsync(island, vm, settings, dir);
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

        // Devices and AirPods widgets with the real Bluetooth devices: DYNAMICBAY_SNAPSHOT_DEVICES=1
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_DEVICES") == "1")
        {
            vm.Bluetooth?.Start();
            if (vm.Bluetooth is not null) vm.AirPods.Start(vm.Bluetooth);
            settings.HomeWidgets.Clear();
            foreach (var w in new[] { Widgets.AirPods, Widgets.Devices, Widgets.Calendar }) settings.HomeWidgets.Add(w);
            await Task.Delay(5000);
            foreach (var (name, edge, align, along) in placements.Where(p => p.name is "top" or "left"))
            {
                settings.Edge = edge; settings.Align = align; settings.Inset = 8; settings.Along = along;
                await Task.Delay(900);
                vm.Tab = 0;
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-devices.png"), compact: false);
            }
            Log.Info("Devices snapshot: " + string.Join(", ", vm.Bluetooth?.Devices.Select(d => $"{d.Name}={d.Kind}/{d.Battery}") ?? Array.Empty<string>()));
            Log.Info($"AirPods snapshot: {vm.AirPods.Name} connected={vm.AirPods.Connected} reading={vm.AirPods.HasReading} L={vm.AirPods.Left} R={vm.AirPods.Right} case={vm.AirPods.Case}");
            return;
        }

        // Rubus: DYNAMICBAY_SNAPSHOT_RUBUS=1 - Rubus sessions in the Claude card, the approval question (two buttons and
        // window only) and the compact island, with demo data (nothing listens, nothing goes to Rubus).
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_RUBUS") == "1")
        {
            await RubusShotsAsync(island, vm, settings, dir);
            return;
        }

        // CursorHop: DYNAMICBAY_SNAPSHOT_CURSORHOP=1 - the card on the System page (mouse there, here, not running)
        // and the peeks, with demo data (CursorHop is neither started nor read).
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_CURSORHOP") == "1")
        {
            await CursorHopShotsAsync(island, vm, settings, dir, placements.Where(p => p.name is "top" or "left"));
            return;
        }

        // Paired devices: DYNAMICBAY_SNAPSHOT_LINK=1 - the card, music of the laptop, the pairing peek, a file from the
        // laptop and its notification in the list, with demo data (no connection is made).
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_LINK") == "1" && Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT") != "settings")
        {
            await LinkShotsAsync(island, vm, settings, dir, placements.Where(p => p.name is "top" or "left"));
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
            // Calendar accounts as after a settings import: Google and iCloud without their sign-in show "Anmelden"
            settings.CalendarAccounts.Add(new CalendarAccount { Id = "snapgoogle01", Kind = CalendarKind.Google, Name = "Google", User = "1234.apps.googleusercontent.com" });
            settings.CalendarAccounts.Add(new CalendarAccount { Id = "snapicloud01", Kind = CalendarKind.ICloud, Name = "iCloud", User = "name@icloud.com", Url = "https://caldav.icloud.com/" });
            settings.CalendarAccounts.Add(new CalendarAccount { Id = "snapics00001", Kind = CalendarKind.Ics, Name = "Feiertage", Url = "https://example.com/feiertage.ics" });
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
            // Code dialogs of the settings export and import (the import one with the wrong-code message)
            foreach (bool export in new[] { true, false })
            {
                var pw = export
                    ? new Settings.PinWindow("Einstellungen exportieren", "Passwörter, Anmeldungen und Kopplungen bleiben auf diesem PC.", "Weiter", confirm: true)
                    : new Settings.PinWindow("Einstellungen importieren", "Ersetzt alle Einstellungen, danach startet DynamicBay neu.", "Importieren", confirm: false, check: _ => "Falscher Code.");
                pw.Show();
                ((System.Windows.Controls.PasswordBox)pw.FindName("Pin")).Password = "4829137705";
                if (export) ((System.Windows.Controls.PasswordBox)pw.FindName("Repeat")).Password = "4829137705";
                else ((System.Windows.Controls.Button)pw.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Task.Delay(600);
                RenderWindow(pw, Path.Combine(dir, export ? "pin-export.png" : "pin-import.png"));
                pw.Close();
            }
            // Devices page with two paired devices, one found nearby and the pairing window open; the code dialog.
            settings.LinkEnabled = true;
            vm.Link?.LoadDemo(DemoCover());
            {
                var pw = new Settings.PairingWindow(null, "LaptopJonathan", "482913");
                pw.Show();
                await Task.Delay(600);
                RenderWindow(pw, Path.Combine(dir, "pairing-code.png"));
                pw.ShowResult(Services.Link.PairingResult.Paired, "LaptopJonathan");
                await Task.Delay(300);
                RenderWindow(pw, Path.Combine(dir, "pairing-code-done.png"));
                pw.Close();
            }
            var win = new Settings.SettingsWindow(settings, vm);
            win.Show();
            // Every page from top to bottom: settings-PP-N.png, one picture per screen height.
            var scroll = (System.Windows.Controls.ScrollViewer)win.FindName("PageScroll");
            foreach (var page in Enum.GetValues<SettingsPage>())
            {
                win.ShowPage(page);
                await Task.Delay(700);
                for (int part = 0; ; part++)
                {
                    RenderWindow(win, Path.Combine(dir, $"settings-{(int)page:00}-{part}.png"));
                    if (scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 1 || part > 6) break;
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scroll.ViewportHeight - 60);
                    await Task.Delay(400);
                }
            }
            // Accounts page with the CalDAV form open
            win.ShowPage(SettingsPage.Accounts);
            if (win.FindName("ICloudForm") is FrameworkElement form) { form.Visibility = Visibility.Visible; await Task.Delay(300); form.BringIntoView(); }
            await Task.Delay(700);
            RenderWindow(win, Path.Combine(dir, "settings-12b-caldav.png"));
            // Switching the theme while the window is open (demo settings, not saved)
            settings.WindowTheme = Settings.SettingsTheme.IsLight() ? "dark" : "light";
            win.ShowPage(SettingsPage.Appearance);
            await Task.Delay(700);
            RenderWindow(win, Path.Combine(dir, "settings-01b-theme.png"));
            win.ShowPage(SettingsPage.Content);
            await Task.Delay(500);
            RenderWindow(win, Path.Combine(dir, "settings-05b-theme.png"));
            win.Close();
            return;
        }

        // Notch style: DYNAMICBAY_SNAPSHOT_NOTCH=1 - idle, compact, peek and open at the top edge.
        if (Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_NOTCH") == "1")
        {
            settings.ShapeStyle = IslandShape.Notch;
            settings.Edge = IslandEdge.Top; settings.Align = IslandAlign.Center; settings.Along = 0.5;
            await Task.Delay(900);
            await Shot(island, vm, IslandMode.Idle, Path.Combine(dir, "notch-1-idle.png"), compact: false);
            await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, "notch-2-compact.png"), compact: true);
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, "notch-4-home.png"), compact: true);
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
                // AirPods widget: earbuds and case, then not connected (connect chip).
                vm.AirPods.LoadDemo();
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.AirPods, Widgets.Devices, Widgets.Media }) settings.HomeWidgets.Add(w);
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8i-airpods.png"), compact: true);
                vm.AirPods.LoadDemo(connected: false);
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8j-airpods-off.png"), compact: true);
                // Second page "System": sound and devices live there
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.Media, Widgets.Calendar }) settings.HomeWidgets.Add(w);
                foreach (var w in new[] { Widgets.Audio, Widgets.Devices, Widgets.CursorHop }) settings.SystemWidgets.Add(w);
                vm.CursorHop.LoadDemo("Desktop-PC", cursorThere: true);
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
                island.ExpandedLayer.ShowNoteSheetForSnapshot(new NoteItem { Text = "Rückruf Herr Wagner morgen 10 Uhr wegen Angebot Speicher" });
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8h-note-sheet.png"), compact: true);
                island.ExpandedLayer.HideNoteSheetForSnapshot();
                // Spotify: up next / playlists / devices over the player
                vm.Spotify.LoadDemoQueue();
                settings.HomeWidgets.Clear();
                foreach (var w in new[] { Widgets.Media, Widgets.Calendar }) settings.HomeWidgets.Add(w);
                island.ExpandedLayer.ShowMusicOverlayForSnapshot();
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8f-upnext.png"), compact: true);
                island.ExpandedLayer.DevicesOverlay.Visibility = Visibility.Collapsed;
                // Spotify and YouTube at once: the media card is a stack with one dot per player.
                vm.Media.LoadDemoPages(new[] { "Spotify", "Chrome" }, 0);
                await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-8k-media-stack.png"), compact: true);
                vm.Media.LoadDemoPages(new[] { "Spotify" }, 0);
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

    private static async Task RubusShotsAsync(IslandWindow island, IslandViewModel vm, AppSettings settings, string dir)
    {
        var color = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));
        var tint = new SolidColorBrush(Color.FromArgb(0x33, 0xD9, 0x77, 0x57));
        var sparkles = (Geometry)Application.Current.FindResource("Glyph.Claude");
        var now = DateTime.Now;
        var deploy = new RubusAnfrage("0123456789abcdef", "rubus-17", Loc.German ? "Claude möchte auf test deployen" : "Claude wants to deploy to test", "automation", "frank-raspi", false);
        vm.Claude.Start();
        vm.Claude.LoadRubusDemo(new RubusStand(new[]
        {
            new RubusSitzung("rubus-17", "Umbau Modbus-Abfrage", "frank-raspi", "automation", RubusZustand.Wartet, "freigabe", "", now.AddMinutes(-1)),
            new RubusSitzung("rubus-21", "Auswertung control_data", "frank-raspi", "automation", RubusZustand.Wartet, "eingabe",
                Loc.German ? "Soll ich die Auffälligkeit nachverfolgen?" : "Should I follow up on the anomaly?", now.AddMinutes(-2)),
            new RubusSitzung("rubus-18", "Dashboard Ladeplan", "homeserver", "rubus", RubusZustand.Arbeitet, null, "", now.AddMinutes(-3)),
            new RubusSitzung("rubus-19", "Doku Anlagen", "homeserver", "vault", RubusZustand.Bereit, null, "", now.AddMinutes(-25)),
            new RubusSitzung("rubus-20", "Tests Prognose", "homeserver", "ml-hw1", RubusZustand.Bereit, "fehler", "API Error: 529 overloaded", now.AddMinutes(-9), now.AddMinutes(-2)),
        }, new[] { deploy }));
        // At the bottom: the installed island at the top must not cover these shots.
        settings.Edge = IslandEdge.Bottom; settings.Align = IslandAlign.Center; settings.Inset = 8; settings.Along = 0.5;
        settings.HomeWidgets.Clear();
        foreach (var w in new[] { Widgets.Claude, Widgets.Media }) settings.HomeWidgets.Add(w);
        vm.Tab = 0;
        await Task.Delay(900);
        await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, "rubus-1-list.png"), compact: false);
        island.SetPeekForSnapshot(App.RubusQuestion(deploy, sparkles, color, tint, _ => { }));
        await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, "rubus-2-question.png"), compact: false);
        island.SetPeekForSnapshot(App.RubusQuestion(deploy with { Titel = Loc.German ? "Claude möchte auf prod deployen" : "Claude wants to deploy to prod", NurImFenster = true },
            sparkles, color, tint, _ => { }));
        await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, "rubus-3-window-only.png"), compact: false);
        settings.CompactItems.Clear();
        settings.CompactItems.Add(Widgets.Claude);
        await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, "rubus-4-compact.png"), compact: false);
    }

    private static async Task CursorHopShotsAsync(IslandWindow island, IslandViewModel vm, AppSettings settings, string dir,
        IEnumerable<(string name, IslandEdge edge, IslandAlign align, double along)> placements)
    {
        Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
        Brush Tint(string key)
        {
            var c = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            return new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        }
        vm.AudioDevices.LoadDemo();
        settings.SystemWidgets.Clear();
        foreach (var w in new[] { Widgets.CursorHop, Widgets.Audio, Widgets.Devices }) settings.SystemWidgets.Add(w);
        foreach (var (name, edge, align, along) in placements)
        {
            settings.Edge = edge; settings.Align = align; settings.Inset = 8; settings.Along = along;
            await Task.Delay(900);
            vm.Tab = 3;
            vm.CursorHop.LoadDemo("Desktop-PC", cursorThere: true);
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-cursorhop-1-there.png"), compact: false);
            vm.CursorHop.LoadDemo("LaptopJonathan", cursorThere: false);
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-cursorhop-2-here.png"), compact: false);
            vm.CursorHop.LoadDemo(null, cursorThere: false, running: false);
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-cursorhop-3-stopped.png"), compact: false);
            vm.CursorHop.LoadDemo(null, cursorThere: false);
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-cursorhop-4-waiting.png"), compact: false);
            vm.Tab = 0;
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon(CursorHopService.IconFor("LaptopJonathan")), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue"),
                Title = Loc.German ? "LaptopJonathan verbunden" : "LaptopJonathan connected", Subtitle = "CursorHop · " + CursorHopService.CursorTextFor(null), Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-cursorhop-5-peek-connected.png"), compact: false);
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon(CursorHopService.IconFor("Desktop-PC")), IconBrush = Res("B.Text2"), IconBackground = Tint("B.Text"),
                Title = Loc.German ? "Desktop-PC getrennt" : "Desktop-PC disconnected", Subtitle = "CursorHop", Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-cursorhop-6-peek-disconnected.png"), compact: false);
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.Clipboard"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue"),
                Title = Loc.German ? "Zwischenablage von Desktop-PC" : "Clipboard from Desktop-PC",
                Subtitle = CursorHopService.FormatText("PlainText") + (Loc.German ? " · Strg+V fügt ein" : " · Ctrl+V pastes"), Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-cursorhop-7-peek-clipboard.png"), compact: false);
        }
        // The clipboard list with an entry from the other PC.
        var clip = new ClipItem { Kind = ClipKind.Text, Text = "Angebot_Speicher_v3.pdf liegt im Teams-Ordner", SourceDevice = "Desktop-PC" };
        vm.Clipboard.Items.Insert(0, clip);
        vm.Tab = 1;
        await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, "cursorhop-8-clipboard-source.png"), compact: false);
    }

    private static async Task LinkShotsAsync(IslandWindow island, IslandViewModel vm, AppSettings settings, string dir,
        IEnumerable<(string name, IslandEdge edge, IslandAlign align, double along)> placements)
    {
        Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
        Brush Tint(string key)
        {
            var c = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            return new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        }
        var link = vm.Link!;
        settings.LinkEnabled = true;
        link.LoadDemo(DemoCover());
        vm.CursorHop.LoadDemo("LaptopJonathan", cursorThere: false);
        vm.AudioDevices.LoadDemo();
        settings.SystemWidgets.Clear();
        foreach (var w in new[] { Widgets.Paired, Widgets.CursorHop, Widgets.Audio }) settings.SystemWidgets.Add(w);
        vm.Notifications.AddRemote("LaptopJonathan", "Teams", "Anna Weber", "Hast du kurz Zeit für das Angebot?", null);
        foreach (var (name, edge, align, along) in placements)
        {
            settings.Edge = edge; settings.Align = align; settings.Inset = 8; settings.Along = along;
            await Task.Delay(900);
            vm.Tab = 3;
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-1-card.png"), compact: false);
            vm.Tab = 0;
            link.PreferRemote = true;
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-2-remote-music.png"), compact: false);
            link.PreferRemote = false;
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-3-local-music-chip.png"), compact: false);
            vm.Tab = 2;
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-4-notification.png"), compact: false);
            vm.Tab = 0;
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.Link"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue"),
                Title = "Code 482 913", Subtitle = Loc.German ? "Mit LaptopJonathan koppeln?" : "Pair with LaptopJonathan?",
                ActionText = Loc.German ? "Bestätigen" : "Confirm", DismissText = Loc.German ? "Ablehnen" : "Decline", Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-link-5-pairing-peek.png"), compact: false);
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.Download"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue"),
                Title = "Angebot_Speicher_v3.pdf", Subtitle = (Loc.German ? "Von " : "From ") + "LaptopJonathan · 48.2 MB",
                Progress = 0.62, TrailingBrush = Res("B.Blue"), Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-link-6-file-progress.png"), compact: false);
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.Laptop"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue"),
                Title = Loc.German ? "LaptopJonathan verbunden" : "LaptopJonathan connected", Subtitle = Loc.German ? "Gekoppeltes Gerät" : "Paired device", Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-link-7-connected.png"), compact: false);
            // Working together: a question of the laptop on the main device, the laptop's battery, the shared timer.
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.Mic"), IconBrush = Res("B.Orange"), IconBackground = Tint("B.Orange"),
                Title = Loc.German ? "Mikrofon aktiv" : "Microphone in use", Subtitle = "Teams · LaptopJonathan",
                ActionText = Loc.German ? "Stumm" : "Mute", DismissText = "OK", Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-link-8-question.png"), compact: false);
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.BatteryLow"), IconBrush = Res("B.Red"), IconBackground = Tint("B.Red"),
                Title = Loc.German ? "LaptopJonathan: Akku 15 %" : "LaptopJonathan: battery 15 %", Subtitle = Loc.T("Battery.Low"), Progress = 0.15, Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-link-9-battery.png"), compact: false);
            vm.Timer.LoadDemo();
            link.CanHandOverTimer = true; // the demo has no connected device: show the button as with one
            link.HandOverText = Loc.German ? "An LaptopJonathan übergeben" : "Hand over to LaptopJonathan";
            island.ForceState(IslandMode.Expanded);
            await Task.Delay(1300);
            Capture(island, Path.Combine(dir, $"{name}-link-10-timer.png"));
            vm.Timer.Stop();

            // "Ton an Gerät": the laptop as the chosen output, the compact indicator and the peek.
            link.LoadAudioDemo();
            vm.AudioDevices.RemoteOutputActive = true;
            // Two speakers instead of three, so the laptop row is in view without scrolling.
            if (vm.AudioDevices.Outputs.FirstOrDefault(o => o.Name.StartsWith("DELL")) is { } monitor) vm.AudioDevices.Outputs.Remove(monitor);
            vm.Tab = 3;
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-11-audio-card.png"), compact: false);
            // Sound on several devices: the slider for all of them and one per device.
            vm.OutputVolume.LoadDemo();
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-11b-audio-volume.png"), compact: false);
            vm.OutputVolume.LoadDemo("receiver");
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-11c-audio-volume-receiver.png"), compact: false);
            vm.OutputVolume.LoadDemo("old");
            await Shot(island, vm, IslandMode.Expanded, Path.Combine(dir, $"{name}-link-11d-audio-volume-old-device.png"), compact: false);
            vm.Tab = 0;
            await Shot(island, vm, IslandMode.Compact, Path.Combine(dir, $"{name}-link-12-audio-compact.png"), compact: false);
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.Volume"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue"),
                Title = App.AudioMovedTitle("LaptopJonathan", null), Subtitle = App.AudioMovedText("LaptopJonathan", null), Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-link-13-audio-peek.png"), compact: false);
            island.SetPeekForSnapshot(new PeekItem
            {
                Icon = Icon("Icon.Volume"), IconBrush = Res("B.Text2"), IconBackground = Tint("B.Text"),
                Title = App.AudioMovedTitle("LaptopJonathan", Services.Link.AudioForwardEnd.LinkLost), Subtitle = App.AudioMovedText("LaptopJonathan", Services.Link.AudioForwardEnd.LinkLost), Seconds = 60,
            });
            await Shot(island, vm, IslandMode.Peek, Path.Combine(dir, $"{name}-link-14-audio-back.png"), compact: false);
        }
    }

    /// <summary>
    /// Renders every frame of the minimal idle line opening and closing into PNGs, from the island itself at a chosen
    /// scale (DYNAMICBAY_SNAPSHOT_SCALE, default 1.5), so 150 % can be checked on any screen. Logs the springs per frame
    /// and how often WPF clipped the shape (that made the line's shadow and edges flicker).
    /// </summary>
    private static async Task MorphFramesAsync(IslandWindow island, IslandViewModel vm, AppSettings settings, string dir)
    {
        double k = double.TryParse(Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_SCALE"), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.5;
        settings.CompactItems.Clear();
        vm.Media.IsPlaying = false;
        vm.Timer.IsActive = false;
        settings.Idle = IdleStyle.Hidden;
        settings.Edge = IslandEdge.Top; settings.Align = IslandAlign.Center; settings.Along = 0.5; settings.Inset = 8;
        island.ForceState(IslandMode.Idle);
        await Task.Delay(1500);
        var root = (FrameworkElement)island.Content;
        // The band at the top edge where the line lives, plus room for the open panel.
        var crop = new Rect(0, 0, root.ActualWidth, Math.Min(root.ActualHeight, 340));
        int frames = 0, clipped = 0;
        async Task Record(string name, Action start, int ms, bool png = true)
        {
            int frame = 0;
            bool pending = false;
            double last = -1, gap = 0;
            // After this frame's layout and render pass (Rendering runs before them).
            EventHandler onFrame = (_, e) =>
            {
                double t = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
                if (t == last) return; // Rendering can fire twice per frame
                gap = last < 0 ? 0 : t - last;
                last = t;
                if (pending) return;
                pending = true;
                island.Dispatcher.BeginInvoke(() => { pending = false; Frame(); }, System.Windows.Threading.DispatcherPriority.Input);
            };
            void Frame()
            {
                var (springs, clip) = island.ShapeStateForTest;
                frames++;
                if (clip) clipped++;
                if (!png)
                {
                    Log.Info(FormattableString.Invariant($"FRAME {name} {frame++}: gap={gap:0.0} {springs}{(clip ? " CLIPPED" : "")}"));
                    return;
                }
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, crop.Width, crop.Height)); // mid gray: the dark line, its light rim and the shadow all show
                    dc.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = crop, Stretch = Stretch.None }, null,
                                     new Rect(0, 0, crop.Width, crop.Height));
                }
                var rtb = new RenderTargetBitmap((int)Math.Round(crop.Width * k), (int)Math.Round(crop.Height * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
                rtb.Render(dv);
                ImageTools.SavePng(rtb, Path.Combine(dir, $"{name}-{frame++:000}.png"));
                Log.Info($"FRAME {name} {frame - 1}: {springs}{(clip ? " CLIPPED" : "")}");
            }
            System.Windows.Media.CompositionTarget.Rendering += onFrame;
            start();
            await Task.Delay(ms);
            System.Windows.Media.CompositionTarget.Rendering -= onFrame;
        }
        await Record("idle", () => { }, 300);
        await Record("open", () => island.ForceState(IslandMode.Expanded), 1400);
        await Record("close", () => island.ForceState(IslandMode.Idle), 1400);
        await Record("after", () => { }, 300);
        // Without PNGs every frame is seen (logged with its gap): every morph that starts or ends in the minimal line.
        for (int i = 0; i < 10; i++)
        {
            await Record("cycle", () => island.ForceState(IslandMode.Expanded), 900, png: false);
            await Record("cycle", () => island.ForceState(IslandMode.Idle), 900, png: false);
        }
        await Record("m-open", () => island.ForceState(IslandMode.Expanded), 1000, png: false);
        await Record("m-close", () => island.ForceState(IslandMode.Idle), 1000, png: false);
        // The idle style changed live: wide bar -> minimal line and back.
        settings.Idle = IdleStyle.Bar;
        await Task.Delay(1200);
        await Record("bar-to-min", () => settings.Idle = IdleStyle.Hidden, 1000, png: false);
        await Record("min-to-bar", () => settings.Idle = IdleStyle.Bar, 1000, png: false);
        settings.Idle = IdleStyle.Hidden;
        await Task.Delay(1200);
        // An activity starts and ends (the compact island grows out of the line and shrinks back into it).
        settings.CompactItems.Add(Widgets.Timer);
        await Record("min-to-compact", () => vm.Timer.IsActive = true, 1000, png: false);
        await Record("compact-to-min", () => vm.Timer.IsActive = false, 1000, png: false);
        // A message peeks out of the line and goes back.
        await Record("min-to-peek", () => island.ShowPeek(new PeekItem { Title = "Test", Subtitle = "Peek", Seconds = 30 }), 1000, png: false);
        await Record("peek-to-min", () => island.DismissPeek(), 1000, png: false);
        // With PNGs for a look at the frames.
        settings.Idle = IdleStyle.Bar;
        await Task.Delay(1200);
        await Record("png-bar-to-min", () => settings.Idle = IdleStyle.Hidden, 1000);
        await Record("png-min-to-compact", () => vm.Timer.IsActive = true, 1000);
        await Record("png-compact-to-min", () => vm.Timer.IsActive = false, 1000);
        Log.Info($"FRAMES: {frames} frames, shape clipped by layout in {clipped}");
    }

    /// <summary>Just enough of the view model for the compact symbols snapshot.</summary>
    private sealed class CompactDemo(int working)
    {
        public bool ShowAudioLink => true;
        public bool ShowTimeTrack => true;
        public bool ShowClaude => true;
        public bool ShowStatus => false;
        public bool ShowMeeting => false;
        public bool ShowDownload => false;
        public bool ShowMedia => false;
        public bool ShowTimer => false;
        public bool ShowCalendar => false;
        public bool ShowClock => false;
        public bool ShowScripts => false;
        public bool ShowBatteryLow => false;
        public object Link { get; } = new { AudioDeviceText = "Ton am LAPTOPJONATHAN" };
        public object Time { get; } = new { CurrentProject = "Kunde Mueller Website Relaunch", Elapsed = "1:24:07" };
        public object Claude { get; } = new { WorkingText = Services.ClaudeService.CompactText(false, working) };
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
            // Mica leaves the window transparent: paint what it shows, light or dark.
            var mica = Settings.SettingsTheme.IsLight() ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);
            var bg = w.Background is SolidColorBrush b && b.Color.A > 0 ? b : new SolidColorBrush(mica);
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

