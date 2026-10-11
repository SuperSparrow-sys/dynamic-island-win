namespace DynamicBay.Core;

public enum GestureEnd { None, Click, Drag }

/// <summary>
/// A press that may turn into a drag (moving the island, scrubbing the timeline, the volume slider). It ends exactly
/// once, however it ends: button released, mouse capture taken away (Alt+Tab, a dialog) or the window hiding. Every
/// path calls <see cref="End"/>; a second call returns <see cref="GestureEnd.None"/>, so the release that drops the
/// capture and the LostMouseCapture it raises never finish the same drag twice. A gesture left "pressed" after an
/// interrupted end kept following the mouse without a button and blocked hover and collapse.
/// </summary>
public sealed class PointerGesture
{
    public bool IsPressed { get; private set; }
    public bool IsDragging { get; private set; }

    public void Press()
    {
        IsPressed = true;
        IsDragging = false;
    }

    /// <summary>The press moved far enough: it is a drag now. False if nothing is pressed (a stale move).</summary>
    public bool BeginDrag()
    {
        if (!IsPressed) return false;
        IsDragging = true;
        return true;
    }

    /// <summary>Ends the gesture and says what it was, so the caller finishes it (once).</summary>
    public GestureEnd End()
    {
        var end = IsDragging ? GestureEnd.Drag : IsPressed ? GestureEnd.Click : GestureEnd.None;
        IsPressed = false;
        IsDragging = false;
        return end;
    }
}
