using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
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
    [System.Text.Json.Serialization.JsonIgnore] public TimeSpan Duration => (End ?? DateTime.Now) - Start;
}

public sealed partial class ProjectTotal : ObservableObject
{
    public string Project { get; init; } = "";
    [ObservableProperty] private string _today = "";
    [ObservableProperty] private bool _running;
}

/// <summary>
/// Simple time tracking: start/stop per project in the island, one entry at a time (switching stops the previous one),
/// today's totals per project, CSV export per month for invoicing. Entries live in %AppData%\DynamicBay\time.json;
/// a running entry survives a restart.
/// </summary>
public sealed partial class TimeTrackingService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<TimeEntry> _entries = new();
    private static string FilePath => Path.Combine(AppSettings.Folder, "time.json");

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _currentProject = "";
    [ObservableProperty] private string _elapsed = "0:00";
    [ObservableProperty] private string _todayTotal = "0:00";
    public ObservableCollection<ProjectTotal> Projects { get; } = new();

    public TimeTrackingService(AppSettings settings)
    {
        _settings = settings;
        _tick.Tick += (_, _) => Update();
        _settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.TimeProjects)) RebuildProjects(); };
        Load();
        RebuildProjects();
        if (Running is not null) { IsRunning = true; CurrentProject = Running.Project; _tick.Start(); }
        Update();
    }

    private TimeEntry? Running => _entries.LastOrDefault(e => e.End is null);

    // ---------- start / stop ----------

    /// <summary>Starts the project (or stops it if it is the one running); a running other project stops first.</summary>
    [RelayCommand]
    private void Toggle(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        var running = Running;
        if (running is not null)
        {
            running.End = DateTime.Now;
            if (running.Project == project) { Stopped(); return; }
        }
        _entries.Add(new TimeEntry { Project = project, Start = DateTime.Now });
        CurrentProject = project;
        IsRunning = true;
        _tick.Start();
        Save();
        Update();
    }

    [RelayCommand]
    private void Stop()
    {
        if (Running is { } r) r.End = DateTime.Now;
        Stopped();
    }

    private void Stopped()
    {
        IsRunning = false;
        CurrentProject = "";
        _tick.Stop();
        Save();
        Update();
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

    private void Update()
    {
        var today = DateTime.Today;
        TimeSpan Sum(string? project) => _entries
            .Where(e => (project is null || e.Project == project) && (e.End ?? DateTime.Now) > today)
            .Aggregate(TimeSpan.Zero, (t, e) => t + ((e.End ?? DateTime.Now) - (e.Start < today ? today : e.Start)));
        foreach (var p in Projects)
        {
            p.Today = Format(Sum(p.Project));
            p.Running = IsRunning && p.Project == CurrentProject;
        }
        TodayTotal = Format(Sum(null));
        Elapsed = Running is { } r ? Format(r.Duration, seconds: true) : "0:00";
    }

    private static string Format(TimeSpan t, bool seconds = false) =>
        seconds ? (t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}")
                : $"{(int)t.TotalHours}:{t.Minutes:00}";

    // ---------- storage and export ----------

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _entries.AddRange(JsonSerializer.Deserialize<List<TimeEntry>>(File.ReadAllText(FilePath)) ?? new());
        }
        catch (Exception ex) { Log.Error("TimeTracking load", ex); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex) { Log.Error("TimeTracking save", ex); }
    }

    /// <summary>
    /// The current month as CSV (semicolons and a BOM so Excel opens it with umlauts and columns right) in Documents,
    /// then Explorer shows it.
    /// </summary>
    [RelayCommand]
    public void ExportMonth()
    {
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Zeiterfassung {first:yyyy-MM}.csv");
        File.WriteAllText(file, ToCsv(_entries, first, first.AddMonths(1)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }

    public static string ToCsv(IEnumerable<TimeEntry> entries, DateTime from, DateTime to)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.German ? "Datum;Projekt;Start;Ende;Stunden" : "Date;Project;Start;End;Hours");
        var culture = Loc.German ? new System.Globalization.CultureInfo("de-DE") : System.Globalization.CultureInfo.InvariantCulture;
        foreach (var e in entries.Where(e => e.Start >= from && e.Start < to).OrderBy(e => e.Start))
        {
            var end = e.End ?? DateTime.Now;
            string project = e.Project.Contains(';') || e.Project.Contains('"') ? "\"" + e.Project.Replace("\"", "\"\"") + "\"" : e.Project;
            sb.AppendLine(string.Join(";", e.Start.ToString("yyyy-MM-dd"), project, e.Start.ToString("HH:mm"), end.ToString("HH:mm"),
                (end - e.Start).TotalHours.ToString("0.00", culture)));
        }
        return sb.ToString();
    }
}
