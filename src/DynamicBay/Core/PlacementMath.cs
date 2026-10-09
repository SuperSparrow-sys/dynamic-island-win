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
    public const double DefaultInsetDip = 8;

    public const double OrientationHysteresisDip = 56;

    /// <summary>
    /// While dragging: should the island already take its vertical (side edge) shape? Decided by the cursor - the side
    /// edges win when the cursor is closer to them than to top/bottom. A margin keeps the shape from flipping back
    /// and forth near the diagonal.
    /// </summary>
    public static bool LiveVertical(double x, double y, PxRect work, bool currentlyVertical, double scale)
    {
        double side = Math.Min(x - work.Left, work.Right - x);
        double cap = Math.Min(y - work.Top, work.Bottom - y);
        double margin = OrientationHysteresisDip * scale;
        return currentlyVertical ? side < cap + margin : side + margin < cap;
    }

    /// <summary>
    /// Derives edge, alignment and position from where the user released the island.
    /// Magnetic: only twelve fixed spots exist (start, middle and end of each edge, at the default distance);
    /// the island goes to the one nearest to where it was dropped. Otherwise it stays exactly where it was let go.
    /// </summary>
    public static Placement FromDrop(PxRect shape, PxRect work, double scale, bool magnetic, double spotInsetDip = DefaultInsetDip)
    {
        if (magnetic) return NearestSpot(shape, work, scale, spotInsetDip);
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

        IslandAlign align;
        double anchor;
        if (center - start < length * CornerZone) { align = IslandAlign.Start; anchor = lo; }
        else if (start + length - center < length * CornerZone) { align = IslandAlign.End; anchor = hi; }
        else { align = IslandAlign.Center; anchor = center; }

        double inset = Math.Max(0, min) / scale;
        double along = Math.Clamp((anchor - start) / length, 0, 1);
        return new Placement(edge, align, along, inset);
    }

    /// <summary>The fixed spot whose docked island centre is closest to the centre of the dropped shape.</summary>
    private static Placement NearestSpot(PxRect shape, PxRect work, double scale, double insetDip)
    {
        double inset = insetDip * scale;
        // The island is wide on the top/bottom edges and tall on the side edges.
        double wide = Math.Max(shape.Width, shape.Height), thin = Math.Min(shape.Width, shape.Height);
        Placement best = default;
        double bestDist = double.MaxValue;
        foreach (var edge in new[] { IslandEdge.Top, IslandEdge.Bottom, IslandEdge.Left, IslandEdge.Right })
        {
            bool horizontal = edge is IslandEdge.Top or IslandEdge.Bottom;
            double start = horizontal ? work.Left : work.Top, length = horizontal ? work.Width : work.Height;
            double cross = edge switch
            {
                IslandEdge.Top => work.Top + inset + thin / 2,
                IslandEdge.Bottom => work.Bottom - inset - thin / 2,
                IslandEdge.Left => work.Left + inset + thin / 2,
                _ => work.Right - inset - thin / 2,
            };
            foreach (var align in new[] { IslandAlign.Start, IslandAlign.Center, IslandAlign.End })
            {
                double anchor = align switch
                {
                    IslandAlign.Start => start + inset,
                    IslandAlign.End => start + length - inset,
                    _ => start + length / 2,
                };
                double center = align switch
                {
                    IslandAlign.Start => anchor + wide / 2,
                    IslandAlign.End => anchor - wide / 2,
                    _ => anchor,
                };
                double cx = horizontal ? center : cross, cy = horizontal ? cross : center;
                double d = (cx - shape.CenterX) * (cx - shape.CenterX) + (cy - shape.CenterY) * (cy - shape.CenterY);
                if (d < bestDist) { bestDist = d; best = new Placement(edge, align, (anchor - start) / length, insetDip); }
            }
        }
        return best;
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
