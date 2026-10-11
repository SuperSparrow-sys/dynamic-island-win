using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public enum ClaudeState { Idle, Working, Waiting }

public sealed partial class ClaudeSession : ObservableObject
{
    public string Id { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string Project => Path.GetFileName(Cwd.TrimEnd('\\')) is { Length: > 0 } p ? p : Cwd;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _lastPrompt = "";
    [ObservableProperty] private DateTime _updated;
    [ObservableProperty] private ClaudeState _state;
    [ObservableProperty] private int _activeAgents;
    /// <summary>claude.ai/code link when the session runs with Remote Control (open from any device).</summary>
    public string? RemoteUrl { get; set; }
    public string Machine { get; init; } = Environment.MachineName;
    public bool IsLocal => string.Equals(Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    public bool HasRemote => !string.IsNullOrEmpty(RemoteUrl);
    public string Where => IsLocal ? Project : $"{Project} · {Machine}";
    public string Age => (DateTime.Now - Updated) switch
    {
        { TotalMinutes: < 1 } => Loc.German ? "jetzt" : "now",
        { TotalHours: < 1 } t => Loc.German ? $"vor {(int)t.TotalMinutes} Min." : $"{(int)t.TotalMinutes} min ago",
        { TotalDays: < 1 } => Updated.ToString("HH:mm"),
        _ => Updated.ToString("dd.MM."),
    };
    public bool IsWorking => State == ClaudeState.Working;
    public bool IsWaiting => State == ClaudeState.Waiting;
    partial void OnStateChanged(ClaudeState value) { OnPropertyChanged(nameof(IsWorking)); OnPropertyChanged(nameof(IsWaiting)); }
}

/// <summary>
/// Claude Code integration: lists local sessions (~/.claude/projects) and receives live agent status from
/// Claude Code hooks (UserPromptSubmit / Notification / Stop) posted to a loopback listener.
/// Claude.ai / Claude Desktop chats are cloud data without a public API and are not read.
/// </summary>
public sealed partial class ClaudeService : ObservableObject
{
    public const int HookPort = 43822;
    public static string HookUrl => $"http://127.0.0.1:{HookPort}/claude/";
    private const string HookMarker = "127.0.0.1:43822/claude";
    private static readonly string ClaudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private readonly DispatcherTimer _scan = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, (ClaudeState State, DateTime At)> _hookState = new();
    private const int MaxHookStates = 200;
    private const int MaxHookBody = 1024 * 1024; // PreToolUse carries the tool input (a whole file for Write)
    private HttpListener? _listener;

    private readonly AppSettings _settings;
    public ObservableCollection<ClaudeSession> Sessions { get; } = new();

    public ClaudeService(AppSettings settings) => _settings = settings;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _anyWorking;
    [ObservableProperty] private bool _anyWaiting;
    [ObservableProperty] private string _workingText = "";
    [ObservableProperty] private bool _hooksInstalled;

    public event Action<ClaudeSession>? Finished;
    public event Action<ClaudeSession, string>? NeedsInput;

    private bool _started;

    /// <summary>Starts or stops the module (the "Claude" switch in settings). When off nothing is read or received.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled) Start();
        else
        {
            _scan.Stop();
            _watcher?.Dispose();
            _watcher = null;
            StopListener();
            Sessions.Clear();
            AnyWorking = AnyWaiting = false;
            _started = false;
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        IsAvailable = Directory.Exists(Path.Combine(ClaudeDir, "projects"));
        HooksInstalled = AreHooksInstalled();
        _scan.Tick -= OnScanTick;
        _scan.Tick += OnScanTick;
        _scan.Start();
        WatchTranscripts();
        Scan();
        // The loopback listener only runs while the hooks that post to it are installed.
        if (HooksInstalled) StartListener();
    }

    // Transcripts are only searched when one changed (file watcher), while a session works or waits (its state
    // depends on the time since the last write), with a synced folder, or once a minute as a safety net.
    private FileSystemWatcher? _watcher;
    private volatile bool _dirty = true;
    private DateTime _lastScan;

    private void WatchTranscripts()
    {
        try
        {
            var root = Path.Combine(ClaudeDir, "projects");
            if (!Directory.Exists(root)) return;
            _watcher = new FileSystemWatcher(root, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler mark = (_, _) => _dirty = true;
            _watcher.Changed += mark;
            _watcher.Created += mark;
            _watcher.Deleted += mark;
            _watcher.Renamed += (_, _) => _dirty = true;
            _watcher.Error += (_, _) => _dirty = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Error("ClaudeWatch", ex); _watcher = null; }
    }

    private void OnScanTick(object? sender, EventArgs e)
    {
        bool busy = Sessions.Any(x => x.State != ClaudeState.Idle || x.ActiveAgents > 0);
        bool shared = !string.IsNullOrWhiteSpace(_settings.ClaudeShareFolder);
        if (_watcher is not null && !_dirty && !busy && !shared && DateTime.UtcNow - _lastScan < TimeSpan.FromMinutes(1)) return;
        Scan();
    }

    // ---------- sessions from transcripts ----------

    private bool _scanning;

    /// <summary>Kicks off a scan; transcript reading happens off the UI thread (files can be tens of MB).</summary>
    private void Scan() => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        _dirty = false;
        _lastScan = DateTime.UtcNow;
        try
        {
            var known = Sessions.Where(x => x.IsLocal).ToDictionary(x => x.Id, x => x.Updated);
            var results = await Task.Run(() => ReadSessions(known));
            if (results is null || !_started) return; // switched off while the sessions were read

            var seen = new HashSet<string>();
            foreach (var r in results)
            {
                seen.Add(Key(Environment.MachineName, r.Id));
                var s = Sessions.FirstOrDefault(x => x.Id == r.Id && x.IsLocal);
                if (r.Meta is { } meta)
                {
                    if (s is null)
                    {
                        s = new ClaudeSession { Id = r.Id, Cwd = meta.cwd };
                        Sessions.Add(s);
                    }
                    s.Title = meta.title.Length > 0 ? meta.title : (meta.prompt.Length > 0 ? meta.prompt : (Loc.German ? "Neue Sitzung" : "New session"));
                    s.LastPrompt = meta.prompt;
                    s.Updated = r.Updated;
                    // Remote Control: bridge id "cse_X" is reachable at claude.ai/code/session_X
                    if (meta.bridge.StartsWith("cse_", StringComparison.Ordinal)) s.RemoteUrl = SafeRemoteUrl("https://claude.ai/code/session_" + meta.bridge[4..]);
                }
                if (s is null) continue;
                s.ActiveAgents = r.Agents;
                // Without hooks, "recently written" approximates "working".
                s.State = _hookState.TryGetValue(r.Id, out var hs) ? hs.State
                    : (DateTime.Now - r.Updated).TotalSeconds < 20 ? ClaudeState.Working : ClaudeState.Idle;
            }
            foreach (var r in await SyncSharedAsync()) seen.Add(Key(r.Machine, r.Id));
            if (!_started) return; // switched off while the shared folder was read
            foreach (var old in Sessions.Where(x => !seen.Contains(Key(x.Machine, x.Id))).ToList()) Sessions.Remove(old);
            // Newest first
            var ordered = Sessions.OrderByDescending(x => x.Updated).ToList();
            for (int i = 0; i < ordered.Count; i++)
                if (Sessions.IndexOf(ordered[i]) != i) Sessions.Move(Sessions.IndexOf(ordered[i]), i);
            UpdateSummary();
        }
        finally { _scanning = false; }
    }

    private sealed record ScanResult(string Id, DateTime Updated, int Agents, (string cwd, string title, string prompt, string bridge)? Meta);

    /// <summary>Background part: newest transcripts, metadata only for files that changed since the last scan.</summary>
    private static List<ScanResult>? ReadSessions(Dictionary<string, DateTime> known)
    {
        var root = Path.Combine(ClaudeDir, "projects");
        if (!Directory.Exists(root)) return null;
        try
        {
            var files = Directory.EnumerateDirectories(root)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl").Select(f => new FileInfo(f)))
                .OrderByDescending(f => f.LastWriteTime).Take(6).ToList();
            var list = new List<ScanResult>();
            foreach (var f in files)
            {
                string id = Path.GetFileNameWithoutExtension(f.Name);
                bool changed = !known.TryGetValue(id, out var upd) || upd != f.LastWriteTime;
                var sub = Path.Combine(f.DirectoryName!, id, "subagents");
                int agents = Directory.Exists(sub)
                    ? Directory.EnumerateFiles(sub, "*.jsonl").Count(p => (DateTime.Now - File.GetLastWriteTime(p)).TotalMinutes < 2) : 0;
                list.Add(new ScanResult(id, f.LastWriteTime, agents, changed ? ReadMeta(f.FullName) : null));
            }
            return list;
        }
        catch { return null; }
    }

    private static string Key(string machine, string id) => machine.ToUpperInvariant() + "|" + id;

    // ---------- sessions of other PCs via a synced folder ----------

    private DateTime _lastShareWrite;
    private string? _lastShareContent;

    /// <summary>A session another PC wrote into the synced folder (already checked).</summary>
    private sealed record SharedSession(string Machine, string Id, string Title, string Cwd, string? Remote, DateTime Updated, ClaudeState State);

    /// <summary>
    /// Writes this PC's session list to {share}\DynamicBay\claude\{PC}.json and merges the lists other PCs wrote
    /// (only metadata: title, project, status, Remote Control link). Returns the merged remote sessions.
    /// The files are read and written in the background; the collection is only changed here, on the UI thread.
    /// </summary>
    private async Task<List<ClaudeSession>> SyncSharedAsync()
    {
        var result = new List<ClaudeSession>();
        string folder = _settings.ClaudeShareFolder;
        if (string.IsNullOrWhiteSpace(folder)) return result;
        string mine = new JsonArray(Sessions.Where(s => s.IsLocal).Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id, ["title"] = s.Title, ["cwd"] = s.Cwd, ["updated"] = s.Updated.ToUniversalTime().ToString("o"),
            ["state"] = s.State.ToString(), ["remote"] = s.RemoteUrl,
        }).ToArray()).ToJsonString();
        var shared = await Task.Run(() => ExchangeShared(folder, mine));
        if (!_started) return result; // switched off while the folder was read: nothing may come back into the list
        foreach (var r in shared)
        {
            var s = Sessions.FirstOrDefault(x => x.Id == r.Id && x.Machine.Equals(r.Machine, StringComparison.OrdinalIgnoreCase));
            if (s is null)
            {
                s = new ClaudeSession { Id = r.Id, Machine = r.Machine, Cwd = r.Cwd };
                Sessions.Add(s);
            }
            s.Title = r.Title;
            s.RemoteUrl = r.Remote;
            s.Updated = r.Updated;
            s.State = r.State;
            result.Add(s);
        }
        return result;
    }

