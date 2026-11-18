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
        Closed += (_, _) => Release();
    }

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
        // The left side ends before the notch; the app name shortens first.
        LeftSide.MaxWidth = MenuBarMath.LeftRoom(r, islands, (int)(12 * scale)) is int room ? room / scale : double.PositiveInfinity;
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

    /// <summary>Like the taskbar: an open window of the app comes to the front (or is minimized), otherwise it starts.</summary>
    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MenuBarModel m || ((FrameworkElement)sender).DataContext is not ShortcutItem item) return;
        var hwnd = AppWindows.Find(item.Path);
        if (hwnd != IntPtr.Zero) AppWindows.Toggle(hwnd);
        else m.Shortcuts?.Launch(item);
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
