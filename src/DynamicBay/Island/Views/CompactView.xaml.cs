using System.Windows;
using System.Windows.Controls;

namespace DynamicBay.Island.Views;

public partial class CompactView : UserControl
{
    public CompactView() => InitializeComponent();

    public void SetVertical(bool vertical)
    {
        Stack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        Stack.Margin = vertical ? new Thickness(0, 14, 0, 14) : new Thickness(14, 0, 14, 0);
        Stack.Spacing = vertical ? 16 : 22;
        var h = vertical ? Visibility.Collapsed : Visibility.Visible;
        var v = vertical ? Visibility.Visible : Visibility.Collapsed;
        MediaH.Visibility = TimerH.Visibility = CalH.Visibility = BatH.Visibility = ClockH.Visibility = ClaudeH.Visibility = h;
        MediaV.Visibility = TimerV.Visibility = CalV.Visibility = BatV.Visibility = ClockV.Visibility = ClaudeV.Visibility = v;
    }
}
