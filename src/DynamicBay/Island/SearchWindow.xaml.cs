using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Motion;
using DynamicBay.Services;

namespace DynamicBay.Island;

/// <summary>
/// The search field in the middle of a display, like Spotlight: apps, settings pages and tools while typing, files a
/// moment later. Behind it the screen frosted (captured before it shows, so it does not follow a video playing
/// behind). Esc or a click elsewhere closes it, arrows choose, Enter opens, Ctrl+Enter opens the file's folder.
/// </summary>
public partial class SearchWindow : Window
{
    private const double Pad = 28, Field = 52, RowHeight = 44, ListInset = 8;
    private const int MaxRows = 9, MaxFiles = 4;

    private readonly SearchService _service;
    private readonly ObservableCollection<SearchItem> _items = new();
    private readonly SpringGroup _motion = new() { EveryFrame = true };
    private readonly Spring _height;
    private readonly Spring _appear;
    private readonly DispatcherTimer _fileDelay = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private List<SearchItem> _apps = new();
    private int _query;
    private int _selected = -1;
    private bool _closing;
    private MonitorInfo? _monitor;
    private Point _mouse;

    /// <summary>Opened or closed (snapshots wait for it).</summary>
    public bool IsOpen => IsVisible && !_closing;

    public SearchWindow(SearchService service)
    {
        _service = service;
        InitializeComponent();
        List.ItemsSource = _items;
        _height = _motion.Add(new Spring(Field, 0.34, 0.86));
        _appear = _motion.Add(new Spring(0, 0.32, 0.8));
        _motion.Updated += Apply;
        _fileDelay.Tick += (_, _) => { _fileDelay.Stop(); _ = FindFilesAsync(); };
        SourceInitialized += (_, _) => Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW);
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnKey;
        DpiChanged += (_, _) => Place();
        Inner.SizeChanged += (_, _) => ClipToShape();
    }

    /// <summary>Opens on <paramref name="monitor"/>, or closes when already open (the Windows key twice).</summary>
    public void Toggle(MonitorInfo monitor)
    {
        if (IsOpen) Dismiss();
        else Open(monitor);
    }

    public void Open(MonitorInfo monitor)
    {
        bool fresh = !IsVisible;
        Log.Info($"Search: open on {monitor.Device}{(fresh ? "" : " (was closing)")}");
        _closing = false;
        IsHitTestVisible = true;
        _monitor = monitor;
        if (fresh)
        {
            ApplyTheme();
            new WindowInteropHelper(this).EnsureHandle();
            Place();
            Place(); // the first one may have moved it to a display with another scale
            Backdrop.Source = Frost(monitor.Scale);
            Query.Text = "";
            _height.Snap(Field);
            _appear.Snap(0);
            Apply();
            Show();
        }
        _mouse = Mouse.GetPosition(this); // a mouse resting over the field selects nothing until it moves
        Activate();
        ForceForeground();
        Query.Focus();
        Keyboard.Focus(Query);
        _appear.Target = 1;
        _appear.Response = 0.32;
        _appear.Damping = 0.8;
        _motion.Kick();
    }

    /// <summary>Closes with the spring; the window hides once it has faded.</summary>
    public void Dismiss()
    {
        if (!IsVisible || _closing) return;
        Log.Info("Search: closed");
        _closing = true;
        IsHitTestVisible = false; // given back in Finish and in Open
        _fileDelay.Stop();
        _query++; // answers still on their way are dropped
        _appear.Target = 0;
        _appear.Response = 0.22;
        _appear.Damping = 1;
        _motion.Kick();
    }

    private void Finish()
    {
        Hide();
        _closing = false;
        IsHitTestVisible = true;
        Backdrop.Source = null; // the capture is several MB
        _items.Clear();
        _service.ForgetFileIcons();
        _apps.Clear();
    }

    private void Apply()
    {
        Panel.Height = _height.Value;
        double a = _appear.Value;
        Root.Opacity = Math.Clamp(a, 0, 1);
        Zoom.ScaleX = Zoom.ScaleY = 0.96 + 0.04 * a;
        if (_closing && _appear.IsSettled) Finish();
    }

    private void ClipToShape()
    {
        double r = Panel.CornerRadius.TopLeft - 1;
        Inner.Clip = new RectangleGeometry(new Rect(0, 0, Inner.ActualWidth, Inner.ActualHeight), r, r);
    }

    // ---------- place and look ----------

    /// <summary>Centred, a fifth from the top of its display (physical pixels; WPF follows with the display's scale).</summary>
    private void Place()
    {
        if (_monitor is not { } m) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var r = Rect(m);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
    }

    private Native.RECT Rect(MonitorInfo m)
    {
        int w = (int)Math.Round(Width * m.Scale), h = (int)Math.Round(Height * m.Scale);
        int x = m.Bounds.Left + (m.Bounds.Width - w) / 2;
        int y = m.Bounds.Top + (int)(m.Bounds.Height * 0.2) - (int)(Pad * m.Scale);
        return new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private void ApplyTheme()
    {
        bool light = MenuBarWindow.SystemIsLight();
        Resources["S.Fore"] = Frozen(light ? 0xE6000000 : 0xF2FFFFFF);
        Resources["S.Dim"] = Frozen(light ? 0x8C000000 : 0x8CFFFFFF);
        Resources["S.Tint"] = Frozen(light ? 0x8CF6F6F8 : 0x6B1E1E20);
        Resources["S.Select"] = Frozen(light ? 0x1A000000u : 0x26FFFFFFu);
        Resources["S.Line"] = Frozen(light ? 0x14000000u : 0x1AFFFFFFu);
        Resources["S.Edge"] = Frozen(light ? 0x1F000000u : 0x33FFFFFFu);
    }

    private static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    /// <summary>The screen where the window will be, frosted (taken while the window is still hidden).</summary>
    private BitmapSource? Frost(double scale)
    {
        if (_monitor is not { } m) return null;
        var r = Rect(m);
        try
        {
            var bgra = new byte[r.Width * r.Height * 4];
            using (var bmp = new System.Drawing.Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr dst = g.GetHdc(), src = GetDC(IntPtr.Zero);
                    try { BitBlt(dst, 0, 0, r.Width, r.Height, src, r.Left, r.Top, 0x00CC0020 | 0x40000000); } // SRCCOPY | CAPTUREBLT
                    finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
                }
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, r.Width, r.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
                try { Marshal.Copy(data.Scan0, bgra, 0, bgra.Length); }
                finally { bmp.UnlockBits(data); }
            }
            const int factor = 4;
            var (px, w, h) = ImageTools.Frost(bgra, r.Width, r.Height, factor, (int)Math.Max(2, Math.Round(5 * scale)));
            var frosted = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            frosted.Freeze();
            return frosted;
        }
        catch (Exception ex)
        {
            Log.Info($"Search: screen behind not readable ({ex.Message})");
            return null;
        }
    }

    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    /// <summary>
    /// Typing must land here. Windows refuses the foreground to a process the last input did not go to (the Windows key
    /// went to the shell): joining the input of the window in front lifts that, without pressing any key.
    /// </summary>
    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var front = Native.GetForegroundWindow();
        if (front == hwnd) return;
        uint frontThread = Native.GetWindowThreadProcessId(front, out _), own = GetCurrentThreadId();
        bool joined = frontThread != 0 && frontThread != own && AttachThreadInput(own, frontThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (joined) AttachThreadInput(own, frontThread, false);
        }
        if (Native.GetForegroundWindow() != hwnd) Log.Info("Search: Windows did not bring the field to the front");
    }

    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    // ---------- results ----------

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = Query.Text;
        Placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _query++;
        _fileDelay.Stop();
        _apps = text.Trim().Length == 0 ? new() : _service.Find(text, MaxRows);
        ShowResults(_apps.Take(MaxRows).ToList(), keepSelection: false);
        if (text.Trim().Length >= 2) _fileDelay.Start();
    }

    /// <summary>Files come later (index or Everything); an answer to an older query is dropped.</summary>
    private async Task FindFilesAsync()
    {
        int query = _query;
        string text = Query.Text;
        List<SearchItem> files;
        try { files = await _service.FindFilesAsync(text, MaxFiles); }
        catch (Exception ex) { Log.Info($"Search: files failed ({ex.Message})"); return; }
        if (query != _query || !IsOpen || files.Count == 0) return;
        int apps = Math.Min(_apps.Count, MaxRows - files.Count);
        ShowResults(_apps.Take(apps).Concat(files).ToList(), keepSelection: true);
    }

    private void ShowResults(List<SearchItem> items, bool keepSelection)
    {
        var selected = keepSelection && _selected >= 0 && _selected < _items.Count ? _items[_selected].Id : null;
        _items.Clear();
        foreach (var item in items)
        {
            item.Glyph = Application.Current.TryFindResource(item.Kind switch
            {
                SearchKind.Setting => "Icon.Settings",
                SearchKind.Tool => "Icon.Cog",
                SearchKind.File => "Icon.File",
                SearchKind.Folder => "Icon.Folder",
                _ => "Icon.AppWindow",
            }) as Geometry;
            _items.Add(item);
            _ = _service.LoadIcon(item);
        }
        int index = selected is null ? 0 : Math.Max(0, items.FindIndex(i => i.Id == selected));
        Select(items.Count == 0 ? -1 : index);
        Line.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _height.Target = items.Count == 0 ? Field : Field + 1 + 2 * ListInset + items.Count * RowHeight;
        _motion.Kick();
    }

    private void Select(int index)
    {
        _selected = index;
        for (int i = 0; i < _items.Count; i++) _items[i].Selected = i == index;
    }

    private SearchItem? Current => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    // ---------- keys and mouse ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                break;
            case Key.Down:
                if (_items.Count > 0) Select(Math.Min(_selected + 1, _items.Count - 1));
                break;
            case Key.Up:
                if (_items.Count > 0) Select(Math.Max(_selected - 1, 0));
                break;
            case Key.Enter:
                OpenCurrent(folder: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
            case Key.Tab:
                break; // focus stays in the field
            default:
                return;
        }
        e.Handled = true;
    }

    private void OpenCurrent(bool folder)
    {
        if (Current is not { } item) return;
        if (folder && _service.OpenFolder(item)) { Dismiss(); return; }
        if (folder) return; // apps and settings have no folder to show
        Dismiss();
        _service.Open(item);
    }

    // A list changing under a still mouse must not move the selection: only a real move selects.
    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p == _mouse) return;
        _mouse = p;
        if (((FrameworkElement)sender).DataContext is SearchItem item) Select(_items.IndexOf(item));
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SearchItem item) return;
        Select(_items.IndexOf(item));
        OpenCurrent(folder: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    /// <summary>Snapshots: types a query as if by hand.</summary>
    internal void Type(string text)
    {
        Query.Text = text;
        Query.CaretIndex = text.Length;
    }
}
