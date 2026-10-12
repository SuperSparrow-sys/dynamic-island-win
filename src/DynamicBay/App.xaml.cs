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
    private FullscreenGames? _fullscreenGames;
    // DynamicBay on another PC (Services/Link): the hub runs the link only while LinkEnabled.
    private Services.Link.LinkHub? _link;
    private NotificationService? _notifications;
    private MessageWindow? _msg;
    private TrayIcon? _tray;
    private IslandManager? _islands;
    private IslandViewModel? _vm;
    private AppSettings _settings = new();
    private Settings.SettingsWindow? _settingsWindow;
    private const int HotkeyId = 0xB001;
    private bool _snapshotMode;
    // Set once the user's settings are loaded in the one real instance. Maintenance runs, a second launch and
    // snapshots end before that and must neither write default settings nor touch the running instance's banners.
    private bool _running;

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
        // Elevated by NvidiaProfile.ApplyAsync when the driver wants an administrator for its profiles.
        if (GetArg(e.Args, "--nvidia-present") is { } game)
        {
            int st;
            try { st = NvidiaProfile.SetPresentLayered(game, e.Args.Contains("on")); }
            catch (Exception ex) { Log.Error("NVIDIA profile", ex); st = int.MinValue; }
            Shutdown(st);
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
            _single = new Mutex(true, DevMode.On ? "DynamicBay.Dev." + DevMode.Name : "DynamicBay.SingleInstance", out bool first);
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
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("Domain", ex.ExceptionObject as Exception);
            // The process is going down without OnExit: best effort, Windows banners back and settings kept.
            if (ex.IsTerminating) SaveAndRestore();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Task", ex.Exception); ex.SetObserved(); };
        // Signing out or shutting down: write everything right away, there may be no time for OnExit.
        SessionEnding += (_, _) => OnSessionEnding();

        if (snapshotDir is null)
        {
            bool fresh = !File.Exists(AppSettings.FilePath);
            _settings = AppSettings.Load();
            // A new test instance starts at the bottom edge, away from the installed island at the top
            if (DevMode.On && fresh) _settings.Edge = IslandEdge.Bottom;
            if (DevMode.AllowPairingAtStart || DevMode.PairAtStart is not null) _settings.LinkEnabled = true;
            _settings.Migrate();
            _settings.Save();
            _running = true;
            // A crash or a hard kill while the sound played on another device left this PC's output muted: unmute it.
            try { if (Services.Link.LinkHub.AudioMute.RecoverLeftover()) Log.Info("Audio link: output unmuted after an unclean exit"); }
            catch (Exception ex) { Log.Error("Audio mute recovery", ex); }
        }
        else
        {
            AppSettings.ReadOnly = true; // demo settings, never written to the user's file
            _settings = new AppSettings { Hidden = false };
        }
        // Snapshots of the other language: DYNAMICBAY_SNAPSHOT_LANG=en (demo settings only).
        Loc.Init(snapshotDir is not null && Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_LANG") is { Length: > 0 } lang ? lang : _settings.Language);
        Settings.SettingsTheme.Mode = _settings.WindowTheme;
        _settings.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppSettings.WindowTheme)) Settings.SettingsTheme.Mode = _settings.WindowTheme; };
        // Without the graphics card for the whole process (see IslandWindow.ApplyRenderMode): no Direct3D device at all,
        // which saves another ~70 MB. Switching back takes effect after a restart; the island itself switches live.
        if (!_settings.GpuRendering) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Log.Info($"DynamicBay {Version} starting (identity: {SparsePackage.HasIdentity}{(DevMode.On ? ", dev " + DevMode.Name : "")})");
        // Start-up cost before our code runs (runtime, assemblies) and UI stalls from here on.
        using (var me = System.Diagnostics.Process.GetCurrentProcess()) Log.Info($"Runtime start took {(DateTime.Now - me.StartTime).TotalMilliseconds:0} ms");
        UiStallMonitor.Start(Dispatcher);

        // First start after installation: register the identity package, then restart once to run with it.
        if (snapshotDir is null && await SparsePackage.EnsureRegisteredAsync(_settings))
        {
            // Settings are kept; the banners stay as they are (this run never changed them, the new one takes over).
            _settings.Save();
            _running = false;
            _single?.ReleaseMutex();
            _single?.Dispose();
            _single = null;
            SparsePackage.Restart();
            Shutdown();
            return;
        }

        var media = new MediaService { CoverLookupEnabled = _settings.MediaCoverLookup };
        _settings.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppSettings.MediaCoverLookup)) media.CoverLookupEnabled = _settings.MediaCoverLookup; };
        var clipboard = new ClipboardService(_settings);
        var shelf = new ShelfService(_settings);
        var notifications = _notifications = new NotificationService(_settings);
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
            Todo = new Services.M365.TodoService(microsoft, _settings), Presence = new Services.M365.PresenceService(microsoft, _settings),
            Contacts = new Services.M365.ContactsService(microsoft),
        };
        _link = new Services.Link.LinkHub(_settings, Dispatcher);
        _link.Attach(media, clipboard, notifications, shelf, battery);
        _link.LocalSpotify = spotify;
        _link.AttachTogether(timer);
        _link.LockThisPc = LockThisPc;
        _link.CursorHop = _vm.CursorHop;
        // "Hauptgerät: automatisch" follows the mouse right away when CursorHop moves it to the other PC.
        _vm.CursorHop.CursorMoved += where => { if (_link.Role is { } role) { role.Update(); _ = role.AnnounceAsync(); } };
        _vm.Link = _link;
        // "Ton an <Gerät>": the audio card shows the device as the output; choosing a local one brings the sound back,
        // and when Windows switches the default output the capture and the mute follow it.
        var hub = _link;
        _vm.AudioDevices.LocalOutputChosen += hub.StopAudio;
        hub.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(Services.Link.LinkHub.IsForwardingAudio)) _vm.AudioDevices.RemoteOutputActive = hub.IsForwardingAudio; };
        _vm.AudioDevices.DefaultChanged += () => hub.DefaultOutputChanged(AudioEndpoints.DefaultId(AudioFlow.Output));
        // First sign-in: the Outlook calendar joins the calendar card right away.
        microsoft.Connected += () =>
        {
            if (!_settings.CalendarAccounts.Any(a => a.Kind == CalendarKind.Microsoft))
                _settings.CalendarAccounts.Add(new CalendarAccount { Kind = CalendarKind.Microsoft, Name = "Outlook" });
            _ = calendar.RefreshAsync();
        };
        _islands = new IslandManager(_vm);
        _vm.OpenSettingsRequested += ShowSettings;
        _vm.OpenTimeTrackingRequested += ShowTimeTracking;
        _vm.OpenSettingsAtRequested += (page, group) => { ShowSettings(); _settingsWindow?.ShowSection(page, group); };
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
        // Each one on its own: a service that fails to start must not keep the others from starting.
        async Task Next() => await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        IslandViewModel vm = _vm;
        TaildropService taildrop = _taildrop;
        await Next();
        await SafeAsync("Media", media.InitAsync);
        await Next();
        Safe("Clipboard", () => clipboard.Start(_msg));
        Safe("Shelf", shelf.Load);
        await Next();
        Safe("Battery", battery.Start);
        if (_settings.AudioEnabled) Safe("Audio", vm.Audio.Start);
        Safe("Work", vm.Work.Start);
        Safe("Time", vm.Time.WatchSession);
        Safe("Downloads", () => vm.Downloads.SetEnabled(_settings.HasCompact(Widgets.Downloads)));
        _settings.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppSettings.CompactItems)) vm.Downloads.SetEnabled(_settings.HasCompact(Widgets.Downloads)); };
        Safe("Bluetooth", bluetooth.Start);
        Safe("AudioDevices", vm.AudioDevices.Start);
        vm.Audio.AudioServiceRestarted += vm.AudioDevices.Reconnect; // Windows Audio restarted: register for device changes again
        Safe("Calendar", calendar.Start);
        Safe("Scripts", vm.Scripts.Start);
        await Next();
        await SafeAsync("Notifications", notifications.StartAsync);
        // Windows stays silent and shows nothing in the corner: the island shows the notifications (only with access to them).
        if (!_snapshotMode) Safe("Banners", () => BannerSuppressor.Apply(_settings, notifications.Access == NotificationAccess.Allowed));
        await Next();
        Safe("Claude", () => vm.Claude.SetEnabled(_settings.ClaudeEnabled));
        Safe("Taildrop", () => taildrop.SetEnabled(_settings.TaildropEnabled));
        Safe("CursorHop", () => vm.CursorHop.SetEnabled(_settings.CursorHopEnabled));
        Safe("CursorHop pop-ups", ApplyCursorHopOverlay);
        _settings.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(AppSettings.CursorHopEnabled)) vm.CursorHop.SetEnabled(_settings.CursorHopEnabled);
            if (ev.PropertyName is nameof(AppSettings.CursorHopEnabled) or nameof(AppSettings.CursorHopHideOverlays)) ApplyCursorHopOverlay();
        };
        Safe("Todo", vm.Todo!.Start);
        Safe("Presence", vm.Presence!.Start);
        Safe("Contacts", vm.Contacts!.Start);
        _settings.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppSettings.TaildropEnabled)) taildrop.SetEnabled(_settings.TaildropEnabled); };
        _settings.PropertyChanged += (_, ev) => { if (ev.PropertyName == nameof(AppSettings.ClaudeEnabled)) vm.Claude.SetEnabled(_settings.ClaudeEnabled); };
        Safe("Link", () => SetLinkEnabled(_settings.LinkEnabled));
        _settings.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(AppSettings.LinkEnabled)) Safe("Link", () => SetLinkEnabled(_settings.LinkEnabled));
        };
        await SafeAsync("Spotify", spotify.InitAsync);
        Safe("FullscreenGames", () => _fullscreenGames = new FullscreenGames(_settings));
        Log.Info($"Services started in {clock.ElapsedMilliseconds} ms");
        // Test instances: DYNAMICBAY_DEV_SETTINGS=Devices opens the settings window on that page (live checks of a page).
        if (DevMode.SettingsAtStart is { } page) { ShowSettings(); _settingsWindow?.ShowPage(page); }
        await SafeAsync("WarmUp", () => Task.WhenAll(warmApps, warmIcons));
        Safe("Shortcuts", vm.Shortcuts.Rebuild);
        Log.Info($"Background warm-up done after {clock.ElapsedMilliseconds} ms");
        _ = UpdateCheck.RunAsync(_islands, Quit, () => _settings.CheckForUpdates);
        // CPU and memory in the log every 90 s (no trimming: smooth animations matter more than a small working set).
        Safe("MemoryTrim", MemoryTrim.Start);
    }

    private readonly CursorHopOverlay _hopOverlay = new();

    /// <summary>CursorHop's own pop-ups go away while the island shows its peeks (test instances leave it alone).</summary>
    private void ApplyCursorHopOverlay() =>
        _hopOverlay.SetEnabled(!DevMode.On && _settings.CursorHopEnabled && _settings.CursorHopHideOverlays && CursorHopService.IsInstalledOnDisk);

    /// <summary>Starts or stops the link to paired devices. The paired list is saved whenever the link changes it.</summary>
    private void SetLinkEnabled(bool on)
    {
        if (!on)
        {
            StopLink();
            return;
        }
        if (_link is null || _link.IsRunning) return;
        _link.SetEnabled(true); // throws when the port is taken; the hub leaves nothing half started
        if (_link.Service is not { } link) return;
        if (DevMode.AllowPairingAtStart) link.AllowPairing();
        // Test instances: DYNAMICBAY_DEV_SENDFILE=<path> sends that file to the first device that comes online (live check).
        if (DevMode.On && Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_SENDFILE") is { Length: > 0 } sendFile)
        {
            var hub = _link;
            void Once(PairedDevice p)
            {
                link.PeerOnline -= Once;
                Log.Info("Dev: sending the test file");
                _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.BeginInvoke(() => hub.SendFilesAsync(p.Id, new[] { sendFile })), TaskScheduler.Default);
            }
            link.PeerOnline += Once;
        }
        // Test instances: DYNAMICBAY_DEV_SENDAUDIO=<seconds> sends the (generated) sound to the first device that comes
        // online for that long (live check; nothing is captured or played, see DevMode).
        if (DevMode.On && int.TryParse(Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_SENDAUDIO"), out int audioSeconds) && audioSeconds > 0)
        {
            var hub = _link;
            void SendAudioOnce(PairedDevice p)
            {
                link.PeerOnline -= SendAudioOnce;
                Log.Info($"Dev: sending sound for {audioSeconds} s");
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(async () =>
                {
                    await hub.StartAudioAsync(p.Id);
                    await Task.Delay(TimeSpan.FromSeconds(audioSeconds));
                    hub.StopAudio();
                }), TaskScheduler.Default);
            }
            link.PeerOnline += SendAudioOnce;
        }
        if (DevMode.PairAtStart is { } address)
            _ = Task.Delay(3000).ContinueWith(_ => link.PairAsync(address), TaskScheduler.Default).Unwrap()
                .ContinueWith(t => Log.Info("Dev: pairing " + (t.IsFaulted ? "failed: " + t.Exception?.GetBaseException().Message : "started")), TaskScheduler.Default);
    }

    /// <summary>The "Ton" peeks: where the sound plays now, and why.</summary>
    internal static string AudioMovedTitle(string device, Services.Link.AudioForwardEnd? why) => why switch
    {
        null => Loc.German ? $"Ton läuft jetzt am {device}" : $"Sound now plays on {device}",
        _ => Loc.German ? "Ton wieder hier" : "Sound back here",
    };

    internal static string AudioMovedText(string device, Services.Link.AudioForwardEnd? why) => why switch
    {
        null => Loc.German ? "Über Kopplung" : "Via pairing",
        Services.Link.AudioForwardEnd.LinkLost => Loc.German ? $"Verbindung zu {device} getrennt" : $"Connection to {device} lost",
        Services.Link.AudioForwardEnd.Refused => Loc.German ? $"„Ton“ ist am {device} aus" : $"“Sound” is off on {device}",
        Services.Link.AudioForwardEnd.RemoteStopped => Loc.German ? $"{device} hat beendet" : $"{device} ended it",
        Services.Link.AudioForwardEnd.Failed => Loc.German ? "Ton konnte nicht wechseln" : "Sound could not move",
        _ => Loc.German ? $"Nicht mehr am {device}" : $"No longer on {device}",
    };

    private void StopLink()
    {
        try { _link?.Stop(); } catch (Exception ex) { Log.Error("Link stop", ex); }
    }

    private static void Safe(string name, Action start)
    {
        try { start(); }
        catch (Exception ex) { Log.Error("Start " + name, ex); }
    }

    private static async Task SafeAsync(string name, Func<Task> start)
    {
        try { await start(); }
        catch (Exception ex) { Log.Error("Start " + name, ex); }
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
                Kind = PeekKind.Local,
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
                Kind = PeekKind.Local,
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
            // The quiet device of a pair leaves it to the main device, which shows it with this device's name.
            if (_link?.ShowsOwnBatteryPeek() == false) return;
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
            // Teams: answered right there (Teams' own shortcuts); Smartphone-Link: opened to answer it.
            bool teams = IsTeamsCall(n);
            if (IsIncomingCall(n) && !_settings.Sharing)
                island.ShowPeek(new PeekItem
                {
                    Image = n.Logo, Icon = n.Logo is null ? Icon("Icon.Call") : null,
                    IconBrush = Res("B.Green"), IconBackground = Tint("B.Green", 0x2E),
                    Title = n.Title, Subtitle = (Loc.German ? "Anruf · " : "Call · ") + n.App,
                    ActionText = teams ? (Loc.German ? "Annehmen" : "Accept") : (Loc.German ? "Öffnen" : "Open"),
                    DismissText = teams ? (Loc.German ? "Ablehnen" : "Decline") : (Loc.German ? "Später" : "Later"),
                    Action = teams ? () => Task.Run(() => { if (!TeamsCalls.Answer(true)) Dispatcher.BeginInvoke(() => notifications.Open(n)); }) : () => notifications.Open(n),
                    OnDismiss = teams ? () => Task.Run(() => TeamsCalls.Answer(false)) : null,
                    Priority = PeekPriority.High, ShowInDnd = true, Seconds = 25,
                });
            if (!_snapshotMode) BannerSuppressor.Seen(n.AppId);
            if (!_snapshotMode && _vm!.Calls.Add(n)) Log.Info("Call added to the recent calls");
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
                Kind = PeekKind.Notification,
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
            // A timer shared with the paired device ends once: on the main device, else where it belongs.
            if (_link?.ShowsTimerEnd() == false) { Log.Info("Timer: ended, the paired device shows it"); return; }
            Log.Info("Timer: ended, shown here");
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
        // Time tracking: still running after hours or in the evening, and back after a break with the PC locked.
        _vm.Time.Reminder += (kind, running) => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Clock"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
            Title = kind == TimeReminder.Evening
                ? (Loc.German ? $"{_vm.Time.CurrentProject} läuft noch" : $"{_vm.Time.CurrentProject} is still running")
                : (Loc.German ? $"{_vm.Time.CurrentProject} seit {TimeTrackingService.Format(running)} h" : $"{_vm.Time.CurrentProject} for {TimeTrackingService.Format(running)} h"),
            Subtitle = Loc.German ? "Zeiterfassung – noch dabei?" : "Time tracking - still on it?",
            ActionText = Loc.German ? "Stoppen" : "Stop", DismissText = Loc.German ? "Weiter" : "Keep going",
            Action = () => _vm.Time.StopCommand.Execute(null),
            Priority = PeekPriority.High, ShowInDnd = true, Seconds = 30,
        });
        _vm.Time.CameBack += (from, to) =>
        {
            if (!_settings.TimeReminders) return;
            int minutes = (int)Math.Round((to - from).TotalMinutes);
            island.ShowPeek(new PeekItem
            {
                Icon = Icon("Icon.Coffee"), IconBrush = Res("B.Orange"), IconBackground = Tint("B.Orange", 0x2E),
                Title = Loc.German ? $"{minutes} min weg" : $"Away {minutes} min",
                Subtitle = Loc.German ? $"Als Pause von {_vm.Time.CurrentProject} abziehen?" : $"Take it off {_vm.Time.CurrentProject} as a break?",
                ActionText = Loc.German ? "Abziehen" : "Take off", DismissText = Loc.German ? "War Arbeit" : "Was work",
                Action = () => _vm.Time.TakeBreak(from, to),
                Priority = PeekPriority.High, Seconds = 40,
            });
        };
        // A meeting whose title names a project: offer to start it.
        calendar.EventStartingSoon += ev =>
        {
            var project = _settings.TimeProjects.FirstOrDefault(p => p.Length >= 3 && ev.Title.Contains(p, StringComparison.CurrentCultureIgnoreCase));
            if (project is null || !_settings.TimeReminders || _vm.Time.CurrentProject == project || !_settings.HomeWidgets.Contains(Widgets.TimeTrack)) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon("Icon.Clock"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
                Title = Loc.German ? $"{project} starten?" : $"Start {project}?",
                Subtitle = $"{ev.Title} · {ev.Start:HH:mm}",
                ActionText = Loc.German ? "Starten" : "Start", DismissText = Loc.German ? "Nein" : "No",
                Action = () => _vm.Time.Start(project),
                Seconds = 20,
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
                Kind = PeekKind.Local,
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
            long bytes = files.Sum(f => FileSize(f));
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

        // CursorHop: the other PC connected or went away (debounced in the service), clipboard or files from it.
        var hop = _vm!.CursorHop;
        hop.PeerConnected += name =>
        {
            // The link may have announced the same device a moment ago (or will, and then stays silent).
            if (!_settings.CursorHopPeeks || !Services.Link.DeviceAnnouncements.TryAnnounce(name, true, DateTime.UtcNow)) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon(CursorHopService.IconFor(name)), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
                Title = Loc.German ? $"{name} verbunden" : $"{name} connected",
                Subtitle = "CursorHop · " + hop.CursorText,
                Seconds = 3,
            });
        };
        hop.PeerDisconnected += name =>
        {
            if (!_settings.CursorHopPeeks || !Services.Link.DeviceAnnouncements.TryAnnounce(name, false, DateTime.UtcNow)) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon(CursorHopService.IconFor(name)), IconBrush = Res("B.Text2"), IconBackground = Tint("B.Text", 0x26),
                Title = Loc.German ? $"{name} getrennt" : $"{name} disconnected",
                Subtitle = "CursorHop",
                Seconds = 3,
            });
        };
        hop.ClipboardReceived += (name, format) =>
        {
            if (name is not null && _settings.ClipboardEnabled) clipboard.MarkFromDevice(name);
            if (!_settings.CursorHopPeeks) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon("Icon.Clipboard"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
                Title = name is null ? (Loc.German ? "Zwischenablage empfangen" : "Clipboard received")
                      : Loc.German ? $"Zwischenablage von {name}" : $"Clipboard from {name}",
                Subtitle = CursorHopService.FormatText(format) + (Loc.German ? " · Strg+V fügt ein" : " · Ctrl+V pastes"),
                Priority = PeekPriority.Low,
                Seconds = 2.6,
                OnClick = () => { if (_settings.ClipboardEnabled && _settings.ShowTrayTab) { _vm.Tab = 1; island.Expand(); } },
            });
        };
        hop.FilesReceived += (name, count, folder) =>
        {
            // The files themselves: the newest ones in CursorHop's folder (the log names the folder, not the files)
            var files = CursorHopService.RecentFiles(folder, count ?? 1, TimeSpan.FromSeconds(30));
            if (files.Count > 0) _vm!.Shelf.Add(files, announce: false);
            if (!_settings.CursorHopPeeks) return;
            string from = (Loc.German ? "Von " : "From ") + (name ?? "CursorHop");
            string? first = files.FirstOrDefault();
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Image = first is null ? null : ShellThumbnail.Get(first, 96),
                Icon = Icon("Icon.Inbox"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
                Title = files.Count == 1 ? Path.GetFileName(first!)
                    : count is int n ? (Loc.German ? $"{n} {(n == 1 ? "Datei" : "Dateien")}" : $"{n} {(n == 1 ? "file" : "files")}") : (Loc.German ? "Dateien empfangen" : "Files received"),
                Subtitle = from + (files.Count > 0 ? (Loc.German ? " · in der Ablage" : " · in the tray") : ""),
                DragPayload = files.Count > 0 ? files.ToArray() : null,
                Seconds = 4,
                OnClick = () =>
                {
                    if (first is not null) { try { Process.Start("explorer.exe", $"/select,\"{first}\""); } catch { } }
                    else if (folder is not null && Directory.Exists(folder)) { try { Process.Start("explorer.exe", $"\"{folder}\""); } catch { } }
                },
            });
        };

        // A download finished: the file, ready to drag out; a click shows it in its folder.
        _vm!.Downloads.Finished += path => island.ShowPeek(new PeekItem
        {
            Kind = PeekKind.Local,
            Image = ShellThumbnail.Get(path, 96),
            Icon = Icon("Icon.Download"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
            Title = Path.GetFileName(path),
            Subtitle = (Loc.German ? "Download fertig · " : "Download complete · ") + FormatSize(FileSize(path)),
            DragPayload = new[] { path },
            Seconds = 4,
            OnClick = () => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } },
        });

        shelf.FilesAdded += count => island.ShowPeek(new PeekItem
        {
            Kind = PeekKind.Local,
            Icon = Icon("Icon.Inbox"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
            Title = Loc.German ? (count == 1 ? "1 Datei abgelegt" : $"{count} Dateien abgelegt") : (count == 1 ? "1 file added" : $"{count} files added"),
            Subtitle = Loc.T("Shelf.EmptyHint"),
            Seconds = 2.2,
            OnClick = () => { if (_settings.ShowTrayTab) _vm!.Tab = 1; island.Expand(); },
        });

        if (_link is { } link) WireLinkPeeks(link, island, Res, Icon, Tint);
    }

    /// <summary>Pairing code, devices coming and going, files arriving and going out, notifications of the other device.</summary>
    private void WireLinkPeeks(Services.Link.LinkHub link, IslandManager island, Func<string, Brush> Res, Func<string, Geometry> Icon, Func<string, byte, Brush> Tint)
    {
        // "Ton an <Gerät>": where the sound plays now.
        link.AudioMoved += (name, why) =>
        {
            bool there = why is null;
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = Icon("Icon.Volume"), IconBrush = Res(there ? "B.Blue" : "B.Text2"), IconBackground = Tint(there ? "B.Blue" : "B.Text", 0x2E),
                Title = AudioMovedTitle(name, why),
                Subtitle = AudioMovedText(name, why),
                ShowInDnd = true, Seconds = 3,
            });
        };

        var pairingPeeks = new Dictionary<Services.Link.PairingSession, PeekItem>();
        link.PairingRequested += session =>
        {
            if (DevMode.AutoConfirmPairing) { Log.Info($"Dev: confirming pairing code {session.Code}"); session.Confirm(true); return; }
            _settingsWindow?.ShowPairingCode(session);
            // The device that was asked shows the code in the island too, with both answers right there.
            if (session.Initiator) return;
            var peek = new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = Icon("Icon.Link"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
                Title = "Code " + Services.Link.PairingCodeText.Format(session.Code),
                Subtitle = Loc.German ? $"Mit {session.PeerName} koppeln?" : $"Pair with {session.PeerName}?",
                ActionText = Loc.German ? "Bestätigen" : "Confirm", DismissText = Loc.German ? "Ablehnen" : "Decline",
                Action = () => session.Confirm(true), OnDismiss = () => session.Confirm(false),
                Priority = PeekPriority.High, ShowInDnd = true, Seconds = 120,
            };
            pairingPeeks[session] = peek;
            island.ShowPeek(peek);
        };
        link.PairingFinished += (session, result) =>
        {
            if (pairingPeeks.Remove(session, out var asked)) island.WithdrawPeek(asked);
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = Icon(result == Services.Link.PairingResult.Paired ? "Icon.Check" : "Icon.Close"),
                IconBrush = Res(result == Services.Link.PairingResult.Paired ? "B.Green" : "B.Text2"),
                IconBackground = Tint(result == Services.Link.PairingResult.Paired ? "B.Green" : "B.Text", 0x26),
                Title = Services.Link.LinkHub.ResultText(result, session.PeerName).TrimEnd('.'),
                Subtitle = Loc.German ? "Geräte koppeln" : "Pair devices",
                ShowInDnd = true, Seconds = 3,
            });
        };
        link.DeviceAnnounced += (name, connected) => island.ShowPeek(new PeekItem
        {
            Kind = PeekKind.Local,
            Icon = Icon(CursorHopService.IconFor(name)), IconBrush = Res(connected ? "B.Blue" : "B.Text2"), IconBackground = Tint(connected ? "B.Blue" : "B.Text", 0x2E),
            Title = connected ? (Loc.German ? $"{name} verbunden" : $"{name} connected") : (Loc.German ? $"{name} getrennt" : $"{name} disconnected"),
            Subtitle = Loc.German ? "Gekoppeltes Gerät" : "Paired device",
            Seconds = 3,
        });

        // Files: one peek that counts up while it arrives, then the file ready to drag out.
        var arriving = new Dictionary<string, PeekItem>();
        link.FileStarted += offer =>
        {
            if (offer.Size < 4L * 1024 * 1024) return; // small files are there before a peek could show
            var peek = new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = Icon("Icon.Download"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
                Title = offer.FileName, Subtitle = (Loc.German ? "Von " : "From ") + offer.PeerName + " · " + FormatSize(offer.Size),
                Progress = 0, TrailingBrush = Res("B.Blue"), Seconds = 600,
            };
            arriving[offer.TransferId] = peek;
            island.ShowPeek(peek);
        };
        link.FileProgress += (offer, bytes) =>
        {
            if (arriving.TryGetValue(offer.TransferId, out var peek)) peek.Progress = offer.Size == 0 ? 1 : (double)bytes / offer.Size;
        };
        link.FileFailed += (offer, _) =>
        {
            if (arriving.Remove(offer.TransferId, out var peek)) island.WithdrawPeek(peek);
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = Icon("Icon.Close"), IconBrush = Res("B.Red"), IconBackground = Tint("B.Red", 0x2E),
                Title = offer.FileName, Subtitle = Loc.German ? $"Von {offer.PeerName} nicht angekommen" : $"From {offer.PeerName}, did not arrive", Seconds = 4,
            });
        };
        link.FileReceived += (offer, path) =>
        {
            if (arriving.Remove(offer.TransferId, out var peek)) island.WithdrawPeek(peek);
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Image = ShellThumbnail.Get(path, 96),
                Icon = Icon("Icon.Inbox"),
                Title = Path.GetFileName(path),
                Subtitle = (Loc.German ? $"Datei von {offer.PeerName} · " : $"File from {offer.PeerName} · ") + FormatSize(offer.Size),
                DragPayload = new[] { path },
                Seconds = 4,
                OnClick = () =>
                {
                    if (_settings.ShelfEnabled && _settings.ShowTrayTab) { _vm!.Tab = 1; island.Expand(); }
                    else try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
                },
            });
        };
        link.FileSent += (name, device, result) =>
        {
            bool ok = result == Services.Link.FileTransferResult.Done;
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Icon = Icon("Icon.Send"), IconBrush = Res(ok ? "B.Blue" : "B.Red"), IconBackground = Tint(ok ? "B.Blue" : "B.Red", 0x2E),
                Title = ok ? (Loc.German ? $"An {device} gesendet" : $"Sent to {device}") : (Loc.German ? $"Senden an {device} fehlgeschlagen" : $"Sending to {device} failed"),
                Subtitle = ok ? name : Services.Link.LinkHub.SendErrorText(result),
                Seconds = ok ? 2.8 : 5,
            });
        };
        // Main device: the quiet one hands its peeks over (its notifications come through the notifications feature).
        island.Divert = item =>
        {
            if (link.Role is not { IsMain: false } role || item.Kind == PeekKind.Local) return false;
            if (item.HasActions) return AskMainDevice(link, island, item);
            if (item.Kind == PeekKind.Notification) return link.HideNotifications || link.Notifications?.Enabled == true;
            if (!_settings.LinkShareNotifications) return false;
            _ = role.ForwardPeekAsync(item.Title, item.Subtitle, item.IconKey ?? IconKeyOf(item.Icon));
            return true;
        };
        link.RemotePeek += p => island.ShowPeek(new PeekItem
        {
            // Local: a peek from the other device is never handed back. Without this both devices sent it to each other
            // whenever neither was the main one for a moment, endlessly (half a core and 370 messages a second after an hour).
            Kind = PeekKind.Local,
            Icon = Icon(p.IconKey ?? "Icon.Bell"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
            Title = p.Title, Subtitle = p.Subtitle.Length > 0 ? p.Subtitle : p.PeerName, Seconds = 4,
        });
        link.RoleChanged += () =>
        {
            island.SetLinkHidden(link.HideIsland);
            _notifications!.HiddenHere = link.HideNotifications;
        };
        WireLinkTogether(link, island, Res, Icon, Tint);

        link.RemoteNotification += n =>
        {
            if (!_settings.NotificationsEnabled || link.HideNotifications) return;
            island.ShowPeek(new PeekItem
            {
                Kind = PeekKind.Local,
                Image = n.Logo, Icon = n.Logo is null ? Icon("Icon.Bell") : null,
                Title = string.Equals(n.Title, n.App, StringComparison.OrdinalIgnoreCase) ? n.App : n.Title,
                Subtitle = (_settings.NotificationShowBody && !_settings.Sharing ? FirstLine(n.Body, n.App) : n.App) + " · " + n.Device,
                Seconds = Math.Clamp(_settings.NotificationSeconds, 2, 15),
                OnClick = () => { if (_settings.ShowNotificationsTab) { _vm!.Tab = 2; island.Expand(); } },
            });
        };
    }

    private Dictionary<Geometry, string>? _iconKeys;

    /// <summary>The resource name of an icon geometry (Icon.Calendar …), so a forwarded peek keeps its symbol.</summary>
    private string? IconKeyOf(Geometry? g)
    {
        if (g is null) return null;
        if (_iconKeys is null)
        {
            _iconKeys = new();
            void Scan(ResourceDictionary d)
            {
                foreach (var key in d.Keys)
                    if (key is string k && k.StartsWith("Icon.", StringComparison.Ordinal) && d[k] is Geometry geo) _iconKeys.TryAdd(geo, k);
                foreach (var m in d.MergedDictionaries) Scan(m);
            }
            Scan(Resources);
        }
        return _iconKeys.TryGetValue(g, out var name) ? name : null;
    }

    private static bool IsTeamsCall(NotificationItem n) =>
        n.AppId.Contains("Teams", StringComparison.OrdinalIgnoreCase) && IsIncomingCall(n);

    /// <summary>A call notification from Smartphone-Link (YourPhoneCalling) or Teams ("ruft an", "is calling").</summary>
    private static bool IsIncomingCall(NotificationItem n)
    {
        if (n.AppId.Contains("YourPhoneCalling", StringComparison.OrdinalIgnoreCase)) return true;
        bool teams = n.AppId.Contains("Teams", StringComparison.OrdinalIgnoreCase);
        string text = n.Title + " " + n.Body;
        return teams && (text.Contains("ruft an", StringComparison.OrdinalIgnoreCase) || text.Contains("ruft Sie an", StringComparison.OrdinalIgnoreCase) || text.Contains("calling", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("Eingehender Anruf", StringComparison.OrdinalIgnoreCase) || text.Contains("Incoming call", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff" };

    /// <summary>Size of a file that may already be moved or deleted again (0 then).</summary>
    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

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
        if (_msg is null || DevMode.On) return; // the installed DynamicBay keeps its hotkeys
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
        // The first instance may still be starting (no message window yet): look again for up to 3 s.
        var target = Native.FindWindow(null, MessageWindow.WindowTitle);
        for (int i = 0; target == IntPtr.Zero && i < 30; i++)
        {
            Thread.Sleep(100);
            target = Native.FindWindow(null, MessageWindow.WindowTitle);
        }
        if (target == IntPtr.Zero) return;
        // We were started by the user and may hand the foreground on: the settings come up in front, not just flashing.
        Native.GetWindowThreadProcessId(target, out uint pid);
        if (pid != 0) Native.AllowSetForegroundWindow(pid);
        Native.PostMessage(target, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
    }

    // ---------------- windows ----------------

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true } open)
        {
            WindowTools.BringBack(open); // also when minimized: Activate alone left it in the taskbar
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _settingsWindow = new Settings.SettingsWindow(_settings, _vm!);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        _settingsWindow.Dispatcher.BeginInvoke(() => Log.Info($"Settings window open in {sw.ElapsedMilliseconds} ms"), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private Settings.TimeTrackingWindow? _timeWindow;

    /// <summary>The time tracking window (one at a time): entries, adding time by hand, projects.</summary>
    public void ShowTimeTracking()
    {
        if (_timeWindow is { IsLoaded: true }) { _timeWindow.Activate(); return; }
        _timeWindow = new Settings.TimeTrackingWindow(_vm!.Time, _settings);
        _timeWindow.Closed += (_, _) => _timeWindow = null;
        _timeWindow.Show();
        _timeWindow.Activate();
    }

    /// <summary>
    /// After a settings import: the new settings.json is on disk and must not be overwritten by this run's settings.
    /// Gives the Windows banners back and frees the single-instance lock before the new process starts, so the new
    /// run sets everything up again and nothing of this run undoes it afterwards.
    /// </summary>
    public void RestartAfterImport()
    {
        FlushAll();
        AppSettings.ReadOnly = true;
        try { BannerSuppressor.RestoreAll(); } catch (Exception ex) { Log.Error("Import banners", ex); }
        _running = false;
        _taildrop?.Dispose();
        _fullscreenGames?.Dispose();
        _vm?.CursorHop.Dispose();
        _hopOverlay.Dispose();
        _vm?.Claude.Stop(); // frees the hook port for the new run
        StopLink(); // frees the link port for the new run
        _tray?.Dispose();
        _msg?.Dispose();
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;
        SparsePackage.Restart();
        Shutdown();
    }

    private void Quit()
    {
        FlushAll();
        _settings.Save();
        _taildrop?.Dispose(); // ends the Tailscale event stream
        _fullscreenGames?.Dispose(); // the game gets its own size back
        _vm?.CursorHop.Dispose();
        _hopOverlay.Dispose();
        StopLink();
        _tray?.Dispose();
        _msg?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Snapshot runs use throwaway settings and maintenance runs or a second launch never loaded the user's:
        // only the real instance writes its settings and gives the Windows banners back.
        if (_running)
        {
            SaveAndRestore();
            _taildrop?.Dispose();
            StopLink();
        }
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Settings to disk and Windows banners back (they come back as soon as DynamicBay isn't running).</summary>
    private void SaveAndRestore()
    {
        if (!_running) return;
        FlushAll();
        try { _settings.Save(); } catch (Exception ex) { Log.Error("Exit save", ex); }
        try { BannerSuppressor.RestoreAll(); } catch (Exception ex) { Log.Error("Exit banners", ex); }
        try { Services.Link.LinkHub.AudioMute.Release(); } catch (Exception ex) { Log.Error("Exit audio mute", ex); } // the sound plays here again
    }

    /// <summary>Pending writes (notes, clipboard history) to disk now and Teams back to the status from before focus.</summary>
    private bool _flushed;

    private void FlushAll()
    {
        // Once: Quit and then OnExit both come here, and the Teams call may wait up to 3 s.
        if (_snapshotMode || _flushed) return;
        _flushed = true;
        try { _vm?.Notes.Flush(); } catch (Exception ex) { Log.Error("Exit notes", ex); }
        try { _vm?.Clipboard.Flush(); } catch (Exception ex) { Log.Error("Exit clipboard", ex); }
        try { _vm?.Presence?.RestoreBeforeExit(); } catch (Exception ex) { Log.Error("Exit presence", ex); }
    }

    private void OnSessionEnding()
    {
        SaveAndRestore();
        // The sign-out can still be cancelled by another app: if we are still running a minute later, silence the
        // Windows banners again (they were given back above) and put Teams back on "Do not disturb" during focus.
        if (!_running || _notifications is null) return;
        var again = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        again.Tick += (_, _) =>
        {
            again.Stop();
            _flushed = false;
            try { BannerSuppressor.Apply(_settings, _notifications.Access == NotificationAccess.Allowed); }
            catch (Exception ex) { Log.Error("Banners", ex); }
            _vm?.Presence?.Reapply();
        };
        again.Start();
    }
}
