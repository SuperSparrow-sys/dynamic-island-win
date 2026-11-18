using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicBay.Core;
using DynamicBay.Services;
using Microsoft.Win32;

namespace DynamicBay.Island;

/// <summary>
/// The menu bar on one display: a strip at the top that Windows reserves like the taskbar (<see cref="AppBar"/>),
/// so maximized windows such as Chrome end below it and their tabs no longer touch the island. The notch is taller
/// and hangs out below it. Makes way for fullscreen apps on its display.
/// </summary>
public partial class MenuBarWindow : Window
{
    private IntPtr _hwnd;
    private AppBar? _bar;
    private readonly bool _reserve;
    private bool _fullscreen;
    private Color[]? _tone;          // colours right below the bar, left to right (null: not sampled yet)
    private LinearGradientBrush? _gradient; // the bar's background once sampled; its stops glide to new colours

    public string Device { get; }

    /// <summary>The bar got its place or gave it back (the island moves into it or out again).</summary>
    public event Action? ReservedChanged;

    /// <param name="reserve">false only for snapshots: the bar is shown without taking space from the windows.</param>
    public MenuBarWindow(string device, MenuBarModel model, bool reserve = true)
    {
        Device = device;
        _reserve = reserve;
        InitializeComponent();
        DataContext = model;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            // Square ends like the Mac menu bar.
            int square = DwmCornerDoNotRound;
            Native.DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref square, sizeof(int));
            ApplyTheme();
            if (_reserve)
            {
                _bar = new AppBar(_hwnd);
                _bar.Register();
            }
            Place();
        };
        // The model outlives the bar: its events are let go when the bar closes.
        AppList.ItemsSource = _shown;
        model.AppsChanged += Refit;
        model.PropertyChanged += OnModelChanged;
        RightSide.SizeChanged += (_, _) => Refit();
        Closed += (_, _) =>
        {
            model.AppsChanged -= Refit;
            model.PropertyChanged -= OnModelChanged;
            Release();
        };
        Refit();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MenuBarModel.ShowAppName) or nameof(MenuBarModel.ShowStart)) Refit();
    }

    // ---------- apps ----------

    /// <summary>Width of one symbol in the bar (16 + padding + margin), also the "more" button.</summary>
    private const double ItemDip = 32;
    /// <summary>What the app name keeps when the symbols need room ("Chrome", "Explo…"); it shortens first.</summary>
    private const double NameMinDip = 90;

    private readonly System.Collections.ObjectModel.ObservableCollection<BarApp> _shown = new();
    private List<BarApp> _hidden = new();

    /// <summary>Snapshots only: room for the left side instead of the notch's position.</summary>
    internal double? RoomOverride { get; set; }

    /// <summary>
    /// Puts as many app symbols into the bar as fit before the notch (and before the right side); the rest go behind
    /// "more". The app in front and the pinned ones stay visible as long as possible.
    /// </summary>
    private void Refit()
    {
        if (DataContext is not MenuBarModel m) return;
        var apps = m.Apps.ToList();
        double left = RoomOverride ?? Math.Min(LeftSide.MaxWidth, ActualWidth - 12 - RightSide.ActualWidth - 24);
        double room = left - (m.ShowStart ? ItemDip : 0) - (m.ShowAppName ? NameMinDip : 0);
        if (ActualWidth <= 0 && RoomOverride is null) room = double.PositiveInfinity; // not laid out yet: the next pass fits
        var (shown, hidden) = RunningApps.Fit(apps.Count, apps.Count(a => a.Pin is not null),
            apps.FindIndex(a => a.Indicator == AppIndicator.Front), ItemDip, ItemDip, room);
        CollectionSync.Apply(_shown, shown.Select(i => apps[i]).ToList(), a => a.Key, ReferenceEquals);
        _hidden = hidden.Select(i => apps[i]).ToList();
        MoreButton.Visibility = _hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreLine.Visibility = _hidden.Any(a => a.Indicator == AppIndicator.Attention) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Left click like the taskbar: start it without a window, toggle a single one; with several windows the one used
    /// last comes to the front, and once the app is in front the click lists all of them.
    /// </summary>
    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is BarApp app) Open(app, (UIElement)sender);
    }

    private void Open(BarApp app, UIElement target)
    {
        var windows = Alive(app);
        // In front as the bar shows it (the line under the symbol), not asked anew: a click may hand the focus around.
        switch (RunningApps.Click(windows.Count, app.Indicator == AppIndicator.Front))
        {
            case AppClick.Launch: Launch(app); break;
            case AppClick.Toggle: AppWindows.Toggle(windows[0].Hwnd); break;
            case AppClick.Activate: AppWindows.Activate(windows[0].Hwnd); break;
            case AppClick.Menu: WindowMenu(app, target, full: false).IsOpen = true; break;
        }
    }

    /// <summary>Middle click: a new window, like the taskbar.</summary>
    private void App_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle || ((FrameworkElement)sender).DataContext is not BarApp app) return;
        e.Handled = true;
        Launch(app);
    }

    /// <summary>Right click: the windows, a new one, close them all.</summary>
    private void App_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BarApp app) return;
        e.Handled = true;
        WindowMenu(app, (UIElement)sender, full: true).IsOpen = true;
    }

    /// <summary>The symbols that did not fit, with their names.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = Menu(MoreButton);
        foreach (var app in _hidden)
        {
            var item = Item(app.Name, app.Name, () => Open(app, MoreButton));
            item.Icon = app.Icon is { } icon ? new Image { Source = icon, Width = 16, Height = 16 }
                : new Controls.Icon { Data = app.Glyph, Filled = app.GlyphFilled, Width = 16, Height = 16, Foreground = Brushes.White };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// The app's windows by title (read now: titles change all the time and are not watched), the one in front marked;
    /// <paramref name="full"/> adds a new window and closing them all.
    /// </summary>
    internal ContextMenu WindowMenu(BarApp app, UIElement target, bool full)
    {
        var menu = Menu(target);
        var windows = Alive(app);
        // The app's windows are in z-order: with the app in front, its first one is the window in front.
        IntPtr front = app.Indicator == AppIndicator.Front && windows.Count > 0 ? windows[0].Hwnd : IntPtr.Zero;
        foreach (var w in windows)
        {
            string title = Native.IsWindow(w.Hwnd) ? WindowTitle(w.Hwnd) : w.Title;
            var item = Item(title, "window", () => AppWindows.Activate(w.Hwnd));
            item.Header = new TextBlock { Text = title.Length > 0 ? title : app.Name, MaxWidth = 340, TextTrimming = TextTrimming.CharacterEllipsis };
            if (w.Hwnd == front)
            {
                item.FontWeight = FontWeights.SemiBold;
                item.Icon = new Controls.Icon { Data = (Geometry)FindResource("Icon.Check"), Width = 14, Height = 14, Foreground = Brushes.White };
            }
            menu.Items.Add(item);
        }
        if (!full) return menu;
        if (windows.Count > 0) menu.Items.Add(Separator());
        menu.Items.Add(windows.Count > 0 ? Item("Neues Fenster", "New window", () => Launch(app)) : Item("Öffnen", "Open", () => Launch(app)));
        if (windows.Count > 0)
            menu.Items.Add(Item(windows.Count > 1 ? "Alle schließen" : "Schließen", windows.Count > 1 ? "Close all" : "Close", () =>
            {
                foreach (var w in windows) Native.PostMessage(w.Hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            }));
        // Pinning like the taskbar: into the launcher list, the one list for the island and the bar.
        if (DataContext is MenuBarModel { Shortcuts: { } shortcuts })
        {
            menu.Items.Add(Separator());
            if (app.Pin is { } pin) menu.Items.Add(Item("Lösen", "Unpin", () => shortcuts.Remove(pin)));
            else if (app.PinPath.Length > 0)
            {
                var item = Item("Anheften", "Pin", () => shortcuts.Add(new[] { app.PinPath }));
                item.IsEnabled = shortcuts.Items.Count < ShortcutsService.MaxItems; // the launcher holds 12
                menu.Items.Add(item);
            }
        }
        return menu;
    }

    /// <summary>Windows still open (snapshots keep their stand-ins).</summary>
    private List<OpenWindow> Alive(BarApp app) => app.Windows.Where(w => Native.IsWindow(w.Hwnd) || !_reserve).ToList();

    private void Launch(BarApp app)
    {
        if (DataContext is not MenuBarModel { Shortcuts: { } shortcuts }) return;
        if (app.Pin is { } pin) shortcuts.Launch(pin);
        else if (app.LaunchPath.Length > 0) shortcuts.Launch(new ShortcutItem { Path = app.LaunchPath });
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    /// <summary>Reserves the strip on its display and moves the window into it (physical pixels).</summary>
    public void Place()
    {
        if (_reserve && _bar?.IsRegistered != true) return;
        var m = Monitors.FindDevice(Device);
        if (m is null) return; // unplugged: the manager closes this bar
        int h = (int)Math.Round(MenuBarMath.HeightDip * m.Scale);
        var rc = new Native.RECT { Left = m.Bounds.Left, Top = m.Bounds.Top, Right = m.Bounds.Right, Bottom = m.Bounds.Top + h };
        if (_bar is not null)
        {
            MenuBarManager.SetReserved(Device, h);
            rc = _bar.Place(m.Bounds, h);
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rc.Left, rc.Top, rc.Width, rc.Height, Native.SWP_NOACTIVATE);
        ReservedChanged?.Invoke();
    }

    private void Release()
    {
        if (_bar is null) return;
        MenuBarManager.SetReserved(Device, 0);
        _bar?.Remove();
        ReservedChanged?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == AppBar.CallbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case AppBar.ABN_POSCHANGED: Place(); break; // taskbar or another bar moved
                case AppBar.ABN_FULLSCREENAPP: SetFullscreen(MenuBarManager.FullscreenOn(Device)); break;
            }
        }
        // New scale (moved to another display, scaling changed): WPF scales first, then the strip gets its new height.
        else if (msg == Native.WM_DPICHANGED) Dispatcher.BeginInvoke(Place);
        return IntPtr.Zero;
    }

    /// <summary>
    /// A fullscreen film or game on this display: the bar steps aside (the space stays reserved, the fullscreen window
    /// covers it anyway) and comes back afterwards. Checked by the manager every second and on Windows' own message.
    /// </summary>
    public void SetFullscreen(bool here)
    {
        if (here == _fullscreen) return;
        _fullscreen = here;
        Log.Info($"Menu bar on {Device}: {(here ? "hidden for a fullscreen app" : "back")}");
        if (here) Hide();
        else { Show(); Place(); }
    }

    // ---------- look ----------

    /// <summary>Light like the taskbar when Windows uses the light system theme (snapshots: DYNAMICBAY_SNAPSHOT_THEME).</summary>
    public static bool SystemIsLight()
    {
        var theme = Environment.GetEnvironmentVariable("DYNAMICBAY_SNAPSHOT_THEME");
        if (theme is "dark" or "light") return theme == "light";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Windows switched between light and dark: matters only while nothing has been sampled below the bar.</summary>
    public void ApplyTheme() => Paint();

    /// <summary>
    /// Takes the colours right below the bar (the window there, or the wallpaper) as its gradient, like the Mac menu bar
    /// takes on the wallpaper. <paramref name="islands"/> (screen rects of the island shapes) and <paramref name="onTop"/>
    /// (windows kept on top) are left out: they lie over that row and would otherwise run into the bar as smears.
    /// </summary>
    public void Tone(IReadOnlyList<Native.RECT> islands, IReadOnlyList<Native.RECT> onTop)
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        Native.GetWindowRect(_hwnd, out var r);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // The left side ends before the notch; the app name shortens first, then symbols move behind "more".
        double maxWidth = MenuBarMath.LeftRoom(r, islands, (int)(12 * scale)) is int room ? room / scale : double.PositiveInfinity;
        if (maxWidth != LeftSide.MaxWidth)
        {
            LeftSide.MaxWidth = maxWidth;
            Refit();
        }
        // 12 px around: the notch's curves and soft edges
        var skip = MenuBarMath.Covered(r, islands.Concat(onTop), 12);
        var colors = SampleRow(r.Left, r.Bottom, r.Width, skip);
        if (colors is null || (_tone is not null && colors.SequenceEqual(_tone))) return;
        _tone = colors;
        Paint();
    }

    /// <summary>
    /// Text dark or light after the sampled colours (else the Windows theme), the background their gradient. Opaque: a
    /// blur behind the bar would also smear the island hanging over it (its colours ran out to both sides).
    /// </summary>
    private void Paint()
    {
        bool light = _tone is { } t ? MenuBarMath.IsLight(t) : SystemIsLight();
        Resources["Bar.Fore"] = Frozen(light ? 0xE6000000 : 0xF2FFFFFF);
        Resources["Bar.Hover"] = Frozen(light ? 0x1A000000u : 0x24FFFFFFu);
        if (_tone is null)
        {
            _gradient = null;
            Background = Frozen(MenuBarMath.Plain(light));
            return;
        }
        if (_gradient is null)
        {
            // Starts in the colour shown so far, then glides into the sampled one like the rest.
            var from = Background is SolidColorBrush solid ? solid.Color : _tone[0];
            var stops = new GradientStopCollection();
            for (int i = 0; i < _tone.Length; i++) stops.Add(new GradientStop(from, (i + 0.5) / _tone.Length));
            _gradient = new LinearGradientBrush(stops, 0);
            Background = _gradient;
        }
        // A new window below: a soft change instead of a jump.
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        for (int i = 0; i < _tone.Length && i < _gradient.GradientStops.Count; i++)
            _gradient.GradientStops[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(_tone[i], TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
    }

    /// <summary>A two-pixel row of the screen below the bar, averaged into <see cref="MenuBarMath.ToneSlices"/> colours.</summary>
    private static Color[]? SampleRow(int left, int top, int width, IReadOnlyList<(int, int)> skip)
    {
        const int rows = 2;
        try
        {
            using var bmp = new System.Drawing.Bitmap(width, rows, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                IntPtr dst = g.GetHdc(), src = GetDC(IntPtr.Zero);
                try { BitBlt(dst, 0, 0, width, rows, src, left, top, 0x00CC0020); } // SRCCOPY (layered windows come along anyway)
                finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
            }
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, rows), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                var bytes = new byte[width * rows * 4];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); // 32 bpp: no row padding
                return MenuBarMath.Slices(bytes, width, rows, MenuBarMath.ToneSlices, skip);
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            Log.Info($"Menu bar: colours below not readable ({ex.Message})");
            return null;
        }
    }

    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    private const int DwmwaWindowCornerPreference = 33, DwmCornerDoNotRound = 1;

    // ---------- clicks ----------

    /// <summary>The start menu where the Mac has the Apple menu: this PC, settings, power.</summary>
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var menu = Menu((UIElement)sender);
        // The Windows key may open the search instead: Windows' own Start menu stays one click away (Ctrl+Esc).
        menu.Items.Add(Item("Startmenü", "Start menu", () => { keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); }));
        menu.Items.Add(Item("Über diesen PC", "About this PC", () => Open("ms-settings:about")));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Einstellungen", "Settings", () => Open("ms-settings:")));
        menu.Items.Add(Item("Task-Manager", "Task Manager", () => Open("taskmgr.exe")));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Energie sparen", "Sleep", () => SetSuspendState(false, false, false)));
        menu.Items.Add(Item("Neu starten", "Restart", () => Run("shutdown.exe", "/r /t 0")));
        menu.Items.Add(Item("Herunterfahren", "Shut down", () => Run("shutdown.exe", "/s /hybrid /t 0")));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Sperren", "Lock", () => LockWorkStation()));
        menu.Items.Add(Item("Abmelden", "Sign out", () => Run("shutdown.exe", "/l")));
        menu.IsOpen = true;
    }

    /// <summary>The window menu of the app in front: minimize, maximize or restore, close.</summary>
    private void AppName_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MenuBarModel m || !Native.IsWindow(m.AppWindow)) return;
        IntPtr hwnd = m.AppWindow;
        bool zoomed = Native.IsZoomed(hwnd);
        var menu = Menu((UIElement)sender);
        menu.Items.Add(Item("Minimieren", "Minimize", () => ShowWindowAsync(hwnd, SwMinimize)));
        menu.Items.Add(zoomed ? Item("Wiederherstellen", "Restore", () => ShowWindowAsync(hwnd, SwRestore))
                              : Item("Maximieren", "Maximize", () => ShowWindowAsync(hwnd, SwMaximize)));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Schließen", "Close", () => Native.PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero)));
        menu.IsOpen = true;
    }

    /// <summary>The song playing: brings its player to the front (Spotify by its own link, others by their app id).</summary>
    private void Media_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MenuBarModel { Media: { } media }) return;
        if (media.IsSpotify) Open("spotify:");
        else if (media.SourceAppId.Length > 0) Run("explorer.exe", @"shell:AppsFolder\" + media.SourceAppId);
    }

    private ContextMenu Menu(UIElement target) =>
        new() { PlacementTarget = target, Placement = PlacementMode.Bottom, Style = (Style)FindResource("Island.Menu") };

    private MenuItem Item(string de, string en, Action action)
    {
        var item = new MenuItem { Header = Loc.German ? de : en, Style = (Style)FindResource("Island.MenuItem") };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Info($"Menu bar: {en} failed ({ex.Message})"); }
        };
        return item;
    }

    private Separator Separator() => new() { Margin = new Thickness(8, 4, 8, 4), Background = (Brush)FindResource("B.Hairline") };

    private static void Run(string file, string args) => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();

    private const int SwMaximize = 3, SwMinimize = 6, SwRestore = 9;
    private const uint WmClose = 0x0010;

    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);

    private void QuickSettings_Click(object sender, RoutedEventArgs e) => Open("ms-actioncenter:controlcenter/&showFooter=true");

    private void Clock_Click(object sender, RoutedEventArgs e) => Open("ms-actioncenter:");

    private void Search_Click(object sender, RoutedEventArgs e) => (DataContext as MenuBarModel)?.Search?.Invoke(Device);

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static void Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Info($"Menu bar: {uri} did not open ({ex.Message})"); }
    }
}
