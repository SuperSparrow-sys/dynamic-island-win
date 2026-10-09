using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
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

/// <summary>Announces Bluetooth devices connecting/disconnecting (with battery level when Windows exposes it).</summary>
public sealed partial class BluetoothService : ObservableObject
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private DeviceWatcher? _watcher;
    private readonly Dictionary<string, string> _connected = new();
    private bool _enumerated;

    public event Action<BluetoothDeviceInfo, bool>? DeviceChanged; // bool = connected

    public void Start()
    {
        try
        {
            string aqs = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _watcher = DeviceInformation.CreateWatcher(aqs, new[] { "System.Devices.Aep.IsConnected" });
            _watcher.Added += (_, d) => OnAdded(d);
            _watcher.Removed += (_, u) => OnRemoved(u.Id);
            _watcher.Updated += (_, u) => { };
            _watcher.EnumerationCompleted += (_, _) => _enumerated = true;
            _watcher.Start();
        }
        catch { }
    }

    private async void OnAdded(DeviceInformation d)
    {
        lock (_connected) _connected[d.Id] = d.Name;
        if (!_enumerated) return; // already connected at startup: no announcement
        var info = await DescribeAsync(d.Id, d.Name);
        Application.Current.Dispatcher.Invoke(() => DeviceChanged?.Invoke(info, true));
    }

    private void OnRemoved(string id)
    {
        string? name;
        lock (_connected)
        {
            if (!_connected.Remove(id, out name)) return;
        }
        var info = new BluetoothDeviceInfo { Id = id, Name = name ?? "Bluetooth", IsAudio = LooksLikeAudio(name) };
        Application.Current.Dispatcher.Invoke(() => DeviceChanged?.Invoke(info, false));
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
