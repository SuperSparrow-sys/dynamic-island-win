using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using DynamicBay.Services;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace DynamicBay.Island;

/// <summary>What every menu bar shows (one model for all displays).</summary>
public sealed partial class MenuBarModel : ObservableObject
{
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private bool _showAppName;
    partial void OnAppNameChanged(string value) => ShowAppName = value.Length > 0;
    [ObservableProperty] private bool _showStart = true;
    [ObservableProperty] private bool _showMedia;
    [ObservableProperty] private string _mediaText = "";
    [ObservableProperty] private bool _showBattery;
    [ObservableProperty] private bool _showNetwork = true;
    [ObservableProperty] private bool _showVolume = true;
    [ObservableProperty] private bool _showDate = true;
    [ObservableProperty] private bool _showSearch = true;
    [ObservableProperty] private Geometry? _volumeIcon;
    [ObservableProperty] private Geometry? _networkIcon;
    [ObservableProperty] private Geometry? _batteryIcon;
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _batteryText = "";
    [ObservableProperty] private bool _showBluetooth;
    [ObservableProperty] private Geometry? _bluetoothIcon;
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string _timeText = "";

    /// <summary>The window the app name stands for (its menu minimizes, maximizes, closes it).</summary>
    public IntPtr AppWindow { get; set; }
    /// <summary>Pinned apps (the launcher list); null in snapshots without them.</summary>
    public ShortcutsService? Shortcuts { get; init; }
    public MediaService? Media { get; init; }
    /// <summary>The magnifier: opens the search on the display of that bar.</summary>
    public Action<string>? Search { get; init; }

    /// <summary>Pinned apps first, then the open ones that are not pinned (each bar fits them into its own room).</summary>
    public ObservableCollection<BarApp> Apps { get; } = new();
    /// <summary>The apps, their windows or their lines changed: the bars fit them in again.</summary>
    public event Action? AppsChanged;
    public void RaiseAppsChanged() => AppsChanged?.Invoke();
}

/// <summary>One symbol on the left of the bar: a pinned app (<see cref="Pin"/>) or an open one that is not pinned.</summary>
public sealed partial class BarApp : ObservableObject
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public ShortcutItem? Pin { get; init; }
    /// <summary>What "new window" starts when the app is not pinned: its program or shell:AppsFolder\{id}.</summary>
    public string LaunchPath { get; init; } = "";
    /// <summary>What "pin" adds to the launcher list: the Start menu entry if there is one (name, icon, its own app id), else <see cref="LaunchPath"/>.</summary>
    public string PinPath { get; init; } = "";
    /// <summary>The app's own icon in its colours; until it is loaded (or without one) <see cref="Glyph"/>.</summary>
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private Geometry? _glyph;
    public bool GlyphFilled { get; init; }
    /// <summary>Open windows, the one used last first.</summary>
    [ObservableProperty] private IReadOnlyList<OpenWindow> _windows = Array.Empty<OpenWindow>();
    [ObservableProperty] private AppIndicator _indicator;
    public bool HasIcon => Icon is not null;
    public bool NoIcon => Icon is null;
    partial void OnIconChanged(ImageSource? value) { OnPropertyChanged(nameof(HasIcon)); OnPropertyChanged(nameof(NoIcon)); }
}

/// <summary>
/// Opens a <see cref="MenuBarWindow"/> on the displays chosen in Darstellung → Kopfleiste and feeds them. Watches
/// the foreground app and the network only while at least one bar is open.
/// </summary>
public sealed class MenuBarManager
{
    private static readonly Dictionary<string, int> Reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pixels a bar reserves at the top of this display (0 = none): the island docks into the bar, not below it.</summary>
    public static int ReservedPx(string device) => Reserved.TryGetValue(device, out int px) ? px : 0;

    internal static void SetReserved(string device, int px)
    {
        if (px > 0) Reserved[device] = px; else Reserved.Remove(device);
    }

