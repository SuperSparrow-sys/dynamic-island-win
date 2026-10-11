using System.Diagnostics;

namespace DynamicBay.Core;

/// <summary>
/// Writes the newest version of a file away from the UI thread. The text is taken on the caller's thread (it reads
/// live objects); the disk write (flush to disk, swap with the backup) ran on the UI thread before and took up to
/// almost a second on a busy disk. Versions that a newer one overtook are skipped, also after a synchronous
/// <see cref="WriteNow"/> (exit), so an older text can never land last.
/// </summary>
public sealed class BackgroundWriter
{
    private readonly Action<string> _write;
    private readonly string _label;
    private readonly object _gate = new();
    private readonly object _file = new();
    private long _version, _written;
    private (long Version, string Text)? _pending;
    private Task _loop = Task.CompletedTask;

    public BackgroundWriter(Action<string> write, string label)
    {
        _write = write;
        _label = label;
    }

    /// <summary>Queues the text; returns at once. Only the newest queued text is written.</summary>
    public void Queue(string text)
    {
        lock (_gate)
        {
            _pending = (++_version, text);
            if (!_loop.IsCompleted) return;
            _loop = Task.Run(Loop);
        }
    }

    /// <summary>Writes now on this thread (exit, import): newer than anything queued before.</summary>
    public void WriteNow(string text)
    {
        long v;
        lock (_gate) { v = ++_version; _pending = null; }
        Write(v, text);
    }

    /// <summary>Waits until queued texts are on disk (tests, exit).</summary>
    public void Drain()
    {
        Task t;
        lock (_gate) t = _loop;
        t.Wait();
    }

    private void Loop()
    {
        while (true)
        {
            (long Version, string Text) next;
            lock (_gate)
            {
                if (_pending is not { } p) return;
                next = p;
                _pending = null;
            }
            Write(next.Version, next.Text);
        }
    }

    private void Write(long version, string text)
    {
        lock (_file)
        {
            if (version <= _written) return; // a newer text is on disk already
            var sw = Stopwatch.StartNew();
            try { _write(text); }
            catch (Exception ex) { Log.Error(_label + " save", ex); return; }
            _written = version;
            if (sw.ElapsedMilliseconds > 100) Log.Info($"{_label} save took {sw.ElapsedMilliseconds} ms (off the UI thread)");
        }
    }
}
