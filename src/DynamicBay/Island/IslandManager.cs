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
                or nameof(AppSettings.Inset) or nameof(AppSettings.Monitor) or nameof(AppSettings.ShapeStyle))
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
        _settings.DockProfileMap[_profileKey] = DockProfile.From(_settings);
        _settings.SaveSoon();
    }

    /// <summary>Displays changed (docked, undocked, monitor switched on): put the island where it was last time with these displays.</summary>
    private void ApplyProfile(string key, bool arrangementChanged)
    {
        _profileKey = key;
        _applyingProfile = true;
        DockProfile? applied;
        try { applied = SwitchProfile(_settings, key, arrangementChanged); }
        finally { _applyingProfile = false; }
        if (_settings.DockProfiles) _settings.SaveSoon();
        if (applied is not null)
        {
            Log.Info($"Dock profile applied: {_settings.ShapeStyle} {applied.Edge}/{applied.Align} on {(applied.Monitor.Length == 0 ? "primary" : applied.Monitor)}");
        }
    }

    /// <summary>
    /// The displays are now <paramref name="key"/>: a new arrangement starts from the current look, a known one gets its
    /// own back (when it really changed, not at the start). A profile from before the shape was remembered keeps the
    /// current shape and remembers it from now on. Returns the profile that was applied.
    /// </summary>
    internal static DockProfile? SwitchProfile(AppSettings s, string key, bool arrangementChanged)
    {
        if (!s.DockProfiles || key.Length == 0) return null;
        if (!s.DockProfileMap.TryGetValue(key, out var p))
        {
            s.DockProfileMap[key] = DockProfile.From(s);
            return null;
        }
        if (!arrangementChanged || p.SameAs(s))
        {
            p.Shape ??= s.ShapeStyle;
            return null;
        }
        p.ApplyTo(s);
        p.Shape ??= s.ShapeStyle;
        return p;
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
            var w = new IslandWindow(_vm, device) { LinkHidden = _linkHidden };
            _mirrors.Add(w);
            if (!_settings.Hidden) w.Show();
        }
        Log.Info($"Displays: {monitors.Count}, mode {_settings.Displays}, mirrors {_mirrors.Count}");
    }

    /// <summary>
    /// Set by the app while a paired device is the main one: returns true when the peek went there instead (it is
    /// not shown here then).
    /// </summary>
    public Func<PeekItem, bool>? Divert { get; set; }

    private readonly Dictionary<string, DateTime> _recentPeeks = new();
    private bool _linkHidden;

    /// <summary>The island makes way for the paired main device (or comes back); the user's own "hidden" stays as it is.</summary>
    public void SetLinkHidden(bool hidden)
    {
        if (hidden == _linkHidden) return;
        _linkHidden = hidden;
        Log.Info(hidden ? "Island hidden: the paired main device shows everything" : "Island back: this device shows everything again");
        foreach (var w in All) w.LinkHidden = hidden;
    }

    /// <summary>
    /// The same message from both devices (a calendar reminder on PC and laptop, a peek forwarded by the other one)
    /// shows once: plain peeks with the same title and subtitle within 10 s are dropped.
    /// </summary>
    internal static bool IsRepeat(Dictionary<string, DateTime> recent, PeekItem item, DateTime now)
    {
        if (item.Kind == PeekKind.Local || item.HasActions) return false;
        foreach (var old in recent.Where(kv => now - kv.Value > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList()) recent.Remove(old);
        string key = item.Title + "\n" + item.Subtitle;
        if (recent.ContainsKey(key)) return true;
        recent[key] = now;
        return false;
    }

    public void ShowPeek(PeekItem item)
    {
        if (Divert?.Invoke(item) == true) return;
        if (IsRepeat(_recentPeeks, item, DateTime.UtcNow)) return;
        // Questions (with buttons) appear once, on the main island; plain peeks on every island.
        if (item.HasActions) { Main.ShowPeek(item); return; }
        foreach (var w in All) w.ShowPeek(item);
    }

    /// <summary>Takes a peek away again on every island (shown or still waiting).</summary>
    public void WithdrawPeek(PeekItem item)
    {
        foreach (var w in All) w.Withdraw(item);
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
