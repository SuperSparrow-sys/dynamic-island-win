using System.Diagnostics;
using System.Windows.Threading;

namespace DynamicBay.Core;

/// <summary>
/// Logs when the UI thread does not respond for more than 200 ms ("UI stall 340 ms"), so stutter during start-up,
/// dragging or opening a page shows up in the log next to what happened at that moment. Costs one tiny dispatcher
/// call every 250 ms.
/// </summary>
public static class UiStallMonitor
{
    public static void Start(Dispatcher ui)
    {
        var thread = new Thread(() =>
        {
            var sw = new Stopwatch();
            while (!ui.HasShutdownStarted)
            {
                Thread.Sleep(250);
                sw.Restart();
                try { ui.Invoke(() => { }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(30)); }
                catch { return; }
                if (sw.ElapsedMilliseconds > 200) Log.Info($"UI stall {sw.ElapsedMilliseconds} ms");
            }
        }) { IsBackground = true, Name = "UiStallMonitor", Priority = ThreadPriority.BelowNormal };
        thread.Start();
        TraceSlowOperations(ui, DevMode.On && int.TryParse(Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_SLOWOPS"), out int ms) ? ms : 150);
    }

    private static readonly System.Reflection.FieldInfo? MethodField =
        typeof(DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? ArgsField =
        typeof(DispatcherOperation).GetField("_args", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// Names every dispatcher operation that held the UI thread longer than <paramref name="thresholdMs"/>
    /// ("Slow UI operation 640 ms: Background LinkHub.Tick"), so a stall in the log comes with its cause. Costs a
    /// timestamp per operation. Test instances: DYNAMICBAY_DEV_SLOWOPS=&lt;ms&gt; for a lower threshold.
    /// </summary>
    private static void TraceSlowOperations(Dispatcher ui, int thresholdMs)
    {
        var started = new Dictionary<DispatcherOperation, long>();
        ui.Hooks.OperationStarted += (_, e) => started[e.Operation] = Stopwatch.GetTimestamp();
        ui.Hooks.OperationCompleted += (_, e) => Done(e.Operation);
        ui.Hooks.OperationAborted += (_, e) => Done(e.Operation);
        void Done(DispatcherOperation op)
        {
            if (!started.Remove(op, out long t0)) return;
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (ms < thresholdMs) return;
            Log.Info($"Slow UI operation {ms:0} ms: {op.Priority} {Describe(op)}");
        }
    }

    /// <summary>What an operation runs: the method, or for an awaited continuation the async method it continues.</summary>
    internal static string Describe(DispatcherOperation op)
    {
        try
        {
            if (MethodField?.GetValue(op) is not Delegate d) return "?";
            var arg = ArgsField?.GetValue(op);
            // await on the UI thread: the delegate is a framework helper, the state is the continued async method
            if (arg is Delegate c && c.Target is { } box && box.GetType().IsGenericType)
                return "await in " + AsyncMethodName(box.GetType());
            if (arg is Delegate c2) return Name(c2);
            // A DispatcherTimer: name its Tick handlers
            if (arg is DispatcherTimer t && TickField?.GetValue(t) is Delegate tick)
                return "timer " + string.Join(", ", tick.GetInvocationList().Select(Name));
            if (arg is { } state && state.GetType().IsGenericType && state.GetType().Name.Contains("StateMachineBox", StringComparison.Ordinal))
                return "await in " + AsyncMethodName(state.GetType());
            return Name(d);
        }
        catch { return "?"; }
    }

    private static readonly System.Reflection.FieldInfo? TickField =
        typeof(DispatcherTimer).GetField("Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static string Name(Delegate d) => $"{d.Method.DeclaringType?.FullName}.{d.Method.Name}";

    /// <summary>"DynamicBay.App+&lt;OnStartup&gt;d__30" out of AsyncStateMachineBox`1[..., that].</summary>
    private static string AsyncMethodName(Type box) =>
        box.GetGenericArguments().LastOrDefault()?.FullName ?? box.FullName ?? "?";
}
