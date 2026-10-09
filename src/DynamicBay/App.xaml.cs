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
    private MessageWindow? _msg;
    private TrayIcon? _tray;
    private IslandWindow? _island;
    private IslandViewModel? _vm;
    private AppSettings _settings = new();
    private Settings.SettingsWindow? _settingsWindow;
    private const int HotkeyId = 0xB001;

    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? snapshotDir = GetArg(e.Args, "--snapshot");

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
        Loc.Init(_settings.Language);
        Log.Info($"DynamicBay {Version} starting");

        var media = new MediaService();
        var clipboard = new ClipboardService(_settings);
        var shelf = new ShelfService(_settings);
        var notifications = new NotificationService(_settings);
        var timer = new TimerService(_settings);
        var calendar = new CalendarService(_settings);
        var battery = new BatteryService(_settings);
        var bluetooth = new BluetoothService();
        var spotify = new SpotifyService(_settings);

        _vm = new IslandViewModel(_settings, media, clipboard, shelf, notifications, timer, calendar, battery, spotify);
        _island = new IslandWindow(_vm);
        _vm.OpenSettingsRequested += ShowSettings;

        if (snapshotDir is not null)
        {
            _island.Show();
            try { await Snapshots.RunAsync(_island, _vm, _settings, snapshotDir); }
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
            if (ev.PropertyName == nameof(AppSettings.ToggleHotkey)) RegisterHotkey();
            if (ev.PropertyName == nameof(AppSettings.StartWithWindows)) Autostart.Apply(_settings.StartWithWindows);
        };
        Autostart.Apply(_settings.StartWithWindows);

        WirePeeks(media, clipboard, shelf, notifications, timer, calendar, battery, bluetooth);

        _tray = new TrayIcon(_settings, ShowSettings, () => _island.SetHidden(!_settings.Hidden), _island.ResetPosition, Quit);

        if (!_settings.Hidden) _island.Show();

        // Services start after the window is up so the first frame isn't delayed.
        shelf.Load();
        clipboard.Start(_msg);
        battery.Start();
        bluetooth.Start();
        calendar.Start();
        await media.InitAsync();
        await spotify.InitAsync();
        await notifications.StartAsync();
        if (_settings.CheckForUpdates) _ = UpdateCheck.RunAsync(_island);
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
        var island = _island!;
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
                OnClick = () => { _vm!.Tab = 1; island.SetExpanded(true); },
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
                OnClick = () => { _vm!.Tab = 0; island.SetExpanded(true); },
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

        bluetooth.DeviceChanged += (dev, connected) =>
        {
            if (!_settings.BluetoothEnabled) return;
            island.ShowPeek(new PeekItem
            {
                Icon = Icon(dev.IsAudio ? "Icon.Headphones" : "Icon.Bluetooth"),
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

        notifications.Arrived += n => island.ShowPeek(new PeekItem
        {
            Image = n.Logo,
            Icon = n.Logo is null ? Icon("Icon.Bell") : null,
            Title = string.Equals(n.Title, n.App, StringComparison.OrdinalIgnoreCase) ? n.App : $"{n.Title}",
            Subtitle = _settings.NotificationShowBody ? FirstLine(n.Body, n.App) : n.App,
            Seconds = Math.Clamp(_settings.NotificationSeconds, 2, 15),
            OnClick = () => notifications.Open(n),
        });

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
                Seconds = 6,
                OnClick = () =>
                {
                    if (mode == TimerMode.Focus) timer.StartBreak();
                    else if (mode == TimerMode.Break) timer.StartFocus();
                },
            });
        };

        calendar.EventStartingSoon += ev => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Calendar"), IconBrush = Res("B.Red"), IconBackground = Tint("B.Red", 0x2E),
            Title = ev.Title, Subtitle = $"{Loc.T("Cal.Starting")} · {ev.Start:HH:mm}",
            Trailing = calendar.SoonText, TrailingBrush = Res("B.Red"), Seconds = 5,
        });

        shelf.FilesAdded += count => island.ShowPeek(new PeekItem
        {
            Icon = Icon("Icon.Inbox"), IconBrush = Res("B.Blue"), IconBackground = Tint("B.Blue", 0x2E),
            Title = Loc.German ? (count == 1 ? "1 Datei abgelegt" : $"{count} Dateien abgelegt") : (count == 1 ? "1 file added" : $"{count} files added"),
            Subtitle = Loc.T("Shelf.EmptyHint"),
            Seconds = 2.2,
            OnClick = () => { _vm!.Tab = 1; island.SetExpanded(true); },
        });
    }

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
    }

    private static readonly uint ShowSettingsMessage = Native.RegisterWindowMessage("DynamicBay.ShowSettings");

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _island?.SetHidden(!_settings.Hidden);
            return true;
        }
        if ((uint)msg == ShowSettingsMessage)
        {
            if (_settings.Hidden) _island?.SetHidden(false);
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
        _settingsWindow = new Settings.SettingsWindow(_settings, _vm!);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void Quit()
    {
        _settings.Save();
        _tray?.Dispose();
        _msg?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings.Save();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
