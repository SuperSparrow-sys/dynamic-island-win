using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicBay.Controls;

/// <summary>
/// An analog clock face like Apple's Clock icon: white dial, grey hour marks, black rounded hands and an orange
/// second hand with a ring in the middle. Ticks once a second while visible.
/// </summary>
public sealed class AnalogClock : FrameworkElement
{
    private static readonly Brush Dial = Frozen(Brushes.White);
    // Proportions taken from the iPhone Clock icon: thick grey marks, thick black hands with a thin neck at the centre.
    private static readonly Pen Mark = FrozenPen(Color.FromRgb(0x8E, 0x8E, 0x93), 0.062);
    private static readonly Pen Hand = FrozenPen(Color.FromRgb(0x1C, 0x1C, 0x1E), 0.085);
    private static readonly Pen Neck = FrozenPen(Color.FromRgb(0x1C, 0x1C, 0x1E), 0.035);
    private static readonly Pen Second = FrozenPen(Color.FromRgb(0xF0, 0x9A, 0x37), 0.016);
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public AnalogClock()
    {
        _tick.Tick += (_, _) => { AlignTick(); InvalidateVisual(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) { AlignTick(); _tick.Start(); InvalidateVisual(); } else _tick.Stop(); };
    }

    /// <summary>Fire just after each full second, so the second hand jumps in time with the real clock.</summary>
    private void AlignTick() => _tick.Interval = TimeSpan.FromMilliseconds(1000 - DateTime.Now.Millisecond + 5);

    private static Brush Frozen(Brush b) { var c = b.Clone(); c.Freeze(); return c; }

    /// <summary>Pens in units of the radius (scaled when drawn): round caps like the iOS hands.</summary>
    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(new SolidColorBrush(c), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override Size MeasureOverride(Size available)
    {
        double s = Math.Min(double.IsInfinity(available.Width) ? 120 : available.Width, double.IsInfinity(available.Height) ? 120 : available.Height);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double r = size / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(Dial, null, c, r, r);

        // Everything else is drawn in a unit circle (radius 1) and scaled.
        dc.PushTransform(new MatrixTransform(r, 0, 0, r, c.X, c.Y));
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6;
            dc.DrawLine(Mark, new Point(Math.Sin(a) * 0.68, -Math.Cos(a) * 0.68), new Point(Math.Sin(a) * 0.84, -Math.Cos(a) * 0.84));
        }
        var now = DateTime.Now;
        double sec = now.Second, min = now.Minute + sec / 60, hour = now.Hour % 12 + min / 60;
        DrawThickHand(dc, hour / 12, 0.5);
        DrawThickHand(dc, min / 60, 0.8);
        dc.DrawEllipse(Ink, null, new Point(0, 0), 0.06, 0.06);
        DrawHand(dc, Second, sec / 60, 0.14, 0.88);
        dc.DrawEllipse(Brushes.White, FrozenPen(Color.FromRgb(0xF0, 0x9A, 0x37), 0.026), new Point(0, 0), 0.032, 0.032);
        dc.Pop();
    }

    /// <summary>Hour/minute hand: thin neck from the centre, then the thick rounded blade.</summary>
    private static void DrawThickHand(DrawingContext dc, double turn, double length)
    {
        double a = turn * 2 * Math.PI;
        double x = Math.Sin(a), y = -Math.Cos(a);
        dc.DrawLine(Neck, new Point(0, 0), new Point(x * 0.16, y * 0.16));
        dc.DrawLine(Hand, new Point(x * 0.16, y * 0.16), new Point(x * length, y * length));
    }

    /// <summary>A hand at a fraction of a full turn, with a short tail behind the centre.</summary>
    private static void DrawHand(DrawingContext dc, Pen pen, double turn, double tail, double length)
    {
        double a = turn * 2 * Math.PI;
        double x = Math.Sin(a), y = -Math.Cos(a);
        dc.DrawLine(pen, new Point(-x * tail, -y * tail), new Point(x * length, y * length));
    }
}
