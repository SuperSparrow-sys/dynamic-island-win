using DynamicBay.Core;

namespace DynamicBay.Tests;

public class PlacementMathTests
{
    // A 1920x1140 work area at 125 % scaling (like the primary display of the dev machine).
    private static readonly PxRect Work = new(0, 0, 1920, 1140);
    private const double Scale = 1.25;

    private static PxRect Shape(double cx, double cy, double w = 260, double h = 15) => new(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);

    [Fact]
    public void Dropped_near_top_center_snaps_to_classic_island()
    {
        var p = PlacementMath.FromDrop(Shape(990, 30), Work, Scale, magnetic: true);
        Assert.Equal(IslandEdge.Top, p.Edge);
        Assert.Equal(IslandAlign.Center, p.Align);
        Assert.Equal(0.5, p.Along, 3);
        Assert.Equal(PlacementMath.DefaultInsetDip, p.Inset);
        Assert.False(p.IsVertical);
    }

    [Fact]
    public void Dropped_on_left_edge_becomes_vertical()
    {
        var p = PlacementMath.FromDrop(Shape(25, 560, w: 15, h: 240), Work, Scale, magnetic: true);
        Assert.Equal(IslandEdge.Left, p.Edge);
        Assert.True(p.IsVertical);
        Assert.Equal(IslandAlign.Center, p.Align);
    }

    [Fact]
    public void Dropped_in_top_right_corner_aligns_to_end()
    {
        var p = PlacementMath.FromDrop(Shape(1760, 20), Work, Scale, magnetic: true); // right edge 30 px from the screen edge
        Assert.Equal(IslandEdge.Top, p.Edge);
        Assert.Equal(IslandAlign.End, p.Align);
        // snapped to 8 dip from the right edge
        Assert.Equal((1920 - 8 * Scale) / 1920, p.Along, 3);
    }

    [Fact]
    public void Dropped_near_bottom_docks_above_taskbar()
    {
        var p = PlacementMath.FromDrop(Shape(960, 1120), Work, Scale, magnetic: true);
        Assert.Equal(IslandEdge.Bottom, p.Edge);
        Assert.Equal(PlacementMath.DefaultInsetDip, p.Inset);
    }

    [Fact]
    public void Magnet_takes_the_nearest_spot_even_far_from_the_edge()
    {
        // Dropped in the upper left quarter, well inside the screen: top edge, start.
        var p = PlacementMath.FromDrop(Shape(500, 300), Work, Scale, magnetic: true);
        Assert.Equal(IslandEdge.Top, p.Edge);
        Assert.Equal(IslandAlign.Start, p.Align);
        Assert.Equal(PlacementMath.DefaultInsetDip, p.Inset);
        // Slightly off the middle of the top edge: exactly the middle.
        var mid = PlacementMath.FromDrop(Shape(1100, 200), Work, Scale, magnetic: true);
        Assert.Equal(IslandAlign.Center, mid.Align);
        Assert.Equal(0.5, mid.Along, 3);
    }

    [Fact]
    public void Without_magnet_the_free_position_is_kept()
    {
        var p = PlacementMath.FromDrop(Shape(700, 300), Work, Scale, magnetic: false);
        Assert.Equal(IslandEdge.Top, p.Edge);
        Assert.Equal((300 - 7.5) / Scale, p.Inset, 1);
        Assert.Equal(700.0 / 1920, p.Along, 3);
    }

    [Fact]
    public void Window_origin_centers_window_on_anchor()
    {
        var p = new Placement(IslandEdge.Top, IslandAlign.Center, 0.5, 8);
        var (x, y) = PlacementMath.WindowOrigin(p, Work, windowW: 1000, windowH: 450, edgeOffsetPx: 42.5, scale: Scale);
        Assert.Equal(460, x, 3);
        Assert.Equal(8 * Scale - 42.5, y, 3);
    }

    [Fact]
    public void Window_origin_right_edge_keeps_shape_inside()
    {
        var p = new Placement(IslandEdge.Right, IslandAlign.Center, 0.5, 8);
        var (x, _) = PlacementMath.WindowOrigin(p, Work, windowW: 600, windowH: 700, edgeOffsetPx: 42.5, scale: Scale);
        // shape edge = window right - offset = work right - inset
        Assert.Equal(1920 - 8 * Scale, x + 600 - 42.5, 3);
    }

    [Fact]
    public void Live_orientation_turns_vertical_near_side_edges_only()
    {
        // Work 1920 x 1140: middle of the top edge stays horizontal, close to the left edge turns vertical.
        Assert.False(PlacementMath.LiveVertical(960, 20, Work, currentlyVertical: false, Scale));
        Assert.True(PlacementMath.LiveVertical(20, 520, Work, currentlyVertical: false, Scale));
        Assert.True(PlacementMath.LiveVertical(1900, 520, Work, currentlyVertical: false, Scale));
    }

    [Fact]
    public void Live_orientation_does_not_flicker_near_the_diagonal()
    {
        // Equal distance to the left and the top edge: keep whatever shape the island has right now.
        Assert.False(PlacementMath.LiveVertical(100, 100, Work, currentlyVertical: false, Scale));
        Assert.True(PlacementMath.LiveVertical(100, 100, Work, currentlyVertical: true, Scale));
    }
}
