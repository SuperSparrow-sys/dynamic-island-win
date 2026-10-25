using System.Windows;
using System.Windows.Media;

namespace DynamicBay.Controls;

/// <summary>The temperature of the next hours as a soft line with a faint fill below (the wide weather widget).</summary>
public sealed class TempCurve : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(TempCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TempCurve),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Points of the line inside a box (2 px room for the stroke above and below); the highest value at the top.</summary>
    public static Point[] Layout(IReadOnlyList<double> values, double width, double height)
    {
        if (values.Count < 2 || width <= 0 || height <= 4) return Array.Empty<Point>();
        double lo = values.Min(), hi = values.Max(), span = Math.Max(hi - lo, 1); // a flat day stays a calm line, not noise
        double mid = (hi + lo) / 2;
        var pts = new Point[values.Count];
        for (int i = 0; i < values.Count; i++)
            pts[i] = new Point(width * i / (values.Count - 1), height / 2 - (values[i] - mid) / span * (height - 4));
        return pts;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pts = Values is { } v ? Layout(v, ActualWidth, ActualHeight) : Array.Empty<Point>();
        if (pts.Length < 2) return;
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var c = line.Open()) Curve(c, pts, false);
        using (var c = fill.Open()) Curve(c, pts, true);
        line.Freeze();
        fill.Freeze();
        var color = Stroke is SolidColorBrush s ? s.Color : Colors.Orange;
        var under = new LinearGradientBrush(Color.FromArgb(70, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        under.Freeze();
        dc.DrawGeometry(under, null, fill);
        var pen = new Pen(Stroke, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>Smooth through every point (Catmull-Rom as Bézier segments); closed along the bottom for the fill.</summary>
    private void Curve(StreamGeometryContext c, Point[] p, bool closed)
    {
        c.BeginFigure(closed ? new Point(p[0].X, ActualHeight) : p[0], closed, closed);
        if (closed) c.LineTo(p[0], false, false);
        for (int i = 0; i < p.Length - 1; i++)
        {
            Point p0 = p[Math.Max(i - 1, 0)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(i + 2, p.Length - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            c.BezierTo(c1, c2, p2, true, true);
        }
        if (closed) c.LineTo(new Point(p[^1].X, ActualHeight), false, false);
    }
}
