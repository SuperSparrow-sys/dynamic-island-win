using DynamicBay.Core;

namespace DynamicBay.Tests;

public class TaskbarMathTests
{
    private static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    // The owner's desk: laptop (primary) left, 4K monitor at 150 % right, both with a taskbar at the bottom.
    private static readonly Native.RECT Laptop = R(0, 0, 1920, 1200), Monitor = R(1920, 0, 5760, 2160);
    private static readonly (string, Native.RECT)[] Displays = { (@"\\.\DISPLAY1", Laptop), (@"\\.\DISPLAY2", Monitor) };

    [Fact]
    public void Each_taskbar_belongs_to_the_display_holding_it()
    {
        Assert.Equal(@"\\.\DISPLAY1", TaskbarMath.DisplayOf(R(0, 1140, 1920, 1200), Displays));
        Assert.Equal(@"\\.\DISPLAY2", TaskbarMath.DisplayOf(R(1920, 2088, 5760, 2160), Displays));
        Assert.Null(TaskbarMath.DisplayOf(R(9000, 0, 9100, 50), Displays));
    }

    [Fact]
    public void Only_a_taskbar_at_the_bottom_is_hidden()
    {
        Assert.True(TaskbarMath.AtBottom(R(1920, 2088, 5760, 2160), Monitor));
        Assert.False(TaskbarMath.AtBottom(R(1920, 2158, 5760, 2230), Monitor)); // Windows' auto-hide: pushed off the edge
        Assert.False(TaskbarMath.AtBottom(R(1920, 0, 5760, 72), Monitor));      // moved to the top
    }

    [Theory]
    [InlineData(3000, 2159, true)]
    [InlineData(3000, 2158, true)]
    [InlineData(3000, 2100, false)]
    [InlineData(1000, 1199, false)] // the laptop's edge is not this display's
    public void Bottom_edge_of_the_display(int x, int y, bool expected) =>
        Assert.Equal(expected, TaskbarMath.AtBottomEdge(x, y, Monitor));

    [Theory]
    [InlineData(false, true, false, false, 0, true)]     // mouse at the edge: comes
    [InlineData(false, true, true, false, 0, false)]     // over a film or game: stays away
    [InlineData(false, false, false, false, 0, false)]
    [InlineData(true, false, false, true, 5000, true)]   // mouse on it or Start open: stays
    [InlineData(true, false, false, false, 200, true)]   // just left: stays a moment
    [InlineData(true, false, false, false, 400, false)]  // left: goes
    public void Shown_like_auto_hide(bool shown, bool atEdge, bool fullscreen, bool held, long sinceHeld, bool expected) =>
        Assert.Equal(expected, TaskbarMath.NextShown(shown, atEdge, fullscreen, held, sinceHeld));

    [Theory]
    [InlineData("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost", true, true)] // seen: Start passes as an app window
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost", false, true)]
    [InlineData("TaskListThumbnailWnd", "explorer", false, true)]
    [InlineData("#32768", "Teams", false, true)]                 // a tray icon's menu
    [InlineData("CabinetWClass", "explorer", true, false)]       // File Explorer is an app
    [InlineData("Shell_SecondaryTrayWnd", "explorer", false, false)]
    [InlineData("WorkerW", "explorer", false, false)]
    [InlineData("Chrome_WidgetWin_1", "chrome", false, false)]
    public void Shell_popups_hold_the_taskbar(string cls, string process, bool appWindow, bool expected) =>
        Assert.Equal(expected, TaskbarMath.IsShellPopup(cls, process, appWindow));

    [Fact]
    public void Maximized_windows_follow_the_work_area()
    {
        // Maximized at 150 %: 11 px frame beyond the work area on every side.
        var maxed = R(1909, 25, 5771, 2099);
        Assert.Equal(2171 - 25, TaskbarMath.MaximizedHeight(maxed, Monitor, from: 2088, to: 2160));
        Assert.Equal(2099 - 25, TaskbarMath.MaximizedHeight(R(1909, 25, 5771, 2171), Monitor, from: 2160, to: 2088));
        Assert.Null(TaskbarMath.MaximizedHeight(R(-9, -9, 1929, 1149), Monitor, 2088, 2160)); // on the laptop
        Assert.Null(TaskbarMath.MaximizedHeight(R(1920, 0, 5760, 2160), Monitor, 2088, 2160)); // fullscreen, not maximized to the work area
    }
}
