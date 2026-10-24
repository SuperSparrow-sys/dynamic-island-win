namespace DynamicBay.Core;

/// <summary>
/// A state that changes only after the raw signal held long enough: on after <c>on</c>, off after <c>off</c>. For the
/// camera, which some apps open for a second every few seconds (LogiTune): the panel must not flicker along.
/// </summary>
public sealed class Settle(TimeSpan on, TimeSpan off)
{
    private bool _raw;
    private DateTime _since = DateTime.MinValue;

    public bool State { get; private set; }

    /// <summary>The raw signal differs from the state and waits for its time.</summary>
    public bool Pending => _raw != State;

    /// <summary>The newest raw value (or the same again, to let time pass); returns the settled state.</summary>
    public bool Update(bool raw, DateTime now)
    {
        if (raw != _raw) { _raw = raw; _since = now; }
        if (_raw != State && now - _since >= (_raw ? on : off)) State = _raw;
        return State;
    }
}
