using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicBay.Core;
using DynamicBay.Island;

namespace DynamicBay;

/// <summary>
/// Memory check for the snapshot mode (DYNAMICBAY_SNAPSHOT=leak): cycles the island through its states again and again
/// with demo data and logs managed memory and working set after a forced GC. Windows, covers and peeks created on the
/// way are tracked with weak references: any still alive at the end is a leak. Writes memory.json to the snapshot folder.
/// DYNAMICBAY_LEAK_ROUNDS sets the number of rounds (default 6).
/// </summary>
public static class LeakCheck
{
    private sealed record Sample(int Round, double ManagedMb, double WorkingSetMb, double PrivateMb);

    public static async Task RunAsync(IslandWindow island, IslandViewModel vm, AppSettings settings, string dir)
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("DYNAMICBAY_LEAK_ROUNDS"), out int r) && r > 0 ? r : 6;
        var tracked = new Dictionary<string, List<WeakReference>>();
        void Track(string kind, object o)
        {
            if (!tracked.TryGetValue(kind, out var list)) tracked[kind] = list = new();
            list.Add(new WeakReference(o));
        }

        // A local script (no network) for the script widget cycle; ids as in the script snapshot (one data folder).
        string script = Path.Combine(dir, "leak-widget.js");
        File.WriteAllText(script, "const w = new ListWidget(); w.addText('Leak ' + new Date().getSeconds()); Script.setWidget(w); Script.complete();");
        vm.Scripts.Start();

        // Icons are kept at the size asked for (the shell may hand back a bigger one): requested -> held pixels.
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var iconPixels = new Dictionary<string, string>();
        foreach (var (file, size, iconOnly) in new[] { (notepad, 48, true), (notepad, 64, true), (notepad, 96, false), (script, 96, false) })
            if (ShellThumbnail.Get(file, size, iconOnly) is BitmapSource icon)
                iconPixels[$"{Path.GetFileName(file)}@{size}{(iconOnly ? " icon" : "")}"] = $"{icon.PixelWidth}x{icon.PixelHeight}";
        Log.Info($"LEAK icon sizes: {string.Join(", ", iconPixels.Select(i => $"{i.Key} -> {i.Value}"))}");

        var samples = new List<Sample>();
        int coverSeed = 0;

        async Task Round()
        {
            // Settings window: open, page through, close.
            var sw = new Settings.SettingsWindow(settings, vm);
            Track("settingsWindow", sw);
            sw.Show();
            await Task.Delay(500);
            foreach (var page in new[] { SettingsPage.Appearance, SettingsPage.Content, SettingsPage.Accounts })
            {
                sw.ShowPage(page);
                await Task.Delay(150);
            }
            sw.Close();
            sw = null;

            // Expand / collapse with every tab and a few home layouts.
            var layouts = new[]
            {
                new[] { Widgets.Media, Widgets.Calendar, Widgets.Timer },
                new[] { Widgets.Clock, Widgets.System, Widgets.Shortcuts, Widgets.Notes },
                new[] { Widgets.Audio, Widgets.Devices, Widgets.TimeTrack, Widgets.Todo },
            };
            foreach (var layout in layouts)
            {
                settings.HomeWidgets.Clear();
                foreach (var w in layout) settings.HomeWidgets.Add(w);
                foreach (int tab in new[] { 0, 1, 2, 0 })
                {
                    vm.Tab = tab;
                    island.ForceState(IslandMode.Expanded);
                    await Task.Delay(450);
                    island.ForceState(IslandMode.Compact);
                    await Task.Delay(350);
                }
            }
            settings.ClockAnalog = !settings.ClockAnalog;

            // Script widgets: add (large + mini line), show, remove.
            var cfg = new ScriptWidgetConfig { Id = "snapL", Name = "Leak", File = script, Size = ScriptSize.Large, ShowInCompact = true, AllowNetwork = false };
            settings.ScriptWidgets.Add(cfg);
            settings.HomeWidgets.Add(cfg.HomeId);
            island.ForceState(IslandMode.Expanded);
            for (int i = 0; i < 25 && vm.Scripts.Get(cfg.Id, "medium") is null; i++) await Task.Delay(100);
            await Task.Delay(400);
            island.ForceState(IslandMode.Compact);
            await Task.Delay(300);
            settings.HomeWidgets.Remove(cfg.HomeId);
            settings.ScriptWidgets.Remove(cfg);
            await Task.Delay(200);

            // Album art changes (new cover each time) and peeks with pictures.
            for (int i = 0; i < 8; i++)
            {
                var cover = Cover(coverSeed++);
                Track("cover", cover);
                vm.Media.LoadDemo(cover, $"Track {coverSeed}", "Leak Test");
                island.ShowPeek(new PeekItem { Image = cover, Title = $"Track {coverSeed}", Subtitle = "Leak Test", Seconds = 0.3 });
                await Task.Delay(220);
                island.DismissPeek();
            }
            island.ForceState(IslandMode.Expanded);
            await Task.Delay(400);
            island.ForceState(IslandMode.Compact);
            await Task.Delay(300);

            // A mirror island (as on a second monitor), shown and closed again.
            var mirror = new IslandWindow(vm, Monitors.Primary().Device);
            Track("mirrorIsland", mirror);
            mirror.Show();
            await Task.Delay(400);
            mirror.SetExpanded(true);
            await Task.Delay(500);
            mirror.Close();
            mirror = null;
            await Task.Delay(300);
        }

        await Round(); // warm-up: caches, first layouts, JIT
        samples.Add(Measure(0));
        Log.Info($"LEAK baseline: {samples[0]}");
        for (int i = 1; i <= rounds; i++)
        {
            await Round();
            samples.Add(Measure(i));
            Log.Info($"LEAK round {i}: {samples[^1]}");
        }
        // Leave the demo cover in place so the tracked ones may go, then count survivors.
        vm.Media.LoadDemo(Cover(-1), "Midnight Drive", "Neon Harbor");
        island.ForceState(IslandMode.Compact);
        await Task.Delay(800);
        var final = Measure(rounds + 1);
        var alive = tracked.ToDictionary(t => t.Key, t => $"{t.Value.Count(w => w.IsAlive)}/{t.Value.Count}");
        Log.Info($"LEAK final: {final}; alive after GC: {string.Join(", ", alive.Select(a => $"{a.Key} {a.Value}"))}");

        var result = new
        {
            date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            version = typeof(LeakCheck).Assembly.GetName().Version?.ToString(),
            rounds,
            baseline = samples[0],
            last = samples[^1],
            final,
            managedGrowthMb = Math.Round(final.ManagedMb - samples[0].ManagedMb, 2),
            workingSetGrowthMb = Math.Round(final.WorkingSetMb - samples[0].WorkingSetMb, 1),
            aliveAfterGc = alive,
            iconPixels,
            samples,
        };
        File.WriteAllText(Path.Combine(dir, "memory.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Sample Measure(int round)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        double managed = GC.GetTotalMemory(true) / 1048576.0;
        using var me = Process.GetCurrentProcess();
        return new Sample(round, Math.Round(managed, 2), Math.Round(me.WorkingSet64 / 1048576.0, 1), Math.Round(me.PrivateMemorySize64 / 1048576.0, 1));
    }

    /// <summary>A new 300 px gradient cover (like a real album art change), different for every seed.</summary>
    private static BitmapSource Cover(int seed)
    {
        var rnd = new Random(seed);
        Color C() => Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(C(), C(), rnd.Next(360)), null, new Rect(0, 0, 300, 300));
            dc.DrawEllipse(new SolidColorBrush(C()), null, new Point(rnd.Next(300), rnd.Next(300)), 90, 90);
        }
        var rtb = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
