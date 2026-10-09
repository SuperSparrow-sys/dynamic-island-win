using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using DynamicBay.Core;

namespace DynamicBay.Services.Scripting;

/// <summary>
/// Runs the user's script widgets: once per needed size (card and/or the one-line Mini), again when the script asks
/// (refreshAfterDate, 1 min .. 6 h) and right after the file is saved, so editing feels live. Runs happen on a
/// background thread, one at a time per script.
/// </summary>
public sealed class ScriptWidgetsService
{
    public static ScriptWidgetsService? Instance { get; private set; }

    public static string Folder => Path.Combine(AppSettings.Folder, "scripts");
    public static string DataFolder(ScriptWidgetConfig c) => Path.Combine(Folder, "data", c.Id);
    public static string PathOf(ScriptWidgetConfig c) => Path.IsPathRooted(c.File) ? c.File : Path.Combine(Folder, c.File);

    public const string Small = "small", Large = "medium", Mini = "accessoryInline";
    public static string FamilyOf(ScriptSize s) => s == ScriptSize.Small ? Small : Large;

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Dictionary<(string id, string family), ScriptResult> _results = new();
    private readonly HashSet<(string id, string family)> _running = new();
    /// <summary>One run at a time per script: the sizes share its data folder (cache files).</summary>
    private readonly Dictionary<string, object> _scriptLocks = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _fileDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scripts that currently show their Mini line in the compact island.</summary>
    public ObservableCollection<string> CompactIds { get; } = new();

    /// <summary>A result arrived (script id).</summary>
    public event Action<string>? Updated;
    /// <summary>The Mini lines changed (content or set): the compact island re-measures.</summary>
    public event Action? CompactChanged;

    public ScriptWidgetsService(AppSettings settings)
    {
        _settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        Instance = this;
    }

