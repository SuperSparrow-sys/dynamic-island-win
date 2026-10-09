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
    private static readonly Size ExpandedH = new(660, 264);
    private static readonly Size ExpandedV = new(384, 472);

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

    private readonly string? _mirrorDevice;
    private readonly PeekHolder _peek = new();

    /// <summary>True for the extra islands shown on other monitors in mirror mode.</summary>
    public bool IsMirror => _mirrorDevice is not null;
    public string? MirrorDevice => _mirrorDevice;

    public IslandWindow(IslandViewModel vm, string? mirrorDevice = null)
    {
        InitializeComponent();
        _vm = vm;
        _settings = vm.Settings;
        _mirrorDevice = mirrorDevice;
        DataContext = vm;
        PeekLayer.DataContext = _peek; // peeks are per window (each mirror runs its own timer)

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
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (HitPad.IsMouseOver && !_dragging && !ShowsQuestion) SetExpanded(true); };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); TryCollapse(); };
        _dropLeaveTimer.Tick += (_, _) => { _dropLeaveTimer.Stop(); _dropActive = false; Refresh(); };
        _watchdog.Tick += (_, _) => Watchdog();
        _replaceDebounce.Tick += (_, _) => ReplaceIfGeometryChanged();

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
        PeekLayer.Answered += yes =>
        {
            var item = _peek.Peek;
            DismissPeek();
            if (yes) item?.Action?.Invoke();
        };

        ExpandedLayer.DragOutActive += active => { _dragOut = active; if (!active) ScheduleCollapseIfAway(); };
        ExpandedLayer.CopiedFeedback += _ => { };

        // Deferred: bindings (visibility, texts) must update before the compact content is measured.
        _vm.CompactChanged += RefreshSoon;
        // Texts inside live activities can change width (Claude project name, "in 8 Min." -> "in 12 Min."): re-measure.
        _vm.Claude.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ClaudeService.WorkingText)) RefreshSoon(); };
        _vm.Calendar.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CalendarService.SoonText)) RefreshSoon(); };
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
            case Native.WM_DPICHANGED:
                _forceReplace = true; // WPF just rescaled the window: size must be recomputed
                goto case Native.WM_SETTINGCHANGE;
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_SETTINGCHANGE:
                // Windows broadcasts these often (theme, Mica, other apps). Re-place only if geometry really changed.
                _replaceDebounce.Stop();
                _replaceDebounce.Start();
                break;
        }
        return IntPtr.Zero;
    }

    private bool _desktopForeground;

    /// <summary>Settings window, app picker, menus of DynamicBay itself never count as fullscreen / excluded app.</summary>
    private static bool IsOwnWindow(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private readonly DispatcherTimer _replaceDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private string _geometryKey = "";
    private bool _forceReplace;

    private void ReplaceIfGeometryChanged()
    {
        _replaceDebounce.Stop();
        var m = TargetMonitor();
        string key = $"{m.Device}|{m.Work.Left},{m.Work.Top},{m.Work.Right},{m.Work.Bottom}|{m.Scale}";
        if (key == _geometryKey && !_forceReplace) return;
        _forceReplace = false;
        Place(animate: false);
    }

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

    /// <summary>The monitor this island lives on: its mirror monitor, the remembered one, or the primary.</summary>
    public MonitorInfo TargetMonitor()
    {
        if (_mirrorDevice is not null) return Monitors.ByDevice(_mirrorDevice);
        return _settings.Displays == DisplayMode.Single ? Monitors.ByDevice(_settings.Monitor) : Monitors.Primary();
    }

    private (Native.RECT work, Native.RECT bounds, double scale) Target()
    {
        var m = TargetMonitor();
        return (m.Work, m.Bounds, m.Scale);
    }

    /// <summary>
    /// Keeps the window inside its monitor so the invisible (but always-on-top) window never overlaps a neighbouring
    /// display. The shape keeps its intended screen position via a compensating translation.
    /// </summary>
    private (int x, int y) ClampToMonitor(int x, int y, Native.RECT bounds, double s)
    {
        int w = (int)Math.Round(Width * s), h = (int)Math.Round(Height * s);
        int cx = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - w));
        int cy = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - h));
        double k = s * U;
        HitPad.RenderTransform = new TranslateTransform((x - cx) / k, (y - cy) / k);
        return (cx, cy);
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
        if (_dragging) return; // WM_DPICHANGED while dragging across monitors
        var (wa, bounds, s) = Target();
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
        (x, y) = ClampToMonitor(x, y, bounds, s);
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
        _geometryKey = $"{TargetMonitor().Device}|{wa.Left},{wa.Top},{wa.Right},{wa.Bottom}|{s}";
        Refresh();
    }

    private static Size WinSize(bool vertical) => vertical ? WindowV : WindowH;

    private static PxRect ToPx(Native.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>The panel is laid out at its final size so content never reflows while the shape morphs.</summary>
    private void SizeExpandedLayer()
    {
        var size = _vertical ? ExpandedV : ExpandedH;
        ExpandedLayer.Width = size.Width;
        ExpandedLayer.Height = size.Height;
    }

    private (int x, int y) WindowOrigin(Native.RECT wa, double s)
    {
        var size = WinSize(_vertical);
        var placement = new Placement(_settings.Edge, _settings.Align, _settings.Along, _settings.Inset);
        var (x, y) = PlacementMath.WindowOrigin(placement, ToPx(wa),
            size.Width * U * s, size.Height * U * s, EdgeOffset * U * s, s);
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
        double s = _hwnd == IntPtr.Zero ? 1 : TargetMonitor().Scale;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, (int)Math.Round(Width * s), (int)Math.Round(Height * s), flags);
    }

    public void ResetPosition()
    {
        _settings.Edge = IslandEdge.Top;
        _settings.Align = IslandAlign.Center;
        _settings.Along = 0.5;
        _settings.Inset = 8;
        _settings.Monitor = "";
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
            case nameof(AppSettings.Monitor):
            case nameof(AppSettings.Displays):
                // Jumping between monitors (possibly with different DPI) is done without the glide.
                if (!_dragging) Place(animate: false);
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
        if (_peek.Peek is not null) return IslandMode.Peek;
        if (_minimized) return IslandMode.Minimized;
        if (_vm.HasCompact) return IslandMode.Compact;
        return IslandMode.Idle;
    }

    private bool _refreshQueued;

    /// <summary>Refresh after pending binding updates and layout have run (one coalesced refresh per burst).</summary>
    private void RefreshSoon()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() => { _refreshQueued = false; Refresh(); }, DispatcherPriority.Loaded);
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

    // While the panel is open, holding Alt reveals the hidden quit button (the island never has keyboard focus,
    // so the key state is polled).
    private readonly DispatcherTimer _altPoll = new() { Interval = TimeSpan.FromMilliseconds(80) };

    private void PollAlt(object? sender, EventArgs e)
    {
        if (_snapshotMode) return; // snapshots set AltHeld themselves
        bool alt = Native.IsAltDown && HitPad.IsMouseOver;
        if (alt != _vm.AltHeld) _vm.AltHeld = alt;
    }

    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        _altPoll.Tick -= PollAlt;
        if (expanded) { _altPoll.Tick += PollAlt; _altPoll.Start(); }
        else { _altPoll.Stop(); _vm.AltHeld = false; }
        if (expanded) _peekTimer.Stop();
        else if (_peek.Peek is not null) _peekTimer.Start();
        if (!expanded && _peek.Peek is null && _peekQueue.Count > 0)
        {
            var queued = _peekQueue.Dequeue();
            Dispatcher.BeginInvoke(async () => { await Task.Delay(300); ShowPeek(queued); });
        }
        Refresh();
    }

    private void ExpandedLayerDevicesReset() { }

    public void Toggle() { if (!ShowsQuestion) SetExpanded(!_expanded); }

    private bool ShowsQuestion => _peek.Peek?.HasActions == true;

    private void OnHoverEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _lastActivity = DateTime.Now;
        if (_minimized) { _minimized = false; Refresh(); }
        // While a question (buttons) is shown, hovering must not open the panel - the buttons must stay reachable.
        if (_settings.ExpandOnHover && !_expanded && !_dragging && !ShowsQuestion && Mouse.LeftButton != MouseButtonState.Pressed)
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
        if (_expanded)
        {
            // Questions must not get lost while the panel is open: show them once it closes.
            if (item.HasActions && _peekQueue.Count < 4) _peekQueue.Enqueue(item);
            return;
        }
        if (_peek.Peek is null)
        {
            _peek.Peek = item;
            _peekTimer.Interval = TimeSpan.FromSeconds(item.Seconds);
            _peekTimer.Start();
            _minimized = false;
            _lastActivity = DateTime.Now;
            Refresh();
        }
        else
        {
            if (item.Priority > _peek.Peek.Priority) { _peekQueue.Clear(); _peek.Peek = null; ShowPeek(item); return; }
            if (_peekQueue.Count < 4) _peekQueue.Enqueue(item);
        }
    }

    private void NextPeek()
    {
        _peekTimer.Stop();
        if (HitPad.IsMouseOver && _peek.Peek is not null)
        {
            // Keep it while the user is looking at / interacting with it.
            _peekTimer.Interval = TimeSpan.FromSeconds(1);
            _peekTimer.Start();
            return;
        }
        _peek.Peek = null;
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

    /// <summary>Closes the current peek and continues with the next queued one.</summary>
    public void DismissPeek()
    {
        _peekTimer.Stop();
        _peek.Peek = null;
        if (_peekQueue.Count > 0)
        {
            var next = _peekQueue.Dequeue();
            Dispatcher.BeginInvoke(async () => { await Task.Delay(260); ShowPeek(next); });
        }
        Refresh();
    }

    private void OnPeekClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || _peek.Peek is null) return;
        if (_peek.Peek.HasActions) return; // questions are answered with their buttons only
        var action = _peek.Peek.OnClick;
        _peek.Peek = null;
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
        // Single mode: the island may travel to any monitor (the one under the cursor).
        // Mirror mode: each island stays on its own monitor.
        var wa = CanChangeMonitor ? Monitors.FromPoint(c.X, c.Y).Work : Target().work;
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
    private bool CanChangeMonitor => _mirrorDevice is null && _settings.Displays == DisplayMode.Single;

    /// <summary>DPI scale the window currently renders at (changes when it crosses monitors).</summary>
    private double WindowScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private Native.RECT ShapeScreenRect(int wx, int wy)
    {
        double s = WindowScale;
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
        var r = ShapeScreenRect((int)_x.Value, (int)_y.Value);
        var oldShapePos = (r.Left, r.Top);
        var monitor = CanChangeMonitor ? Monitors.FromPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2) : TargetMonitor();
        var wa = monitor.Work;
        double s = monitor.Scale;
        bool monitorChanged = CanChangeMonitor && !string.Equals(monitor.Device, TargetMonitor().Device, StringComparison.OrdinalIgnoreCase);

        var p = PlacementMath.FromDrop(new PxRect(r.Left, r.Top, r.Right, r.Bottom), ToPx(wa), s, _settings.MagneticSnap);
        var edge = p.Edge;

        bool orientationChanges = p.IsVertical != _vertical;
        _settings.PropertyChanged -= OnSettingChanged;
        _settings.Edge = p.Edge;
        _settings.Align = p.Align;
        _settings.Along = p.Along;
        _settings.Inset = p.Inset;
        if (CanChangeMonitor) _settings.Monitor = monitor.IsPrimary ? "" : monitor.Device;
        _settings.PropertyChanged += OnSettingChanged;
        _settings.SaveSoon();
        if (monitorChanged)
        {
            // Different monitor (maybe different DPI): place directly instead of gliding across the seam.
            Place(animate: false);
            return;
        }

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
        (tx, ty) = ClampToMonitor(tx, ty, monitor.Bounds, s);
        UpdateLayout();
        // Start position that keeps the shape where the user let go.
        var offset = Shape.TransformToAncestor(this).Transform(new Point(0, 0));
        int sx = (int)(oldShapePos.Left - offset.X * s), sy = (int)(oldShapePos.Top - offset.Y * s);
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
            _peek.Peek = null;
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
            var name = IsOwnWindow(fg) ? null : Native.ProcessName(fg);
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
        if (fg == IntPtr.Zero || fg == _hwnd || IsOwnWindow(fg)) return false;
        string cls = Native.ClassName(fg);
        if (cls is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;
        // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4)
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4 && OnMyMonitor(fg)) return true;
        // Maximized windows also cover the monitor but keep their caption; real fullscreen windows don't.
        long style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
        if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var (_, bounds, _) = Target();
        return r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
    }

    /// <summary>Fullscreen apps only hide the island on the monitor they actually cover.</summary>
    private bool OnMyMonitor(IntPtr hwnd)
    {
        var mon = Monitors.Describe(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
        return string.Equals(mon.Device, TargetMonitor().Device, StringComparison.OrdinalIgnoreCase);
    }

    // ================= Design snapshots =================

    /// <summary>Forces a state for screenshot-based design review (DynamicBay.exe --snapshot).</summary>
    private bool _snapshotMode;

    public void SetPeekForSnapshot(PeekItem item) => _peek.Peek = item;

    public void ForceState(IslandMode mode, bool vertical = false)
    {
        _snapshotMode = true;
        if (_suppressed) { _suppressed = false; FadeIn(); }
        _expanded = mode == IslandMode.Expanded;
        _dropActive = mode == IslandMode.Drop;
        _minimized = mode == IslandMode.Minimized;
        if (mode != IslandMode.Peek) _peek.Peek = null;
        _peekTimer.Stop();
        Refresh();
    }
}
