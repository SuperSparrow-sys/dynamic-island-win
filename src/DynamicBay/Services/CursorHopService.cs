using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using Microsoft.Win32;

namespace DynamicBay.Services;

/// <summary>
/// CursorHop (software KVM: one mouse and keyboard for several PCs). It has no public interface and its local gRPC
/// service is private (own token), so DynamicBay only reads its log, read-only and shared with CursorHop's writer:
/// <c>%LocalAppData%\CursorHop\logs\core.log.yyyy-MM-dd</c> (a new file every day, see <see cref="CursorHopLog"/>).
/// Optionally starts CursorHop when it is not running: when DynamicBay starts, after waking up and when the network
/// comes back - never a second instance.
/// </summary>
public sealed partial class CursorHopService : ObservableObject, IDisposable
{
    public const string ProcessCore = "cursorhop", ProcessApp = "CursorHop-App";

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private readonly CursorHopState _state = new();
    private CursorHopAnnouncer _announcer = new();
    private readonly DispatcherTimer _tick;
    private int _tickCount;
    private bool _running;
    private readonly CursorHopLogTail _tail = new();
    private DateTime _startedAt;

    [ObservableProperty] private bool _isInstalled;
    /// <summary>The CursorHop core process runs.</summary>
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isConnected;
    /// <summary>Connected devices, comma separated ("" = none).</summary>
    [ObservableProperty] private string _devices = "";
    /// <summary>The mouse is on this machine (or nothing is connected).</summary>
    [ObservableProperty] private bool _cursorHere = true;
    [ObservableProperty] private string _cursorText = "";
    [ObservableProperty] private string _statusText = "";
    /// <summary>First line of the card: the connected device(s), or that there is none.</summary>
    [ObservableProperty] private string _deviceTitle = "";
    [ObservableProperty] private bool _deviceIsLaptop;
    /// <summary>Installed but not running: the card offers to start it.</summary>
    [ObservableProperty] private bool _canStart;

    partial void OnIsRunningChanged(bool value) => UpdateTexts();
    partial void OnIsInstalledChanged(bool value) => UpdateTexts();

    /// <summary>A device connected and stayed (debounced, see <see cref="CursorHopAnnouncer"/>).</summary>
    public event Action<string>? PeerConnected;
    /// <summary>A device went away and stayed away.</summary>
    public event Action<string>? PeerDisconnected;
    /// <summary>The mouse went to a device (name) or came back here (null). Not debounced: it changes on every edge crossing.</summary>
    public event Action<string?>? CursorMoved;
    /// <summary>Clipboard content arrived: device name (null if unknown) and CursorHop's format name.</summary>
    public event Action<string?, string?>? ClipboardReceived;
    /// <summary>Files arrived: device, number (if logged) and folder (if logged).</summary>
    public event Action<string?, int?, string?>? FilesReceived;

    public CursorHopService(AppSettings settings)
    {
        _settings = settings;
        _isInstalled = IsInstalledOnDisk;
        _tick = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
        UpdateTexts();
    }

    public static string ExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CursorHop", "CursorHop.exe");

    public static bool IsInstalledOnDisk => File.Exists(ExePath);

    /// <summary>
    /// The files CursorHop just wrote into its folder: the log only says how many, so the newest ones (at most
    /// <paramref name="count"/>, changed within <paramref name="window"/>) are taken. Folders are skipped.
    /// </summary>
    public static List<string> RecentFiles(string? folder, int count, TimeSpan window, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || count <= 0) return new();
        var since = (now ?? DateTime.Now) - window;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(f => (f.FullName, Time: f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime))
                .Where(f => f.Time >= since)
                .OrderByDescending(f => f.Time)
                .Take(Math.Min(count, 50))
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    public static string LogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorHop", "logs");

    /// <summary>CursorHop names its daily file after the UTC date (the timestamps in it are UTC).</summary>
    internal static string LogFileFor(DateTime utc) => Path.Combine(LogFolder, "core.log." + utc.ToString("yyyy-MM-dd"));

