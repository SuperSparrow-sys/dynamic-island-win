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
    }
}
