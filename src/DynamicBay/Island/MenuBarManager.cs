using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
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
    [ObservableProperty] private bool _showStart = true;
    [ObservableProperty] private bool _showApps = true;
    [ObservableProperty] private bool _showMedia;
    [ObservableProperty] private string _mediaText = "";
    [ObservableProperty] private bool _showBattery;
    [ObservableProperty] private bool _showNetwork = true;
    [ObservableProperty] private bool _showVolume = true;
    [ObservableProperty] private bool _showDate = true;
    [ObservableProperty] private Geometry? _volumeIcon;
    [ObservableProperty] private Geometry? _networkIcon;
    [ObservableProperty] private Geometry? _batteryIcon;
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _batteryText = "";
    [ObservableProperty] private bool _showBluetooth;
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string _timeText = "";

    /// <summary>The window the app name stands for (its menu minimizes, maximizes, closes it).</summary>
    public IntPtr AppWindow { get; set; }
    /// <summary>Pinned apps (the launcher list); null in snapshots without them.</summary>
    public ShortcutsService? Shortcuts { get; init; }
    public MediaService? Media { get; init; }
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

    public MenuBarManager(IslandViewModel vm, IslandManager islands)
    {
        _vm = vm;
        _settings = vm.Settings;
        _islands = islands;
        _model = new MenuBarModel { Shortcuts = vm.Shortcuts, Media = vm.Media };
        _signal.Tick += (_, _) => { if (!ActivityState.Current.Away) ReadNetwork(); };
        _toneSoon.Tick += (_, _) => { _toneSoon.Stop(); CheckFullscreen(); ToneAll(); };
        _tone.Tick += (_, _) => { if (!ActivityState.Current.Away) ToneAll(); };
        _fullscreenCheck.Tick += (_, _) => { if (!ActivityState.Current.Away) CheckFullscreen(); };
    }

    public void Start()
    {
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.MenuBar) or nameof(AppSettings.Monitor) or nameof(AppSettings.Displays)) Sync();
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
        var wanted = MenuBarMath.Screens(_settings.MenuBar, monitors.Select(m => (m.Device, m.IsPrimary)), _islands.Main.TargetMonitor().Device);
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
        Log.Info($"Menu bar: {_settings.MenuBar}, on {_bars.Count} display(s)");
    }

    public void CloseAll()
    {
        Watch(false);
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
        // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4): exclusive games, slide shows
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4 && fg != IntPtr.Zero
            && string.Equals(Monitors.Describe(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST)).Device, device, StringComparison.OrdinalIgnoreCase))
            return true;
        bool full = false;
        EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & (Native.WS_EX_TOOLWINDOW | WsExLayered)) != 0) return true; // overlays, the island, display tools
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
        _model.ShowApps = s.MenuBarApps;
        _model.ShowVolume = s.MenuBarVolume;
        _model.ShowNetwork = s.MenuBarNetwork;
        _model.VolumeIcon = Icon(MenuBarMath.VolumeIcon(_vm.Audio.Volume, _vm.Audio.SpeakerMuted));
        var b = _vm.Battery;
        _model.HasBattery = b.HasBattery;
        _model.ShowBattery = b.HasBattery && s.MenuBarBattery;
        _model.BatteryText = b.HasBattery && s.MenuBarBatteryPercent ? $"{b.Percent} %" : "";
        _model.BatteryIcon = b.HasBattery ? Icon(MenuBarMath.BatteryIcon(b.Percent, b.IsCharging || b.IsPluggedIn)) : null;
        _model.ShowBluetooth = s.MenuBarBluetooth && _vm.Bluetooth?.HasDevices == true;
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
        // Desktop and taskbar: keep the last app (its window menu would otherwise close the desktop: the shutdown dialog).
        if (ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;
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
            if (path is null) return p.ProcessName;
            if (_names.TryGetValue(path, out var cached)) return cached;
            var info = FileVersionInfo.GetVersionInfo(path);
            string name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : p.ProcessName;
            _names[path] = name;
            return name;
        }
        catch { return null; } // the process ended in between
    }

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
