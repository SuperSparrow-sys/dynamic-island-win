using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicBay.Controls;

/// <summary>
/// A looping animation that only runs while its element is actually visible:
/// <c>&lt;c:Loop.Storyboard&gt;&lt;Storyboard RepeatBehavior="Forever"&gt;…&lt;/Storyboard&gt;&lt;/c:Loop.Storyboard&gt;</c>.
/// A storyboard started from a Loaded trigger keeps ticking while its layer is hidden, and any running animation makes
/// WPF redraw the whole (transparent, software-composited) island window every frame - a constant CPU load.
/// Loops also run at 30 frames per second, which is plenty for a subtle motion.
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
        if (e.NewValue is Storyboard sb && !sb.IsFrozen) Timeline.SetDesiredFrameRate(sb, 30);
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
        if (run) sb.Begin(el, true);
        else sb.Stop(el);
    }
}
