using System.Collections.Concurrent;

namespace DynamicBay.Core;

/// <summary>
/// One background STA thread for calls that may wait on another app, like reading the clipboard (the app that copied
/// renders the data only when asked) or encoding a large image. The work runs one item after the other; the caller
/// awaits the result and continues on its own thread.
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();

    public StaWorker(string name)
    {
        var thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
