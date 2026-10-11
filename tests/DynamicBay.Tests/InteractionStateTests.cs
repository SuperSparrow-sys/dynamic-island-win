using DynamicBay.Core;

namespace DynamicBay.Tests;

/// <summary>Press, drag and their end on every path (release, lost capture, hiding) - see PointerGesture.</summary>
public class PointerGestureTests
{
    [Fact]
    public void Press_and_release_is_a_click()
    {
        var g = new PointerGesture();
        g.Press();
        Assert.True(g.IsPressed);
        Assert.Equal(GestureEnd.Click, g.End());
        Assert.False(g.IsPressed);
    }

    [Fact]
    public void Drag_ends_once_even_if_release_and_lost_capture_both_arrive()
    {
        var g = new PointerGesture();
        g.Press();
        Assert.True(g.BeginDrag());
        Assert.True(g.IsDragging);
        Assert.Equal(GestureEnd.Drag, g.End());   // button up
        Assert.Equal(GestureEnd.None, g.End());   // the LostMouseCapture that releasing raises
        Assert.False(g.IsDragging);
    }

    [Fact]
    public void Capture_lost_mid_drag_ends_the_drag()
    {
        var g = new PointerGesture();
        g.Press();
        g.BeginDrag();
        Assert.Equal(GestureEnd.Drag, g.End());   // Alt+Tab, the island hiding
        Assert.False(g.IsPressed);
        Assert.False(g.IsDragging);
    }

    [Fact]
    public void A_move_after_an_interrupted_gesture_does_not_start_a_drag()
    {
        var g = new PointerGesture();
        g.Press();
        g.End();
        Assert.False(g.BeginDrag());
        Assert.False(g.IsDragging);
        Assert.Equal(GestureEnd.None, g.End());
    }

    [Fact]
    public void A_new_press_starts_clean()
    {
        var g = new PointerGesture();
        g.Press();
        g.BeginDrag();
        g.Press(); // a press without the end of the last one (it never arrived)
        Assert.False(g.IsDragging);
        Assert.Equal(GestureEnd.Click, g.End());
    }

    [Fact]
    public void End_without_press_is_nothing() => Assert.Equal(GestureEnd.None, new PointerGesture().End());
}
