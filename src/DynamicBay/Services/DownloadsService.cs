using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Browser downloads in the island, like on the iPhone. Chrome, Edge, Brave and Opera write a ".crdownload" file,
/// Firefox a ".part" file into the Downloads folder while loading: the island shows its size and speed (browsers do
/// not tell other apps the full size, so there is no percentage), and announces the finished file - ready to drag out.
/// </summary>
public sealed partial class DownloadsService : ObservableObject
{
    private static readonly string[] Partial = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, (long bytes, DateTime at)> _active = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private long _lastTotal;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private int _count;

    /// <summary>A download finished: the final file.</summary>
    public event Action<string>? Finished;

    public static bool IsPartial(string path) => Partial.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>"Bericht.pdf.crdownload" → "Bericht.pdf"; Chrome's first name "Unbestätigt 123.crdownload" → "".</summary>
    public static string FinalName(string partialPath)
    {
        string name = Path.GetFileNameWithoutExtension(partialPath);
        return name.StartsWith("Unbestätigt", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unconfirmed", StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == (_watcher is not null)) return;
        if (!enabled) { _watcher?.Dispose(); _watcher = null; _tick.Stop(); _active.Clear(); Update(); return; }
        try
        {
            _watcher = new FileSystemWatcher(KnownFolders.Downloads) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
            _watcher.Created += (_, e) => _ui.BeginInvoke(() => OnCreated(e.FullPath));
            _watcher.Renamed += (_, e) => _ui.BeginInvoke(() => OnRenamed(e.OldFullPath, e.FullPath));
            _watcher.Deleted += (_, e) => _ui.BeginInvoke(() => { if (_active.Remove(e.FullPath)) Update(); });
            _watcher.EnableRaisingEvents = true;
            _tick.Tick -= OnTick;
            _tick.Tick += OnTick;
            // Downloads already running when the island starts.
            foreach (var f in Directory.EnumerateFiles(KnownFolders.Downloads).Where(IsPartial)) OnCreated(f);
        }
        catch (Exception ex) { Log.Error("Downloads", ex); }
    }

    private void OnCreated(string path)
    {
        if (!IsPartial(path) || _active.ContainsKey(path)) return;
        _active[path] = (0, DateTime.UtcNow);
        _tick.Start();
        Update();
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        bool wasActive = _active.Remove(oldPath);
        if (IsPartial(newPath)) { _active[newPath] = (0, DateTime.UtcNow); _tick.Start(); Update(); return; } // Chrome: "Unbestätigt" → real name
        if (wasActive)
        {
            Update();
            // The partial file got its final name: the download is complete.
            if (File.Exists(newPath)) { Log.Info($"Download finished: {Path.GetFileName(newPath)}"); Finished?.Invoke(newPath); }
        }
    }

    private void OnTick(object? sender, EventArgs e) => Update();

    private void Update()
    {
        long total = 0;
        string name = "";
        foreach (var path in _active.Keys.ToList())
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { _active.Remove(path); continue; }
                total += fi.Length;
                if (name.Length == 0) name = FinalName(path);
            }
            catch { }
        }
        // Speed from the growth since the last second (smoothed a little).
        long delta = Math.Max(0, total - _lastTotal);
        _lastTotal = total;
        Count = _active.Count;
        IsActive = _active.Count > 0;
        if (!IsActive) { _tick.Stop(); Name = SizeText = SpeedText = ""; _lastTotal = 0; return; }
        Name = _active.Count > 1 ? (Loc.German ? $"{_active.Count} Downloads" : $"{_active.Count} downloads") : (name.Length > 0 ? name : (Loc.German ? "Download" : "Download"));
        SizeText = FormatSize(total);
        SpeedText = delta > 0 ? FormatSize(delta) + "/s" : "";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        IsActive = true; Count = 1; Name = "Präsentation Q3.pptx"; SizeText = "48,2 MB"; SpeedText = "6,4 MB/s";
    }
}
