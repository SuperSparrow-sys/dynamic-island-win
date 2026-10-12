using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicBay.Services;

public enum DeviceKind { Headphones, Speaker, Keyboard, Mouse, Phone, Other }

public sealed partial class BluetoothDeviceInfo : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public DeviceKind Kind { get; init; }
    public bool IsLowEnergy { get; init; }
    public bool IsAudio => Kind is DeviceKind.Headphones or DeviceKind.Speaker;
    /// <summary>Keyboards and mice reconnect after every sleep: no connect peek for them.</summary>
    public bool IsInput => Kind is DeviceKind.Keyboard or DeviceKind.Mouse;
    [ObservableProperty] private int? _battery;
}

/// <summary>
/// Bluetooth devices: announces connects/disconnects (headphones, speakers, phones) and keeps the list of connected
/// devices with their battery level for the "Geräte" widget - including Bluetooth LE keyboards and mice.
/// Watches every paired device (classic and LE) and reacts to its "connected" flag.
/// </summary>
public sealed partial class BluetoothService : ObservableObject
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedKey = "System.Devices.Aep.IsConnected";
    private const string ContainerKey = "System.Devices.Aep.ContainerId";
    private readonly List<DeviceWatcher> _watchers = new();
    private readonly Dictionary<string, (string name, bool le)> _paired = new();
    private readonly HashSet<string> _connected = new();
    private readonly HashSet<string> _lowWarned = new();
    private int _enumerated;
    private Dispatcher? _ui;
    private readonly DispatcherTimer _batteryPoll = new() { Interval = TimeSpan.FromMinutes(2) };

    /// <summary>Connected devices, for the widget (UI thread).</summary>
    public ObservableCollection<BluetoothDeviceInfo> Devices { get; } = new();
    [ObservableProperty] private bool _hasDevices;

    public event Action<BluetoothDeviceInfo, bool>? DeviceChanged; // bool = connected
    /// <summary>A device's battery dropped below 15 % (once per device and day).</summary>
    public event Action<BluetoothDeviceInfo>? BatteryLow;

    public void Start()
    {
        _ui = Application.Current?.Dispatcher;
        _batteryPoll.Tick += async (_, _) => await RefreshBatteriesAsync();
        _batteryPoll.Start();
        Watch(BluetoothDevice.GetDeviceSelectorFromPairingState(true), le: false);
        Watch(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), le: true);
    }

    private void Watch(string aqs, bool le)
    {
        try
        {
            var w = DeviceInformation.CreateWatcher(aqs, new[] { ConnectedKey }, DeviceInformationKind.AssociationEndpoint);
            w.Added += (_, d) =>
            {
                lock (_paired) _paired[d.Id] = (d.Name, le);
                // Also "not connected": after a watcher restart this clears devices that went while it was down.
                SetConnected(d.Id, IsConnected(d.Properties));
            };
            w.Updated += (_, u) => { if (u.Properties.ContainsKey(ConnectedKey)) SetConnected(u.Id, IsConnected(u.Properties)); };
            w.Removed += (_, u) => SetConnected(u.Id, false);
            w.EnumerationCompleted += (_, _) =>
            {
                if (Interlocked.Increment(ref _enumerated) == 2)
                    lock (_paired) Log.Info($"Bluetooth: {_paired.Count} paired, connected: {string.Join(", ", _connected.Select(id => _paired.GetValueOrDefault(id).name))}");
            };
            // Windows aborts a watcher now and then (Bluetooth stack restarted, radio switched off and on): start it again.
            w.Stopped += (s, _) =>
            {
                if (s.Status != DeviceWatcherStatus.Aborted) return;
                Log.Info("Bluetooth: watcher aborted - restarting in 5 s");
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
                {
                    try { if (s.Status is DeviceWatcherStatus.Aborted or DeviceWatcherStatus.Stopped) s.Start(); }
                    catch (Exception ex) { Log.Error("Bluetooth", ex); }
                });
            };
            w.Start();
            _watchers.Add(w);
        }
        catch (Exception ex) { Log.Error("Bluetooth", ex); }
    }

    private static bool IsConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(ConnectedKey, out var v) && v is bool b && b;

    private async void SetConnected(string id, bool connected)
    {
        try
        {
            (string name, bool le) p;
            lock (_paired)
            {
                if (connected ? !_connected.Add(id) : !_connected.Remove(id)) return; // no change
                p = _paired.GetValueOrDefault(id, ("Bluetooth", false));
            }
            bool announce = _enumerated >= 2; // devices already connected at startup are listed, not announced
            if (!connected)
            {
                _ui?.BeginInvoke(() =>
                {
                    var gone = Devices.FirstOrDefault(d => d.Id == id);
                    if (gone is not null) Devices.Remove(gone);
                    HasDevices = Devices.Count > 0;
                    if (gone is not null && TwinOf(gone) is { } twin) { _ = ShowTwinAsync(twin); return; } // still there under its other identity
                    if (announce && gone is not null && !gone.IsInput && Calm(_lastChange, gone.Name, DateTime.UtcNow)) DeviceChanged?.Invoke(gone, false);
                });
                if (announce) Log.Info($"Bluetooth: {p.name} disconnected");
                return;
            }
            var info = await DescribeAsync(id, p.name, p.le);
            if (string.IsNullOrWhiteSpace(info.Name)) return; // nameless LE endpoints (e.g. a phone's second identity)
            Log.Info($"Bluetooth: {info.Name} connected ({info.Kind}{(info.Battery is int b ? $", battery {b} %" : "")})");
            _ui?.BeginInvoke(() =>
            {
                // Disconnected again while it was being described: its removal has already run, do not list it.
                lock (_paired) if (!_connected.Contains(id)) return;
                if (!Merge(Devices, info)) return;
                HasDevices = true;
                if (announce && !info.IsInput && Calm(_lastChange, info.Name, DateTime.UtcNow)) DeviceChanged?.Invoke(info, true);
                CheckLow(info);
                if (info.Battery is null) _ = RetryBatteryAsync(info);
            });
        }
        catch (Exception ex) { Log.Error("Bluetooth", ex); }
    }

    /// <summary>
    /// Adds a connected device to the list. The same physical device over classic and LE shows once: when the one
    /// already listed has no battery level yet, it takes the other one's. False: nothing new to show.
    /// </summary>
    internal static bool Merge(ICollection<BluetoothDeviceInfo> devices, BluetoothDeviceInfo info)
    {
        if (devices.Any(d => d.Id == info.Id)) return false;
        if (devices.FirstOrDefault(d => SameDevice(d.Name, d.Kind, info.Name, info.Kind)) is { } twin)
        {
            twin.Battery ??= info.Battery;
            return false;
        }
        devices.Add(info);
        return true;
    }

    /// <summary>
    /// The same physical device under two Bluetooth identities: the same name, or for phones one name the start of the
    /// other (an iPhone shows up as "iPhone" with its battery and as "iPhone von Philipp" without).
    /// </summary>
    internal static bool SameDevice(string a, DeviceKind ka, string b, DeviceKind kb)
    {
        if (a == b) return true;
        if (ka != DeviceKind.Phone || kb != DeviceKind.Phone) return false;
        return b.StartsWith(a + " ", StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + " ", StringComparison.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, DateTime> _lastChange = new();

    /// <summary>A connected id of the same device under its other identity (not listed, it was merged into this one).</summary>
    private (string Id, string Name, bool Le)? TwinOf(BluetoothDeviceInfo d)
    {
        lock (_paired)
            foreach (var id in _connected)
                if (id != d.Id && _paired.GetValueOrDefault(id) is { name: { } n } p && SameDevice(d.Name, d.Kind, n, KindFromName(n)))
                    return (id, n, p.le);
        return null;
    }

    private async Task ShowTwinAsync((string Id, string Name, bool Le) twin)
    {
        var info = await DescribeAsync(twin.Id, twin.Name, twin.Le);
        lock (_paired) if (!_connected.Contains(twin.Id)) return;
        if (!Merge(Devices, info)) return;
        HasDevices = true;
        if (info.Battery is null) _ = RetryBatteryAsync(info);
    }

    /// <summary>
    /// A device that connects and drops every few seconds (a phone's second identity) is announced once, not every
    /// time: true when it has been quiet for a minute. Notes the change either way.
    /// </summary>
    internal static bool Calm(Dictionary<string, DateTime> last, string name, DateTime now)
    {
        bool calm = !last.TryGetValue(name, out var before) || now - before > TimeSpan.FromMinutes(1);
        last[name] = now;
        return calm;
    }

    /// <summary>A phone reports its battery a few seconds after it connects: look again soon instead of in 2 minutes.</summary>
    internal static readonly int[] BatteryRetrySeconds = { 3, 10, 30, 60 };

    private async Task RetryBatteryAsync(BluetoothDeviceInfo d)
    {
        foreach (int s in BatteryRetrySeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(s));
            if (!Devices.Contains(d) || d.Battery is not null) return;
            if (await ReadBatteryOfAsync(d) is int level)
            {
                d.Battery = level;
                Log.Info($"Bluetooth: {d.Name} battery {level} % (after {s} s)");
                CheckLow(d);
                return;
            }
        }
    }

    /// <summary>The battery of a listed device, also from its other connection (classic or LE) with the same name.</summary>
    private async Task<int?> ReadBatteryOfAsync(BluetoothDeviceInfo d)
    {
        List<string> ids;
        lock (_paired) ids = _connected.Where(id => id == d.Id || _paired.GetValueOrDefault(id).name is { } n && SameDevice(d.Name, d.Kind, n, KindFromName(n)))
                                       .OrderBy(id => id != d.Id).ToList();
        foreach (var id in ids)
            if (await ReadBatteryAsync(id) is int level) return level;
        return null;
    }

    private async Task RefreshBatteriesAsync()
    {
        foreach (var d in Devices.ToList())
        {
            var level = await ReadBatteryOfAsync(d);
            if (level is null) continue;
            d.Battery = level;
            CheckLow(d);
        }
    }

    private void CheckLow(BluetoothDeviceInfo d)
    {
        if (d.Battery is not int b) return;
        string key = d.Id + "|" + DateTime.Today.ToString("yyyyMMdd");
        if (b < 15 && _lowWarned.Add(key)) BatteryLow?.Invoke(d);
    }

    private static DeviceKind KindFromName(string name)
    {
        string n = name.ToLowerInvariant();
        if (new[] { "flip", "charge", "boom", "speaker", "soundlink", "xtreme", "go 3", "clip" }.Any(n.Contains)) return DeviceKind.Speaker;
        if (new[] { "airpods", "buds", "headphone", "headset", "wh-", "wf-", "beats", "bose", "earbuds", "soundcore" }.Any(n.Contains)) return DeviceKind.Headphones;
        if (new[] { "keyboard", "keys", "mchncl", "mechanical", "tastatur" }.Any(n.Contains)) return DeviceKind.Keyboard;
        if (new[] { "mouse", "mx master", "mx anywhere", "maus", "trackpad" }.Any(n.Contains)) return DeviceKind.Mouse;
        if (new[] { "iphone", "pixel", "galaxy", "phone" }.Any(n.Contains)) return DeviceKind.Phone;
        return DeviceKind.Other;
    }

    private static async Task<BluetoothDeviceInfo> DescribeAsync(string id, string name, bool le)
    {
        var kind = KindFromName(name);
        try
        {
            if (le)
            {
                using var dev = await BluetoothLEDevice.FromIdAsync(id);
                // GAP appearance: category 15 = HID (sub 1 keyboard, 2 mouse), 1 = phone, 0x41 = audio sink
                if (dev is not null && kind == DeviceKind.Other)
                    kind = (dev.Appearance.Category, dev.Appearance.SubCategory) switch
                    {
                        (15, 1) => DeviceKind.Keyboard,
                        (15, 2) => DeviceKind.Mouse,
                        (1, _) => DeviceKind.Phone,
                        (0x41, _) => DeviceKind.Speaker,
                        _ => DeviceKind.Other,
                    };
            }
            else
            {
                using var dev = await BluetoothDevice.FromIdAsync(id);
                if (dev is not null && kind == DeviceKind.Other)
                    kind = dev.ClassOfDevice.MajorClass switch
                    {
                        BluetoothMajorClass.AudioVideo => DeviceKind.Headphones,
                        BluetoothMajorClass.Phone => DeviceKind.Phone,
                        BluetoothMajorClass.Peripheral => dev.ClassOfDevice.MinorClass.ToString().Contains("Keyboard") ? DeviceKind.Keyboard : DeviceKind.Mouse,
                        _ => DeviceKind.Other,
                    };
            }
        }
        catch { }
        return new BluetoothDeviceInfo { Id = id, Name = name, Kind = kind, IsLowEnergy = le, Battery = await ReadBatteryAsync(id) };
    }

    /// <summary>Battery level from the PnP devnodes sharing the device's container (what Windows shows in Settings).</summary>
    private static async Task<int?> ReadBatteryAsync(string id)
    {
        try
        {
            var di = await DeviceInformation.CreateFromIdAsync(id, new[] { ContainerKey }, DeviceInformationKind.AssociationEndpoint);
            if (di.Properties.TryGetValue(ContainerKey, out var cid) && cid is Guid container)
            {
                var nodes = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", new[] { BatteryKey }, DeviceInformationKind.Device);
                foreach (var n in nodes)
                    if (n.Properties.TryGetValue(BatteryKey, out var b) && b is byte level) return level;
            }
        }
        catch { }
        return null;
    }
}
