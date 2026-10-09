using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicBay.Controls;

/// <summary>
/// A looping animation that only runs while its element is actually visible:
/// <c>&lt;c:Loop.Storyboard&gt;&lt;Storyboard RepeatBehavior="Forever"&gt;…&lt;/Storyboard&gt;&lt;/c:Loop.Storyboard&gt;</c>.
/// A storyboard started from a Loaded trigger keeps ticking while its layer is hidden, and any running animation makes
/// WPF redraw the whole (transparent, software-composited) island window every frame - a constant CPU load.
/// </summary>
public static class Loop
{
    public static readonly DependencyProperty StoryboardProperty = DependencyProperty.RegisterAttached(
        "Storyboard", typeof(Storyboard), typeof(Loop), new PropertyMetadata(null, OnChanged));

    private static readonly DependencyProperty RunningProperty = DependencyProperty.RegisterAttached(
        "Running", typeof(bool), typeof(Loop), new PropertyMetadata(false));

    public static Storyboard? GetStoryboard(DependencyObject d) => (Storyboard?)d.GetValue(StoryboardProperty);
    public static void SetStoryboard(DependencyObject d, Storyboard? value) => d.SetValue(StoryboardProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.IsVisibleChanged -= OnVisibility;
        el.IsVisibleChanged += OnVisibility;
        el.Unloaded -= OnUnloaded;
        el.Unloaded += OnUnloaded;
        Update(el);
    }

    private static void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) => Update((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement el)
    {
        var sb = GetStoryboard(el);
        if (sb is null) return;
        bool run = el.IsVisible && el.IsLoaded;
        bool running = (bool)el.GetValue(RunningProperty);
        if (run == running) return;
        el.SetValue(RunningProperty, run);
        if (run)
        {
            if (!sb.IsFrozen) Timeline.SetDesiredFrameRate(sb, Motion.FrameRate.Limit > 0 ? Motion.FrameRate.Limit : null);
            sb.Begin(el, true);
        }
        else sb.Stop(el);
    }
}
