using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DynamicBay.Core;
using DynamicBay.Services.Link;

namespace DynamicBay.Tests;

/// <summary>Work that left the UI thread (settings saves) and lists that are no longer rebuilt on every update.</summary>
public class UiThreadTests
{
    [Fact]
    public void Background_writer_returns_at_once_and_writes_the_newest_text_last()
    {
        var written = new List<string>();
        var gate = new ManualResetEventSlim(false);
        var w = new BackgroundWriter(t =>
        {
            gate.Wait(); // a slow disk: the caller must not wait for it
            lock (written) written.Add(t);
        }, "Test");

        for (int i = 1; i <= 5; i++) w.Queue("v" + i);
        Assert.Empty(written); // Queue returned at once although the write blocks
        gate.Set();
        w.Drain();
        Assert.Equal("v5", written[^1]);
        Assert.True(written.Count <= 2); // versions overtaken while one was written are skipped
    }

    [Fact]
    public void Background_writer_never_lets_an_older_text_land_after_a_direct_write()
    {
        var written = new List<string>();
        var inWrite = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var w = new BackgroundWriter(t =>
        {
            if (t == "old") { inWrite.Set(); release.Wait(); }
            lock (written) written.Add(t);
        }, "Test");
        w.Queue("old");
        inWrite.Wait();
        w.Queue("older-but-queued"); // waits behind the running write
        var exit = Task.Run(() => w.WriteNow("exit")); // the exit save
        Thread.Sleep(50);
        release.Set();
        exit.Wait();
        w.Drain();
        Assert.Equal("exit", written[^1]);
    }

    private sealed record Row(string Id, string Text);

    [Fact]
    public void Collection_sync_keeps_unchanged_rows_and_reports_no_change_for_the_same_list()
    {
        var a = new Row("a", "A");
        var b = new Row("b", "B");
        var list = new ObservableCollection<Row> { a, b };
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);

        Assert.Equal(0, CollectionSync.Apply(list, new[] { new Row("a", "A"), new Row("b", "B") }, r => r.Id, (x, y) => x == y));
        Assert.Empty(events);
        Assert.Same(a, list[0]); // the row (and its template) stays

        CollectionSync.Apply(list, new[] { new Row("c", "C"), new Row("a", "A2") }, r => r.Id, (x, y) => x == y);
        Assert.Equal(new[] { "c", "a" }, list.Select(r => r.Id));
        Assert.Equal("A2", list[1].Text);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);

        CollectionSync.Apply(list, new[] { new Row("a", "A2"), new Row("c", "C") }, r => r.Id, (x, y) => x == y);
        Assert.Equal(new[] { "a", "c" }, list.Select(r => r.Id));
        CollectionSync.Apply(list, Array.Empty<Row>(), r => r.Id, (x, y) => x == y);
        Assert.Empty(list);
    }

    [Fact]
    public void Found_devices_compare_by_content_not_by_the_address_list()
    {
        var x = new LinkFoundView("id", "Laptop", "100.64.1.2", "Tailscale") { Addresses = new List<string> { "100.64.1.2", "192.168.1.5" } };
        var y = new LinkFoundView("id", "Laptop", "100.64.1.2", "Tailscale") { Addresses = new List<string> { "100.64.1.2", "192.168.1.5" } };
        Assert.NotEqual(x, y); // the record compares the list by reference
        Assert.True(LinkFoundView.Same(x, y));
        Assert.False(LinkFoundView.Same(x, y with { Addresses = new[] { "100.64.1.2" } }));
    }
}
