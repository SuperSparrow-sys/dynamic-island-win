using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Island;
using DynamicBay.Services.Link;

namespace DynamicBay;

/// <summary>Working together with the paired device in the island: questions on the main device, its battery, locking.</summary>
public partial class App
{
    /// <summary>Questions that came back from the main device unanswered (it went away): they show here.</summary>
    private readonly HashSet<PeekItem> _askHere = new();

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    /// <summary>"Gemeinsam sperren": locks this PC. A test instance never does, it only writes it to the log.</summary>
    private static void LockThisPc()
    {
        if (DevMode.On) { Log.Info("Dev: this PC would lock now (test instances never lock)"); return; }
        if (!LockWorkStation()) Log.Info("Link: locking this PC did not work");
    }

    /// <summary>
    /// The quiet device hands a question (peek with buttons) to the main device; the answer runs here. True: it went
    /// there. A timeout counts as dismissed; if the main device goes away first, the question shows here after all.
    /// </summary>
    private bool AskMainDevice(LinkHub link, IslandManager island, PeekItem item)
    {
        if (_askHere.Remove(item)) return false;
        var q = new LinkQuestion(item.Title, item.Subtitle, item.IconKey ?? IconKeyOf(item.Icon), ColorOf(item.IconBrush),
            item.ActionText!, item.DismissText ?? "OK", item.Seconds, item.Priority == PeekPriority.High, item.ShowInDnd);
        var sentAt = DateTime.UtcNow;
        return link.TryAskMainDevice(q, answer => Dispatcher.BeginInvoke(() =>
        {
            switch (answer)
            {
                case LinkAnswer.Yes: item.Action?.Invoke(); break;
                case LinkAnswer.No: item.OnDismiss?.Invoke(); break;
                case LinkAnswer.Timeout: if (item.TimeoutIsNo) item.OnDismiss?.Invoke(); break;
                default:
                    if (DateTime.UtcNow - sentAt < TimeSpan.FromSeconds(item.Seconds)) { _askHere.Add(item); island.ShowPeek(item); }
                    else if (item.TimeoutIsNo) item.OnDismiss?.Invoke();
                    break;
            }
        }));
    }

    /// <summary>
    /// Shown here and asked on the paired device as well (a ringing phone: "Annehmen" works on both). The answer there
    /// runs the buttons here and takes the peek here away; no answer changes nothing.
    /// </summary>
    private void AskPairedToo(LinkHub link, IslandManager island, PeekItem item)
    {
        var q = new LinkQuestion(item.Title, item.Subtitle, item.IconKey ?? IconKeyOf(item.Icon), ColorOf(item.IconBrush),
            item.ActionText!, item.DismissText ?? "OK", item.Seconds, item.Priority == PeekPriority.High, item.ShowInDnd);
        link.TryAskPairedDevice(q, answer => Dispatcher.BeginInvoke(() =>
        {
            if (answer is not (LinkAnswer.Yes or LinkAnswer.No)) return;
            island.WithdrawPeek(item);
            if (answer == LinkAnswer.Yes) item.Action?.Invoke();
            else item.OnDismiss?.Invoke();
        }));
    }

    private static string? ColorOf(Brush? b) => b is SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : null;

