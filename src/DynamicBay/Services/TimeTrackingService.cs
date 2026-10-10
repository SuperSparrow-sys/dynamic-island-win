using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public sealed class TimeEntry
{
    public string Project { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
}

public sealed partial class ProjectTotal : ObservableObject
{
    public string Project { get; init; } = "";
    [ObservableProperty] private string _today = "";
    [ObservableProperty] private bool _running;
}

/// <summary>
/// Time tracking into ONE file the user picks (CSV, opens in Excel): every project, every day, in quarter hours.
/// Start and end are rounded to the nearest 15 minutes (at least 15 minutes per entry). While a project runs the
/// file is rewritten every 15 minutes (the open entry has no end yet), and right away on start, switch and stop.
/// The file is the only store: it is read at start, so edits made in Excel are kept.
/// </summary>
public sealed partial class TimeTrackingService : ObservableObject
{
    public static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(5) }; // shows minutes only: a few seconds late at most
    private readonly DispatcherTimer _write = new() { Interval = Quarter };
    private readonly List<TimeEntry> _entries = new();
    private DateTime _runningSince; // exact start, for the live display (the file has the rounded one)

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _currentProject = "";
    [ObservableProperty] private string _elapsed = "0:00";
    [ObservableProperty] private string _todayTotal = "0:00";
    [ObservableProperty] private string _status = "";
    public ObservableCollection<ProjectTotal> Projects { get; } = new();

    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Zeiterfassung.csv");
    public string FilePath => string.IsNullOrWhiteSpace(_settings.TimeTrackingFile) ? DefaultFile : _settings.TimeTrackingFile;

    public TimeTrackingService(AppSettings settings)
    {
        _settings = settings;
        _tick.Tick += (_, _) => Update();
        _write.Tick += (_, _) => Write();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.TimeProjects)) RebuildProjects();
            if (e.PropertyName == nameof(AppSettings.TimeTrackingFile)) { Write(); }
        };
        Load();
        RebuildProjects();
        if (Running is { } r) { IsRunning = true; CurrentProject = r.Project; _runningSince = r.Start; _tick.Start(); _write.Start(); }
        Update();
    }

    private TimeEntry? Running => _entries.LastOrDefault(e => e.End is null);

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
        CloseRunning();
        if (same) { Stopped(); return; }
        var now = DateTime.Now;
        _entries.Add(new TimeEntry { Project = project, Start = RoundQuarter(now) });
        _runningSince = now;
        CurrentProject = project;
        IsRunning = true;
        _tick.Start();
        _write.Stop(); _write.Start();
        Write();
        Update();
    }

    [RelayCommand]
    private void Stop()
    {
        CloseRunning();
        Stopped();
    }

    /// <summary>Ends the running entry at the nearest quarter hour, at least one quarter after its start.</summary>
    private void CloseRunning()
    {
        if (Running is not { } r) return;
        var end = RoundQuarter(DateTime.Now);
        r.End = end - r.Start < Quarter ? r.Start + Quarter : end;
    }

    private void Stopped()
    {
        IsRunning = false;
        CurrentProject = "";
        _tick.Stop();
        _write.Stop();
        Write();
        Update();
    }

    /// <summary>A new project from the island's "+" (or the settings).</summary>
    public void AddProject(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _settings.TimeProjects.Contains(name)) return;
        _settings.TimeProjects.Add(name);
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
        TimeSpan Sum(string? project) => _entries
            .Where(e => (project is null || e.Project == project) && e.Start.Date == today)
            .Aggregate(TimeSpan.Zero, (t, e) => t + Booked(e));
        foreach (var p in Projects)
        {
            p.Today = Format(Sum(p.Project));
            p.Running = IsRunning && p.Project == CurrentProject;
        }
        TodayTotal = Format(Sum(null));
        Elapsed = IsRunning ? FormatLive(DateTime.Now - _runningSince) : "0:00";
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";
    /// <summary>Hours and minutes only (0:25, 1:05) - running seconds were too restless.</summary>
    private static string FormatLive(TimeSpan t) => Format(t);

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

    /// <summary>Datum;Projekt;Start;Ende;Stunden - semicolons and a BOM so Excel shows columns and umlauts right.</summary>
    public static string ToCsv(IEnumerable<TimeEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Datum;Projekt;Start;Ende;Stunden");
        foreach (var e in entries.OrderBy(e => e.Start))
        {
            string project = e.Project.IndexOfAny(new[] { ';', '"' }) >= 0 ? "\"" + e.Project.Replace("\"", "\"\"") + "\"" : e.Project;
            string end = e.End?.ToString("HH:mm") ?? "";
            string hours = e.End is { } en ? Math.Max(0.25, (en - e.Start).TotalHours).ToString("0.00", De) : "";
            sb.AppendLine(string.Join(";", e.Start.ToString("dd.MM.yyyy"), project, e.Start.ToString("HH:mm"), end, hours));
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
            var entry = new TimeEntry { Project = cols[1], Start = day + start };
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
}
