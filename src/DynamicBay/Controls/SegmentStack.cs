using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicBay.Controls;

/// <summary>
/// Lays out the visible live-activity segments side by side (or stacked when the island is vertical)
/// with hairline separators between them.
/// </summary>
public sealed class SegmentStack : Panel
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(SegmentStack),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(SegmentStack),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private static readonly Brush Separator = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private bool Horizontal => Orientation == Orientation.Horizontal;

    protected override Size MeasureOverride(Size available)
    {
        double main = 0, cross = 0;
        int visible = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (c.Visibility == Visibility.Collapsed) continue;
            var s = c.DesiredSize;
            main += Horizontal ? s.Width : s.Height;
            cross = Math.Max(cross, Horizontal ? s.Height : s.Width);
            visible++;
        }
        if (visible > 1) main += Spacing * (visible - 1);
        return Horizontal ? new Size(main, cross) : new Size(cross, main);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double pos = 0;
        double cross = Horizontal ? final.Height : final.Width;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            var s = c.DesiredSize;
            if (Horizontal)
            {
                c.Arrange(new Rect(pos, (cross - s.Height) / 2, s.Width, s.Height));
                pos += s.Width + Spacing;
            }
            else
            {
                c.Arrange(new Rect((cross - s.Width) / 2, pos, s.Width, s.Height));
                pos += s.Height + Spacing;
            }
        }
        InvalidateVisual();
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        UIElement? prev = null;
        double pos = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            var s = c.DesiredSize;
            if (prev is not null)
            {
                double mid = pos - Spacing / 2;
                if (Horizontal) dc.DrawRectangle(Separator, null, new Rect(mid - 0.5, ActualHeight * 0.22, 1, ActualHeight * 0.56));
                else dc.DrawRectangle(Separator, null, new Rect(ActualWidth * 0.22, mid - 0.5, ActualWidth * 0.56, 1));
            }
            pos += (Horizontal ? s.Width : s.Height) + Spacing;
            prev = c;
        }
    }
}
