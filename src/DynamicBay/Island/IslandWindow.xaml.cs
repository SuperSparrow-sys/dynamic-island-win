using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Motion;
using DynamicBay.Services;

namespace DynamicBay.Island;

public enum IslandMode { Idle, Compact, Peek, Expanded, Drop, Minimized }

public partial class IslandWindow : Window
{
    // Distance (DIP, before user scale) from the window edge to the shape edge: window margin + hit padding.
    private const double EdgeOffset = 34;
    private static readonly Size WindowH = new(790, 360);
    private static readonly Size WindowV = new(480, 560);
    private static readonly Size ExpandedH = new(660, 244);
    private static readonly Size ExpandedV = new(384, 452);

    private readonly IslandViewModel _vm;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;

    // Morph springs (DIP) and window position springs (physical px)
    private readonly SpringGroup _shapeAnim = new();
    private readonly Spring _w, _h, _r;
    private readonly SpringGroup _moveAnim = new();
    private readonly Spring _x, _y;
    private readonly SpringGroup _layerAnim = new();
    private readonly Dictionary<FrameworkElement, Spring> _layers = new();

    private IslandMode _mode = IslandMode.Idle;
    private bool _expanded, _dropActive, _minimized, _suppressed, _dragOut;
    private bool _vertical;

    // Peeks
    private readonly Queue<PeekItem> _peekQueue = new();
    private readonly DispatcherTimer _peekTimer = new();

    // Hover / collapse / auto-hide
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _dropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private DateTime _lastActivity = DateTime.Now;

    // Dragging the island
    private bool _pressed, _dragging;
    private Native.POINT _pressCursor;
    private Native.RECT _pressWindow;

    public IslandMode Mode => _mode;

