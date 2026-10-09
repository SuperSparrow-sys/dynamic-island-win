using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicBay.Core;

/// <summary>
/// Keeps the resident memory of the always-running island small: after start-up (icons, app list, services) and
/// whenever the island has been idle for a while, the GC compacts and the process hands untouched pages back to
/// Windows. Pages that are needed again come back from the standby list in microseconds.
/// </summary>
public static class MemoryTrim
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(90) };
    private static Func<bool>? _isIdle;
    private const long TrimAboveBytes = 120L * 1024 * 1024;

    public static void Start(Func<bool> isIdle)
    {
        _isIdle = isIdle;
        Timer.Tick += (_, _) => LogCpu();
        Timer.Start();
    }

    /// <summary>Compacts the managed heap and releases the working set (call after a burst of work, e.g. start-up).</summary>
    public static void TrimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var p = Process.GetCurrentProcess();
            long before = p.WorkingSet64;
            SetProcessWorkingSetSize(p.Handle, -1, -1);
            p.Refresh();
            Log.Info($"Memory trimmed: {before / 1048576} MB -> {p.WorkingSet64 / 1048576} MB");
        }
        catch (Exception ex) { Log.Error("MemoryTrim", ex); }
    }

    private static TimeSpan _lastCpu;
    private static DateTime _lastCpuAt;

    /// <summary>Average CPU use since the last tick, written to the log, so background load is visible on the user PC.</summary>
    private static void LogCpu()
    {
        using var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        if (_lastCpuAt != default)
        {
            double pct = (p.TotalProcessorTime - _lastCpu).TotalSeconds / (now - _lastCpuAt).TotalSeconds * 100;
            Log.Info($"CPU {pct:0.0} % of one core, working set {p.WorkingSet64 / 1048576} MB");
        }
        _lastCpu = p.TotalProcessorTime;
        _lastCpuAt = now;
    }

    private static void TrimIfLarge()
    {
        using var p = Process.GetCurrentProcess();
        if (p.WorkingSet64 > TrimAboveBytes) TrimNow();
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
