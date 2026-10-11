using System.IO;
using System.Text;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>
/// CursorHop log lines (Fixtures/CursorHop: real lines from CursorHop 0.12 on a secondary PC, device id and name
/// replaced) through the parser, the state and the debounce of the peeks.
/// </summary>
public class CursorHopTests
{
    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CursorHop", name), Encoding.UTF8);

    private static List<CursorHopEvent> Events(string name) =>
        Fixture(name).Select(CursorHopLog.Parse).Where(e => e is not null).Select(e => e!).ToList();

    [Fact]
    public void Peer_hello_gives_name_connection_and_role()
    {
        var e = CursorHopLog.Parse("2026-10-11T09:42:45.269263Z  INFO cursorhop::forwarding: [PEER] PeerHello processed — connection ready peer=11111111-2222-4333-8444-555555555555 name=Desktop-PC platform=Windows synthetic_id=inbound-192.168.10.***:36285 is_tofu_pending=false peer_is_primary_over_us=true");
        Assert.NotNull(e);
        Assert.Equal(CursorHopEventKind.PeerConnected, e.Kind);
        Assert.Equal("11111111-2222-4333-8444-555555555555", e.Peer);
        Assert.Equal("Desktop-PC", e.Name);
        Assert.Equal("Windows", e.Platform);
        Assert.Equal("inbound-192.168.10.***:36285", e.Connection);
        Assert.True(e.PeerIsPrimary);
        Assert.Equal(new DateTime(2026, 10, 11, 9, 42, 45, DateTimeKind.Utc), e.Time.AddTicks(-(e.Time.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public void Clipboard_line_gives_format_and_connection()
    {
        var e = CursorHopLog.Parse("2026-10-11T09:55:01.190113Z  INFO cursorhop::injection: [CLIP_RECV] received REGULAR clipboard frame — injecting to local clipboard (Ctrl+V will paste into focused window) format=PlainText data_len=32 peer=inbound-192.168.10.***:36285");
        Assert.Equal(CursorHopEventKind.ClipboardReceived, e!.Kind);
        Assert.Equal("PlainText", e.Format);
        Assert.Equal("inbound-192.168.10.***:36285", e.Peer);
    }

    [Fact]
    public void Lines_without_meaning_for_the_island_are_ignored()
    {
        foreach (var line in Fixture("ignored.log"))
            Assert.True(CursorHopLog.Parse(line) is null, line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("2026-10-11T09:42:45.269263Z  INFO cursorhop::forwarding: [PEER] PeerHello processed — connection ready")] // no peer id
    [InlineData("2026-10-11T09:4")] // a line cut off while it was written
    [InlineData("[PEER] PeerHello processed — connection ready peer=x name=y")] // no timestamp
    public void Broken_lines_give_nothing(string line) => Assert.Null(CursorHopLog.Parse(line));

    [Fact]
    public void Nul_padding_does_not_hide_a_line()
    {
        var e = CursorHopLog.Parse("\0\02026-10-11T09:40:01Z  INFO cursorhop: CursorHop core starting version=\"0.12.1\"\r");
        Assert.Equal(CursorHopEventKind.CoreStarted, e!.Kind);
    }

    [Fact]
    public void Start_lines_reset_the_state()
    {
        var events = Events("startup.log");
        Assert.Equal(2, events.Count(e => e.Kind == CursorHopEventKind.CoreStarted));
        Assert.Equal(2, events.Count(e => e.Kind == CursorHopEventKind.AllPeersGone));
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public void Real_session_on_a_secondary_ends_connected_with_the_mouse_at_the_primary()
    {
        var state = new CursorHopState();
        var changes = Events("secondary-session.log").SelectMany(state.Apply).ToList();

        var peer = Assert.Single(state.Peers);
        Assert.Equal("Desktop-PC", peer.Name);
        Assert.True(peer.IsPrimary);
        Assert.Equal("inbound-192.168.10.***:36285", peer.Connection);
        Assert.Equal("Desktop-PC", state.CursorOn); // the last line: the Primary let go, its mouse is back there

        // The first connection (inbound …:53633) never finished its handshake: its end changes nothing.
        Assert.Equal(5, changes.Count(c => c.Kind == CursorHopEventKind.PeerConnected));
        Assert.Equal(4, changes.Count(c => c.Kind == CursorHopEventKind.PeerDisconnected));
        Assert.All(changes.Where(c => c.Kind is CursorHopEventKind.PeerConnected or CursorHopEventKind.PeerDisconnected), c => Assert.Equal("Desktop-PC", c.Name));

        // Clipboard from the connection id resolves to the device name.
        var clips = changes.Where(c => c.Kind == CursorHopEventKind.ClipboardReceived).ToList();
        Assert.Equal(4, clips.Count);
        Assert.All(clips, c => { Assert.Equal("Desktop-PC", c.Name); Assert.Equal("PlainText", c.Format); });
    }

    [Fact]
    public void On_a_secondary_the_mouse_is_at_the_primary_until_it_claims_this_screen()
    {
        var state = new CursorHopState();
        var lines = Fixture("secondary-session.log");
        // Up to the hello at 09:42:45: connected, the mouse is over at the Primary.
        foreach (var e in lines.TakeWhile(l => string.CompareOrdinal(l, "2026-10-11T09:42:46") < 0).Select(CursorHopLog.Parse).Where(e => e is not null))
            state.Apply(e!);
        Assert.Equal("Desktop-PC", state.CursorOn);
        // 09:42:49 "peer claimed Primary role" (+ "[IDLE_CURSOR] reveal reason=remote_input"): the mouse is here.
        state.Apply(CursorHopLog.Parse(lines.First(l => l.StartsWith("2026-10-11T09:42:49")))!);
        Assert.Null(state.CursorOn);
        // 09:42:50 "peer released Primary role": back at the Primary.
        var moved = state.Apply(CursorHopLog.Parse(lines.First(l => l.StartsWith("2026-10-11T09:42:50.6")))!);
        Assert.Equal("Desktop-PC", state.CursorOn);
        Assert.Equal("Desktop-PC", Assert.Single(moved).Name);
    }

    [Fact]
    public void On_the_primary_the_mouse_leaves_with_switch_lines()
    {
        // Message texts from cursorhop.exe 0.12.1, not seen in a log yet (this PC is a secondary); fields guessed.
        var state = new CursorHopState();
        state.Apply(new CursorHopEvent(CursorHopEventKind.PeerConnected, DateTime.UtcNow) { Peer = "a", Name = "Laptop", PeerIsPrimary = false });
        Assert.Null(state.CursorOn);
        state.Apply(CursorHopLog.Parse("2026-10-11T10:00:00.000000Z  INFO cursorhop::forwarding: [SWITCH] now controlling remote device peer=a")!);
        Assert.Equal("Laptop", state.CursorOn);
        state.Apply(CursorHopLog.Parse("2026-10-11T10:00:05.000000Z  INFO cursorhop::forwarding: [SWITCH] returned to local control")!);
        Assert.Null(state.CursorOn);
        // Without a known field: the only connected device.
        state.Apply(CursorHopLog.Parse("2026-10-11T10:00:06.000000Z  INFO cursorhop::forwarding: [SWITCH] hopped to new target")!);
        Assert.Equal("Laptop", state.CursorOn);
    }

    [Fact]
    public void A_late_end_of_an_old_connection_keeps_the_new_one()
    {
        var state = new CursorHopState();
        state.Apply(new CursorHopEvent(CursorHopEventKind.PeerConnected, DateTime.UtcNow) { Peer = "id", Name = "PC", Connection = "inbound-1" });
        state.Apply(new CursorHopEvent(CursorHopEventKind.PeerConnected, DateTime.UtcNow) { Peer = "id", Name = "PC", Connection = "inbound-2" });
        Assert.Empty(state.Apply(new CursorHopEvent(CursorHopEventKind.PeerDisconnected, DateTime.UtcNow) { Peer = "inbound-1" }));
        Assert.Single(state.Peers);
        Assert.Single(state.Apply(new CursorHopEvent(CursorHopEventKind.PeerDisconnected, DateTime.UtcNow) { Peer = "inbound-2" }));
        Assert.Empty(state.Peers);
    }

    [Fact]
    public void Reconnect_bursts_announce_only_the_connection_that_holds()
    {
        // The real session: connected and gone again five times within two minutes after the laptop woke up.
        var state = new CursorHopState();
        var announcer = new CursorHopAnnouncer();
        var said = new List<(string, bool)>();
        foreach (var e in Events("secondary-session.log"))
        {
            said.AddRange(announcer.Due(e.Time)); // the service ticks every second; lines are a fine enough clock here
            foreach (var c in state.Apply(e))
            {
                if (c.Kind == CursorHopEventKind.PeerConnected) announcer.Connected(c.Name!, e.Time);
                if (c.Kind == CursorHopEventKind.PeerDisconnected) announcer.Disconnected(c.Name!, e.Time);
            }
        }
        said.AddRange(announcer.Due(DateTime.MaxValue));
        Assert.Equal(new[] { ("Desktop-PC", true) }, said);
    }

    [Fact]
    public void A_real_drop_is_announced_after_the_delay()
    {
        var a = new CursorHopAnnouncer();
        var t = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        a.Connected("Laptop", t);
        Assert.Empty(a.Due(t.AddSeconds(1)));
        Assert.Equal(new[] { ("Laptop", true) }, a.Due(t + CursorHopAnnouncer.ConnectDelay));
        a.Disconnected("Laptop", t.AddMinutes(1));
        Assert.Empty(a.Due(t.AddMinutes(1).AddSeconds(5)));
        Assert.Equal(new[] { ("Laptop", false) }, a.Due(t.AddMinutes(1) + CursorHopAnnouncer.DisconnectDelay));
        Assert.False(a.HasPending);
        // Known from the log at start: no "verbunden" peek, but its drop is announced.
        var b = new CursorHopAnnouncer();
        b.Silently("PC");
        b.Connected("PC", t);
        Assert.Empty(b.Due(t.AddMinutes(1)));
        b.Disconnected("PC", t);
        Assert.Single(b.Due(t.AddMinutes(5)));
    }

    [Fact]
    public void Tail_reads_while_cursorhop_writes_and_waits_for_whole_lines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DynamicBayTests", "cursorhop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "core.log.2026-10-11");
        try
        {
            var tail = new CursorHopLogTail();
            tail.Follow(file);
            Assert.Empty(tail.ReadNew()); // not there yet
            // Like CursorHop: the file stays open for writing (sharing read and delete).
            using (var w = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                void Write(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write(b); w.Flush(); }
                Write("first line — ok\nsecond ");
                Assert.Equal(new[] { "first line — ok" }, tail.ReadNew());
                Assert.Empty(tail.ReadNew());
                Write("half\r\n");
                Assert.Equal(new[] { "second half" }, tail.ReadNew());
            }
            // Replaced by a shorter file: from the start again.
            File.WriteAllText(file, "new\n");
            Assert.Equal(new[] { "new" }, tail.ReadNew());
            // Midnight: the next day's file from its start.
            var next = Path.Combine(dir, "core.log.2026-10-12");
            File.WriteAllText(next, "a\nb\n");
            tail.Follow(next);
            Assert.Equal(new[] { "a", "b" }, tail.ReadNew());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Daily_file_name_uses_the_utc_date()
    {
        Assert.EndsWith(Path.Combine("CursorHop", "logs", "core.log.2026-10-11"), CursorHopService.LogFileFor(new DateTime(2026, 10, 11, 23, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Texts_work_on_either_side()
    {
        try
        {
            Loc.Init("de");
            Assert.Equal("Maus ist hier", CursorHopService.CursorTextFor(null));
            Assert.Equal("Maus am Desktop-PC", CursorHopService.CursorTextFor("Desktop-PC"));
            Assert.Equal("Text", CursorHopService.FormatText("PlainText"));
            Assert.Equal("Bild", CursorHopService.FormatText("Image"));
            Assert.Equal("Icon.Laptop", CursorHopService.IconFor("LaptopJonathan"));
            Assert.Equal("Icon.Monitor", CursorHopService.IconFor("Desktop-PC"));
            Loc.Init("en");
            Assert.Equal("Mouse on Desktop-PC", CursorHopService.CursorTextFor("Desktop-PC"));
        }
        finally { Loc.Init("de"); }
    }

    [Fact]
    public void Migration_adds_the_card_once_where_cursorhop_is_installed()
    {
        var s = new AppSettings { SettingsVersion = 10 };
        s.Migrate(cursorHopInstalled: true);
        Assert.Contains(Widgets.CursorHop, s.SystemWidgets);
        s.SystemWidgets.Remove(Widgets.CursorHop); // user took it off
        s.Migrate(cursorHopInstalled: true);
        Assert.DoesNotContain(Widgets.CursorHop, s.SystemWidgets);

        var without = new AppSettings { SettingsVersion = 10 };
        without.Migrate(cursorHopInstalled: false);
        Assert.DoesNotContain(Widgets.CursorHop, without.SystemWidgets);
        Assert.True(without.CursorHopEnabled && without.CursorHopAutoStart && without.CursorHopPeeks);
    }

    [Fact]
    public void Clipboard_entry_shows_the_device_it_came_from()
    {
        try
        {
            Loc.Init("de");
            var item = new ClipItem { Kind = ClipKind.Text, Text = "x" };
            var changed = new List<string?>();
            item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            item.SourceDevice = "Desktop-PC";
            Assert.EndsWith(" · Desktop-PC", item.Label);
            Assert.Contains(nameof(ClipItem.Label), changed);
        }
        finally { Loc.Init("de"); }
    }
}
