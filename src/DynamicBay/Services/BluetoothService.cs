using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicBay.Services;

public sealed class BluetoothDeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsAudio { get; init; }
    public int? Battery { get; init; }
}

/// <summary>
/// Announces Bluetooth devices connecting/disconnecting (with battery level when Windows exposes it).
/// Watches every paired device and reacts to its "connected" flag changing - that is reported reliably for
/// headphones, speakers and the like, also for devices paired later.
/// </summary>
public sealed partial class BluetoothService : ObservableObject
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedKey = "System.Devices.Aep.IsConnected";
    private DeviceWatcher? _watcher;
    private readonly Dictionary<string, string> _names = new();
    private readonly HashSet<string> _connected = new();
    private bool _enumerated;

    public event Action<BluetoothDeviceInfo, bool>? DeviceChanged; // bool = connected

    public void Start()
    {
        try
        {
            string aqs = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            _watcher = DeviceInformation.CreateWatcher(aqs, new[] { ConnectedKey }, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, d) =>
            {
                lock (_names) _names[d.Id] = d.Name;
                if (IsConnected(d.Properties)) SetConnected(d.Id, true);
            };
            _watcher.Updated += (_, u) =>
            {
                if (u.Properties.ContainsKey(ConnectedKey)) SetConnected(u.Id, IsConnected(u.Properties));
            };
            _watcher.Removed += (_, u) => SetConnected(u.Id, false);
            _watcher.EnumerationCompleted += (_, _) =>
            {
                _enumerated = true;
                lock (_names) Log.Info($"Bluetooth: {_names.Count} paired, connected: {string.Join(", ", _connected.Select(id => _names.GetValueOrDefault(id, "?")))}");
            };
            _watcher.Start();
        }
        catch (Exception ex) { Log.Error("Bluetooth", ex); }
    }

    private static bool IsConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(ConnectedKey, out var v) && v is bool b && b;

    private async void SetConnected(string id, bool connected)
    {
        string name;
        lock (_names)
        {
            if (connected ? !_connected.Add(id) : !_connected.Remove(id)) return; // no change
            name = _names.GetValueOrDefault(id, "Bluetooth");
        }
        if (!_enumerated) return; // already connected at startup: no announcement
        var info = connected ? await DescribeAsync(id, name) : new BluetoothDeviceInfo { Id = id, Name = name, IsAudio = LooksLikeAudio(name) };
        Log.Info($"Bluetooth: {name} {(connected ? "connected" : "disconnected")}{(info.IsAudio ? " (audio)" : "")}{(info.Battery is int b ? $", battery {b} %" : "")}");
        Application.Current?.Dispatcher.Invoke(() => DeviceChanged?.Invoke(info, connected));
    }

    private static bool LooksLikeAudio(string? name)
    {
        if (name is null) return false;
        string n = name.ToLowerInvariant();
        return new[] { "airpods", "buds", "headphone", "headset", "wh-", "wf-", "beats", "jbl", "bose", "sony", "earbuds", "speaker", "soundcore" }
            .Any(n.Contains);
    }

    private static async Task<BluetoothDeviceInfo> DescribeAsync(string id, string name)
    {
        bool audio = LooksLikeAudio(name);
        int? battery = null;
        try
        {
            var dev = await BluetoothDevice.FromIdAsync(id);
            if (dev is not null)
            {
                var major = dev.ClassOfDevice.MajorClass;
                audio |= major == BluetoothMajorClass.AudioVideo;
                // Battery lives on the PnP devnodes sharing the device's container.
                var di = await DeviceInformation.CreateFromIdAsync(id, new[] { "System.Devices.Aep.ContainerId" });
                if (di.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var cid) && cid is Guid container)
                {
                    var nodes = await DeviceInformation.FindAllAsync(
                        $"System.Devices.ContainerId:=\"{{{container}}}\"", new[] { BatteryKey }, DeviceInformationKind.Device);
                    foreach (var n in nodes)
                        if (n.Properties.TryGetValue(BatteryKey, out var b) && b is byte level) { battery = level; break; }
                }
            }
        }
        catch { }
        return new BluetoothDeviceInfo { Id = id, Name = name, IsAudio = audio, Battery = battery };
    }
}
