using System.Diagnostics;
using System.Windows.Media;

namespace DynamicBay.Motion;

/// <summary>
/// A physically simulated value, parameterized like SwiftUI springs (response in seconds, damping ratio).
/// Retargeting keeps the current velocity, which is what makes interrupted morphs feel continuous.
/// </summary>
public sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Response;
    public double Damping;

    public Spring(double value, double response = 0.42, double damping = 0.82)
    {
        Value = Target = value;
        Response = response;
        Damping = damping;
    }

    public bool IsSettled => Math.Abs(Target - Value) < 0.01 && Math.Abs(Velocity) < 0.05;

    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    public void Step(double dt)
    {
        double k = Math.Pow(2 * Math.PI / Response, 2);
        double c = 4 * Math.PI * Damping / Response;
        double a = -k * (Value - Target) - c * Velocity;
        Velocity += a * dt;
        Value += Velocity * dt;
        if (IsSettled) { Value = Target; Velocity = 0; }
    }
}

/// <summary>Drives a set of springs from the WPF render loop and stops itself when everything settles.</summary>
public sealed class SpringGroup
{
    private const double FixedStep = 1.0 / 480.0;
    private readonly List<Spring> _springs = new();
    private readonly Stopwatch _clock = new();
    private double _last;
    private double _accumulator;
    private bool _running;

    public event Action? Updated;

    public Spring Add(Spring s)
    {
        _springs.Add(s);
        return s;
    }

    public void Kick()
    {
        if (_running) return;
        _running = true;
        _clock.Restart();
        _last = 0;
        _accumulator = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _last, 0.05);
        _last = now;
        _accumulator += dt;
        while (_accumulator >= FixedStep)
        {
            foreach (var s in _springs) s.Step(FixedStep);
            _accumulator -= FixedStep;
        }
        Updated?.Invoke();
        if (_springs.TrueForAll(s => s.IsSettled))
        {
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
        }
    }
}
