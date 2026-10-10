using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Island;
using DynamicBay.Services;

namespace DynamicBay;

public partial class App : Application
{
    private static Mutex? _single;
    private TaildropService? _taildrop;
    private MessageWindow? _msg;
    private TrayIcon? _tray;
    private IslandManager? _islands;
    private IslandViewModel? _vm;
    private AppSettings _settings = new();
    private Settings.SettingsWindow? _settingsWindow;
    private const int HotkeyId = 0xB001;
    private bool _snapshotMode;

    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Maintenance commands used by the installer/uninstaller.
        if (e.Args.Contains("--restore-banners"))
        {
            BannerSuppressor.RestoreAll();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--uninstall"))
        {
            // Undo everything DynamicBay changed outside its own folder.
            BannerSuppressor.RestoreAll();
            Autostart.Apply(false);
            new ClaudeService(AppSettings.Load()).RemoveHooks();
            await SparsePackage.RemoveAsync();
            Shutdown();
            return;
        }

        string? snapshotDir = GetArg(e.Args, "--snapshot");
        _snapshotMode = snapshotDir is not null;

        if (snapshotDir is null)
        {
            _single = new Mutex(true, "DynamicBay.SingleInstance", out bool first);
            if (!first)
            {
                // Second launch (e.g. from the start menu) opens settings in the running instance.
                SignalRunningInstance();
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Domain", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Task", ex.Exception); ex.SetObserved(); };

        _settings = snapshotDir is null ? AppSettings.Load() : new AppSettings { Hidden = false };
        if (snapshotDir is null) { _settings.Migrate(); _settings.Save(); }
        Loc.Init(_settings.Language);
        // Without the graphics card for the whole process (see IslandWindow.ApplyRenderMode): no Direct3D device at all,
        // which saves another ~70 MB. Switching back takes effect after a restart; the island itself switches live.
        if (!_settings.GpuRendering) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Log.Info($"DynamicBay {Version} starting (identity: {SparsePackage.HasIdentity})");
        // Start-up cost before our code runs (runtime, assemblies) and UI stalls from here on.
        using (var me = System.Diagnostics.Process.GetCurrentProcess()) Log.Info($"Runtime start took {(DateTime.Now - me.StartTime).TotalMilliseconds:0} ms");
        UiStallMonitor.Start(Dispatcher);

        // First start after installation: register the identity package, then restart once to run with it.
        if (snapshotDir is null && await SparsePackage.EnsureRegisteredAsync(_settings))
        {
            _single?.ReleaseMutex();
            _single?.Dispose();
            _single = null;
            SparsePackage.Restart();
            Shutdown();
            return;
        }

        var media = new MediaService();
        var clipboard = new ClipboardService(_settings);
        var shelf = new ShelfService(_settings);
        var notifications = new NotificationService(_settings);
        var timer = new TimerService(_settings);
        var calendar = new CalendarService(_settings);
        var battery = new BatteryService(_settings);
        var bluetooth = new BluetoothService();
        var spotify = new SpotifyService(_settings);
        _taildrop = new TaildropService(_settings);

        var microsoft = new Services.M365.MicrosoftAccount(_settings);
        calendar.Microsoft = microsoft;
        _vm = new IslandViewModel(_settings, media, clipboard, shelf, notifications, timer, calendar, battery, spotify)
        {
            Bluetooth = bluetooth, Microsoft = microsoft,
            Todo = new Services.M365.TodoService(microsoft), Presence = new Services.M365.PresenceService(microsoft),
            Contacts = new Services.M365.ContactsService(microsoft),
        };
        // First sign-in: the Outlook calendar joins the calendar card right away.
        microsoft.Connected += () =>
        {
            if (!_settings.CalendarAccounts.Any(a => a.Kind == CalendarKind.Microsoft))
                _settings.CalendarAccounts.Add(new CalendarAccount { Kind = CalendarKind.Microsoft, Name = "Outlook" });
            _ = calendar.RefreshAsync();
        };
        _islands = new IslandManager(_vm);
        _vm.OpenSettingsRequested += ShowSettings;
        _vm.QuitRequested += Quit;

        if (snapshotDir is not null)
        {
            _islands.Main.Show();
            try { await Snapshots.RunAsync(_islands.Main, _vm, _settings, snapshotDir); }
            catch (Exception ex) { Log.Error("Snapshot", ex); }
            Shutdown();
            return;
        }

        _msg = new MessageWindow();
        _msg.Message += OnMessage;
        RegisterHotkey();
        _settings.PropertyChanged += (_, ev) =>
        {
            _settings.SaveSoon();
            if (ev.PropertyName is nameof(AppSettings.ToggleHotkey) or nameof(AppSettings.MicMuteHotkey) or nameof(AppSettings.AudioEnabled)) RegisterHotkey();
            if (ev.PropertyName == nameof(AppSettings.AudioEnabled)) { if (_settings.AudioEnabled) _vm?.Audio.Start(); else _vm?.Audio.Stop(); }
            if (ev.PropertyName == nameof(AppSettings.StartWithWindows)) Autostart.Apply(_settings.StartWithWindows);
            if (ev.PropertyName == nameof(AppSettings.FrameRate)) Motion.FrameRate.Limit = _settings.FrameRate;
            if (ev.PropertyName is nameof(AppSettings.SuppressBanners) or nameof(AppSettings.BannerExceptions) or nameof(AppSettings.Hidden) or nameof(AppSettings.NotificationsEnabled))
                BannerSuppressor.Apply(_settings, notifications.Access == NotificationAccess.Allowed);
        };
        Autostart.Apply(_settings.StartWithWindows);
        Motion.FrameRate.Limit = _settings.FrameRate;

        WirePeeks(media, clipboard, shelf, notifications, timer, calendar, battery, bluetooth);

        _tray = new TrayIcon(_settings, ShowSettings, _islands.ToggleHidden, _islands.ResetPosition, Quit);

        _islands.Show();
        var clock = Stopwatch.StartNew();

        // Heavy lookups warm up in the background (app list on an STA thread, icon palette on the thread pool).
        var warmApps = InstalledApps.WarmUpAsync();
        var warmIcons = Task.Run(() => AppIcons.Count);

        // Services start one by one after the island is visible, yielding in between so it stays responsive.
        async Task Next() => await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        await Next();
        await media.InitAsync();
        await Next();
        clipboard.Start(_msg);
        shelf.Load();
        await Next();
        battery.Start();
        if (_settings.AudioEnabled) _vm.Audio.Start();
        _vm.Work.Start();
        bluetooth.Start();
        _vm.AudioDevices.Start();
        calendar.Start();
        _vm.Scripts.Start();
        await Next();
        await notifications.StartAsync();
        // Windows stays silent and shows nothing in the corner: the island shows the notifications (only with access to them).
        if (!_snapshotMode) BannerSuppressor.Apply(_settings, notifications.Access == NotificationAccess.Allowed);
        await Next();
        _vm.Claude.SetEnabled(_settings.ClaudeEnabled);
        _taildrop.SetEnabled(_settings.TaildropEnabled);
        _vm.Todo!.Start();
        _vm.Presence!.Start();
        _vm.Contacts!.Start();
        _settings.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppSettings.TaildropEnabled)) _taildrop.SetEnabled(_settings.TaildropEnabled); };
        _settings.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppSettings.ClaudeEnabled)) _vm.Claude.SetEnabled(_settings.ClaudeEnabled); };
        await spotify.InitAsync();
        Log.Info($"Services started in {clock.ElapsedMilliseconds} ms");
        await Task.WhenAll(warmApps, warmIcons);
        _vm.Shortcuts.Rebuild();
        Log.Info($"Background warm-up done after {clock.ElapsedMilliseconds} ms");
        if (_settings.CheckForUpdates) _ = UpdateCheck.RunAsync(_islands, Quit);
        // CPU and memory in the log every 90 s (no trimming: smooth animations matter more than a small working set).
        MemoryTrim.Start();
    }

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ---------------- peeks ----------------

    private void WirePeeks(MediaService media, ClipboardService clipboard, ShelfService shelf, NotificationService notifications,
        TimerService timer, CalendarService calendar, BatteryService battery, BluetoothService bluetooth)
    {
        var island = _islands!;
        Brush Res(string key) => (Brush)FindResource(key);
        Geometry Icon(string key) => (Geometry)FindResource(key);
        Brush Tint(string key, byte alpha = 0x33)
        {
            var c = ((SolidColorBrush)FindResource(key)).Color;
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        clipboard.ScreenshotCaptured += item =>
        {
            if (!_settings.PeekOnScreenshot) return;
            island.ShowPeek(new PeekItem
            {
                Image = item.Thumbnail,
                Title = Loc.T("Peek.Screenshot"),
                Subtitle = Loc.T("Peek.ScreenshotHint"),
                Trailing = $"{item.PixelWidth}×{item.PixelHeight}",
                TrailingBrush = Res("B.Text3"),
                Seconds = 3.5,
                OnClick = () => { if (_settings.ShowTrayTab) _vm!.Tab = 1; island.Expand(); },
            });
        };

        media.TrackChanged += () =>
        {
            if (!_settings.MediaPeekOnTrackChange || !_settings.MediaEnabled) return;
            island.ShowPeek(new PeekItem
            {
                Image = media.Cover,
                Icon = media.Cover is null ? Icon("Glyph.Music") : null,
                IconFilled = true,
                IconBrush = media.AccentBrush,
                Title = media.Title,
                Subtitle = media.Artist,
                ShowWaveform = true,
                WaveformBrush = media.AccentBrush,
                Seconds = 2.6,
                Priority = PeekPriority.Low,
                OnClick = () => { _vm!.Tab = 0; island.Expand(); },
            });
        };

        battery.Changed += ev =>
        {
            var (icon, title, brush, prio) = ev switch
            {
                BatteryEvent.PluggedIn => ("Icon.BatteryCharging", Loc.T("Battery.Charging"), "B.Green", PeekPriority.Normal),
                BatteryEvent.Unplugged => ("Icon.Battery", Loc.T("Battery.Unplugged"), "B.Text", PeekPriority.Low),
                BatteryEvent.Low => ("Icon.BatteryLow", Loc.T("Battery.Low"), "B.Red", PeekPriority.High),
                _ => ("Icon.BatteryFull", Loc.T("Battery.Full"), "B.Green", PeekPriority.Low),
            };
            island.ShowPeek(new PeekItem
            {
                Icon = Icon(icon), IconBrush = Res(brush), IconBackground = Tint(brush == "B.Text" ? "B.Text" : brush, 0x26),
                Title = title, Trailing = $"{battery.Percent} %", TrailingBrush = Res(brush),
                Progress = battery.Percent / 100.0, Priority = prio, Seconds = 2.8,
            });
        };

        // Keyboard, mouse or headphones running low (once per device and day)
        bluetooth.BatteryLow += dev =>
        {
            if (!_settings.BluetoothEnabled) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon(dev.Kind switch { DeviceKind.Keyboard => "Icon.Keyboard", DeviceKind.Mouse => "Icon.Mouse", DeviceKind.Speaker => "Icon.Speaker", DeviceKind.Phone => "Icon.Phone", _ => "Icon.Headphones" }),
                IconBrush = Res("B.Red"),
                IconBackground = Tint("B.Red", 0x26),
                Title = dev.Name,
                Subtitle = Loc.German ? "Akku fast leer" : "Battery low",
                Trailing = dev.Battery is int b ? $"{b} %" : null,
                TrailingBrush = Res("B.Red"),
                Progress = dev.Battery is int b2 ? b2 / 100.0 : null,
                Priority = PeekPriority.High,
                Seconds = 5,
            });
        };
        bluetooth.DeviceChanged += (dev, connected) =>
        {
            if (!_settings.BluetoothEnabled) return;
            // Headphones or a speaker dropped out: offer to connect them again right there.
            if (!connected && dev.IsAudio && _vm!.AudioDevices.CanConnect(dev.Name))
            {
                island.ShowPeek(new PeekItem
                {
                    Icon = Icon(dev.Kind == DeviceKind.Speaker ? "Icon.Speaker" : "Icon.Headphones"),
                    IconBrush = Res("B.Text2"), IconBackground = Tint("B.Text", 0x26),
                    Title = dev.Name, Subtitle = Loc.T("Bt.Disconnected"),
                    ActionText = Loc.German ? "Verbinden" : "Connect", DismissText = "OK",
                    Action = () => _ = _vm.AudioDevices.ConnectAsync(dev.Name),
                    Seconds = 7,
                });
                return;
            }
            island.ShowPeek(new PeekItem
            {
                Icon = Icon(dev.Kind switch { DeviceKind.Speaker => "Icon.Speaker", DeviceKind.Headphones => "Icon.Headphones", DeviceKind.Phone => "Icon.Phone", _ => "Icon.Bluetooth" }),
                IconBrush = Res(connected ? "B.Blue" : "B.Text2"),
                IconBackground = Tint(connected ? "B.Blue" : "B.Text", 0x26),
                Title = dev.Name,
                Subtitle = Loc.T(connected ? "Bt.Connected" : "Bt.Disconnected"),
                Trailing = connected && dev.Battery is int b ? $"{b} %" : null,
                TrailingBrush = Res("B.Green"),
                Progress = connected && dev.Battery is int b2 ? b2 / 100.0 : null,
                Seconds = 3,
            });
        };

        // Claude asks something (also from a Remote Control session on another PC, which only shows up as a notification):
        // the compact island shows the waiting Claude symbol until the notification is opened or dismissed.
        notifications.Seen += n =>
        {
            // Incoming call (Smartphone-Link, Teams): always shown, also when the island is quiet - but never while sharing the screen.
            if (IsIncomingCall(n) && !_settings.Sharing)
                island.ShowPeek(new PeekItem
                {
                    Image = n.Logo, Icon = n.Logo is null ? Icon("Icon.Call") : null,
                    IconBrush = Res("B.Green"), IconBackground = Tint("B.Green", 0x2E),
                    Title = n.Title, Subtitle = (Loc.German ? "Anruf · " : "Call · ") + n.App,
                    ActionText = Loc.German ? "Öffnen" : "Open", DismissText = Loc.German ? "Später" : "Later",
                    Action = () => notifications.Open(n),
                    Priority = PeekPriority.High, ShowInDnd = true, Seconds = 25,
                });
            if (!_snapshotMode) BannerSuppressor.Seen(n.AppId);
            if (_settings.ClaudeEnabled && ClaudeService.NeedsAnswer(n.App, n.AppId, n.Title, n.Body)) _vm!.Claude.RemoteAsked(n.Id);
        };
        notifications.Removed += n => _vm?.Claude.RemoteAnswered(n.Id);
        notifications.Arrived += n =>
        {
            if (IsIncomingCall(n)) return; // shown by Seen above, with buttons
            // The island shows its own screenshot peek (with the picture); the Snipping Tool toast would be a second one.
            if (n.AppId == BannerSuppressor.SnippingTool && _settings.PeekOnScreenshot && _settings.ClipboardEnabled) return;
            island.ShowPeek(new PeekItem
            {
                Image = n.Logo,
                Icon = n.Logo is null ? Icon("Icon.Bell") : null,
                Title = string.Equals(n.Title, n.App, StringComparison.OrdinalIgnoreCase) ? n.App : $"{n.Title}",
                Subtitle = _settings.NotificationShowBody && !_settings.Sharing ? FirstLine(n.Body, n.App) : n.App,
                Seconds = Math.Clamp(_settings.NotificationSeconds, 2, 15),
                OnClick = () => { _vm?.Claude.RemoteAnswered(n.Id); notifications.Open(n); },
            });
        };

        timer.Finished += mode =>
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
            island.ShowPeek(new PeekItem
            {
                Icon = Icon("Icon.Timer"), IconBrush = Res("B.Orange"), IconBackground = Tint("B.Orange", 0x2E),
                Title = mode switch
                {
                    TimerMode.Focus => Loc.T("Timer.FocusDone"),
                    TimerMode.Break => Loc.T("Timer.BreakDone"),
                    _ => Loc.T("Timer.Done"),
                },
                Priority = PeekPriority.High,
                ShowInDnd = true,
                Seconds = 6,
                OnClick = () =>
                {
                    if (mode == TimerMode.Focus) timer.StartBreak();
                    else if (mode == TimerMode.Break) timer.StartFocus();
                },
            });
        };

        calendar.EventStartingSoon += ev => island.ShowPeek(ev.Meeting is { } meeting
            // Teams/Zoom: the reminder offers to join right away (stays until answered or the meeting started).
            ? new PeekItem
            {
                Icon = Icon("Icon.Calendar"), IconBrush = Res("B.Green"), IconBackground = Tint("B.Green", 0x2E),
                Title = ev.Title, Subtitle = $"{meeting.Service} · {ev.Start:HH:mm}",
                ActionText = Loc.German ? "Beitreten" : "Join", DismissText = Loc.German ? "Später" : "Later",
                Action = () => MeetingLinks.Open(meeting),
                Priority = PeekPriority.High,
                Seconds = Math.Clamp((ev.Start - DateTime.Now).TotalSeconds + 120, 15, 900),
            }
            : new PeekItem
            {
                Icon = Icon("Icon.Calendar"), IconBrush = Res("B.Red"), IconBackground = Tint("B.Red", 0x2E),
                Title = ev.Title, Subtitle = $"{Loc.T("Cal.Starting")} · {ev.Start:HH:mm}",
                Trailing = calendar.SoonText, TrailingBrush = Res("B.Red"), Seconds = 5,
            });

        // Microphone: ask once when a recording starts, confirm every mute change.
        _vm!.Audio.MicStarted += app =>
        {
            if (!_settings.AudioEnabled || !_settings.AskMuteOnMicStart || _vm.Audio.MicMuted) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon("Icon.Mic"), IconBrush = Res("B.Orange"), IconBackground = Tint("B.Orange", 0x2E),
                Title = Loc.German ? "Mikrofon aktiv" : "Microphone in use",
                Subtitle = app,
                ActionText = Loc.German ? "Stumm" : "Mute",
                DismissText = "OK",
                Action = () => _vm.Audio.SetMicMute(true),
                Seconds = 8,
            });
        };
        // AirPods and co. as microphone switch into call mode (mono, telephone sound): the PC's microphone took over.
        _vm.AudioDevices.BluetoothMicAvoided += (headset, mic) => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Headphones"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
            Title = Loc.German ? $"Guter Ton auf {headset}" : $"Good sound on {headset}",
            Subtitle = Loc.German ? $"Mikrofon: {mic}" : $"Microphone: {mic}",
            ActionText = Loc.German ? "Headset-Mikro" : "Headset mic", DismissText = "OK",
            Action = () => _vm.AudioDevices.UseBluetoothMic(headset),
            Seconds = 6,
        });
        _vm.Audio.MicMuteChanged += muted =>
        {
            if (!_settings.AudioEnabled) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon(muted ? "Icon.MicOff" : "Icon.Mic"),
                IconBrush = Res(muted ? "B.Red" : "B.Green"), IconBackground = Tint(muted ? "B.Red" : "B.Green", 0x2E),
                Title = muted ? (Loc.German ? "Mikrofon stumm" : "Microphone muted") : (Loc.German ? "Mikrofon an" : "Microphone on"),
                Subtitle = _settings.MicMuteHotkey,
                Priority = PeekPriority.High,
                ShowInDnd = true,
                Seconds = 1.8,
            });
        };

        var claudeTint = new SolidColorBrush(Color.FromArgb(0x33, 0xD9, 0x77, 0x57));
        var claudeColor = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));
        claudeTint.Freeze(); claudeColor.Freeze();
        _vm!.Claude.Finished += s => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Sparkles"), IconBrush = claudeColor, IconBackground = claudeTint,
            Title = Loc.German ? "Claude ist fertig" : "Claude is done",
            Subtitle = $"{s.Project} · {s.Title}",
            Seconds = 4,
            OnClick = () => _vm.Claude.Resume(s),
        });
        _vm.Claude.NeedsInput += (s, message) => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Sparkles"), IconBrush = Res("B.Orange"), IconBackground = Tint("B.Orange", 0x2E),
            Title = Loc.German ? "Claude braucht dich" : "Claude needs you",
            Subtitle = string.IsNullOrWhiteSpace(message) ? s.Project : message,
            Priority = PeekPriority.High,
            Seconds = 6,
            OnClick = () => _vm.Claude.Resume(s),
        });

        // Files from other devices (Taildrop): into the shelf and announced with a preview.
        _taildrop!.Received += files =>
        {
            if (_settings.ShelfEnabled) shelf.Add(files, announce: false);
            string first = files[0];
            long bytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            bool image = files.All(f => ImageExtensions.Contains(Path.GetExtension(f)));
            island.ShowPeek(new PeekItem
            {
                Image = ShellThumbnail.Get(first, 96),
                Icon = Icon("Icon.Inbox"),
                Title = files.Count == 1 ? Path.GetFileName(first)
                      : Loc.German ? $"{files.Count} {(image ? "Fotos" : "Dateien")}" : $"{files.Count} {(image ? "photos" : "files")}",
                Subtitle = (Loc.German ? "Über Taildrop empfangen · " : "Received via Taildrop · ") + FormatSize(bytes),
                Seconds = 4,
                Priority = PeekPriority.Normal,
                DragPayload = files.ToArray(),
                OnClick = () =>
                {
                    if (_settings.ShelfEnabled && _settings.ShowTrayTab) { _vm!.Tab = 1; island.Expand(); }
                    else try { Process.Start("explorer.exe", $"/select,\"{first}\""); } catch { }
                },
            });
        };

        TaildropService.Sent += (name, target, error) => Dispatcher.BeginInvoke(() => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Send"), IconBrush = Res(error is null ? "B.Blue" : "B.Red"), IconBackground = Tint(error is null ? "B.Blue" : "B.Red", 0x2E),
            Title = error is null ? (Loc.German ? $"An {target} gesendet" : $"Sent to {target}") : (Loc.German ? $"Senden an {target} fehlgeschlagen" : $"Sending to {target} failed"),
            Subtitle = error ?? name,
            Seconds = error is null ? 2.8 : 6,
        }));

        shelf.FilesAdded += count => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Inbox"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
            Title = Loc.German ? (count == 1 ? "1 Datei abgelegt" : $"{count} Dateien abgelegt") : (count == 1 ? "1 file added" : $"{count} files added"),
            Subtitle = Loc.T("Shelf.EmptyHint"),
            Seconds = 2.2,
            OnClick = () => { if (_settings.ShowTrayTab) _vm!.Tab = 1; island.Expand(); },
        });
    }

    /// <summary>A call notification from Smartphone-Link (YourPhoneCalling) or Teams ("ruft an", "is calling").</summary>
    private static bool IsIncomingCall(NotificationItem n)
    {
        if (n.AppId.Contains("YourPhoneCalling", StringComparison.OrdinalIgnoreCase)) return true;
        bool teams = n.AppId.Contains("Teams", StringComparison.OrdinalIgnoreCase);
        string text = n.Title + " " + n.Body;
        return teams && (text.Contains("ruft an", StringComparison.OrdinalIgnoreCase) || text.Contains("calling", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("Eingehender Anruf", StringComparison.OrdinalIgnoreCase) || text.Contains("Incoming call", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff" };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.0} MB",
        _ => $"{bytes / 1073741824.0:0.0} GB",
    };

    private static string FirstLine(string body, string fallback)
    {
        var line = body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        return string.IsNullOrEmpty(line) ? fallback : line;
    }

    // ---------------- hotkey / messages ----------------

    private void RegisterHotkey()
    {
        if (_msg is null) return;
        Native.UnregisterHotKey(_msg.Handle, HotkeyId);
        if (Hotkey.TryParse(_settings.ToggleHotkey, out uint mods, out uint vk))
            if (!Native.RegisterHotKey(_msg.Handle, HotkeyId, mods | Native.MOD_NOREPEAT, vk))
                Log.Info($"Hotkey {_settings.ToggleHotkey} is already in use");

        Native.UnregisterHotKey(_msg.Handle, MicHotkeyId);
        if (_settings.AudioEnabled && Hotkey.TryParse(_settings.MicMuteHotkey, out uint mm, out uint mvk))
            if (!Native.RegisterHotKey(_msg.Handle, MicHotkeyId, mm | Native.MOD_NOREPEAT, mvk))
                Log.Info($"Hotkey {_settings.MicMuteHotkey} is already in use");
    }

    private const int MicHotkeyId = 0xB002;

    private static readonly uint ShowSettingsMessage = Native.RegisterWindowMessage("DynamicBay.ShowSettings");

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _islands?.ToggleHidden();
            return true;
        }
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == MicHotkeyId)
        {
            _vm?.Audio.ToggleMicMute();
            return true;
        }
        if ((uint)msg == ShowSettingsMessage)
        {
            if (_settings.Hidden) _islands?.ToggleHidden();
            ShowSettings();
            return true;
        }
        return false;
    }

    private static void SignalRunningInstance()
    {
        var target = Native.FindWindow(null, "DynamicBay.Messages");
        if (target != IntPtr.Zero) Native.PostMessage(target, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
    }

    // ---------------- windows ----------------

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _settingsWindow = new Settings.SettingsWindow(_settings, _vm!);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        _settingsWindow.Dispatcher.BeginInvoke(() => Log.Info($"Settings window open in {sw.ElapsedMilliseconds} ms"), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void Quit()
    {
        _settings.Save();
        _taildrop?.Dispose(); // ends the Tailscale event stream
        _tray?.Dispose();
        _msg?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Snapshot runs use throwaway settings; never overwrite the user's file with demo placements.
        if (!_snapshotMode)
        {
            _settings.Save();
            BannerSuppressor.RestoreAll(); // Windows banners come back as soon as DynamicBay isn't running
            _taildrop?.Dispose();
        }
        _tray?.Dispose();
        base.OnExit(e);
    }
}
