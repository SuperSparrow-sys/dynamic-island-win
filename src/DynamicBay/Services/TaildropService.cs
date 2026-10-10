using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Files sent from any device with Tailscale (iPhone, iPad, Android, Mac, Linux, Windows) via Taildrop.
/// Tailscale reports incoming files on its event stream (<c>tailscale debug watch-ipn</c>, no admin rights needed);
/// its tray app then moves them into Downloads (no setting for another folder on Windows). DynamicBay matches those
/// arrivals by name - so ordinary browser downloads are never mistaken for them - optionally moves them to a folder
/// of the user's choice, and reports them so they show up in the island and the shelf.
/// </summary>
public sealed class TaildropService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private Process? _watch;
    private FileSystemWatcher? _downloads;
    private bool _running;
    private int _restarts;

    /// <summary>Names Tailscale finished receiving, waiting for the file to show up in Downloads.</summary>
    private readonly Dictionary<string, DateTime> _expected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _batch = new();
    private readonly DispatcherTimer _batchTimer;

    /// <summary>Files that arrived (final paths, after moving), grouped when several come at once.</summary>
    public event Action<IReadOnlyList<string>>? Received;

    public TaildropService(AppSettings settings)
    {
        _settings = settings;
        _batchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Normal, (_, _) => FlushBatch(), _ui);
        _batchTimer.Stop();
    }

    public static string DownloadsFolder => KnownFolders.Downloads;

    private static string? TailscaleExe()
    {
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
        })
            if (File.Exists(p)) return p;
        return null;
    }

    public static bool IsTailscaleInstalled => TailscaleExe() is not null;

    public void SetEnabled(bool enabled)
    {
        if (enabled) Start(); else Stop();
    }

    private void Start()
    {
        if (_running) return;
        var exe = TailscaleExe();
        if (exe is null) { Log.Info("Taildrop: Tailscale not installed"); return; }
        _running = true;
        try
        {
            _downloads = new FileSystemWatcher(DownloadsFolder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
            _downloads.Created += (_, e) => OnDownloadsFile(e.FullPath);
            _downloads.Renamed += (_, e) => OnDownloadsFile(e.FullPath);
            _downloads.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Error("Taildrop", ex); }
        StartWatch(exe);
    }

    private void Stop()
    {
        _running = false;
        try { _watch?.Kill(); } catch { }
        _watch?.Dispose();
        _watch = null;
        _downloads?.Dispose();
        _downloads = null;
    }

    public void Dispose() => Stop();

    // ---------- Tailscale event stream ----------

    private void StartWatch(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "debug watch-ipn --initial=false --peer-changes=false --peer-patches=false")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Exited += (_, _) => _ui.BeginInvoke(() => OnWatchExited(exe));
            p.Start();
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            _watch = p;
            var reader = p.StandardOutput;
            new Thread(() => ReadStream(reader)) { IsBackground = true, Name = "TaildropWatch" }.Start();
            Log.Info("Taildrop: watching Tailscale");
        }
        catch (Exception ex) { Log.Error("Taildrop", ex); }
    }

    private async void OnWatchExited(string exe)
    {
        if (!_running) return;
        // Tailscale restarted or not running yet: try again, slower each time (max. once a minute).
        _restarts++;
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * _restarts)));
        if (_running) StartWatch(exe);
    }

    /// <summary>The stream is a sequence of pretty-printed JSON objects: split them by brace depth (outside strings).</summary>
    private void ReadStream(StreamReader reader)
    {
        var sb = new StringBuilder();
        int depth = 0;
        bool inString = false, escape = false;
        try
        {
            int ch;
            while ((ch = reader.Read()) >= 0)
            {
                char c = (char)ch;
                if (depth > 0 || c == '{') sb.Append(c);
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    var json = sb.ToString();
                    sb.Clear();
                    if (json.Contains("\"IncomingFiles\"", StringComparison.Ordinal) || json.Contains("\"FilesWaiting\"", StringComparison.Ordinal)) HandleNotify(json);
                }
            }
        }
        catch (Exception ex) { Log.Info($"Taildrop stream ended: {ex.Message}"); }
    }

    private void HandleNotify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // "Files are waiting": the tray app is about to move them into Downloads (safety net if names don't match).
            if (doc.RootElement.TryGetProperty("FilesWaiting", out var waiting) && waiting.ValueKind != JsonValueKind.Null)
                _ui.BeginInvoke(() => { _waitingUntil = DateTime.UtcNow.AddSeconds(30); Log.Info("Taildrop: files waiting"); });
            if (!doc.RootElement.TryGetProperty("IncomingFiles", out var files) || files.ValueKind != JsonValueKind.Array) return;
            foreach (var f in files.EnumerateArray())
            {
                string name = f.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                bool done = f.TryGetProperty("Done", out var d) && d.ValueKind == JsonValueKind.True;
                if (name.Length == 0 || !done) continue;
                _ui.BeginInvoke(() => Expect(name));
            }
        }
        catch (Exception ex) { Log.Info($"Taildrop: unreadable event ({ex.Message})"); }
    }

    // ---------- matching the files in Downloads ----------

    private void Expect(string name)
    {
        if (_expected.ContainsKey(name)) return;
        Log.Info($"Taildrop: received {name}");
        _expected[name] = DateTime.UtcNow;
        _restarts = 0;
        // The tray app may already have moved it.
        foreach (var path in Candidates(name)) OnDownloadsFile(path);
    }

    private static IEnumerable<string> Candidates(string name)
    {
        string dir = DownloadsFolder, stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        IEnumerable<string> found;
        try { found = Directory.EnumerateFiles(dir, stem + "*" + ext).ToList(); } catch { yield break; }
        foreach (var f in found)
            if ((DateTime.Now - File.GetCreationTime(f)).TotalSeconds < 60) yield return f;
    }

    /// <summary>"IMG_1.jpeg" also matches "IMG_1 (2).jpeg" (Tailscale renames on conflicts).</summary>
    private string? MatchExpected(string fileName)
    {
        foreach (var (name, at) in _expected.ToList())
        {
            if ((DateTime.UtcNow - at).TotalMinutes > 3) { _expected.Remove(name); continue; }
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            if (fileName.Equals(name, StringComparison.OrdinalIgnoreCase)) return name;
            if (fileName.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(")" + ext, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    private DateTime _waitingUntil;
    private static readonly string[] TempExtensions = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

    private void OnDownloadsFile(string path)
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(() => OnDownloadsFile(path)); return; }
        if (_announced.Contains(path)) return;
        var key = MatchExpected(Path.GetFileName(path));
        if (key is not null) _expected.Remove(key);
        // Safety net: right after "files waiting", a new file without the browser's download mark (Zone.Identifier).
        else if (!(DateTime.UtcNow < _waitingUntil && File.Exists(path) && !HasDownloadMark(path)
                   && !TempExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))) return;
        _announced.Add(path);
        if (_announced.Count > 200) _announced.Clear();
        _ = ArriveAsync(path);
    }

    private async Task ArriveAsync(string path)
    {
        // Wait until the tray app has finished writing/moving it.
        for (int i = 0; i < 40 && !IsReady(path); i++) await Task.Delay(150);
        string final = path;
        string folder = _settings.TaildropFolder;
        if (!string.IsNullOrWhiteSpace(folder) && !PathsEqual(folder, DownloadsFolder))
        {
            try
            {
                Directory.CreateDirectory(folder);
                final = UniquePath(Path.Combine(folder, Path.GetFileName(path)));
                File.Move(path, final);
            }
            catch (Exception ex) { Log.Error("Taildrop move", ex); final = path; }
        }
        _batch.Add(final);
        _batchTimer.Stop();
        _batchTimer.Start();
    }

    private void FlushBatch()
    {
        _batchTimer.Stop();
        if (_batch.Count == 0) return;
        var files = _batch.ToList();
        _batch.Clear();
        Received?.Invoke(files);
    }

    private static bool HasDownloadMark(string path)
    {
        try { return File.Exists(path + ":Zone.Identifier"); } catch { return false; }
    }

    private static bool IsReady(string path)
    {
        try { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch { return false; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
