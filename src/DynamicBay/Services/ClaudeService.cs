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

    private void Scan()
    {
        var root = Path.Combine(ClaudeDir, "projects");
        if (!Directory.Exists(root)) return;
        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateDirectories(root)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl").Select(f => new FileInfo(f)))
                .OrderByDescending(f => f.LastWriteTime).Take(6).ToList();
        }
        catch { return; }

        var seen = new HashSet<string>();
        foreach (var f in files)
        {
            string id = Path.GetFileNameWithoutExtension(f.Name);
            seen.Add(Key(Environment.MachineName, id));
            var s = Sessions.FirstOrDefault(x => x.Id == id && x.IsLocal);
            if (s is null || s.Updated != f.LastWriteTime)
            {
                var (cwd, title, prompt, bridge) = ReadMeta(f.FullName);
                if (s is null)
                {
                    s = new ClaudeSession { Id = id, Cwd = cwd };
                    Sessions.Add(s);
                }
                s.Title = title.Length > 0 ? title : (prompt.Length > 0 ? prompt : (Loc.German ? "Neue Sitzung" : "New session"));
                s.LastPrompt = prompt;
                s.Updated = f.LastWriteTime;
                // Remote Control: bridge id "cse_X" is reachable at claude.ai/code/session_X
                if (bridge.StartsWith("cse_", StringComparison.Ordinal)) s.RemoteUrl = "https://claude.ai/code/session_" + bridge[4..];
            }
            // Sub-agents that wrote in the last 2 minutes count as active.
            var sub = Path.Combine(f.DirectoryName!, id, "subagents");
            s.ActiveAgents = Directory.Exists(sub)
                ? Directory.EnumerateFiles(sub, "*.jsonl").Count(p => (DateTime.Now - File.GetLastWriteTime(p)).TotalMinutes < 2) : 0;
            // Without hooks, "recently written" approximates "working".
            s.State = _hookState.TryGetValue(id, out var hs) ? hs
                : (DateTime.Now - f.LastWriteTime).TotalSeconds < 20 ? ClaudeState.Working : ClaudeState.Idle;
        }
        foreach (var r in SyncShared()) seen.Add(Key(r.Machine, r.Id));
        foreach (var old in Sessions.Where(x => !seen.Contains(Key(x.Machine, x.Id))).ToList()) Sessions.Remove(old);
        // Newest first
        var ordered = Sessions.OrderByDescending(x => x.Updated).ToList();
        for (int i = 0; i < ordered.Count; i++)
            if (Sessions.IndexOf(ordered[i]) != i) Sessions.Move(Sessions.IndexOf(ordered[i]), i);
        UpdateSummary();
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

    private void UpdateSummary()
    {
        var working = Sessions.Where(s => s.State == ClaudeState.Working).ToList();
        AnyWorking = working.Count > 0;
        AnyWaiting = Sessions.Any(s => s.State == ClaudeState.Waiting);
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
