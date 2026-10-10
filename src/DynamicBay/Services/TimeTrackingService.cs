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
    private bool _demo;

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
    /// <summary>Back at the PC after a while (locked) with a project running: (away from, back at).</summary>
    public event Action<DateTime, DateTime>? CameBack;

    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Zeiterfassung.csv");
    public string FilePath => string.IsNullOrWhiteSpace(_settings.TimeTrackingFile) ? DefaultFile : _settings.TimeTrackingFile;

    public TimeTrackingService(AppSettings settings)
    {
        _settings = settings;
        _tick.Tick += (_, _) => { Update(); CheckReminders(); };
        _write.Tick += (_, _) => Write();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.TimeProjects)) RebuildProjects();
            if (e.PropertyName == nameof(AppSettings.TimeTrackingFile)) { Write(); }
        };
        Load();
        RebuildProjects();
        if (Running is { } r) { IsRunning = true; CurrentProject = r.Project; _runningSince = r.Start; _lastReminder = DateTime.Now; _tick.Start(); _write.Start(); }
        Update();
    }

    /// <summary>Lock and unlock: a long break can be taken off the running project.</summary>
    public void WatchSession()
    {
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect) _lockedAt = DateTime.Now;
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect && _lockedAt is { } at)
            {
                _lockedAt = null;
                var now = DateTime.Now;
                if (IsRunning && now - at >= Away && Running is { } r && r.Start < at)
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => CameBack?.Invoke(at, now));
            }
        };
    }

    private TimeEntry? Running => _entries.LastOrDefault(e => e.End is null);
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
        // Once in the evening (from 19:00): forgot to stop?
        if (now.Hour >= 19 && _eveningAsked.Date != now.Date) { _eveningAsked = now; Reminder?.Invoke(TimeReminder.Evening, running); }
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
        if (entry.IsRunning) CurrentProject = entry.Project;
        Saved();
    }

    public void DeleteEntry(TimeEntry entry)
    {
        if (!_entries.Contains(entry)) return;
        if (entry.IsRunning)
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

    public TimeSpan Total(DateTime from, DateTime to, string? project = null) => _entries
        .Where(e => e.Start >= from && e.Start < to && (project is null || e.Project == project))
        .Aggregate(TimeSpan.Zero, (t, e) => t + Booked(e));

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

    /// <summary>Booked time of an entry: rounded quarters; a running one counts up to the next quarter so far.</summary>
    private static TimeSpan Booked(TimeEntry e)
    {
        var end = e.End ?? RoundQuarter(DateTime.Now);
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
        try
        {
            if (!File.Exists(FilePath)) return;
            _entries.AddRange(Parse(File.ReadAllLines(FilePath, Encoding.UTF8)));
        }
        catch (Exception ex) { Log.Error("TimeTracking load", ex); Status = ex.Message; }
    }

    /// <summary>Writes the whole file (sorted). If Excel has it open, it tries again on the next change or quarter.</summary>
    private void Write()
    {
        if (_demo) return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, ToCsv(_entries), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = "";
        }
        catch (IOException)
        {
            Status = Loc.German ? "Datei ist geöffnet (z. B. in Excel) – wird später geschrieben" : "File is open (e.g. in Excel) - will be written later";
        }
        catch (Exception ex) { Log.Error("TimeTracking write", ex); Status = ex.Message; }
    }

    /// <summary>Datum;Projekt;Start;Ende;Stunden;Beschreibung - semicolons and a BOM so Excel shows columns and umlauts right.</summary>
    public static string ToCsv(IEnumerable<TimeEntry> entries)
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
        return sb.ToString();
    }

    public static List<TimeEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<TimeEntry>();
        foreach (var line in lines.Skip(1))
        {
            var cols = SplitCsv(line);
            if (cols.Count < 3) continue;
            if (!DateTime.TryParseExact(cols[0], new[] { "dd.MM.yyyy", "yyyy-MM-dd" }, De, DateTimeStyles.None, out var day)) continue;
            if (!TimeSpan.TryParse(cols[2], De, out var start)) continue;
            var entry = new TimeEntry { Project = cols[1], Start = day + start, Description = cols.Count > 5 ? cols[5] : "" };
            if (cols.Count > 3 && TimeSpan.TryParse(cols[3], De, out var end))
                entry.End = day + end < entry.Start ? day.AddDays(1) + end : day + end; // past midnight
            list.Add(entry);
        }
        return list;
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
        _entries.Clear();
        _entries.AddRange(entries);
        Update();
        Changed?.Invoke();
    }
}
