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
    }

    private void SyncMirrors(bool force = false)
    {
        var monitors = Monitors.All();
        string signature = string.Join("|", monitors.Select(m => $"{m.Device}:{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width}x{m.Bounds.Height}"));
        if (!force && signature == _monitorSignature) return;
        _monitorSignature = signature;

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
