using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using Microsoft.Win32;

namespace DynamicBay.Services;

public sealed class TimeEntry
{
    public string Project { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public string Description { get; set; } = "";
    public bool IsRunning => End is null;
    public TimeSpan Duration => (End ?? TimeTrackingService.RoundQuarter(DateTime.Now)) - Start;
    public string TimeText => $"{Start:HH:mm} – {(End is { } e ? e.ToString("HH:mm") : Loc.German ? "läuft" : "running")}";
    public string HoursText => TimeTrackingService.Format(Duration < TimeSpan.Zero ? TimeSpan.Zero : Duration) + " h";
    public bool HasDescription => Description.Length > 0;
}

public sealed partial class ProjectTotal : ObservableObject
{
    public string Project { get; init; } = "";
    [ObservableProperty] private string _today = "";
    [ObservableProperty] private bool _running;
}

/// <summary>What the island should ask about the running time tracking.</summary>
public enum TimeReminder { LongRunning, Evening }

/// <summary>
/// Time tracking into ONE file the user picks (CSV, opens in Excel): every project, every day, in quarter hours.
/// Start and end are rounded to the nearest 15 minutes (at least 15 minutes per entry). While a project runs the
/// file is rewritten every 15 minutes (the open entry has no end yet), and right away on every change.
/// The file is the only store: it is read at start, so edits made in Excel are kept.
/// A click that is taken back within a few minutes books nothing. Entries can be added by hand (with a description),
/// changed and deleted in the time tracking window; projects can be renamed.
/// </summary>
public sealed partial class TimeTrackingService : ObservableObject
{
    public static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    /// <summary>Stopped (or switched) within this time: a slip, nothing is booked.</summary>
    public static readonly TimeSpan Slip = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(10);
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(5) }; // shows minutes only: a few seconds late at most
    private readonly DispatcherTimer _write = new() { Interval = Quarter };
    private readonly List<TimeEntry> _entries = new();
    private DateTime _runningSince; // exact start, for the live display (the file has the rounded one)
    private DateTime _lastReminder;
    private DateTime _eveningAsked;
    private DateTime? _lockedAt;
    private bool _sessionLocked;
    private bool _demo;
    /// <summary>The file is there but could not be read (open in Excel, network drive away): it must not be overwritten.</summary>
    private bool _loadFailed;
    private readonly DispatcherTimer _reload = new() { Interval = Quarter };
    /// <summary>Rows of the file that are no entry (typed in Excel, broken): written back unchanged at the end.</summary>
    private readonly List<string> _unreadable = new();
    /// <summary>When the file was written last before this start (a project still running then: the PC was off since).</summary>
    private DateTime _fileWrittenAt;
    private DateTime _backedUp;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _currentProject = "";
    [ObservableProperty] private string _elapsed = "0:00";
    [ObservableProperty] private string _todayTotal = "0:00";
    [ObservableProperty] private string _status = "";
    public ObservableCollection<ProjectTotal> Projects { get; } = new();

    /// <summary>Entries were added, changed or removed (the window refreshes its list).</summary>
    public event Action? Changed;
    /// <summary>Running a long time, or still running in the evening.</summary>
    public event Action<TimeReminder, TimeSpan>? Reminder;
    /// <summary>Back at the PC after a while (locked, asleep or shut down) with a project running: (away from, back at).</summary>
    public event Action<DateTime, DateTime>? CameBack;

    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Zeiterfassung.csv");
    public string FilePath => string.IsNullOrWhiteSpace(_settings.TimeTrackingFile) ? DefaultFile : _settings.TimeTrackingFile;

