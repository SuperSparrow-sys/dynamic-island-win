using System.IO;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

public class AtomicFileTests
{
    [Fact]
    public void Writes_a_new_file_and_keeps_the_old_one_as_backup()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dynamicbay-atomic-{Guid.NewGuid():N}");
        string file = Path.Combine(dir, "a.json");
        try
        {
            AtomicFile.WriteAllText(file, "eins");
            Assert.Equal("eins", File.ReadAllText(file));
            AtomicFile.WriteAllText(file, "zwei");
            Assert.Equal("zwei", File.ReadAllText(file));
            Assert.Equal("eins", File.ReadAllText(file + ".bak"));
            Assert.False(File.Exists(file + ".tmp"));
            AtomicFile.WriteAllText(file, "drei", backup: false);
            Assert.Equal("drei", File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_broken_file_is_copied_aside()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dynamicbay-aside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "notes.json");
        try
        {
            File.WriteAllText(file, "{ kaputt");
            Assert.True(AtomicFile.SetAside(file));
            var copy = Assert.Single(Directory.GetFiles(dir, "notes.corrupt-*.json"));
            Assert.Equal("{ kaputt", File.ReadAllText(copy));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class DownloadsMigrationTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public void Downloads_join_the_small_island_once(int from)
    {
        var s = new AppSettings { SettingsVersion = from };
        s.CompactItems.Remove(Widgets.Downloads);
        s.Migrate();
        Assert.Contains(Widgets.Downloads, s.CompactItems);
        Assert.True(s.SettingsVersion >= 10); // later migrations may follow
        s.CompactItems.Remove(Widgets.Downloads); // switched off afterwards: stays off
        s.Migrate();
        Assert.DoesNotContain(Widgets.Downloads, s.CompactItems);
    }
}

public class TimeTrackingFileTests
{
    private static string TempCsv() => Path.Combine(Path.GetTempPath(), $"dynamicbay-time-{Guid.NewGuid():N}.csv");

    private static void Remove(string file)
    {
        foreach (var f in new[] { file, file + ".bak", file + ".tmp" }) try { File.Delete(f); } catch { }
    }

    [Fact]
    public void Entries_longer_than_a_day_keep_their_days()
    {
        var entries = new List<TimeEntry>
        {
            new() { Project = "Messe", Start = new DateTime(2026, 10, 9, 9, 0, 0), End = new DateTime(2026, 10, 10, 11, 0, 0) },
            new() { Project = "Nacht", Start = new DateTime(2026, 10, 9, 22, 0, 0), End = new DateTime(2026, 10, 11, 2, 0, 0) },
            new() { Project = "Kurz", Start = new DateTime(2026, 10, 9, 23, 0, 0), End = new DateTime(2026, 10, 10, 1, 0, 0) },
        };
        var back = TimeTrackingService.Parse(TimeTrackingService.ToCsv(entries).Trim().Split(Environment.NewLine));
        Assert.Equal(new DateTime(2026, 10, 10, 11, 0, 0), back.Single(e => e.Project == "Messe").End);
        Assert.Equal(new DateTime(2026, 10, 11, 2, 0, 0), back.Single(e => e.Project == "Nacht").End);
        Assert.Equal(new DateTime(2026, 10, 10, 1, 0, 0), back.Single(e => e.Project == "Kurz").End);
    }

    [Fact]
    public void Rows_that_are_no_entry_are_kept_and_written_back()
    {
        var lines = new[]
        {
            "Datum;Projekt;Start;Ende;Stunden;Beschreibung",
            "09.10.2026;Intern;9:00;10:00;1,00;",
            "Summe Oktober;;;;12,5;",
            "09.10.2026;Intern;9;10;1;",   // "9" is no clock time (TimeSpan.TryParse made it nine days)
            ";;;;;",
        };
        var unreadable = new List<string>();
        var back = TimeTrackingService.Parse(lines, unreadable);
        var e = Assert.Single(back);
        Assert.Equal(new DateTime(2026, 10, 9, 9, 0, 0), e.Start);
        Assert.Equal(new[] { "Summe Oktober;;;;12,5;", "09.10.2026;Intern;9;10;1;" }, unreadable);
        var csv = TimeTrackingService.ToCsv(back, unreadable).Trim().Split(Environment.NewLine);
        Assert.Equal("09.10.2026;Intern;9;10;1;", csv[^1]);
        Assert.Equal("Summe Oktober;;;;12,5;", csv[^2]);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_overwritten()
    {
        string file = TempCsv();
        try
        {
            File.WriteAllText(file, TimeTrackingService.ToCsv(new[]
            {
                new TimeEntry { Project = "Alt", Start = new DateTime(2026, 10, 1, 9, 0, 0), End = new DateTime(2026, 10, 1, 17, 0, 0) },
            }));
            var settings = new AppSettings { TimeTrackingFile = file };
            TimeTrackingService time;
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // like Excel holding it
            {
                time = new TimeTrackingService(settings);
                Assert.Empty(time.Entries);
                time.AddEntry("Neu", new DateTime(2026, 10, 9, 9, 0, 0), new DateTime(2026, 10, 9, 10, 0, 0), "");
            }
            Assert.Contains("Alt", File.ReadAllText(file));
            // Readable again: the history comes in, the entry made meanwhile stays, and both are written.
            time.AddEntry("Neu", new DateTime(2026, 10, 9, 11, 0, 0), new DateTime(2026, 10, 9, 12, 0, 0), "");
            var saved = TimeTrackingService.Parse(File.ReadAllLines(file));
            Assert.Equal(3, saved.Count);
            Assert.Contains(saved, e => e.Project == "Alt");
            Assert.Equal(2, saved.Count(e => e.Project == "Neu"));
        }
        finally { Remove(file); }
    }

    [Fact]
    public void Picking_an_existing_file_merges_instead_of_overwriting()
    {
        string a = TempCsv(), b = TempCsv();
        try
        {
            var settings = new AppSettings { TimeTrackingFile = a };
            var time = new TimeTrackingService(settings);
            time.AddEntry("Hier", new DateTime(2026, 10, 9, 9, 0, 0), new DateTime(2026, 10, 9, 10, 0, 0), "");
            File.WriteAllText(b, TimeTrackingService.ToCsv(new[]
            {
                new TimeEntry { Project = "Dort", Start = new DateTime(2026, 10, 2, 9, 0, 0), End = new DateTime(2026, 10, 2, 12, 0, 0) },
                new TimeEntry { Project = "Hier", Start = new DateTime(2026, 10, 9, 9, 0, 0), End = new DateTime(2026, 10, 9, 10, 0, 0) },
            }));
            settings.TimeTrackingFile = b;
            var saved = TimeTrackingService.Parse(File.ReadAllLines(b));
            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, e => e.Project == "Dort");
            Assert.Single(saved, e => e.Project == "Hier");
        }
        finally { Remove(a); Remove(b); }
    }

    [Fact]
    public void Only_the_newest_open_entry_counts_up_to_now()
    {
        string file = TempCsv();
        try
        {
            var day = new DateTime(2026, 10, 1);
            File.WriteAllText(file, string.Join(Environment.NewLine,
                "Datum;Projekt;Start;Ende;Stunden;Beschreibung",
                "01.10.2026;Vergessen;08:00;;;",
                "01.10.2026;Läuft;09:00;;;"));
            var time = new TimeTrackingService(new AppSettings { TimeTrackingFile = file });
            Assert.True(time.IsRunning);
            Assert.Equal("Läuft", time.CurrentProject);
            Assert.Equal(TimeTrackingService.Quarter, time.Total(day, day.AddDays(1), "Vergessen"));
        }
        finally { Remove(file); }
    }
}

public class CalendarPickTests
{
    private static readonly DateTime Day = new(2026, 10, 9);

    private static CalendarEvent Ev(string title, DateTime start, DateTime end, bool allDay = false) =>
        new() { Title = title, Start = start, End = end, AllDay = allDay };

    [Fact]
    public void Back_to_back_meetings_announce_the_next_one()
    {
        var first = Ev("Erstes", Day.AddHours(10), Day.AddHours(11));
        var second = Ev("Zweites", Day.AddHours(11), Day.AddHours(12));
        var now = Day.AddHours(10).AddMinutes(55);
        Assert.Same(second, CalendarService.PickNext(new[] { first, second }, now, 10));
        Assert.Same(second, Assert.Single(CalendarService.StartingSoon(new[] { first, second }, now, 10)));
    }

    [Fact]
    public void A_meeting_over_several_days_does_not_block_the_day()
    {
        var fair = Ev("Messe", Day.AddDays(-1).AddHours(9), Day.AddDays(1).AddHours(17));
        var later = Ev("Telefonat", Day.AddHours(15), Day.AddHours(15.5));
        var allDay = Ev("Geburtstag", Day, Day.AddDays(1), allDay: true);
        var events = new[] { allDay, fair, later };
        Assert.Same(later, CalendarService.PickNext(events, Day.AddHours(12), 10));
        // Nothing else left today: the running one is shown.
        Assert.Same(fair, CalendarService.PickNext(events, Day.AddHours(16), 10));
    }

    [Fact]
    public void A_meeting_that_just_began_stays_the_next_one()
    {
        var now = Day.AddHours(10).AddMinutes(3);
        var running = Ev("Daily", Day.AddHours(10), Day.AddHours(10.25));
        var later = Ev("Review", Day.AddHours(14), Day.AddHours(15));
        Assert.Same(running, CalendarService.PickNext(new[] { running, later }, now, 10));
    }
}

public class ShelfReachTests
{
    [Fact]
    public void Files_on_a_drive_that_is_away_are_not_gone()
    {
        string file = Path.GetTempFileName();
        try
        {
            Assert.Equal(ShelfPathState.There, ShelfService.StateOf(file));
            Assert.Equal(ShelfPathState.Gone, ShelfService.StateOf(file + ".fehlt"));
            var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            char free = "QRSTUVWXYZ".FirstOrDefault(c => !used.Contains(c));
            if (free != default) Assert.Equal(ShelfPathState.Unreachable, ShelfService.StateOf($@"{free}:\Ordner\Datei.txt"));
        }
        finally { File.Delete(file); }
    }
}