    public static bool IsProcessRunning(string name)
    {
        var list = Process.GetProcessesByName(name);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    // ---------- start / stop ----------

    public void SetEnabled(bool enabled)
    {
        if (enabled) Start(); else Stop();
    }

    private void Start()
    {
        if (_running) return;
        IsInstalled = IsInstalledOnDisk;
        if (!IsInstalled) { Log.Info("CursorHop: not installed"); return; }
        _running = true;
        SystemEvents.PowerModeChanged += OnPowerMode;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        IsRunning = IsProcessRunning(ProcessCore);
        Replay();
        _tick.Start();
        // Not right away: at sign-in Windows starts CursorHop itself, a moment later than DynamicBay maybe.
        if (_settings.CursorHopAutoStart) _ui.BeginInvoke(async () => { await Task.Delay(20000); EnsureRunning("DynamicBay start"); });
        Log.Info($"CursorHop: watching the log (running {IsRunning}, connected: {(Devices.Length > 0 ? Devices : "none")})");
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        _tick.Stop();
        SystemEvents.PowerModeChanged -= OnPowerMode;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        _state.Reset();
        _announcer = new();
        _tail.Follow(null);
        Publish();
    }

    public void Dispose() => Stop();

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.BeginInvoke(async () => { await Task.Delay(5000); EnsureRunning("resume"); });
    }

    private void OnNetwork(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _ui.BeginInvoke(async () => { await Task.Delay(3000); EnsureRunning("network back"); });
    }

    /// <summary>Starts CursorHop when neither its window process nor its core runs (setting "mitstarten").</summary>
    private void EnsureRunning(string reason)
    {
        if (!_running || !_settings.CursorHopEnabled || !_settings.CursorHopAutoStart || DevMode.On) return; // test instances leave the real CursorHop alone
        if ((DateTime.UtcNow - _startedAt).TotalSeconds < 60) return; // just started it, it is still coming up
        if (IsProcessRunning(ProcessCore) || IsProcessRunning(ProcessApp)) return;
        _startedAt = DateTime.UtcNow;
        Log.Info($"CursorHop: not running, starting it ({reason})");
        Launch();
    }

