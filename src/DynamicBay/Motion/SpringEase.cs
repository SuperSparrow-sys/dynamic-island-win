using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicBay.Motion;

/// <summary>
/// Easing curve of a damped spring released from rest, normalized to the animation duration.
/// Bounce 0 = critically damped (no overshoot), 0.3 = a gentle Apple-like overshoot.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty BounceProperty = DependencyProperty.Register(
        nameof(Bounce), typeof(double), typeof(SpringEase), new PropertyMetadata(0.15));

    public double Bounce { get => (double)GetValue(BounceProperty); set => SetValue(BounceProperty, value); }

    public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseIn = curve applied as-is

    protected override double EaseInCore(double t) => Evaluate(t, Bounce);

    public static double Evaluate(double t, double bounce)
    {
        if (t >= 1) return 1;
        // The spring settles (to ~0.1%) at t = 1. Time is scaled so response ~ duration.
        double zeta = Math.Clamp(1 - bounce, 0.05, 1);
        double settle = 7.0;               // decay budget across the normalized duration
        double omega = settle / zeta;      // natural frequency so exp(-zeta*omega) ~ e^-7
        double x = t;
        if (zeta >= 0.999)
            return 1 - (1 + omega * x) * Math.Exp(-omega * x);
        double wd = omega * Math.Sqrt(1 - zeta * zeta);
        double envelope = Math.Exp(-zeta * omega * x);
        return 1 - envelope * (Math.Cos(wd * x) + zeta * omega / wd * Math.Sin(wd * x));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
