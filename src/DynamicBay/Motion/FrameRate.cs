namespace DynamicBay.Motion;

/// <summary>
/// Frame rate of the island's own animations (springs, waveform, loops), set in Darstellung → Bildrate.
/// 0 = every frame the display offers (smoothest); 30 or 60 saves CPU and power.
/// </summary>
public static class FrameRate
{
    public static int Limit { get; set; }

    /// <summary>
    /// Whether to draw this frame. Drawn frames stay on an even grid (every 2nd frame at 30 fps on a 60 Hz display),
    /// so a limit does not make motion stutter unevenly. <paramref name="lastDrawn"/> starts below zero.
    /// </summary>
    public static bool Due(ref double lastDrawn, double now)
    {
        if (Limit <= 0) { lastDrawn = now; return true; }
        double interval = 1.0 / Limit;
        double since = now - lastDrawn;
        if (since < interval * 0.8) return false;
        lastDrawn = since > interval * 2 ? now : lastDrawn + interval;
        return true;
    }
}