    public IslandWindow(IslandViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _settings = vm.Settings;
        DataContext = vm;

        _w = _shapeAnim.Add(new Spring(200));
        _h = _shapeAnim.Add(new Spring(12));
        _r = _shapeAnim.Add(new Spring(6));
        _shapeAnim.Updated += ApplyShape;

        _x = _moveAnim.Add(new Spring(0, 0.5, 0.86));
        _y = _moveAnim.Add(new Spring(0, 0.5, 0.86));
        _moveAnim.Updated += () => MoveWindowPx((int)Math.Round(_x.Value), (int)Math.Round(_y.Value));

        foreach (var layer in new FrameworkElement[] { CompactLayer, PeekLayer, DropLayer, ExpandedLayer })
            _layers[layer] = _layerAnim.Add(new Spring(0, 0.3, 1.0));
        _layerAnim.Updated += ApplyLayers;

        _peekTimer.Tick += (_, _) => NextPeek();
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (HitPad.IsMouseOver && !_dragging) SetExpanded(true); };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); TryCollapse(); };
        _dropLeaveTimer.Tick += (_, _) => { _dropLeaveTimer.Stop(); _dropActive = false; Refresh(); };
        _watchdog.Tick += (_, _) => Watchdog();

        HitPad.MouseEnter += OnHoverEnter;
        HitPad.MouseLeave += OnHoverLeave;
        HitPad.MouseLeftButtonDown += OnPress;
        HitPad.MouseMove += OnPressMove;
        HitPad.MouseLeftButtonUp += OnRelease;
        HitPad.LostMouseCapture += (_, _) => { if (_dragging) FinishDrag(); _pressed = false; };
        HitPad.DragEnter += OnDragEnter;
        HitPad.DragOver += (_, e) => { _dropLeaveTimer.Stop(); e.Effects = DragDropEffects.Copy; e.Handled = true; };
        HitPad.DragLeave += (_, _) => _dropLeaveTimer.Start();
        HitPad.Drop += OnDrop;
        PeekLayer.MouseLeftButtonUp += OnPeekClick;

        ExpandedLayer.DragOutActive += active => { _dragOut = active; if (!active) ScheduleCollapseIfAway(); };
        ExpandedLayer.CopiedFeedback += _ => { };

        _vm.CompactChanged += Refresh;
        _vm.HideRequested += () => SetHidden(true);
        _vm.PropertyChanged += OnVmChanged;
        _settings.PropertyChanged += OnSettingChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplyUserScale();
            Place(animate: false);
            Refresh();
            SnapShapeToTargets();
            _watchdog.Start();
        };
    }

    // ================= Win32 setup =================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: not in Alt+Tab. No-activate: never steals focus from the app you're typing in.
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyLayer();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_WINDOWPOSCHANGING when _settings.Layer == LayerMode.Desktop && !_desktopForeground:
                var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                pos.flags &= ~Native.SWP_NOZORDER;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_SETTINGCHANGE:
            case Native.WM_DPICHANGED:
                Dispatcher.BeginInvoke(() => Place(animate: false), DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    private bool _desktopForeground;

    private void ApplyLayer()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_settings.Layer == LayerMode.Floating)
        {
            Topmost = true;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            Topmost = false;
            Native.SetWindowPos(_hwnd, _desktopForeground ? Native.HWND_TOPMOST : Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (!_desktopForeground)
                Native.SetWindowPos(_hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    // ================= Placement =================

    private static (Native.RECT work, Native.RECT bounds, double scale) Primary()
    {
        var mon = Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(mon, ref info);
        Native.GetDpiForMonitor(mon, 0, out uint dpi, out _);
        return (info.rcWork, info.rcMonitor, dpi / 96.0);
    }

    private double U => _settings.Scale <= 0 ? 1 : _settings.Scale;

    private void ApplyUserScale()
    {
        UserScale.ScaleX = UserScale.ScaleY = U;
    }

    /// <summary>Positions the window from the saved placement (edge, alignment, position along the edge, inset).</summary>
    private void Place(bool animate)
    {
        if (_hwnd == IntPtr.Zero) return;
        var (wa, _, s) = Primary();
        bool vertical = _settings.Edge is IslandEdge.Left or IslandEdge.Right;
        if (vertical != _vertical || Width != WinSize(vertical).Width * U)
        {
            _vertical = vertical;
            _vm.IsVertical = vertical;
            CompactLayer.SetVertical(vertical);
            ExpandedLayer.SetVertical(vertical);
        }
        var win = WinSize(vertical);
        Width = win.Width * U;
        Height = win.Height * U;
        SizeExpandedLayer();
        AlignShape();

        var (x, y) = WindowOrigin(wa, s);
        if (animate)
        {
            _x.Target = x;
            _y.Target = y;
            _moveAnim.Kick();
        }
        else
        {
            _x.Snap(x);
            _y.Snap(y);
            MoveWindowPx(x, y, resize: true);
        }
        Log.Info($"Placed island: edge={_settings.Edge} align={_settings.Align} window=({x},{y}) {Width:0}x{Height:0} dip, scale {s:0.00}, work=({wa.Left},{wa.Top},{wa.Right},{wa.Bottom})");
        Refresh();
    }

    private static Size WinSize(bool vertical) => vertical ? WindowV : WindowH;

    /// <summary>The panel is laid out at its final size so content never reflows while the shape morphs.</summary>
    private void SizeExpandedLayer()
    {
        var size = _vertical ? ExpandedV : ExpandedH;
        ExpandedLayer.Width = size.Width;
        ExpandedLayer.Height = size.Height;
    }

    private (int x, int y) WindowOrigin(Native.RECT wa, double s)
    {
        double wpx = WinSize(_vertical).Width * U * s, hpx = WinSize(_vertical).Height * U * s;
        double off = EdgeOffset * U * s, inset = _settings.Inset * s;
        double x, y;
        switch (_settings.Edge)
        {
            case IslandEdge.Top:
            case IslandEdge.Bottom:
            {
                double ax = wa.Left + _settings.Along * wa.Width;
                x = _settings.Align switch
                {
                    IslandAlign.Start => ax - off,
                    IslandAlign.End => ax - wpx + off,
                    _ => ax - wpx / 2,
                };
                y = _settings.Edge == IslandEdge.Top ? wa.Top + inset - off : wa.Bottom - inset - hpx + off;
                break;
            }
            default:
            {
                double ay = wa.Top + _settings.Along * wa.Height;
                y = _settings.Align switch
                {
                    IslandAlign.Start => ay - off,
                    IslandAlign.End => ay - hpx + off,
                    _ => ay - hpx / 2,
                };
                x = _settings.Edge == IslandEdge.Left ? wa.Left + inset - off : wa.Right - inset - wpx + off;
                break;
            }
        }
        return ((int)Math.Round(x), (int)Math.Round(y));
    }

    private void AlignShape()
    {
        var (h, v) = _settings.Edge switch
        {
            IslandEdge.Top => (AlignH(), VerticalAlignment.Top),
            IslandEdge.Bottom => (AlignH(), VerticalAlignment.Bottom),
            IslandEdge.Left => (HorizontalAlignment.Left, AlignV()),
            _ => (HorizontalAlignment.Right, AlignV()),
        };
        HitPad.HorizontalAlignment = h;
        HitPad.VerticalAlignment = v;
        // Shadow falls away from the docked edge.
        Shadow.Direction = _settings.Edge switch { IslandEdge.Bottom => 90, IslandEdge.Left => 0, IslandEdge.Right => 180, _ => 270 };
    }

    private HorizontalAlignment AlignH() => _settings.Align switch
    {
        IslandAlign.Start => HorizontalAlignment.Left,
        IslandAlign.End => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    private VerticalAlignment AlignV() => _settings.Align switch
    {
        IslandAlign.Start => VerticalAlignment.Top,
        IslandAlign.End => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Center,
    };

    private void MoveWindowPx(int x, int y, bool resize = false)
    {
        if (_hwnd == IntPtr.Zero) return;
        uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER;
        if (!resize) flags |= Native.SWP_NOSIZE;
        var (_, _, s) = Primary();
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, (int)Math.Round(Width * s), (int)Math.Round(Height * s), flags);
    }

    public void ResetPosition()
    {
        _settings.Edge = IslandEdge.Top;
        _settings.Align = IslandAlign.Center;
        _settings.Along = 0.5;
        _settings.Inset = 8;
        _settings.SaveSoon();
        Place(animate: true);
    }

    // ================= Mode & morph =================

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.IsPinned) && !_vm.IsPinned) ScheduleCollapseIfAway();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Layer): ApplyLayer(); break;
            case nameof(AppSettings.Scale): ApplyUserScale(); Place(false); break;
            case nameof(AppSettings.Edge):
            case nameof(AppSettings.Align):
            case nameof(AppSettings.Along):
            case nameof(AppSettings.Inset):
                if (!_dragging) Place(animate: true);
                break;
            case nameof(AppSettings.Shadow): ShapeBg.Effect = _settings.Shadow ? Shadow : null; break;
            case nameof(AppSettings.Hidden): SetHidden(_settings.Hidden); break;
            case nameof(AppSettings.Idle):
            case nameof(AppSettings.AutoHide):
                _lastActivity = DateTime.Now; _minimized = false; Refresh(); break;
        }
    }

    private IslandMode DesiredMode()
    {
        if (_dropActive) return IslandMode.Drop;
        if (_expanded) return IslandMode.Expanded;
        if (_vm.Peek is not null) return IslandMode.Peek;
        if (_minimized) return IslandMode.Minimized;
        if (_vm.HasCompact) return IslandMode.Compact;
        return IslandMode.Idle;
    }

    /// <summary>Re-evaluates the mode and springs the shape toward it.</summary>
    public void Refresh()
    {
        var mode = DesiredMode();
        bool grow = Rank(mode) > Rank(_mode);
        _mode = mode;

        var target = TargetSize(mode);
        double radius = mode switch
        {
            IslandMode.Expanded => 32,
            IslandMode.Drop => 30,
            IslandMode.Peek => 22,
            _ => Math.Min(target.Width, target.Height) / 2,
        };

        double speed = Math.Clamp(_settings.AnimationSpeed, 0.5, 2);
        // Opening gets a lively, slightly bouncy spring; closing settles without overshoot.
        double response = (grow ? 0.5 : 0.42) / speed;
        double damping = grow ? 0.72 : 0.9;
        foreach (var s in new[] { _w, _h, _r }) { s.Response = response; s.Damping = damping; }
        _w.Target = target.Width;
        _h.Target = target.Height;
        _r.Target = radius;
        _shapeAnim.Kick();

        SetLayer(CompactLayer, mode == IslandMode.Compact);
        SetLayer(PeekLayer, mode == IslandMode.Peek);
        SetLayer(DropLayer, mode == IslandMode.Drop);
        SetLayer(ExpandedLayer, mode == IslandMode.Expanded);
        _layerAnim.Kick();
    }

    private static int Rank(IslandMode m) => m switch
    {
        IslandMode.Minimized => 0,
        IslandMode.Idle => 1,
        IslandMode.Compact => 2,
        IslandMode.Peek => 3,
        IslandMode.Drop => 4,
        _ => 5,
    };

    private Size TargetSize(IslandMode mode)
    {
        bool v = _vertical;
        switch (mode)
        {
            case IslandMode.Expanded: return v ? ExpandedV : ExpandedH;
            case IslandMode.Drop: return new Size(380, 132);
            case IslandMode.Peek:
                PeekLayer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return new Size(Math.Max(300, PeekLayer.DesiredSize.Width), 58);
            case IslandMode.Compact:
                CompactLayer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var d = CompactLayer.DesiredSize;
                return v ? new Size(32, Math.Max(110, d.Height)) : new Size(Math.Max(150, d.Width), 28);
            case IslandMode.Minimized: return v ? new Size(5, 84) : new Size(84, 5);
            default:
                if (_settings.Idle == IdleStyle.Hidden) return v ? new Size(5, 84) : new Size(84, 5);
                return v ? new Size(12, 190) : new Size(210, 12);
        }
    }

    private void SetLayer(FrameworkElement layer, bool active)
    {
        var s = _layers[layer];
        // Incoming content waits a beat so it appears into the grown shape, like the iPhone island.
        s.Response = active ? 0.36 : 0.16;
        s.Target = active ? 1 : 0;
        layer.IsHitTestVisible = active;
    }

    private void SnapShapeToTargets()
    {
        _w.Snap(_w.Target); _h.Snap(_h.Target); _r.Snap(_r.Target);
        foreach (var (layer, s) in _layers) s.Snap(s.Target);
        ApplyShape();
        ApplyLayers();
    }

    private void ApplyShape()
    {
        double w = Math.Max(1, _w.Value), h = Math.Max(1, _h.Value);
        double r = Math.Clamp(_r.Value, 0, Math.Min(w, h) / 2);
        Shape.Width = w;
        Shape.Height = h;
        ShapeBg.CornerRadius = new CornerRadius(r);
        Rim.CornerRadius = new CornerRadius(r);
        ShapeContent.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    private void ApplyLayers()
    {
        foreach (var (layer, s) in _layers)
        {
            double o = Math.Clamp(s.Value, 0, 1);
            layer.Opacity = o;
            layer.Visibility = o < 0.01 && s.Target == 0 ? Visibility.Hidden : Visibility.Visible;
            double scale = 0.9 + 0.1 * o;
            if (layer.RenderTransform is not ScaleTransform st)
            {
                layer.RenderTransformOrigin = new Point(0.5, 0.5);
                layer.RenderTransform = st = new ScaleTransform();
            }
            st.ScaleX = st.ScaleY = scale;
            // Blur only while transitioning (keeps idle rendering cheap and text crisp).
            if (o > 0.02 && o < 0.98)
            {
                if (layer.Effect is not BlurEffect be) layer.Effect = be = new BlurEffect { RenderingBias = RenderingBias.Performance };
                be.Radius = (1 - o) * 10;
            }
            else if (layer.Effect is not null) layer.Effect = null;
        }
    }

    // ================= Expand / collapse =================

    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        if (expanded) _peekTimer.Stop();
        else if (_vm.Peek is not null) _peekTimer.Start();
        if (!expanded) ExpandedLayerDevicesReset();
        Refresh();
    }

    private void ExpandedLayerDevicesReset() { }

    public void Toggle() => SetExpanded(!_expanded);

    private void OnHoverEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _lastActivity = DateTime.Now;
        if (_minimized) { _minimized = false; Refresh(); }
        if (_settings.ExpandOnHover && !_expanded && !_dragging && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.HoverDelayMs));
            _hoverTimer.Start();
        }
    }

    private void OnHoverLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        _lastActivity = DateTime.Now;
        ScheduleCollapseIfAway();
    }

    private void ScheduleCollapseIfAway()
    {
        if (!_expanded || _vm.IsPinned || _dragOut) return;
        if (HitPad.IsMouseOver) return;
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(80, _settings.CollapseDelayMs));
        _collapseTimer.Start();
    }

    private void TryCollapse()
    {
        if (HitPad.IsMouseOver || _vm.IsPinned || _dragOut) return;
        // Don't collapse while a context menu/dialog from the island is open.
        if (Mouse.Captured is not null && Mouse.Captured != HitPad) return;
        SetExpanded(false);
    }

    // ================= Peeks =================

    public void ShowPeek(PeekItem item)
    {
        if (_settings.DoNotDisturb && item.Priority < PeekPriority.High) return;
        if (_suppressed || _settings.Hidden) return;
        if (_expanded) return; // the expanded panel already shows the information
        if (_vm.Peek is null)
        {
            _vm.Peek = item;
            _peekTimer.Interval = TimeSpan.FromSeconds(item.Seconds);
            _peekTimer.Start();
            _minimized = false;
            _lastActivity = DateTime.Now;
            Refresh();
        }
        else
        {
            if (item.Priority > _vm.Peek.Priority) { _peekQueue.Clear(); _vm.Peek = null; ShowPeek(item); return; }
            if (_peekQueue.Count < 4) _peekQueue.Enqueue(item);
        }
    }

    private void NextPeek()
    {
        _peekTimer.Stop();
        if (HitPad.IsMouseOver && _vm.Peek is not null)
        {
            // Keep it while the user is looking at / interacting with it.
            _peekTimer.Interval = TimeSpan.FromSeconds(1);
            _peekTimer.Start();
            return;
        }
        _vm.Peek = null;
        if (_peekQueue.Count > 0)
        {
            // Brief collapse between peeks reads as two distinct events.
            Refresh();
            var next = _peekQueue.Dequeue();
            Dispatcher.BeginInvoke(async () => { await Task.Delay(260); ShowPeek(next); });
            return;
        }
        Refresh();
    }

    private void OnPeekClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || _vm.Peek is null) return;
        var action = _vm.Peek.OnClick;
        _vm.Peek = null;
        _peekTimer.Stop();
        if (action is not null) action();
        else SetExpanded(true);
        Refresh();
        e.Handled = true;
    }

    // ================= Drag & drop files =================

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!_settings.ShelfEnabled || !e.Data.GetDataPresent(DataFormats.FileDrop) || _dragOut) return;
        _dropLeaveTimer.Stop();
        _hoverTimer.Stop();
        if (_expanded)
        {
            _vm.Tab = 1; // show the shelf, drop lands on its card
            return;
        }
        _dropActive = true;
        Refresh();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dropLeaveTimer.Stop();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && !_dragOut)
            _vm.Shelf.Add(files);
        _dropActive = false;
        Refresh();
    }

    // ================= Moving the island =================

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        _pressed = true;
        _dragging = false;
        _pressCursor = Native.CursorPos();
        Native.GetWindowRect(_hwnd, out _pressWindow);
        HitPad.CaptureMouse();
    }

    private void OnPressMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var c = Native.CursorPos();
        int dx = c.X - _pressCursor.X, dy = c.Y - _pressCursor.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 6) return;
            _dragging = true;
            _hoverTimer.Stop();
            _collapseTimer.Stop();
            _moveAnim_Stop();
            if (_expanded) { _expanded = false; }
            Refresh();
        }
        var (wa, _, s) = Primary();
        // Keep the shape on the primary monitor.
        int nx = _pressWindow.Left + dx, ny = _pressWindow.Top + dy;
        var shape = ShapeScreenRect(nx, ny);
        if (shape.Left < wa.Left) nx += wa.Left - shape.Left;
        if (shape.Right > wa.Right) nx -= shape.Right - wa.Right;
        if (shape.Top < wa.Top) ny += wa.Top - shape.Top;
        if (shape.Bottom > wa.Bottom) ny -= shape.Bottom - wa.Bottom;
        _x.Snap(nx);
        _y.Snap(ny);
        MoveWindowPx(nx, ny);
    }

    private void _moveAnim_Stop()
    {
        _x.Snap(_x.Value);
        _y.Snap(_y.Value);
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        bool wasDragging = _dragging;
        HitPad.ReleaseMouseCapture();
        if (wasDragging) { FinishDrag(); return; }
        // A click: open/close. (Clicks on buttons inside never reach here.)
        if (e.OriginalSource is DependencyObject d && IsInside(d, ExpandedLayer)) return;
        Toggle();
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        while (d is not null)
        {
            if (d == parent) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>Shape rectangle on screen (px) if the window were at (wx, wy).</summary>
    private Native.RECT ShapeScreenRect(int wx, int wy)
    {
        var (_, _, s) = Primary();
        var p = Shape.TransformToAncestor(this).Transform(new Point(0, 0));
        double w = Shape.ActualWidth * U, h = Shape.ActualHeight * U;
        return new Native.RECT
        {
            Left = (int)(wx + p.X * s),
            Top = (int)(wy + p.Y * s),
            Right = (int)(wx + (p.X + w) * s),
            Bottom = (int)(wy + (p.Y + h) * s),
        };
    }

    private void FinishDrag()
    {
        _dragging = false;
        var (wa, _, s) = Primary();
        var r = ShapeScreenRect((int)_x.Value, (int)_y.Value);
        var oldShapePos = (r.Left, r.Top);

        double dTop = r.Top - wa.Top, dBottom = wa.Bottom - r.Bottom, dLeft = r.Left - wa.Left, dRight = wa.Right - r.Right;
        double min = new[] { dTop, dBottom, dLeft, dRight }.Min();
        IslandEdge edge = min == dTop ? IslandEdge.Top : min == dBottom ? IslandEdge.Bottom : min == dLeft ? IslandEdge.Left : IslandEdge.Right;
        bool horizontalEdge = edge is IslandEdge.Top or IslandEdge.Bottom;
        double snapPx = 70 * s;

        IslandAlign align;
        double anchor, along, inset = Math.Max(0, min) / s;
        if (horizontalEdge)
        {
            double cx = (r.Left + r.Right) / 2.0, zone = wa.Width * 0.18;
            if (cx - wa.Left < zone) { align = IslandAlign.Start; anchor = r.Left; }
            else if (wa.Right - cx < zone) { align = IslandAlign.End; anchor = r.Right; }
            else { align = IslandAlign.Center; anchor = cx; }
            if (_settings.MagneticSnap)
            {
                double center = wa.Left + wa.Width / 2.0;
                if (align == IslandAlign.Center && Math.Abs(anchor - center) < snapPx) anchor = center;
                if (align == IslandAlign.Start && anchor - wa.Left < snapPx) anchor = wa.Left + 8 * s;
                if (align == IslandAlign.End && wa.Right - anchor < snapPx) anchor = wa.Right - 8 * s;
            }
            along = (anchor - wa.Left) / wa.Width;
        }
        else
        {
            double cy = (r.Top + r.Bottom) / 2.0, zone = wa.Height * 0.18;
            if (cy - wa.Top < zone) { align = IslandAlign.Start; anchor = r.Top; }
            else if (wa.Bottom - cy < zone) { align = IslandAlign.End; anchor = r.Bottom; }
            else { align = IslandAlign.Center; anchor = cy; }
            if (_settings.MagneticSnap)
            {
                double center = wa.Top + wa.Height / 2.0;
                if (align == IslandAlign.Center && Math.Abs(anchor - center) < snapPx) anchor = center;
                if (align == IslandAlign.Start && anchor - wa.Top < snapPx) anchor = wa.Top + 8 * s;
                if (align == IslandAlign.End && wa.Bottom - anchor < snapPx) anchor = wa.Bottom - 8 * s;
            }
            along = (anchor - wa.Top) / wa.Height;
        }
        if (_settings.MagneticSnap && inset < 44) inset = 8;

        bool orientationChanges = (edge is IslandEdge.Left or IslandEdge.Right) != _vertical;
        _settings.PropertyChanged -= OnSettingChanged;
        _settings.Edge = edge;
        _settings.Align = align;
        _settings.Along = Math.Clamp(along, 0, 1);
        _settings.Inset = inset;
        _settings.PropertyChanged += OnSettingChanged;
        _settings.SaveSoon();

        // Re-layout for the new edge, then glide from where the shape visually is to its new home.
        bool vertical = edge is IslandEdge.Left or IslandEdge.Right;
        if (orientationChanges)
        {
            _vertical = vertical;
            _vm.IsVertical = vertical;
            CompactLayer.SetVertical(vertical);
            ExpandedLayer.SetVertical(vertical);
        }
        var win = WinSize(vertical);
        Width = win.Width * U;
        Height = win.Height * U;
        SizeExpandedLayer();
        AlignShape();
        UpdateLayout();

        var (tx, ty) = WindowOrigin(wa, s);
        // Start position that keeps the shape where the user let go.
        var p = Shape.TransformToAncestor(this).Transform(new Point(0, 0));
        int sx = (int)(oldShapePos.Left - p.X * s), sy = (int)(oldShapePos.Top - p.Y * s);
        MoveWindowPx(sx, sy, resize: true);
        _x.Snap(sx); _y.Snap(sy);
        _x.Target = tx; _y.Target = ty;
        _moveAnim.Kick();
        Refresh();
    }

    // ================= Hide / fullscreen / auto-hide =================

    public void SetHidden(bool hidden)
    {
        if (_settings.Hidden != hidden) { _settings.Hidden = hidden; _settings.SaveSoon(); return; }
        if (hidden)
        {
            _expanded = false;
            _vm.Peek = null;
            FadeOut();
        }
        else if (!_suppressed) FadeIn();
    }

    private void FadeOut()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        anim.Completed += (_, _) => { if (_settings.Hidden || _suppressed) Hide(); };
        BeginAnimation(OpacityProperty, anim);
    }

    private void FadeIn()
    {
        if (!IsVisible) Show();
        ApplyLayer();
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        _minimized = false;
        Refresh();
    }

    private void Watchdog()
    {
        // Fullscreen apps and excluded apps hide the island; it comes back afterwards.
        bool suppress = false;
        var fg = Native.GetForegroundWindow();
        if (_settings.HideInFullscreen && IsFullscreen(fg) && !_snapshotMode) suppress = true;
        if (!suppress && _settings.ExcludedApps.Count > 0)
        {
            var name = Native.ProcessName(fg);
            if (name is not null && _settings.ExcludedApps.Any(a => string.Equals(a.Replace(".exe", ""), name, StringComparison.OrdinalIgnoreCase)))
                suppress = true;
        }
        if (suppress != _suppressed)
        {
            Log.Info($"Island {(suppress ? "suppressed" : "restored")} (foreground: {Native.ProcessName(fg)} / {Native.ClassName(fg)})");
            _suppressed = suppress;
            if (suppress) FadeOut();
            else if (!_settings.Hidden) FadeIn();
        }

        // Desktop layer: float above the desktop itself (Win+D) but below every app window.
        if (_settings.Layer == LayerMode.Desktop)
        {
            string cls = fg == IntPtr.Zero ? "" : Native.ClassName(fg);
            bool desk = cls is "WorkerW" or "Progman" || fg == Native.GetShellWindow();
            if (desk != _desktopForeground) { _desktopForeground = desk; ApplyLayer(); }
        }
        else if (IsVisible && !_suppressed)
        {
            // Other topmost windows (taskbar, overlays) can bury us; reassert cheaply.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // Auto-hide to a thin line after inactivity.
        if (_settings.AutoHide && !_minimized && _mode is IslandMode.Idle or IslandMode.Compact &&
            !HitPad.IsMouseOver && (DateTime.Now - _lastActivity).TotalSeconds > _settings.AutoHideSeconds)
        {
            _minimized = true;
            Refresh();
        }
    }

    private bool IsFullscreen(IntPtr fg)
    {
        if (fg == IntPtr.Zero || fg == _hwnd) return false;
        string cls = Native.ClassName(fg);
        if (cls is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;
        // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4)
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4 && OnPrimary(fg)) return true;
        // Maximized windows also cover the monitor but keep their caption; real fullscreen windows don't.
        long style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
        if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var (_, bounds, _) = Primary();
        return r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
    }

    private static bool OnPrimary(IntPtr hwnd)
    {
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(mon, ref info);
        return (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
    }

    // ================= Design snapshots =================

    /// <summary>Forces a state for screenshot-based design review (DynamicBay.exe --snapshot).</summary>
    private bool _snapshotMode;

    public void ForceState(IslandMode mode, bool vertical = false)
    {
        _snapshotMode = true;
        if (_suppressed) { _suppressed = false; FadeIn(); }
        _expanded = mode == IslandMode.Expanded;
        _dropActive = mode == IslandMode.Drop;
        _minimized = mode == IslandMode.Minimized;
        if (mode != IslandMode.Peek) _vm.Peek = null;
        _peekTimer.Stop();
        Refresh();
    }
}