    private readonly IslandViewModel _vm;
    private readonly AppSettings _settings;
    private readonly IslandManager _islands;
    private readonly MenuBarModel _model;
    private readonly List<MenuBarWindow> _bars = new();
    private readonly DispatcherTimer _signal = new() { Interval = TimeSpan.FromSeconds(20) };
    // The colours below the bars: soon after a window came to the front or was moved, and every 2 s for content that
    // changes by itself (a page scrolled, an app switched to dark).
    private readonly DispatcherTimer _toneSoon = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _tone = new() { Interval = TimeSpan.FromSeconds(2) };
    // Films and games in fullscreen: Windows' own message misses some (a video switching to fullscreen in the browser).
    private readonly DispatcherTimer _fullscreenCheck = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _hook;
    private WinEventProc? _proc;

    public MenuBarManager(IslandViewModel vm, IslandManager islands, Action<string>? search = null)
    {
        _vm = vm;
        _settings = vm.Settings;
        _islands = islands;
        _model = new MenuBarModel { Shortcuts = vm.Shortcuts, Media = vm.Media, Search = search };
        _signal.Tick += (_, _) => { if (!ActivityState.Current.Away) ReadNetwork(); };
        _toneSoon.Tick += (_, _) => { _toneSoon.Stop(); CheckFullscreen(); ToneAll(); };
        _tone.Tick += (_, _) => { if (!ActivityState.Current.Away) ToneAll(); };
        _fullscreenCheck.Tick += (_, _) => { if (!ActivityState.Current.Away) CheckFullscreen(); };
        _appsSoon.Tick += (_, _) => RefreshApps();
        _youngCheck.Tick += (_, _) => RefreshApps();
    }