    private void WireLinkTogether(LinkHub link, IslandManager island, Func<string, Brush> Res, Func<string, Geometry> Icon, Func<string, byte, Brush> Tint)
    {
        Geometry IconOr(string? key, string fallback) => key is not null && TryFindResource(key) is Geometry g ? g : Icon(fallback);

        // Questions of the quiet device: here with working buttons, the answer goes back.
        var asked = new Dictionary<string, PeekItem>();
        link.QuestionAsked += r =>
        {
            var q = r.Question;
            Brush brush = Res("B.Blue"), tint = Tint("B.Blue", 0x2E);
            if (q.Color is { } hex && ColorConverter.ConvertFromString(hex) is Color c)
            {
                var b = new SolidColorBrush(c); b.Freeze(); brush = b;
                var t = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B)); t.Freeze(); tint = t;
            }
            var peek = new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = IconOr(q.IconKey, "Icon.Bell"), IconBrush = brush, IconBackground = tint,
                Title = q.Title, Subtitle = (q.Subtitle.Length > 0 ? q.Subtitle + " · " : "") + r.PeerName,
                ActionText = q.ActionText, DismissText = q.DismissText,
                Action = () => { asked.Remove(r.Id); _ = link.Questions?.AnswerAsync(r.Id, true); },
                OnDismiss = () => { asked.Remove(r.Id); _ = link.Questions?.AnswerAsync(r.Id, false); },
                Priority = q.Important ? PeekPriority.High : PeekPriority.Normal, ShowInDnd = q.ShowInDnd, Seconds = q.Seconds,
            };
            asked[r.Id] = peek;
            island.ShowPeek(peek);
        };
        link.QuestionExpired += id => { if (asked.Remove(id, out var p)) island.WithdrawPeek(p); };

        // The other device's battery: "Laptop: Akku 15 %" on the main device.
        link.RemoteBattery += e =>
        {
            var (icon, text, brush, prio) = e.Kind switch
            {
                "plugged" => ("Icon.BatteryCharging", Loc.T("Battery.Charging"), "B.Green", PeekPriority.Normal),
                "unplugged" => ("Icon.Battery", Loc.T("Battery.Unplugged"), "B.Text", PeekPriority.Low),
                "low" => ("Icon.BatteryLow", Loc.T("Battery.Low"), "B.Red", PeekPriority.High),
                _ => ("Icon.BatteryFull", Loc.T("Battery.Full"), "B.Green", PeekPriority.Low),
            };
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = Icon(icon), IconBrush = Res(brush), IconBackground = Tint(brush, 0x26),
                Title = Loc.German ? $"{e.PeerName}: Akku {e.Percent} %" : $"{e.PeerName}: battery {e.Percent} %",
                Subtitle = text, Progress = e.Percent / 100.0, Priority = prio, Seconds = 3.2,
            });
        };

        if (DevMode.On) WatchDevCommands(link, island, asked);
    }

    /// <summary>
    /// Test instances only: commands in "dev-commands.txt" in the instance's data folder drive the live check of two
    /// paired instances (timer, Nicht stören, battery, a question, a lock) without touching a real account or the screen lock.
    /// </summary>
    private void WatchDevCommands(LinkHub link, IslandManager island, Dictionary<string, PeekItem> asked)
    {
        string file = Path.Combine(AppSettings.Folder, "dev-commands.txt");
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        poll.Tick += (_, _) =>
        {
            string[] lines;
            try
            {
                if (!File.Exists(file)) return;
                lines = File.ReadAllLines(file);
                File.Delete(file);
            }
            catch (IOException) { return; }
            foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                Log.Info("Dev: command " + line);
                var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var timer = link.LocalTimer;
                switch (w[0])
                {
                    case "timer": timer?.StartMinutes(w.Length > 1 ? double.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 5); break;
                    case "focus": timer?.StartFocus(); break;
                    case "pause": timer?.TogglePause(); break;
                    case "stop": timer?.Stop(); break;
                    case "handover": link.HandOverTimerCommand.Execute(null); break;
                    case "state":
                        Log.Info($"Dev: timer {(timer is { IsActive: true } t ? $"{(t.IsRunning ? "running" : "paused")} {t.RemainingText} {t.Mode}, {(t.RemoteOwner is null ? "own" : "copy")}" : "off")}, "
                                 + $"dnd {_settings.DoNotDisturb}, peer quiet {_settings.PeerQuiet}, main {link.IsMain}, own battery peek {link.ShowsOwnBatteryPeek()}, timer end here {link.ShowsTimerEnd()}");
                        break;
                    case "dnd": _settings.DoNotDisturb = w.Length > 1 && w[1] == "on"; break;
                    case "main": link.MainChoice = w.Length > 1 ? w[1] switch { "this" => 1, "other" => 2, "auto" => 3, _ => 0 } : 0; break;
                    case "battery": if (link.BatteryShare is { } b && w.Length > 2) _ = b.PublishAsync(w[1], int.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture)); break;
                    case "lock": if (link.Lock is { } l) _ = l.LockedHereAsync(); break;
                    case "peek":
                        // "peek <title>": an ordinary peek (not local): on the quiet device it goes to the main one
                        island.ShowPeek(new PeekItem { Title = w.Length > 1 ? string.Join(' ', w.Skip(1)) : "Dev peek", Subtitle = "Live check", Seconds = 3 });
                        break;
                    case "ask":
                        island.ShowPeek(new PeekItem
                        {
                            Title = "Dev question", Subtitle = "Live check", ActionText = "Yes", DismissText = "No", Seconds = w.Length > 1 ? double.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 20,
                            Action = () => Log.Info("Dev: question answered here: yes"), OnDismiss = () => Log.Info("Dev: question answered here: no or timeout"),
                        });
                        break;
                    case "answer":
                        if (asked.Keys.LastOrDefault() is { } id && asked.Remove(id, out var shown))
                        {
                            island.WithdrawPeek(shown);
                            _ = link.Questions?.AnswerAsync(id, w.Length > 1 && w[1] == "yes");
                        }
                        break;
                    case "media":
                        // "media <count> [ms]": fake music for the other instance (a new title with a cover each time), nothing plays
                        if (link.Media is { } media)
                        {
                            int count = w.Length > 1 ? int.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 10;
                            int every = w.Length > 2 ? int.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture) : 1000;
                            _ = DevMediaAsync(media, count, every);
                        }
                        break;
                    case "set":
                        if (w.Length > 2 && typeof(AppSettings).GetProperty(w[1]) is { PropertyType: var pt } prop && pt == typeof(bool) && w[1].StartsWith("Link", StringComparison.Ordinal))
                            prop.SetValue(_settings, w[2] == "on");
                        break;
                }
            }
        };
        poll.Start();
    }

    private static async Task DevMediaAsync(LinkMediaFeature media, int count, int every)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new LinearGradientBrush(Colors.DarkSlateBlue, Colors.Coral, 45), null, new Rect(0, 0, 300, 300));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        byte[] cover = ms.ToArray();
        for (int i = 0; i < count; i++)
        {
            await media.PublishAsync(new LinkNowPlaying(true, $"Dev song {i % 3}", "Dev artist", "Dev", i % 2 == 0, i, 200, cover), force: true);
            await Task.Delay(every);
        }
    }
}