    /// <summary>Background part: writes the own file when its content changed, reads (and checks) the other PCs' files.</summary>
    private List<SharedSession> ExchangeShared(string folder, string mine)
    {
        var list = new List<SharedSession>();
        if (!Directory.Exists(folder)) return list;
        string dir = Path.Combine(folder, "DynamicBay", "claude");
        try
        {
            Directory.CreateDirectory(dir);
            string own = Path.Combine(dir, Environment.MachineName + ".json");
            // At most every 20 s and only on changes; once an hour anyway, other PCs skip files older than a day.
            double since = (DateTime.Now - _lastShareWrite).TotalSeconds;
            if (since > 20 && (mine != _lastShareContent || since > 3600 || !File.Exists(own)))
            {
                File.WriteAllText(own, mine);
                _lastShareWrite = DateTime.Now;
                _lastShareContent = mine;
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                string machine = Path.GetFileNameWithoutExtension(file);
                if (machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if ((DateTime.Now - info.LastWriteTime).TotalHours > 24 || info.Length > 256 * 1024) continue;
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonArray arr) continue;
                foreach (var n in arr.Take(4))
                {
                    // Anyone with access to the folder can write these files: only well-formed ids and claude.ai links.
                    string id = n?["id"]?.ToString() ?? "";
                    if (!IsSessionId(id)) continue;
                    var updated = DateTime.TryParse(n!["updated"]?.ToString(), out var u) ? u.ToLocalTime() : info.LastWriteTime;
                    var state = Enum.TryParse<ClaudeState>(n["state"]?.ToString(), out var st) && (DateTime.Now - updated).TotalMinutes < 10 ? st : ClaudeState.Idle;
                    list.Add(new SharedSession(machine, id, Clip(n["title"]?.ToString() ?? "Claude Code", 200), Clip(n["cwd"]?.ToString() ?? "", 260),
                        SafeRemoteUrl(n["remote"]?.ToString()), updated, state));
                }
            }
        }
        catch (Exception ex) { Log.Error("ClaudeShare", ex); }
        return list;
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>Claude Code session ids are GUIDs; anything else never reaches a command line.</summary>
    internal static bool IsSessionId(string? id) => Guid.TryParseExact(id, "D", out _);

    private static readonly Regex RemoteUrlPattern = new(@"^https://claude\.ai/code/[A-Za-z0-9_\-/]+\z", RegexOptions.CultureInvariant);

    /// <summary>The link if it is a claude.ai/code address, else null (it is opened with the shell).</summary>
    internal static string? SafeRemoteUrl(string? url) => url is not null && RemoteUrlPattern.IsMatch(url) ? url : null;

    /// <summary>
    /// A working folder from a hook: only an existing folder, else "". It never reaches a shell string (resume passes
    /// arguments one by one), so only quotes and control characters are refused (folders with ampersands or semicolons stay usable).
    /// </summary>
    internal static string SafeCwd(string? cwd) =>
        !string.IsNullOrEmpty(cwd) && cwd.Length < 1024 && !cwd.Any(c => c == '"' || char.IsControl(c)) && Directory.Exists(cwd) ? cwd : "";

    // ---------- questions from other machines (Remote Control) ----------

    /// <summary>Open questions that only arrived as a Windows notification (no local hook), with arrival time.</summary>
    private readonly Dictionary<uint, DateTime> _remoteAsks = new();

    /// <summary>
    /// Does a notification mean "Claude needs you"? It must come from Claude (the app, or claude.ai in a browser) and
    /// must not just report that a task is finished.
    /// </summary>
    public static bool NeedsAnswer(string app, string? appId, string title, string body)
    {
        bool fromClaude = app.Contains("Claude", StringComparison.OrdinalIgnoreCase) || (appId?.Contains("claude", StringComparison.OrdinalIgnoreCase) ?? false)
                          || title.Contains("Claude", StringComparison.OrdinalIgnoreCase);
        if (!fromClaude) return false;
        var text = title + " " + body;
        string[] done = { "fertig", "abgeschlossen", "erledigt", "finished", "completed", "is done", "done" };
        return !done.Any(d => text.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A Claude notification asks for input (e.g. a Remote Control session on another PC): show "waiting".</summary>
    public void RemoteAsked(uint notificationId)
    {
        _remoteAsks[notificationId] = DateTime.Now;
        UpdateSummary();
    }

    /// <summary>Opened or dismissed in Windows: the question is handled.</summary>
    public void RemoteAnswered(uint notificationId)
    {
        if (_remoteAsks.Remove(notificationId)) UpdateSummary();
    }

    private void UpdateSummary()
    {
        // Questions nobody reacted to fade after 30 minutes.
        foreach (var old in _remoteAsks.Where(r => DateTime.Now - r.Value > TimeSpan.FromMinutes(30)).Select(r => r.Key).ToList()) _remoteAsks.Remove(old);
        var working = Sessions.Where(s => s.State == ClaudeState.Working).ToList();
        AnyWorking = working.Count > 0;
        AnyWaiting = Sessions.Any(s => s.State == ClaudeState.Waiting) || _remoteAsks.Count > 0;
        WorkingText = CompactText(AnyWaiting, working.Count);
    }

    /// <summary>Text next to the symbol in the compact island: none for one session (a project name would be cut off).</summary>
    public static string CompactText(bool waiting, int working) =>
        waiting ? (Loc.German ? "wartet" : "waiting")
        : working > 1 ? (Loc.German ? $"{working} aktiv" : $"{working} active")
        : "";

    /// <summary>Reads cwd from the head and the latest title / last prompt from the tail (files can be large).</summary>
    private static (string cwd, string title, string prompt, string bridge) ReadMeta(string path)
    {
        string cwd = "", title = "", prompt = "", bridge = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using (var head = new StreamReader(fs, Encoding.UTF8, true, 4096, leaveOpen: true))
            {
                for (int i = 0; i < 40 && cwd.Length == 0; i++)
                {
                    var line = head.ReadLine();
                    if (line is null) break;
                    if (line.Contains("\"cwd\"")) cwd = JsonNode.Parse(line)?["cwd"]?.ToString() ?? "";
                }
            }
            long tail = Math.Min(fs.Length, 512 * 1024);
            fs.Seek(-tail, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');
            foreach (var line in lines.Reverse())
            {
                if (title.Length == 0 && line.Contains("\"custom-title\"")) title = JsonNode.Parse(line)?["customTitle"]?.ToString() ?? "";
                if (title.Length == 0 && line.Contains("\"ai-title\"")) title = JsonNode.Parse(line)?["aiTitle"]?.ToString() ?? "";
                if (prompt.Length == 0 && line.Contains("\"last-prompt\"")) prompt = JsonNode.Parse(line)?["lastPrompt"]?.ToString() ?? "";
                if (cwd.Length == 0 && line.Contains("\"cwd\"")) try { cwd = JsonNode.Parse(line)?["cwd"]?.ToString() ?? ""; } catch { }
                if (bridge.Length == 0 && line.Contains("\"bridge-session\"")) try { bridge = JsonNode.Parse(line)?["bridgeSessionId"]?.ToString() ?? ""; } catch { }
                if (title.Length > 0 && prompt.Length > 0 && cwd.Length > 0) break;
            }
        }
        catch { }
        prompt = prompt.Replace('\n', ' ').Trim();
        if (prompt.Length > 120) prompt = prompt[..120] + "…";
        return (cwd, title, prompt, bridge);
    }

    // ---------- live status from hooks ----------

    private void StartListener()
    {
        if (_listener is not null || DevMode.On) return; // the hook port belongs to the installed DynamicBay
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(HookUrl);
            listener.Start();
            _listener = listener;
            _ = Task.Run(() => ListenLoop(listener));
        }
        catch (Exception ex) { Log.Error("ClaudeHooks", ex); }
    }

    private void StopListener()
    {
        _listener?.Close();
        _listener = null;
    }

    private async Task ListenLoop(HttpListener listener)
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                var json = await ReadHookAsync(ctx);
                if (json is null) continue;
                _ = Application.Current.Dispatcher.BeginInvoke(() => OnHook(json));
            }
            catch { }
        }
    }

    /// <summary>
    /// Only takes what curl in a hook sends: a POST without an Origin header (browsers always send one, so web pages
    /// cannot post here) and a bounded body (max. 1 MB).
    /// </summary>
    private static async Task<JsonNode?> ReadHookAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        try
        {
            if (req.Headers["Origin"] is not null || req.Headers["Sec-Fetch-Site"] is not null) { ctx.Response.StatusCode = 403; return null; }
            if (req.HttpMethod != "POST") { ctx.Response.StatusCode = 405; return null; }
            if (req.ContentLength64 > MaxHookBody) { ctx.Response.StatusCode = 413; return null; }
            using var body = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await req.InputStream.ReadAsync(chunk)) > 0)
            {
                body.Write(chunk, 0, n);
                if (body.Length > MaxHookBody) { ctx.Response.StatusCode = 413; return null; }
            }
            var json = JsonNode.Parse(Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
            ctx.Response.StatusCode = 204;
            return json;
        }
        catch { ctx.Response.StatusCode = 400; return null; }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private void OnHook(JsonNode json)
    {
        string id = json["session_id"]?.ToString() ?? "";
        string ev = json["hook_event_name"]?.ToString() ?? "";
        if (!IsSessionId(id)) return;
        var state = ev switch
        {
            "UserPromptSubmit" or "PreToolUse" or "SubagentStart" => ClaudeState.Working,
            "Notification" => ClaudeState.Waiting,
            "Stop" or "SessionEnd" => ClaudeState.Idle,
            _ => (ClaudeState?)null,
        };
        if (state is null) return;
        var previous = _hookState.TryGetValue(id, out var p) ? p.State : ClaudeState.Idle;
        RememberHook(id, state.Value);
        Scan();
        var session = Sessions.FirstOrDefault(s => s.Id == id)
                      ?? new ClaudeSession { Id = id, Cwd = SafeCwd(json["cwd"]?.ToString()), Title = "Claude Code" };
        session.State = state.Value;
        UpdateSummary();
        if (ev == "Stop" && previous == ClaudeState.Working) Finished?.Invoke(session);
        if (ev == "Notification") NeedsInput?.Invoke(session, json["message"]?.ToString() ?? "");
    }

    /// <summary>Keeps at most 200 hook states; the oldest idle ones go first.</summary>
    private void RememberHook(string id, ClaudeState state)
    {
        _hookState[id] = (state, DateTime.UtcNow);
        if (_hookState.Count <= MaxHookStates) return;
        foreach (var old in _hookState.OrderBy(kv => kv.Value.State == ClaudeState.Idle ? 0 : 1).ThenBy(kv => kv.Value.At)
                     .Take(_hookState.Count - MaxHookStates).Select(kv => kv.Key).ToList())
            _hookState.Remove(old);
    }

    // ---------- hook installation (opt-in) ----------

    private static string SettingsPath => Path.Combine(ClaudeDir, "settings.json");

    private static string HookCommand =>
        $"curl -s -m 2 -X POST {HookUrl} -H \"Content-Type: application/json\" --data-binary @- || exit 0";

    public static bool AreHooksInstalled()
    {
        try { return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(HookMarker); }
        catch { return false; }
    }

    /// <summary>Adds DynamicBay hooks to ~/.claude/settings.json (backup first, existing hooks kept).</summary>
    [RelayCommand]
    public void InstallHooks()
    {
        if (DevMode.On) { Log.Info("Claude: hooks are not changed by a test instance"); return; }
        try
        {
            Directory.CreateDirectory(ClaudeDir);
            JsonObject root = File.Exists(SettingsPath) ? (JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new()) : new();
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".dynamicbay-backup", true);
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            foreach (var ev in new[] { "UserPromptSubmit", "Notification", "Stop" })
            {
                var arr = hooks[ev] as JsonArray ?? new JsonArray();
                if (!arr.ToJsonString().Contains(HookMarker))
                    arr.Add(new JsonObject
                    {
                        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = HookCommand, ["timeout"] = 5 }),
                    });
                hooks[ev] = arr;
            }
            root["hooks"] = hooks;
            File.WriteAllText(SettingsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            HooksInstalled = true;
            if (_started) StartListener();
        }
        catch (Exception ex) { Log.Error("ClaudeHooksInstall", ex); }
    }

    [RelayCommand]
    public void RemoveHooks()
    {
        if (DevMode.On) return;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is not JsonObject root || root["hooks"] is not JsonObject hooks) return;
            foreach (var ev in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[ev] is not JsonArray arr) continue;
                foreach (var entry in arr.Where(e => e?.ToJsonString().Contains(HookMarker) == true).ToList()) arr.Remove(entry);
                if (arr.Count == 0) hooks.Remove(ev);
            }
            if (hooks.Count == 0) root.Remove("hooks");
            File.WriteAllText(SettingsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            HooksInstalled = false;
            StopListener();
        }
        catch (Exception ex) { Log.Error("ClaudeHooksRemove", ex); }
    }

    // ---------- actions ----------

    /// <summary>Opens a session through Remote Control (browser / Claude app) - works for sessions on any PC.</summary>
    [RelayCommand]
    public void OpenRemote(ClaudeSession? s)
    {
        if (SafeRemoteUrl(s?.RemoteUrl) is not { } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("ClaudeRemote", ex); }
    }

    /// <summary>Local sessions resume in a terminal; sessions of other PCs open via Remote Control.</summary>
    [RelayCommand]
    public void Resume(ClaudeSession? s)
    {
        if (s is null) return;
        if (!s.IsLocal) { OpenRemote(s); return; }
        if (!IsSessionId(s.Id)) return;
        string dir = Directory.Exists(s.Cwd) ? s.Cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            // Windows Terminal if present, else a classic console. Arguments go one by one, never as a shell string;
            // wt splits its command line at ';' (new tab), so a ';' in the folder is escaped as '\;'.
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in new[] { "-d", dir.Replace(";", "\\;"), "cmd", "/k", "claude", "--resume", s.Id }) wt.ArgumentList.Add(a);
            Process.Start(wt);
        }
        catch
        {
            try
            {
                // The folder is the working directory, not part of the command line cmd parses.
                var cmd = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, WorkingDirectory = dir };
                foreach (var a in new[] { "/k", "claude", "--resume", s.Id }) cmd.ArgumentList.Add(a);
                Process.Start(cmd);
            }
            catch (Exception ex) { Log.Error("ClaudeResume", ex); }
        }
    }

    [RelayCommand]
    public void OpenDesktop()
    {
        var app = InstalledApps.All().FirstOrDefault(a => a.Name.Equals("Claude", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (app is not null) Process.Start("explorer.exe", app.LaunchPath);
            else Process.Start(new ProcessStartInfo("https://claude.ai/new") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("ClaudeDesktop", ex); }
    }

    [RelayCommand]
    public void OpenWeb() => Process.Start(new ProcessStartInfo("https://claude.ai/code") { UseShellExecute = true });

    public void Stop() => StopListener();
}
