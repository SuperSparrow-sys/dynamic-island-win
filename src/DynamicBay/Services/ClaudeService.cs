using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
    private readonly Dictionary<string, ClaudeState> _hookState = new();
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
            _listener?.Close();
            _listener = null;
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
        Scan();
        StartListener();
    }

    private void OnScanTick(object? sender, EventArgs e) => Scan();

    // ---------- sessions from transcripts ----------

    private bool _scanning;

    /// <summary>Kicks off a scan; transcript reading happens off the UI thread (files can be tens of MB).</summary>
    private void Scan() => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var known = Sessions.Where(x => x.IsLocal).ToDictionary(x => x.Id, x => x.Updated);
            var results = await Task.Run(() => ReadSessions(known));
            if (results is null) return;

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
                    if (meta.bridge.StartsWith("cse_", StringComparison.Ordinal)) s.RemoteUrl = "https://claude.ai/code/session_" + meta.bridge[4..];
                }
                if (s is null) continue;
                s.ActiveAgents = r.Agents;
                // Without hooks, "recently written" approximates "working".
                s.State = _hookState.TryGetValue(r.Id, out var hs) ? hs
                    : (DateTime.Now - r.Updated).TotalSeconds < 20 ? ClaudeState.Working : ClaudeState.Idle;
            }
            foreach (var r in SyncShared()) seen.Add(Key(r.Machine, r.Id));
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

    /// <summary>
    /// Writes this PC's session list to {share}\DynamicBay\claude\{PC}.json and merges the lists other PCs wrote
    /// (only metadata: title, project, status, Remote Control link). Returns the merged remote sessions.
    /// </summary>
    private List<ClaudeSession> SyncShared()
    {
        var result = new List<ClaudeSession>();
        string folder = _settings.ClaudeShareFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;
        string dir = Path.Combine(folder, "DynamicBay", "claude");
        try
        {
            Directory.CreateDirectory(dir);
            if ((DateTime.Now - _lastShareWrite).TotalSeconds > 20)
            {
                _lastShareWrite = DateTime.Now;
                var mine = new JsonArray(Sessions.Where(s => s.IsLocal).Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id, ["title"] = s.Title, ["cwd"] = s.Cwd, ["updated"] = s.Updated.ToUniversalTime().ToString("o"),
                    ["state"] = s.State.ToString(), ["remote"] = s.RemoteUrl,
                }).ToArray());
                File.WriteAllText(Path.Combine(dir, Environment.MachineName + ".json"), mine.ToJsonString());
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                string machine = Path.GetFileNameWithoutExtension(file);
                if (machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
                if ((DateTime.Now - File.GetLastWriteTime(file)).TotalHours > 24) continue;
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonArray arr) continue;
                foreach (var n in arr.Take(4))
                {
                    string id = n?["id"]?.ToString() ?? "";
                    if (id.Length == 0) continue;
                    var s = Sessions.FirstOrDefault(x => x.Id == id && x.Machine.Equals(machine, StringComparison.OrdinalIgnoreCase));
                    if (s is null)
                    {
                        s = new ClaudeSession { Id = id, Machine = machine, Cwd = n!["cwd"]?.ToString() ?? "" };
                        Sessions.Add(s);
                    }
                    s.Title = n!["title"]?.ToString() ?? "Claude Code";
                    s.RemoteUrl = n["remote"]?.ToString();
                    s.Updated = DateTime.TryParse(n["updated"]?.ToString(), out var u) ? u.ToLocalTime() : File.GetLastWriteTime(file);
                    s.State = Enum.TryParse<ClaudeState>(n["state"]?.ToString(), out var st) && (DateTime.Now - s.Updated).TotalMinutes < 10 ? st : ClaudeState.Idle;
                    result.Add(s);
                }
            }
        }
        catch (Exception ex) { Log.Error("ClaudeShare", ex); }
        return result;
    }

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
        WorkingText = AnyWaiting ? (Loc.German ? "wartet" : "waiting")
            : working.Count > 1 ? (Loc.German ? $"{working.Count} aktiv" : $"{working.Count} active")
            : working.Count == 1 ? working[0].Project : "";
    }

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
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(HookUrl);
            _listener.Start();
            _ = Task.Run(ListenLoop);
        }
        catch (Exception ex) { Log.Error("ClaudeHooks", ex); }
    }

    private async Task ListenLoop()
    {
        while (_listener is { IsListening: true })
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                string body;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                var json = JsonNode.Parse(body);
                if (json is null) continue;
                _ = Application.Current.Dispatcher.BeginInvoke(() => OnHook(json));
            }
            catch { }
        }
    }

    private void OnHook(JsonNode json)
    {
        string id = json["session_id"]?.ToString() ?? "";
        string ev = json["hook_event_name"]?.ToString() ?? "";
        if (id.Length == 0) return;
        var state = ev switch
        {
            "UserPromptSubmit" or "PreToolUse" or "SubagentStart" => ClaudeState.Working,
            "Notification" => ClaudeState.Waiting,
            "Stop" or "SessionEnd" => ClaudeState.Idle,
            _ => (ClaudeState?)null,
        };
        if (state is null) return;
        var previous = _hookState.TryGetValue(id, out var p) ? p : ClaudeState.Idle;
        _hookState[id] = state.Value;
        Scan();
        var session = Sessions.FirstOrDefault(s => s.Id == id)
                      ?? new ClaudeSession { Id = id, Cwd = json["cwd"]?.ToString() ?? "", Title = "Claude Code" };
        session.State = state.Value;
        UpdateSummary();
        if (ev == "Stop" && previous == ClaudeState.Working) Finished?.Invoke(session);
        if (ev == "Notification") NeedsInput?.Invoke(session, json["message"]?.ToString() ?? "");
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
        }
        catch (Exception ex) { Log.Error("ClaudeHooksInstall", ex); }
    }

    [RelayCommand]
    public void RemoveHooks()
    {
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
        }
        catch (Exception ex) { Log.Error("ClaudeHooksRemove", ex); }
    }

    // ---------- actions ----------

    /// <summary>Opens a session through Remote Control (browser / Claude app) - works for sessions on any PC.</summary>
    [RelayCommand]
    public void OpenRemote(ClaudeSession? s)
    {
        if (s?.RemoteUrl is null) return;
        try { Process.Start(new ProcessStartInfo(s.RemoteUrl) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("ClaudeRemote", ex); }
    }

    /// <summary>Local sessions resume in a terminal; sessions of other PCs open via Remote Control.</summary>
    [RelayCommand]
    public void Resume(ClaudeSession? s)
    {
        if (s is null) return;
        if (!s.IsLocal) { OpenRemote(s); return; }
        string dir = Directory.Exists(s.Cwd) ? s.Cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            // Windows Terminal if present, else a classic console.
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir}\" cmd /k claude --resume {s.Id}") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe", $"/k cd /d \"{dir}\" && claude --resume {s.Id}") { UseShellExecute = true }); }
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

    public void Stop() => _listener?.Close();
}
