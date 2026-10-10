using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DynamicBay.Core;

namespace DynamicBay.Island;

/// <summary>
/// Owns the island windows: one main island, plus one mirror per additional monitor in mirror mode.
/// Peeks and commands fan out to every island.
/// </summary>
public sealed class IslandManager
{
    private readonly IslandViewModel _vm;
    private readonly AppSettings _settings;
    private readonly List<IslandWindow> _mirrors = new();
    private readonly DispatcherTimer _displayCheck = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _monitorSignature = "";

    public IslandWindow Main { get; }
    public IEnumerable<IslandWindow> All => _mirrors.Prepend(Main);

    public IslandManager(IslandViewModel vm)
    {
        _vm = vm;
        _settings = vm.Settings;
        Main = new IslandWindow(vm);
        _settings.PropertyChanged += OnSettingsChanged;
        // Monitors can be plugged in/out at any time.
        _displayCheck.Tick += (_, _) => SyncMirrors();
    }

    public void Show()
    {
        if (!_settings.Hidden) Main.Show();
        SyncMirrors(force: true);
        _displayCheck.Start();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.Displays)) SyncMirrors(force: true);
        // The user placed the island (drag, settings): remember it for this arrangement of displays.
        if (!_applyingProfile && e.PropertyName is nameof(AppSettings.Edge) or nameof(AppSettings.Align) or nameof(AppSettings.Along)
                or nameof(AppSettings.Inset) or nameof(AppSettings.Monitor))
            RememberPlacement();
    }

    // ---------- dock profiles ----------

    private bool _applyingProfile;
    private string _profileKey = "";

    /// <summary>Displays by name and resolution (not position): the same office monitor gives the same profile.</summary>
    private static string ProfileKey(IEnumerable<MonitorInfo> monitors) =>
        string.Join("|", monitors.OrderBy(m => m.Device, StringComparer.OrdinalIgnoreCase).Select(m => $"{m.Device}:{m.Bounds.Width}x{m.Bounds.Height}{(m.IsPrimary ? "*" : "")}"));

    private void RememberPlacement()
    {
        if (!_settings.DockProfiles || _profileKey.Length == 0) return;
        _settings.DockProfileMap[_profileKey] = new DockProfile
        {
            Edge = _settings.Edge, Align = _settings.Align, Along = _settings.Along, Inset = _settings.Inset, Monitor = _settings.Monitor,
        };
        _settings.SaveSoon();
    }

    /// <summary>Displays changed (docked, undocked, monitor switched on): put the island where it was last time with these displays.</summary>
    private void ApplyProfile(string key, bool arrangementChanged)
    {
        _profileKey = key;
        if (!_settings.DockProfiles) return;
        if (!_settings.DockProfileMap.TryGetValue(key, out var p))
        {
            // First time with these displays: start from the current placement.
            RememberPlacement();
            return;
        }
        if (!arrangementChanged) return;
        if (p.Edge == _settings.Edge && p.Align == _settings.Align && Math.Abs(p.Along - _settings.Along) < 0.001
            && Math.Abs(p.Inset - _settings.Inset) < 0.01 && string.Equals(p.Monitor, _settings.Monitor, StringComparison.OrdinalIgnoreCase)) return;
        _applyingProfile = true;
        try
        {
            _settings.Monitor = p.Monitor;
            _settings.Edge = p.Edge;
            _settings.Align = p.Align;
            _settings.Along = p.Along;
            _settings.Inset = p.Inset;
        }
        finally { _applyingProfile = false; }
        Log.Info($"Dock profile applied: {p.Edge}/{p.Align} on {(p.Monitor.Length == 0 ? "primary" : p.Monitor)}");
    }

    private void SyncMirrors(bool force = false)
    {
        var monitors = Monitors.All();
        string signature = string.Join("|", monitors.Select(m => $"{m.Device}:{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width}x{m.Bounds.Height}"));
        if (!force && signature == _monitorSignature) return;
        bool changed = _monitorSignature.Length > 0 && signature != _monitorSignature;
        _monitorSignature = signature;
        ApplyProfile(ProfileKey(monitors), changed);

        var wanted = _settings.Displays == DisplayMode.Mirror
            ? monitors.Where(m => !m.IsPrimary).Select(m => m.Device).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in _mirrors.Where(w => !wanted.Contains(w.MirrorDevice!)).ToList())
        {
            _mirrors.Remove(w);
            w.Close();
        }
        foreach (var device in wanted.Where(d => _mirrors.All(w => !string.Equals(w.MirrorDevice, d, StringComparison.OrdinalIgnoreCase))))
        {
            var w = new IslandWindow(_vm, device);
            _mirrors.Add(w);
            if (!_settings.Hidden) w.Show();
        }
        Log.Info($"Displays: {monitors.Count}, mode {_settings.Displays}, mirrors {_mirrors.Count}");
    }

    public void ShowPeek(PeekItem item)
    {
        // Questions (with buttons) appear once, on the main island; plain peeks on every island.
        if (item.HasActions) { Main.ShowPeek(item); return; }
        foreach (var w in All) w.ShowPeek(item);
    }

    /// <summary>Opens the island the user is most likely looking at (the one under the cursor, else the main one).</summary>
    public void Expand()
    {
        var c = Native.CursorPos();
        var monitor = Monitors.FromPoint(c.X, c.Y).Device;
        var target = All.FirstOrDefault(w => string.Equals(w.TargetMonitor().Device, monitor, StringComparison.OrdinalIgnoreCase)) ?? Main;
        target.SetExpanded(true);
    }

    public void ToggleHidden() => Main.SetHidden(!_settings.Hidden);

    public void ResetPosition() => Main.ResetPosition();

    public void CloseAll()
    {
        _displayCheck.Stop();
        foreach (var w in All.ToList()) w.Close();
    }
}