    /// <summary>Same as the Windows autostart entry; with CursorHop running it brings its window up instead.</summary>
    private static void Launch()
    {
        try { Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ExePath)! })?.Dispose(); }
        catch (Exception ex) { Log.Error("CursorHop start", ex); }
    }

    [RelayCommand]
    private void Open()
    {
        if (!IsInstalledOnDisk) return;
        _startedAt = DateTime.UtcNow;
        Launch();
    }

    // ---------- log ----------

    /// <summary>Today's log (and yesterday's, a connection may be older) without peeks: the state right now.</summary>
    private void Replay()
    {
        var now = DateTime.UtcNow;
        foreach (var path in new[] { LogFileFor(now.AddDays(-1)), LogFileFor(now) })
        {
            if (!File.Exists(path)) continue;
            _tail.Follow(path);
            // Up to the end: a big log read in parts must not turn its older half into peeks on the next tick
            for (int part = 0; part < 64 && ReadNew(silent: true); part++) { }
        }
        if (!IsRunning) _state.Reset();
        foreach (var p in _state.Peers) _announcer.Silently(p.Name);
        if (_tail.File != LogFileFor(now)) _tail.Follow(LogFileFor(now)); // today's file comes later
        Publish();
    }

    private void Tick()
    {
        if (!_running) return;
        // The process every 5 s; the log every second (a length check when nothing was written).
        if (++_tickCount % 5 == 0 && !_checking)
        {
            // The process list on a worker thread: it walks every process and took the UI thread with it.
            _checking = true;
            _ = Task.Run(() => IsProcessRunning(ProcessCore)).ContinueWith(t =>
            {
                _checking = false;
                if (t.IsCompletedSuccessfully && _running) ApplyRunning(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        TailLog();
    }

    private bool _checking;

    private void ApplyRunning(bool running)
    {
        if (running == IsRunning) return;
        IsRunning = running;
        // Closed or crashed: every connection is gone with it.
        if (!running)
            foreach (var c in _state.Apply(new CursorHopEvent(CursorHopEventKind.CoreStarted, DateTime.UtcNow)))
                _announcer.Disconnected(c.Name!, DateTime.UtcNow);
        Publish();
    }

    private void TailLog()
    {
        // New day (UTC, like the file names): finish yesterday's file, then follow the new one from its start.
        var today = LogFileFor(DateTime.UtcNow);
        if (_tail.File != today && File.Exists(today))
        {
            ReadNew(silent: false);
            _tail.Follow(today);
        }
        ReadNew(silent: false);
        foreach (var (name, connected) in _announcer.Due(DateTime.UtcNow))
        {
            Log.Info($"CursorHop: {name} {(connected ? "connected" : "disconnected")}");
            (connected ? PeerConnected : PeerDisconnected)?.Invoke(name);
        }
    }

    /// <returns>Whether anything was read (the reader hands out at most a few MB per call).</returns>
    private bool ReadNew(bool silent)
    {
        List<string> lines;
        try { lines = _tail.ReadNew(); }
        catch (Exception ex) { Log.Info($"CursorHop: log not readable ({ex.Message})"); return false; }
        if (lines.Count == 0) return false;
        if (!silent && !IsRunning) IsRunning = true; // it is writing its log right now
        foreach (var line in lines)
            if (CursorHopLog.Parse(line) is { } e) Apply(e, silent);
        if (!silent) Publish();
        return true;
    }

    private void Apply(CursorHopEvent e, bool silent)
    {
        foreach (var c in _state.Apply(e))
        {
            if (silent) continue;
            switch (c.Kind)
            {
                case CursorHopEventKind.PeerConnected: _announcer.Connected(c.Name!, DateTime.UtcNow); break;
                case CursorHopEventKind.PeerDisconnected: _announcer.Disconnected(c.Name!, DateTime.UtcNow); break;
                case CursorHopEventKind.RemoteControlStarted or CursorHopEventKind.ReturnedLocal
                    or CursorHopEventKind.RemoteControlEnded or CursorHopEventKind.ControllingRemote:
                    CursorMoved?.Invoke(_state.CursorOn);
                    break;
                case CursorHopEventKind.ClipboardReceived:
                    Log.Info($"CursorHop: clipboard ({c.Format}) from {c.Name ?? "?"}");
                    ClipboardReceived?.Invoke(c.Name, c.Format);
                    break;
                case CursorHopEventKind.FilesReceived:
                    Log.Info($"CursorHop: {c.Count?.ToString() ?? "?"} file(s) from {c.Name ?? "?"} in {c.Folder ?? "?"}");
                    FilesReceived?.Invoke(c.Name, c.Count, c.Folder);
                    break;
            }
        }
    }

    private void Publish()
    {
        Devices = string.Join(", ", _state.Peers.Select(p => p.Name));
        IsConnected = _state.Peers.Count > 0;
        CursorHere = _state.CursorOn is null;
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        StatusText = !IsInstalled ? (Loc.German ? "Nicht installiert" : "Not installed")
                   : !IsRunning ? (Loc.German ? "Läuft nicht" : "Not running")
                   : IsConnected ? (Loc.German ? "Verbunden" : "Connected")
                   : (Loc.German ? "Läuft, wartet auf Geräte" : "Running, waiting for devices");
        DeviceTitle = Devices.Length > 0 ? Devices : (Loc.German ? "Kein Gerät verbunden" : "No device connected");
        DeviceIsLaptop = IconFor(_state.Peers.FirstOrDefault()?.Name) == "Icon.Laptop";
        CanStart = IsInstalled && !IsRunning;
        CursorText = CursorTextFor(_state.CursorOn);
    }

    /// <summary>"Maus ist hier" / "Maus am PC-Jonathan" - true on the Primary and on a secondary alike.</summary>
    public static string CursorTextFor(string? device) => device is null
        ? (Loc.German ? "Maus ist hier" : "Mouse is here")
        : (Loc.German ? $"Maus am {device}" : $"Mouse on {device}");

    /// <summary>Laptop glyph for names that sound like one, a monitor for the rest (the log has no device type).</summary>
    public static string IconFor(string? device) =>
        device is not null && (device.Contains("laptop", StringComparison.OrdinalIgnoreCase) || device.Contains("book", StringComparison.OrdinalIgnoreCase)
                               || device.Contains("surface", StringComparison.OrdinalIgnoreCase) || device.Contains("notebook", StringComparison.OrdinalIgnoreCase))
            ? "Icon.Laptop" : "Icon.Monitor";

    /// <summary>"PlainText" → "Text", "Image" → "Bild" ...</summary>
    public static string FormatText(string? format) => format?.ToLowerInvariant() switch
    {
        null or "" => Loc.German ? "Inhalt" : "Content",
        var f when f.Contains("image") || f.Contains("png") || f.Contains("bitmap") => Loc.German ? "Bild" : "Image",
        var f when f.Contains("file") => Loc.German ? "Dateien" : "Files",
        _ => "Text",
    };

    /// <summary>Snapshot mode: a connected device (null: none) with the mouse on it or here; no process, no log.</summary>
    internal void LoadDemo(string? device, bool cursorThere, bool running = true)
    {
        IsInstalled = true;
        IsRunning = running;
        _state.Reset();
        if (device is null) { Publish(); return; }
        _state.Apply(new CursorHopEvent(CursorHopEventKind.PeerConnected, DateTime.UtcNow) { Peer = "demo", Name = device, PeerIsPrimary = cursorThere });
        if (!cursorThere) _state.Apply(new CursorHopEvent(CursorHopEventKind.RemoteControlStarted, DateTime.UtcNow) { Peer = "demo" });
        Publish();
    }
}
