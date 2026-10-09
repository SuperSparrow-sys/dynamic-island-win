namespace DynamicBay.Core;

/// <summary>Screen rectangle in physical pixels.</summary>
public readonly record struct PxRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;
}

public readonly record struct Placement(IslandEdge Edge, IslandAlign Align, double Along, double Inset)
{
    public bool IsVertical => Edge is IslandEdge.Left or IslandEdge.Right;
}

/// <summary>
/// Pure placement math (no WPF), so it can be unit tested:
/// where a dropped island docks, and where its window goes for a given placement.
/// </summary>
public static class PlacementMath
{
    public const double CornerZone = 0.18;   // fraction of the edge length that counts as "corner"
    public const double SnapDistanceDip = 70;
    public const double SnapInsetDip = 44;
    public const double DefaultInsetDip = 8;

    /// <summary>Derives edge, alignment and position from where the user released the island.</summary>
    public static Placement FromDrop(PxRect shape, PxRect work, double scale, bool magnetic)
    {
        double dTop = shape.Top - work.Top, dBottom = work.Bottom - shape.Bottom;
        double dLeft = shape.Left - work.Left, dRight = work.Right - shape.Right;
        double min = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));
        IslandEdge edge = min == dTop ? IslandEdge.Top
            : min == dBottom ? IslandEdge.Bottom
            : min == dLeft ? IslandEdge.Left
            : IslandEdge.Right;

        bool horizontal = edge is IslandEdge.Top or IslandEdge.Bottom;
        double start = horizontal ? work.Left : work.Top;
        double length = horizontal ? work.Width : work.Height;
        double center = horizontal ? shape.CenterX : shape.CenterY;
        double lo = horizontal ? shape.Left : shape.Top;
        double hi = horizontal ? shape.Right : shape.Bottom;
        double snap = SnapDistanceDip * scale;

        IslandAlign align;
        double anchor;
        if (center - start < length * CornerZone) { align = IslandAlign.Start; anchor = lo; }
        else if (start + length - center < length * CornerZone) { align = IslandAlign.End; anchor = hi; }
        else { align = IslandAlign.Center; anchor = center; }

        if (magnetic)
        {
            double mid = start + length / 2;
            if (align == IslandAlign.Center && Math.Abs(anchor - mid) < snap) anchor = mid;
            if (align == IslandAlign.Start && anchor - start < snap) anchor = start + DefaultInsetDip * scale;
            if (align == IslandAlign.End && start + length - anchor < snap) anchor = start + length - DefaultInsetDip * scale;
        }

        double inset = Math.Max(0, min) / scale;
        if (magnetic && inset < SnapInsetDip) inset = DefaultInsetDip;
        double along = Math.Clamp((anchor - start) / length, 0, 1);
        return new Placement(edge, align, along, inset);
    }

    /// <summary>
    /// Top-left of the island window (px) for a placement. <paramref name="edgeOffsetPx"/> is the distance from the
    /// window border to the shape border on the docked side.
    /// </summary>
    public static (double X, double Y) WindowOrigin(Placement p, PxRect work, double windowW, double windowH, double edgeOffsetPx, double scale)
    {
        double inset = p.Inset * scale;
        if (!p.IsVertical)
        {
            double ax = work.Left + p.Along * work.Width;
            double x = p.Align switch
            {
                IslandAlign.Start => ax - edgeOffsetPx,
                IslandAlign.End => ax - windowW + edgeOffsetPx,
                _ => ax - windowW / 2,
            };
            double y = p.Edge == IslandEdge.Top ? work.Top + inset - edgeOffsetPx : work.Bottom - inset - windowH + edgeOffsetPx;
            return (x, y);
        }
        else
        {
            double ay = work.Top + p.Along * work.Height;
            double y = p.Align switch
            {
                IslandAlign.Start => ay - edgeOffsetPx,
                IslandAlign.End => ay - windowH + edgeOffsetPx,
                _ => ay - windowH / 2,
            };
            double x = p.Edge == IslandEdge.Left ? work.Left + inset - edgeOffsetPx : work.Right - inset - windowW + edgeOffsetPx;
            return (x, y);
        }
    }
}
