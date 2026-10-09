using System.Windows;
using System.Windows.Media;

namespace DynamicBay.Controls;

/// <summary>Animated equalizer bars for the music live activity. Bars ease toward random targets for an organic feel.</summary>
public sealed class Waveform : FrameworkElement
{
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(Waveform), new PropertyMetadata(false, (d, _) => ((Waveform)d).UpdateRunning()));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(Waveform),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(int), typeof(Waveform), new PropertyMetadata(4));

    public static readonly DependencyProperty VerticalProperty = DependencyProperty.Register(
        nameof(Vertical), typeof(bool), typeof(Waveform),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public int Bars { get => (int)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public bool Vertical { get => (bool)GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }

    private double[] _level = Array.Empty<double>();
    private double[] _target = Array.Empty<double>();
    private double[] _vel = Array.Empty<double>();
    private readonly Random _rng = new();
    private TimeSpan _last;
    private double _retarget;
    private bool _hooked;
    private double _drawn = -1;

    public Waveform()
    {
        Width = 16;
        Height = 12;
        Loaded += (_, _) => UpdateRunning();
        Unloaded += (_, _) => Unhook();
        IsVisibleChanged += (_, _) => UpdateRunning();
    }

    private void Ensure()
    {
        if (_level.Length == Bars) return;
        _level = Enumerable.Repeat(0.2, Bars).ToArray();
        _target = Enumerable.Repeat(0.2, Bars).ToArray();
        _vel = new double[Bars];
    }

    private void UpdateRunning()
    {
        Ensure();
        // Keep animating briefly after pause so bars settle down smoothly.
        if (IsVisible && IsLoaded) Hook(); else Unhook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (!Motion.FrameRate.Due(ref _drawn, t.TotalSeconds)) return;
        if (_last == TimeSpan.Zero) { _last = t; return; }
        double dt = Math.Min((t - _last).TotalSeconds, 0.05);
        if (dt <= 0) return;
        _last = t;

        _retarget -= dt;
        if (_retarget <= 0)
        {
            _retarget = 0.11 + _rng.NextDouble() * 0.08;
            for (int i = 0; i < Bars; i++)
                _target[i] = IsPlaying ? 0.25 + _rng.NextDouble() * 0.75 : 0.18;
        }
        bool moving = false;
        for (int i = 0; i < Bars; i++)
        {
            // Critically damped follow.
            double k = 260, c = 2 * Math.Sqrt(k);
            double a = -k * (_level[i] - _target[i]) - c * _vel[i];
            _vel[i] += a * dt;
            _level[i] += _vel[i] * dt;
            if (Math.Abs(_vel[i]) > 0.001 || Math.Abs(_level[i] - _target[i]) > 0.001) moving = true;
        }
        InvalidateVisual();
        if (!IsPlaying && !moving) Unhook();
    }

    protected override void OnRender(DrawingContext dc)
    {
        Ensure();
        int n = Bars;
        double w = ActualWidth, h = ActualHeight;
        if (Vertical) (w, h) = (h, w);
        double gap = Math.Max(1.5, w / (n * 3.2));
        double barW = (w - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            double lh = Math.Max(barW, h * Math.Clamp(_level[i], 0.1, 1));
            double x = i * (barW + gap);
            double y = (h - lh) / 2;
            Rect r = Vertical ? new Rect(y, x, lh, barW) : new Rect(x, y, barW, lh);
            dc.DrawRoundedRectangle(BarBrush, null, r, barW / 2, barW / 2);
        }
    }
}