    public void Start()
    {
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.MenuBarApps) or nameof(AppSettings.MenuBarRunning) && _shell is not null) RefreshApps();
            if (e.PropertyName is nameof(AppSettings.MenuBar) or nameof(AppSettings.MenuBarDisplays) or nameof(AppSettings.MenuBarHideTaskbar) or nameof(AppSettings.Monitor) or nameof(AppSettings.Displays)) Sync();
            else if (e.PropertyName?.StartsWith("MenuBar", StringComparison.Ordinal) == true && _watching) UpdateAll();
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Sync);
        // Windows switched between light and dark (also the automatic switch in the evening).
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) Application.Current.Dispatcher.BeginInvoke(() => { foreach (var b in _bars) b.ApplyTheme(); });
        };
        Sync();
    }

    /// <summary>Bars on the displays the setting asks for; the rest close (and give their space back).</summary>
    private void Sync()
    {
        var monitors = Monitors.All();
        var chosen = _settings.MenuBarDisplays;
        var wanted = MenuBarMath.Screens(_settings.MenuBar, monitors.Select(m => (m.Device, m.IsPrimary)), _islands.Main.TargetMonitor().Device,
            d => monitors.FirstOrDefault(m => m.Device == d) is { } m && chosen.Contains(MenuBarMath.DisplayKey(m.Device, m.Bounds.Width, m.Bounds.Height), StringComparer.OrdinalIgnoreCase));
        foreach (var bar in _bars.Where(b => !wanted.Contains(b.Device, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _bars.Remove(bar);
            bar.Close();
        }
        foreach (var device in wanted.Where(d => _bars.All(b => !string.Equals(b.Device, d, StringComparison.OrdinalIgnoreCase))))
        {
            var bar = new MenuBarWindow(device, _model);
            // The island sits above the bar (both topmost: the one raised last wins).
            bar.ReservedChanged += () => { foreach (var w in _islands.All) w.RaiseAbove(); };
            _bars.Add(bar);
            bar.Show();
            bar.Tone(IslandShapes(), OnTopWindows());
        }
        Watch(_bars.Count > 0);
        TaskbarHider.Apply(_settings.MenuBarHideTaskbar ? _bars.Select(b => b.Device) : Array.Empty<string>());
        Log.Info($"Menu bar: {_settings.MenuBar}, on {_bars.Count} display(s)");
    }

    /// <summary>Explorer restarted (TaskbarCreated): every bar registers its strip again.</summary>
    public void Renew()
    {
        foreach (var bar in _bars) bar.Renew();
    }

    public void CloseAll()
    {
        Watch(false);
        TaskbarHider.RestoreAll();
        foreach (var bar in _bars.ToList()) bar.Close();
        _bars.Clear();
    }

    // ---------- sources ----------

    private bool _watching;

    private void Watch(bool on)
    {
        if (on == _watching) return;
        _watching = on;
        if (on)
        {
            _vm.Clock.PropertyChanged += OnSource;
            _vm.Battery.PropertyChanged += OnSource;
            _vm.Audio.PropertyChanged += OnSource;
            _vm.Media.PropertyChanged += OnMedia;
            if (_vm.Bluetooth is { } bt) bt.PropertyChanged += OnSource;
            NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
            _proc = OnForeground;
            _hook = SetWinEventHook(EventSystemForeground, EventSystemMoveSizeEnd, IntPtr.Zero, _proc, 0, 0, 0);
            _signal.Start();
            _tone.Start();
            _fullscreenCheck.Start();
            UpdateAll();
            ReadNetwork();
            ShowApp(Native.GetForegroundWindow());
        }
        else
        {
            _vm.Clock.PropertyChanged -= OnSource;
            _vm.Battery.PropertyChanged -= OnSource;
            _vm.Audio.PropertyChanged -= OnSource;
            _vm.Media.PropertyChanged -= OnMedia;
            if (_vm.Bluetooth is { } bt) bt.PropertyChanged -= OnSource;
            NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _proc = null;
            _signal.Stop();
            _tone.Stop();
            _toneSoon.Stop();
            _fullscreenCheck.Stop();
            TrackApps(false);
        }
    }

    private void CheckFullscreen()
    {
        foreach (var bar in _bars) bar.SetFullscreen(FullscreenOn(bar.Device));
    }

    /// <summary>
    /// Whether the top window on this display (not only the one in front: a film keeps running while you work on the
    /// other screen) is a fullscreen film or game, or Windows reports a fullscreen game or presentation there.
    /// </summary>
    public static bool FullscreenOn(string device)
    {
        var m = Monitors.FindDevice(device);
        if (m is null) return false;
        var fg = Native.GetForegroundWindow();
        if (Native.SHQueryUserNotificationState(out int state) == 0 && MenuBarMath.IsFullscreenState(state) && fg != IntPtr.Zero
            && string.Equals(Monitors.Describe(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST)).Device, device, StringComparison.OrdinalIgnoreCase))
            return true;
        bool full = false;
        EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if (MenuBarMath.IgnoredForFullscreen(ex)) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            if (!Native.GetWindowRect(hwnd, out var r) || r.Right <= m.Bounds.Left || r.Left >= m.Bounds.Right || r.Bottom <= m.Bounds.Top || r.Top >= m.Bounds.Bottom) return true;
            string cls = Native.ClassName(hwnd);
            // Start, search, Alt+Tab and the desktop are no fullscreen apps.
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow"
                or "XamlExplorerHostIslandWindow" or "ForegroundStaging" or "MultitaskingViewFrame") return true;
            // Tooltips, menus and dialogs of an app (owned) and small windows (picture in picture) lying over a film do not
            // end it: the top window that covers at least half of this display decides.
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            long w = Math.Min(r.Right, m.Bounds.Right) - Math.Max(r.Left, m.Bounds.Left), h = Math.Min(r.Bottom, m.Bounds.Bottom) - Math.Max(r.Top, m.Bounds.Top);
            if (w * h * 2 < (long)m.Bounds.Width * m.Bounds.Height) return true;
            long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            full = MenuBarMath.IsFullscreen(r, m.Bounds, (style & Native.WS_CAPTION) == Native.WS_CAPTION, Native.IsZoomed(hwnd), (style & Native.WS_THICKFRAME) != 0);
            return false;
        }, IntPtr.Zero);
        return full;
    }

    private void ToneAll()
    {
        var islands = IslandShapes();
        var onTop = OnTopWindows();
        foreach (var bar in _bars) bar.Tone(islands, onTop);
    }

    private List<Native.RECT> IslandShapes() => _islands.All.Where(w => w.IsVisible).Select(w => w.ShapeOnScreen()).ToList();

    /// <summary>
    /// Other apps' windows kept on top (picture in picture, a pinned video). Layered ones do not count: overlays such as
    /// display tools span a whole screen and are mostly transparent, skipping them would leave nothing to sample.
    /// </summary>
    private static List<Native.RECT> OnTopWindows()
    {
        var list = new List<Native.RECT>();
        EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & WsExTopmost) == 0 || (ex & WsExLayered) != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (Native.GetWindowRect(hwnd, out var r) && r.Width > 0 && r.Height > 0) list.Add(r);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private const long WsExTopmost = 0x8, WsExLayered = 0x80000;
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private void OnSource(object? sender, PropertyChangedEventArgs e) => UpdateAll();

    private void UpdateAll()
    {
        var s = _settings;
        var c = _vm.Clock;
        _model.TimeText = c.Time + c.Seconds + (c.AmPm.Length > 0 ? " " + c.AmPm : "");
        _model.DateText = MenuBarMath.DateText(DateTime.Now, Loc.German, s.MenuBarWeekday, s.MenuBarDate);
        _model.ShowDate = _model.DateText.Length > 0;
        _model.ShowStart = s.MenuBarStart;
        TrackApps(s.MenuBarApps || s.MenuBarRunning);
        _model.ShowVolume = s.MenuBarVolume;
        _model.ShowNetwork = s.MenuBarNetwork;
        _model.ShowSearch = s.MenuBarSearch && _model.Search is not null;
        _model.VolumeIcon = Icon(MenuBarMath.VolumeIcon(_vm.Audio.Volume, _vm.Audio.SpeakerMuted));
        var b = _vm.Battery;
        _model.HasBattery = b.HasBattery;
        _model.ShowBattery = b.HasBattery && s.MenuBarBattery;
        _model.BatteryText = b.HasBattery && s.MenuBarBatteryPercent ? $"{b.Percent} %" : "";
        _model.BatteryIcon = b.HasBattery ? Icon(MenuBarMath.BatteryIcon(b.Percent, b.IsCharging || b.IsPluggedIn)) : null;
        // Like the Mac: always there while switched on, with dots when a device is connected.
        _model.ShowBluetooth = s.MenuBarBluetooth && _vm.Bluetooth is not null;
        _model.BluetoothIcon = Icon(_vm.Bluetooth?.HasDevices == true ? "Fluent.BluetoothConnected" : "Fluent.Bluetooth");
        UpdateMedia();
    }

    // Only what the bar shows: the position ticks while the panel is open must not rebuild everything.
    private void OnMedia(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaService.IsPlaying) or nameof(MediaService.Title) or nameof(MediaService.Artist)) UpdateMedia();
    }

    private void UpdateMedia()
    {
        var m = _vm.Media;
        _model.MediaText = MenuBarMath.MediaText(m.Title, m.Artist);
        _model.ShowMedia = _settings.MenuBarMedia && m.IsPlaying && _model.MediaText.Length > 0;
    }

    private void OnNetworkChanged(object? sender) => Application.Current?.Dispatcher.BeginInvoke(ReadNetwork);

    private void ReadNetwork()
    {
        bool connected = false, wlan = false;
        int bars = 0;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is not null && profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
            {
                connected = true;
                wlan = profile.IsWlanConnectionProfile;
                bars = profile.GetSignalBars() ?? 0;
            }
        }
        catch (Exception ex) { Log.Info($"Menu bar: network not readable ({ex.Message})"); }
        _model.NetworkIcon = Icon(MenuBarMath.NetworkIcon(connected, wlan, bars));
    }

    private static Geometry? Icon(string key) => Application.Current?.TryFindResource(key) as Geometry;

    // ---------- foreground app ----------

    private const uint EventSystemForeground = 0x0003, EventSystemMoveSizeEnd = 0x000B; // the range between: menus, capture, move
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

    private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (ev == EventSystemForeground) ShowApp(hwnd);
        _toneSoon.Stop(); // the window paints first
        _toneSoon.Start();
    }

    /// <summary>The app in front by its product name ("Google Chrome"); own windows and the desktop keep the last name.</summary>
    private void ShowApp(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return;
        // Never the desktop's own window: its window menu would close the desktop (the shutdown dialog).
        var owner = GetAncestor(hwnd, 3 /* GA_ROOTOWNER */);
        switch (MenuBarMath.ForegroundAppFor(ClassName(hwnd), AppWindows.IsSwitchable(hwnd), owner != hwnd && AppWindows.IsSwitchable(owner)))
        {
            case ForegroundApp.Keep: return;
            case ForegroundApp.None: _model.AppName = ""; _model.AppWindow = IntPtr.Zero; return;
            case ForegroundApp.Owner: hwnd = owner; Native.GetWindowThreadProcessId(hwnd, out pid); break;
        }
        if (pid == Environment.ProcessId) return;
        string? name = AppName(pid, hwnd);
        if (string.IsNullOrWhiteSpace(name)) return;
        _model.AppName = name;
        _model.AppWindow = hwnd;
    }

    private string? AppName(uint pid, IntPtr hwnd)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { } // elevated or protected process: name only
            // Store apps run inside ApplicationFrameHost: their window title is the app name.
            if (string.Equals(p.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return WindowText(hwnd);
            return path is null ? p.ProcessName : ProgramName(path);
        }
        catch { return null; } // the process ended in between
    }

    /// <summary>A program by its product name ("Google Chrome"), cached per path.</summary>
    private string ProgramName(string path)
    {
        if (_names.TryGetValue(path, out var cached)) return cached;
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : name;
        }
        catch { } // not readable: the file name
        return _names[path] = name;
    }

    // ---------- open apps ----------

    // A hidden window that receives Windows' shell messages like the taskbar does: windows created, destroyed,
    // activated, redrawn, blinking. Exists only while a bar shows apps.
    private HwndSource? _shell;
    private int _shellMsg;
    // Messages come in bursts (a window opens, gets its title, comes to the front): one look afterwards.
    private readonly DispatcherTimer _appsSoon = new() { Interval = TimeSpan.FromMilliseconds(120) };
    // App id and program per window with the time it was first seen: a young window's id is read again.
    private readonly Dictionary<IntPtr, (OpenWindow Window, long Seen)> _windowInfo = new();
    // Chrome apps, Electron apps and Teams set their app id after the window opens, often without a shell message.
    private readonly DispatcherTimer _youngCheck = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<IntPtr> _flashing = new();
    private IntPtr _front;
    private readonly Dictionary<string, BarApp> _apps = new();
    private readonly Dictionary<string, string?> _pinTargets = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = new();
    private static readonly StaWorker IconReader = new("Menu bar icons");

    private void TrackApps(bool on)
    {
        on &= _watching;
        if (on == (_shell is not null)) return;
        if (on)
        {
            _shell = new HwndSource(new HwndSourceParameters("DynamicBay shell hook")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
                ExtendedWindowStyle = (int)Native.WS_EX_TOOLWINDOW,
                Width = 0, Height = 0,
            });
            _shell.AddHook(ShellProc);
            _shellMsg = RegisterWindowMessage("SHELLHOOK");
            if (!RegisterShellHookWindow(_shell.Handle)) Log.Info("Menu bar: shell hook not registered, open apps do not update");
            _vm.Shortcuts.Items.CollectionChanged += OnPinsChanged;
            RefreshApps();
            Log.Info($"Menu bar: watching open apps ({_model.Apps.Count} symbols)");
        }
        else
        {
            DeregisterShellHookWindow(_shell!.Handle);
            _shell.RemoveHook(ShellProc);
            _shell.Dispose();
            _shell = null;
            _vm.Shortcuts.Items.CollectionChanged -= OnPinsChanged;
            _appsSoon.Stop();
            _youngCheck.Stop();
            _windowInfo.Clear();
            _flashing.Clear();
            _apps.Clear();
            _model.Apps.Clear();
            _model.RaiseAppsChanged();
        }
    }

    private void OnPinsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var key in _apps.Keys.Where(k => k.StartsWith("pin:", StringComparison.Ordinal)).ToList()) _apps.Remove(key);
        SoonApps();
    }

    private void SoonApps()
    {
        _appsSoon.Stop();
        _appsSoon.Start();
    }

    private const int HshellCreated = 1, HshellDestroyed = 2, HshellActivated = 4, HshellRedraw = 6, HshellReplaced = 13,
                      HshellRudeActivated = 0x8004, HshellFlash = 0x8006;

    private IntPtr ShellProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != _shellMsg || msg == 0) return IntPtr.Zero;
        switch ((int)wParam.ToInt64())
        {
            case HshellCreated or HshellDestroyed or HshellReplaced or HshellActivated or HshellRudeActivated:
                SoonApps();
                break;
            // Also every title change: only a window not listed yet matters (some get their title after they open).
            case HshellRedraw:
                if (_windowInfo.TryGetValue(lParam, out var known) ? RunningApps.IsYoung(known.Seen, Environment.TickCount64)
                                                                     : AppWindows.IsSwitchable(lParam)) SoonApps();
                break;
            // Sent again and again while a window blinks: only the first time counts, and not for the app in front.
            case HshellFlash:
                if (_windowInfo.ContainsKey(lParam) && !_flashing.Contains(lParam) && Root(Native.GetForegroundWindow()) != lParam)
                {
                    _flashing.Add(lParam);
                    SoonApps();
                }
                break;
        }
        return IntPtr.Zero;
    }

    private static IntPtr Root(IntPtr hwnd) => hwnd == IntPtr.Zero ? hwnd : GetAncestor(hwnd, 3 /* GA_ROOTOWNER */);

    /// <summary>
    /// Reads the open windows (as Alt+Tab: <see cref="AppWindows.IsSwitchable"/>, no own ones, none on other virtual
    /// desktops), groups them per app and updates the symbols and their lines.
    /// </summary>
    private void RefreshApps()
    {
        _appsSoon.Stop();
        if (_shell is null) return;
        var windows = new List<OpenWindow>();
        var alive = new HashSet<IntPtr>();
        long now = Environment.TickCount64;
        bool young = false;
        EnumWindows((hwnd, _) =>
        {
            if (!AppWindows.IsSwitchable(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            alive.Add(hwnd);
            if (!_windowInfo.TryGetValue(hwnd, out var info))
            {
                young = true;
                _windowInfo[hwnd] = info = (new OpenWindow(hwnd, TaskbarIdentity.Read(hwnd), AppWindows.ProcessPath(pid)), now);
            }
            else if (RunningApps.IsYoung(info.Seen, now))
            {
                young = true;
                _windowInfo[hwnd] = info = (RunningApps.Reread(info.Window, TaskbarIdentity.Read(hwnd)), info.Seen);
            }
            var w = info.Window;
            windows.Add(w);
            return true;
        }, IntPtr.Zero);
        foreach (var gone in _windowInfo.Keys.Where(h => !alive.Contains(h)).ToList()) _windowInfo.Remove(gone);
        _flashing.IntersectWith(alive);
        // Once a second while a window is young, then quiet again.
        if (young) _youngCheck.Start(); else _youngCheck.Stop();

        var pins = _settings.MenuBarApps ? _vm.Shortcuts.Items.ToList() : new List<ShortcutItem>();
        var entries = new HashSet<string>((InstalledApps.Cached ?? new()).Select(a => a.LaunchPath), StringComparer.OrdinalIgnoreCase);
        var (pinned, others) = RunningApps.Group(windows, pins.Count,
            (i, w) => AppWindows.Matches(pins[i].Path, w.AppId, w.ExePath, PinTarget(pins[i].Path), AppWindows.KnownFolder)
                      && !RunningApps.IsOwnApp(w.AppId, pins[i].Path, entries.Contains));
        // Own windows in front (a menu of the bar, the island, the settings) keep the app that was in front before.
        IntPtr front = Root(Native.GetForegroundWindow());
        Native.GetWindowThreadProcessId(front, out uint frontPid);
        if (front == IntPtr.Zero || frontPid == Environment.ProcessId) front = _front; else _front = front;
        var list = new List<BarApp>();
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            string key = "pin:" + pin.Path.ToLowerInvariant();
            if (!_apps.TryGetValue(key, out var app))
                _apps[key] = app = new BarApp { Key = key, Name = pin.Name, Pin = pin, Icon = pin.Original, Glyph = pin.Original is null ? pin.Glyph : null, GlyphFilled = pin.GlyphFilled };
            list.Add(Update(app, pinned[i], front));
        }
        if (_settings.MenuBarRunning)
        {
            _order = RunningApps.Order(_order, others.Select(o => o.Key).ToList());
            foreach (var key in _order)
            {
                var group = others.First(o => o.Key == key).Windows;
                if (!_apps.TryGetValue(key, out var app)) _apps[key] = app = Running(key, group[0]);
                list.Add(Update(app, group, front));
            }
        }
        foreach (var gone in _apps.Keys.Except(list.Select(a => a.Key)).ToList()) _apps.Remove(gone);
        CollectionSync.Apply(_model.Apps, list, a => a.Key, ReferenceEquals);
        _model.RaiseAppsChanged();
    }

    private BarApp Update(BarApp app, List<OpenWindow> windows, IntPtr front)
    {
        bool inFront = windows.Any(w => w.Hwnd == front);
        if (inFront) foreach (var w in windows) _flashing.Remove(w.Hwnd); // seen: the colour goes
        app.Windows = windows;
        app.Indicator = RunningApps.Indicator(windows.Count > 0, inFront, windows.Any(w => _flashing.Contains(w.Hwnd)));
        return app;
    }

    private string? PinTarget(string path)
    {
        if (!_pinTargets.TryGetValue(path, out var target)) _pinTargets[path] = target = AppWindows.Target(path);
        return target;
    }

    /// <summary>An open app that is not pinned: named after its Start menu entry or its program, icon loaded aside.</summary>
    private BarApp Running(string key, OpenWindow w)
    {
        string? appsFolder = string.IsNullOrEmpty(w.AppId) ? null : @"shell:AppsFolder\" + w.AppId;
        // Store apps run inside ApplicationFrameHost: their program would start nothing, their title is their name.
        bool frame = w.ExePath is not null && Path.GetFileName(w.ExePath).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        string? entry = appsFolder is null ? null
            : InstalledApps.Cached?.FirstOrDefault(a => string.Equals(a.LaunchPath, appsFolder, StringComparison.OrdinalIgnoreCase))?.Name;
        string name = entry ?? (frame || w.ExePath is null ? WindowText(w.Hwnd) : ProgramName(w.ExePath));
        var app = new BarApp
        {
            Key = key,
            Name = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(w.ExePath ?? "?"),
            LaunchPath = w.ExePath is not null && !frame ? w.ExePath : appsFolder ?? "",
            PinPath = entry is not null ? appsFolder! : w.ExePath is not null && !frame ? w.ExePath : appsFolder ?? "",
            Glyph = Application.Current?.TryFindResource("Icon.AppWindow") as Geometry,
        };
        _ = LoadIcon(app, appsFolder, w.ExePath);
        return app;
    }

    /// <summary>
    /// 48 px like the search: icons that only come in 32 or 48 px arrive inside a light framed tile when asked for more.
    /// The Start menu entry first (the right icon for Chrome profiles and Store apps), then the program.
    /// </summary>
    internal static async Task LoadIcon(BarApp app, string? appsFolder, string? exe)
    {
        try
        {
            var icon = await IconReader.Run(() => (appsFolder is null ? null : ShellThumbnail.Get(appsFolder, 48, iconOnly: true))
                                                  ?? (exe is null ? null : ShellThumbnail.Get(exe, 48, iconOnly: true)));
            if (icon is not null) app.Icon = icon;
        }
        catch (Exception ex) { Log.Info($"Menu bar: no icon for {app.Name} ({ex.Message})"); }
    }

    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowText(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
