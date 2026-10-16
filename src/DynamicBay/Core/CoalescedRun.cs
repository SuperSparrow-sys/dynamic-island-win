namespace DynamicBay.Core;

/// <summary>
/// One run at a time, for work started by change events: a request while it runs leads to exactly one more run
/// afterwards, so no change is lost and a burst costs two runs, not one per event. Call from one thread (the UI thread).
/// </summary>
public sealed class CoalescedRun
{
    private readonly Func<Task> _run;
    private bool _running, _again;

    public CoalescedRun(Func<Task> run) => _run = run;

    public async Task RequestAsync()
    {
        if (_running) { _again = true; return; }
        _running = true;
        try
        {
            do
            {
                _again = false;
                await _run();
            } while (_again);
        }
        finally { _running = false; }
    }
}