    public void Start()
    {
        Directory.CreateDirectory(Folder);
        _timer.Tick += (_, _) => RunDue();
        _timer.Start();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.ScriptWidgets) or nameof(AppSettings.HomeWidgets)) { UpdateCompactIds(); RunDue(); }
        };
        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.js") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            FileSystemEventHandler changed = (_, e) => _ui.BeginInvoke(() => { _changedFiles.Add(e.Name ?? ""); _fileDebounce.Stop(); _fileDebounce.Start(); });
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
        }
        catch (Exception ex) { Log.Error("ScriptWatch", ex); }
        _fileDebounce.Tick += (_, _) =>
        {
            _fileDebounce.Stop();
            foreach (var c in _settings.ScriptWidgets.Where(c => _changedFiles.Contains(c.File)).ToList()) RunNow(c);
            _changedFiles.Clear();
        };
        UpdateCompactIds();
        RunDue();
    }

    public ScriptWidgetConfig? ConfigOf(string id) => _settings.ScriptWidgets.FirstOrDefault(c => c.Id == id);

    public ScriptResult? Get(string id, string family) => _results.TryGetValue((id, family), out var r) ? r : null;

    /// <summary>Short status for the settings: "OK · 21:05" or the error.</summary>
    public string StatusOf(ScriptWidgetConfig c)
    {
        var all = _results.Where(r => r.Key.id == c.Id).Select(r => r.Value).OrderByDescending(r => r.RanAt).ToList();
        if (all.Count == 0) return Loc.German ? "Noch nicht ausgeführt" : "Not run yet";
        var err = all.FirstOrDefault(r => r.Error is not null);
        if (err is not null) return (Loc.German ? "Fehler: " : "Error: ") + err.Error;
        return (Loc.German ? "OK · aktualisiert " : "OK · updated ") + all[0].RanAt.ToString("HH:mm");
    }

    private IEnumerable<(ScriptWidgetConfig c, string family)> Needed()
    {
        foreach (var c in _settings.ScriptWidgets)
        {
            if (_settings.HomeWidgets.Contains(c.HomeId)) yield return (c, FamilyOf(c.Size));
            if (c.ShowInCompact) yield return (c, Mini);
        }
    }

    private void RunDue()
    {
        var now = DateTime.Now;
        foreach (var (c, family) in Needed())
        {
            var r = Get(c.Id, family);
            if (r is null || r.NextRun <= now) Run(c, family);
        }
    }

    /// <summary>Runs every needed size of this script now (settings button, file saved).</summary>
    public void RunNow(ScriptWidgetConfig c)
    {
        foreach (var (cfg, family) in Needed().Where(n => n.c.Id == c.Id)) Run(cfg, family);
        if (!Needed().Any(n => n.c.Id == c.Id)) Run(c, FamilyOf(c.Size)); // not shown anywhere yet: still test it
    }

    private void Run(ScriptWidgetConfig c, string family)
    {
        var key = (c.Id, family);
        if (!_running.Add(key)) return;
        var path = PathOf(c);
        var data = DataFolder(c);
        bool network = c.AllowNetwork;
        string name = c.Name;
        if (!_scriptLocks.TryGetValue(c.Id, out var gate)) _scriptLocks[c.Id] = gate = new object();
        Task.Run(() =>
        {
            ScriptResult result;
            lock (gate)
            try
            {
                var code = File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException("Skriptdatei fehlt: " + Path.GetFileName(path));
                var sw = Stopwatch.StartNew();
                result = ScriptEngine.Run(code, family, data, network, name, m => Log.Info($"[Script {name}] {m}"));
                Log.Info($"Script '{name}' ({family}) ran in {sw.ElapsedMilliseconds} ms{(result.Error is null ? "" : ": " + result.Error)}");
            }
            catch (Exception ex) { result = new ScriptResult(null, ex.Message, DateTime.Now.AddMinutes(5), DateTime.Now); }
            _ui.BeginInvoke(() =>
            {
                _running.Remove(key);
                _results[key] = result;
                Updated?.Invoke(c.Id);
                if (family == Mini) { UpdateCompactIds(); CompactChanged?.Invoke(); }
            });
        });
    }

    private void UpdateCompactIds()
    {
        var want = _settings.ScriptWidgets.Where(c => c.ShowInCompact && Get(c.Id, Mini)?.Widget is not null).Select(c => c.Id).ToList();
        if (want.SequenceEqual(CompactIds)) return;
        CompactIds.Clear();
        foreach (var id in want) CompactIds.Add(id);
        CompactChanged?.Invoke();
    }

    /// <summary>Copies a script file into the scripts folder and registers it.</summary>
    public ScriptWidgetConfig Import(string sourcePath)
    {
        Directory.CreateDirectory(Folder);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var file = UniqueFile(name);
        File.Copy(sourcePath, Path.Combine(Folder, file));
        return Register(name, file);
    }

    /// <summary>Creates a new script from the template and registers it.</summary>
    public ScriptWidgetConfig CreateNew()
    {
        Directory.CreateDirectory(Folder);
        var name = Loc.German ? "Mein Widget" : "My widget";
        var file = UniqueFile(name);
        File.WriteAllText(Path.Combine(Folder, file), Template, new System.Text.UTF8Encoding(false));
        return Register(name, file);
    }

    private ScriptWidgetConfig Register(string name, string file)
    {
        var c = new ScriptWidgetConfig { Name = name, File = file };
        _settings.ScriptWidgets.Add(c);
        if (!_settings.HomeWidgets.Contains(c.HomeId)) _settings.HomeWidgets.Add(c.HomeId);
        _settings.SaveSoon();
        return c;
    }

    public void Remove(ScriptWidgetConfig c)
    {
        _settings.HomeWidgets.Remove(c.HomeId);
        _settings.ScriptWidgets.Remove(c);
        foreach (var k in _results.Keys.Where(k => k.id == c.Id).ToList()) _results.Remove(k);
        UpdateCompactIds();
        try { File.Move(PathOf(c), PathOf(c) + ".removed", true); } catch { }
        _settings.SaveSoon();
    }

    private static string UniqueFile(string name)
    {
        var safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        if (safe.Length == 0) safe = "Widget";
        var file = safe + ".js";
        for (int i = 2; File.Exists(Path.Combine(Folder, file)); i++) file = $"{safe} {i}.js";
        return file;
    }

    /// <summary>Opens the script in Notepad - never via the .js file association (that would run it in Windows Script Host).</summary>
    public static void Edit(ScriptWidgetConfig c)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { PathOf(c) }, UseShellExecute = false }); }
        catch (Exception ex) { Log.Error("ScriptEdit", ex); }
    }

    private const string Template = """
        // Eigenes DynamicBay-Widget (JavaScript, wie Scriptable auf dem iPhone).
        // Speichern genügt - die Insel aktualisiert das Widget sofort. Anleitung: docs/SCRIPTS.md
        // config.widgetFamily: "medium" (Groß), "small" (Klein), "accessoryInline" (Mini in der Insel)

        const w = new ListWidget();
        w.backgroundColor = new Color("#141414");

        if (config.widgetFamily === "accessoryInline") {
          const t = w.addText("Hallo");
          t.font = Font.semiboldSystemFont(12);
          t.textColor = new Color("#EDEDED");
        } else {
          w.setPadding(14, 16, 14, 16);
          const title = w.addText("Mein Widget");
          title.font = Font.semiboldSystemFont(12);
          title.textColor = new Color("#8A8A8A");
          w.addSpacer();
          const big = w.addText(new Date().toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" }));
          big.font = Font.boldRoundedSystemFont(30);
          big.textColor = new Color("#EDEDED");
          w.addSpacer();
        }

        w.refreshAfterDate = new Date(Date.now() + 15 * 60 * 1000);
        Script.setWidget(w);
        Script.complete();
        """;
}
