using System.Diagnostics;
using System.Windows.Threading;

namespace DynamicBay.Core;

/// <summary>
/// Writes CPU use and working set to the log every 90 s, so background load is visible on the user's PC.
/// (Memory is deliberately not trimmed: pages handed back to Windows had to be read in again on the next animation.)
/// </summary>
public static class MemoryTrim
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(90) };
    private static TimeSpan _lastCpu;
    private static DateTime _lastCpuAt;

    public static void Start()
    {
        Timer.Tick += (_, _) => LogCpu();
        Timer.Start();
    }

    /// <summary>Average CPU use since the last tick.</summary>
    private static void LogCpu()
    {
        using var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        if (_lastCpuAt != default)
        {
            double pct = (p.TotalProcessorTime - _lastCpu).TotalSeconds / (now - _lastCpuAt).TotalSeconds * 100;
            Log.Info($"CPU {pct:0.0} % of one core, working set {p.WorkingSet64 / 1048576} MB, managed {GC.GetTotalMemory(false) / 1048576} MB");
        }
        _lastCpu = p.TotalProcessorTime;
        _lastCpuAt = now;
    }
}
