using System.Windows;
using System.Windows.Controls;

namespace DynamicBay.Controls;

/// <summary>
/// Lays out N equal cells in the grid shape that gives the biggest cells for the available space,
/// so every launcher tile is visible at any widget size (item templates use a Viewbox to scale).
/// </summary>
public sealed class FitGrid : Panel
{
    public static readonly DependencyProperty MaxCellProperty = DependencyProperty.Register(
        nameof(MaxCell), typeof(double), typeof(FitGrid), new FrameworkPropertyMetadata(46.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MaxCell { get => (double)GetValue(MaxCellProperty); set => SetValue(MaxCellProperty, value); }

    private (int cols, int rows, double cell) Fit(Size size, int n)
    {
        if (n == 0) return (0, 0, 0);
        double best = 0;
        int bc = 1, br = n;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (int)Math.Ceiling(n / (double)cols);
            double cell = Math.Min(MaxCell, Math.Min(size.Width / cols, size.Height / rows));
            if (cell > best + 0.01) { best = cell; bc = cols; br = rows; }
        }
        return (bc, br, best);
    }

    protected override Size MeasureOverride(Size available)
    {
        int n = InternalChildren.Count;
        var size = new Size(double.IsInfinity(available.Width) ? MaxCell * n : available.Width,
                            double.IsInfinity(available.Height) ? MaxCell : available.Height);
        var (cols, rows, cell) = Fit(size, n);
        foreach (UIElement c in InternalChildren) c.Measure(new Size(cell, cell));
        return new Size(cols * cell, rows * cell);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int n = InternalChildren.Count;
        var (cols, rows, cell) = Fit(final, n);
        double ox = (final.Width - cols * cell) / 2, oy = (final.Height - rows * cell) / 2;
        for (int i = 0; i < n; i++)
            InternalChildren[i].Arrange(new Rect(ox + i % cols * cell, oy + i / cols * cell, cell, cell));
        return final;
    }
}