    public TimeTrackingService(AppSettings settings)
    {
        _settings = settings;
        _tick.Tick += (_, _) => { Update(); CheckReminders(); };
        _write.Tick += (_, _) => Write();
        _reload.Tick += (_, _) => { if (TryReload()) Saved(); };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.TimeProjects)) RebuildProjects();
            if (e.PropertyName == nameof(AppSettings.TimeTrackingFile)) SwitchFile();
        };
        Load();
        RebuildProjects();
        SyncRunning();
        Update();
    }

    /// <summary>The newest open entry is the running project (at start, and after entries came in from the file).</summary>
    private void SyncRunning()
    {
        if (Running is not { } r) return;
        if (!IsRunning)
        {
            // The file has the rounded start, which can lie a few minutes ahead: never count down from the future.
            _runningSince = Min(r.Start, DateTime.Now);
            _lastReminder = DateTime.Now;
            _tick.Start();
            _write.Start();
        }
        IsRunning = true;
        CurrentProject = r.Project;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>
    /// Lock and unlock, sleep and wake: a long break can be taken off the running project. Also right now: the PC was
    /// shut down with a project running (the file was last written long ago).
    /// </summary>
    public void WatchSession()
    {
        var ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        // The events come on a system thread; the time is taken there (before sleep the UI may not run any more).
        SystemEvents.SessionSwitch += (_, e) =>
        {
            var now = DateTime.Now;
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect)
                ui.BeginInvoke(() => { _sessionLocked = true; Left(now); });
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
                ui.BeginInvoke(() => { _sessionLocked = false; Returned(now); });
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            var now = DateTime.Now;
            if (e.Mode == PowerModes.Suspend) ui.BeginInvoke(() => Left(now));
            // Woken but still locked: the question waits for the unlock, where it can be seen.
            else if (e.Mode == PowerModes.Resume) ui.BeginInvoke(() => { if (!_sessionLocked) Returned(now); });
        };
        // Only when the user signed in again since the last write (shut down - also with fast startup, which keeps the
        // uptime running - or signed out): a mere restart of DynamicBay (update) is no break.
        var written = _fileWrittenAt;
        var at = DateTime.Now;
        if (IsRunning && Running is { } r && r.Start < written && SignedInAt() > written && at - written > Quarter)
            ui.BeginInvoke(() => CameBack?.Invoke(written, at));
    }

    /// <summary>When this Windows session began: the start of the oldest Explorer of this user (DateTime.MinValue if unknown).</summary>
    private static DateTime SignedInAt()
    {
        var oldest = DateTime.MaxValue;
        int session;
        using (var me = System.Diagnostics.Process.GetCurrentProcess()) session = me.SessionId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
            using (p)
                try { if (p.SessionId == session && p.StartTime < oldest) oldest = p.StartTime; } catch { }
        return oldest == DateTime.MaxValue ? DateTime.MinValue : oldest;
    }

    /// <summary>Locked or asleep: the first of both counts (locked, then the lid closed).</summary>
    private void Left(DateTime at) => _lockedAt ??= at;

    private void Returned(DateTime now)
    {
        if (_lockedAt is not { } at) return;
        _lockedAt = null;
        if (IsRunning && now - at >= Away && Running is { } r && r.Start < at) CameBack?.Invoke(at, now);
    }

    /// <summary>Several open entries (edited in Excel, merged from two files): the newest one runs.</summary>
    private TimeEntry? Running => _entries.Where(e => e.End is null).MaxBy(e => e.Start);
    public IReadOnlyList<TimeEntry> Entries => _entries;

    /// <summary>To the nearest quarter hour (7:52 → 7:45, 7:53 → 8:00).</summary>
    public static DateTime RoundQuarter(DateTime t)
    {
        long q = Quarter.Ticks;
        return new DateTime((t.Ticks + q / 2) / q * q, t.Kind);
    }

    // ---------- start / stop ----------

    /// <summary>Starts the project, or stops it if it is the one running; another running project stops first.</summary>
    [RelayCommand]
    private void Toggle(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        bool same = Running?.Project == project;
        if (same) { Stop(); return; }
        Start(project);
    }

    public void Start(string project)
    {
        if (Running?.Project == project) return;
        CloseRunning();
        var now = DateTime.Now;
        _entries.Add(new TimeEntry { Project = project, Start = RoundQuarter(now) });
        _runningSince = now;
        _lastReminder = now;
        CurrentProject = project;
        IsRunning = true;
        _tick.Start();
        _write.Stop(); _write.Start();
        Saved();
    }

    [RelayCommand]
    private void Stop()
    {
        CloseRunning();
        IsRunning = false;
        CurrentProject = "";
        _tick.Stop();
        _write.Stop();
        Saved();
    }

    /// <summary>
    /// Ends the running entry at the nearest quarter hour, at least one quarter after its start.
    /// Taken back within a few minutes (a slip of the finger): the entry goes away, nothing is booked.
    /// </summary>
    private void CloseRunning()
    {
        if (Running is not { } r) return;
        if (DateTime.Now - _runningSince < Slip) { _entries.Remove(r); return; }
        var end = RoundQuarter(DateTime.Now);
        r.End = end - r.Start < Quarter ? r.Start + Quarter : end;
    }

    /// <summary>Takes a break off the running project: it ends when you left, and starts again now.</summary>
    public void TakeBreak(DateTime from, DateTime to)
    {
        if (Running is not { } r) return;
        var end = RoundQuarter(from);
        var restart = RoundQuarter(to);
        if (end <= r.Start) end = r.Start + Quarter;
        if (restart < end) restart = end;
        r.End = end;
        _entries.Add(new TimeEntry { Project = r.Project, Start = restart, Description = r.Description });
        _runningSince = to;
        Saved();
    }

    private void CheckReminders()
    {
        if (!IsRunning || !_settings.TimeReminders || _demo) return;
        var now = DateTime.Now;
        var running = now - _runningSince;
        // Every 4 hours: still on it?
        if (now - _lastReminder >= TimeSpan.FromHours(4)) { _lastReminder = now; Reminder?.Invoke(TimeReminder.LongRunning, running); }
        // Once in the evening (from 19:00): forgot to stop? Not for a project just started in the evening.
        if (now.Hour >= 19 && running >= TimeSpan.FromHours(1) && _eveningAsked.Date != now.Date) { _eveningAsked = now; Reminder?.Invoke(TimeReminder.Evening, running); }
    }

    // ---------- editing (time tracking window) ----------

    /// <summary>Adds time by hand ("1 h for the customer call"), rounded to quarter hours.</summary>
    public void AddEntry(string project, DateTime start, DateTime end, string description)
    {
        project = project.Trim();
        if (project.Length == 0) return;
        AddProject(project);
        var (s, e) = Normalize(start, end);
        _entries.Add(new TimeEntry { Project = project, Start = s, End = e, Description = description.Trim() });
        Saved();
    }

    public void UpdateEntry(TimeEntry entry, string project, DateTime start, DateTime? end, string description)
    {
        if (!_entries.Contains(entry)) return;
        project = project.Trim();
        if (project.Length > 0) { AddProject(project); entry.Project = project; }
        if (end is { } en) { var (s, e) = Normalize(start, en); entry.Start = s; entry.End = e; }
        else entry.Start = RoundQuarter(start); // the running one: only the start
        entry.Description = description.Trim();
        if (entry.IsRunning && entry == Running)
        {
            CurrentProject = entry.Project;
            _runningSince = Min(entry.Start, DateTime.Now); // the live time follows the new start
        }
        Saved();
    }

    public void DeleteEntry(TimeEntry entry)
    {
        if (!_entries.Contains(entry)) return;
        if (entry == Running)
        {
            _entries.Remove(entry);
            IsRunning = false; CurrentProject = ""; _tick.Stop(); _write.Stop();
        }
        else _entries.Remove(entry);
        Saved();
    }

    private static (DateTime, DateTime) Normalize(DateTime start, DateTime end)
    {
        var s = RoundQuarter(start);
        var e = RoundQuarter(end);
        if (e <= s) e = s + Quarter;
        return (s, e);
    }

    /// <summary>A new project from the island's "+", the window or the settings.</summary>
    public void AddProject(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _settings.TimeProjects.Contains(name)) return;
        _settings.TimeProjects.Add(name);
    }

    /// <summary>Renames a project everywhere, also in the entries already in the file.</summary>
    public void RenameProject(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == oldName) return;
        int i = _settings.TimeProjects.IndexOf(oldName);
        if (_settings.TimeProjects.Contains(newName)) { if (i >= 0) _settings.TimeProjects.RemoveAt(i); }
        else if (i >= 0) _settings.TimeProjects[i] = newName;
        else _settings.TimeProjects.Add(newName);
        foreach (var e in _entries.Where(e => e.Project == oldName)) e.Project = newName;
        if (CurrentProject == oldName) CurrentProject = newName;
        Saved();
    }

    /// <summary>Removes the project from the list; its recorded time stays in the file.</summary>
    public void RemoveProject(string name) => _settings.TimeProjects.Remove(name);

    public IEnumerable<TimeEntry> EntriesOn(DateTime day) => _entries.Where(e => e.Start.Date == day.Date).OrderBy(e => e.Start);

    public TimeSpan Total(DateTime from, DateTime to, string? project = null)
    {
        var running = Running;
        return _entries
            .Where(e => e.Start >= from && e.Start < to && (project is null || e.Project == project))
            .Aggregate(TimeSpan.Zero, (t, e) => t + Booked(e, e == running));
    }

    private void Saved()
    {
        Write();
        Update();
        Changed?.Invoke();
    }

    // ---------- totals ----------

    private void RebuildProjects()
    {
        var names = _settings.TimeProjects.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        if (names.SequenceEqual(Projects.Select(p => p.Project))) return;
        Projects.Clear();
        foreach (var n in names) Projects.Add(new ProjectTotal { Project = n });
        Update();
    }

    /// <summary>
    /// Booked time of an entry: rounded quarters; the running one counts up to the next quarter so far. Another entry
    /// left open (only the newest one runs) counts its first quarter, not all the time up to now.
    /// </summary>
    private static TimeSpan Booked(TimeEntry e, bool running)
    {
        var end = e.End ?? (running ? RoundQuarter(DateTime.Now) : e.Start);
        var t = end - e.Start;
        return t < Quarter ? Quarter : t;
    }

    private void Update()
    {
        var today = DateTime.Today;
        foreach (var p in Projects)
        {
            p.Today = Format(Total(today, today.AddDays(1), p.Project));
            p.Running = IsRunning && p.Project == CurrentProject;
        }
        TodayTotal = Format(Total(today, today.AddDays(1)));
        Elapsed = IsRunning ? Format(DateTime.Now - _runningSince) : "0:00";
    }

    /// <summary>Hours and minutes only (0:25, 1:05) - running seconds were too restless.</summary>
    public static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";

    // ---------- the file ----------

    private static readonly CultureInfo De = new("de-DE");

    private void Load()
    {
        if (TryRead(FilePath, out var entries, out var unreadable))
        {
            _entries.AddRange(entries);
            _unreadable.AddRange(unreadable);
            try { if (File.Exists(FilePath)) _fileWrittenAt = File.GetLastWriteTime(FilePath); } catch { }
        }
        else LoadFailed();
    }

    /// <summary>Nothing is written until the file could be read; it is tried again every quarter and before every write.</summary>
    private void LoadFailed()
    {
        _loadFailed = true;
        _reload.Start();
    }

    /// <summary>
    /// Reads the file (also while Excel has it open). False: it is there but could not be read, or its drive is away -
    /// then it must not be treated as empty. A missing file on a drive that answers is simply new.
    /// </summary>
    private bool TryRead(string path, out List<TimeEntry> entries, out List<string> unreadable)
    {
        entries = new();
        unreadable = new();
        try
        {
            if (!File.Exists(path))
            {
                // File.Exists is also false for a network drive or USB disk that is away.
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || Directory.Exists(root)) return true;
                Log.Info($"TimeTracking: {root} not reachable");
                Status = Loc.German ? "Datei nicht erreichbar, wird später gespeichert" : "File not reachable, saved later";
                return false;
            }
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                while (reader.ReadLine() is { } line) lines.Add(line);
            entries = Parse(lines, unreadable);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("TimeTracking load", ex);
            Status = Loc.German ? "Datei nicht lesbar, wird später gespeichert" : "File can't be read, saved later";
            return false;
        }
    }

    /// <summary>The file could not be read so far: read it now and keep what was booked in the meantime on top.</summary>
    private bool TryReload()
    {
        if (!_loadFailed) return true;
        if (!TryRead(FilePath, out var loaded, out var unreadable)) return false;
        _loadFailed = false;
        _reload.Stop();
        Merge(loaded, unreadable);
        Log.Info($"TimeTracking: file read, {loaded.Count} entries");
        return true;
    }

    /// <summary>
    /// Another file was picked. One that exists already is read and merged with the entries here (nothing is
    /// overwritten); then everything is written into it.
    /// </summary>
    private void SwitchFile()
    {
        if (_demo) return;
        if (TryRead(FilePath, out var loaded, out var unreadable))
        {
            _loadFailed = false;
            _reload.Stop();
            _backedUp = default; // the new file gets its own backup before the first write
            Merge(loaded, unreadable);
            Saved();
        }
        else LoadFailed();
    }

    /// <summary>Adds entries from the file that are not here yet (same day, start and project = the same entry).</summary>
    private void Merge(List<TimeEntry> loaded, List<string> unreadable)
    {
        var have = _entries.Select(e => (e.Start, e.Project)).ToHashSet();
        _entries.InsertRange(0, loaded.Where(e => !have.Contains((e.Start, e.Project))));
        foreach (var line in unreadable) if (!_unreadable.Contains(line)) _unreadable.Add(line);
        // Only one project runs: one left open in the file while another was started here ends after its first quarter
        // (what it counted anyway), so it does not stay open in the file for good.
        var running = Running;
        foreach (var open in _entries.Where(e => e.End is null && e != running)) open.End = open.Start + Quarter;
        SyncRunning();
        Update();
        Changed?.Invoke();
    }

    /// <summary>Writes the whole file (sorted). If Excel has it open, it tries again on the next change or quarter.</summary>
    private void Write()
    {
        if (_demo) return;
        // Not read yet: writing now would replace the whole history with the few entries made since.
        if (_loadFailed && !TryReload()) return;
        try
        {
            BackUpOnce();
            // Temp file first, then swapped in: a crash while writing can't leave half a file.
            AtomicFile.WriteAllText(FilePath, ToCsv(_entries, _unreadable), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), backup: false);
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"TimeTracking write: {ex.Message}");
            Status = Loc.German ? "Datei in Excel offen, wird später gespeichert" : "File open in Excel, saved later";
        }
        catch (Exception ex) { Log.Error("TimeTracking write", ex); Status = ex.Message; }
    }

    /// <summary>Once a day (and once per start) the file as it was goes to "….csv.bak" before it is overwritten.</summary>
    private void BackUpOnce()
    {
        if (_backedUp == DateTime.Today) return;
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            _backedUp = DateTime.Today;
        }
        catch (Exception ex) { Log.Info($"TimeTracking backup: {ex.Message}"); }
    }

    /// <summary>
    /// Datum;Projekt;Start;Ende;Stunden;Beschreibung - semicolons and a BOM so Excel shows columns and umlauts right.
    /// unreadable: rows that are no entry, written back unchanged at the end.
    /// </summary>
    public static string ToCsv(IEnumerable<TimeEntry> entries, IEnumerable<string>? unreadable = null)
    {
        static string Cell(string v) => v.IndexOfAny(new[] { ';', '"' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder();
        sb.AppendLine("Datum;Projekt;Start;Ende;Stunden;Beschreibung");
        foreach (var e in entries.OrderBy(e => e.Start))
        {
            string end = e.End?.ToString("HH:mm") ?? "";
            string hours = e.End is { } en ? Math.Max(0.25, (en - e.Start).TotalHours).ToString("0.00", De) : "";
            string description = e.Description.Replace("\r", " ").Replace("\n", " ");
            sb.AppendLine(string.Join(";", e.Start.ToString("dd.MM.yyyy"), Cell(e.Project), e.Start.ToString("HH:mm"), end, hours, Cell(description)));
        }
        foreach (var line in unreadable ?? Enumerable.Empty<string>()) sb.AppendLine(line);
        return sb.ToString();
    }

    public static List<TimeEntry> Parse(IEnumerable<string> lines) => Parse(lines, null);

    /// <summary>unreadable: gets the rows that are no entry (kept, so the next write does not delete them).</summary>
    public static List<TimeEntry> Parse(IEnumerable<string> lines, List<string>? unreadable)
    {
        var list = new List<TimeEntry>();
        foreach (var line in lines.Skip(1))
        {
            var cols = SplitCsv(line);
            if (cols.All(string.IsNullOrWhiteSpace)) continue; // empty row (Excel keeps ";;;;;")
            if (ParseEntry(cols) is { } entry) list.Add(entry);
            else unreadable?.Add(line);
        }
        return list;
    }

    private static readonly string[] DateFormats = { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" };
    /// <summary>Exact clock times only: TimeSpan.TryParse would read "9" as nine days.</summary>
    private static readonly string[] TimeFormats = { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };

    private static bool TryTime(string s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s.Trim(), TimeFormats, CultureInfo.InvariantCulture, out t) && t < TimeSpan.FromDays(1);

    private static bool TryHours(string s, out double hours) =>
        (double.TryParse(s.Trim(), NumberStyles.Float, De, out hours) || double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hours))
        && hours > 0 && hours < 24 * 366;

    private static TimeEntry? ParseEntry(List<string> cols)
    {
        if (cols.Count < 3) return null;
        if (!DateTime.TryParseExact(cols[0].Trim(), DateFormats, De, DateTimeStyles.None, out var day)) return null;
        if (!TryTime(cols[2], out var start)) return null;
        var entry = new TimeEntry { Project = cols[1], Start = day + start, Description = cols.Count > 5 ? cols[5] : "" };
        double hours = 0;
        bool hasHours = cols.Count > 4 && TryHours(cols[4], out hours);
        string endText = cols.Count > 3 ? cols[3].Trim() : "";
        if (endText.Length == 0)
        {
            // Only the hours typed in: the end follows from them. Neither: the running entry.
            if (hasHours) entry.End = entry.Start + TimeSpan.FromHours(hours);
            return entry;
        }
        if (!TryTime(endText, out var end))
        {
            if (!hasHours) return null; // an end that can't be read must not turn into a running entry
            entry.End = entry.Start + TimeSpan.FromHours(hours);
            return entry;
        }
        var endAt = day + end < entry.Start ? day.AddDays(1) + end : day + end; // past midnight
        // The end has no date: an entry of a day or more only shows in the hours (same clock time, whole days more).
        if (hasHours && hours >= 24)
        {
            double extra = (entry.Start + TimeSpan.FromHours(hours) - endAt).TotalMinutes;
            double days = Math.Round(extra / 1440);
            if (days >= 1 && Math.Abs(extra - days * 1440) <= 2) endAt = endAt.AddDays(days);
        }
        entry.End = endAt;
        return entry;
    }

    private static List<string> SplitCsv(string line)
    {
        var cols = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { cols.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cols.Add(cur.ToString());
        return cols;
    }

    /// <summary>Shows the file in Explorer (writes it first, so it exists).</summary>
    [RelayCommand]
    private void ShowFile()
    {
        Write();
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{FilePath}\""); } catch { }
    }

    /// <summary>For snapshots only: entries in memory, the user's file is neither read further nor written.</summary>
    public void LoadDemo(IEnumerable<TimeEntry> entries)
    {
        _demo = true;
        _reload.Stop();
        _entries.Clear();
        _entries.AddRange(entries);
        Update();
        Changed?.Invoke();
    }
}
