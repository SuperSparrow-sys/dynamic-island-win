using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace DynamicBay.Services;

/// <summary>What AirPods tell everybody nearby: battery per earbud and case, charging. Levels in percent, null = unknown.</summary>
public sealed record AirPodsReading(int Model, int? Left, int? Right, int? Case, bool LeftCharging, bool RightCharging, bool CaseCharging);

/// <summary>
/// Apple's "proximity pairing" advertisement (manufacturer data of company 0x004C, type 0x07), which AirPods send about
/// once a second. Only the first bytes are plain; the rest is encrypted for the owner's iPhone. Layout as decoded by
/// OpenPods and AirPodsDesktop: [0] 0x07, [1] length, [2] 0x01, [3..4] model, [5] status, [6] earbuds (two nibbles),
/// [7] charging flags (high nibble) and case (low nibble), [8] lid. A nibble 0-10 is tens of percent, 15 is unknown.
/// Bit 0x20 of the status says whether the left earbud sends; the sending one's level sits in the low nibble.
/// </summary>
public static class AirPodsAdvert
{
    public const ushort AppleCompanyId = 0x004C;
    public const byte ProximityPairing = 0x07;

    public static AirPodsReading? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 9 || d[0] != ProximityPairing || d[1] < 7) return null;
        int model = d[3] | d[4] << 8;
        if (model >> 8 != 0x20) return null; // Apple and Beats headphones: 0x20xx
        byte status = d[5];
        bool leftSends = (status & 0x20) != 0;
        int sender = d[6] & 0xF, other = d[6] >> 4;
        int flags = d[7] >> 4;
        return new AirPodsReading(
            model,
            Level(leftSends ? sender : other),
            Level(leftSends ? other : sender),
            Level(d[7] & 0xF),
            LeftCharging: (flags & (leftSends ? 1 : 2)) != 0,
            RightCharging: (flags & (leftSends ? 2 : 1)) != 0,
            CaseCharging: (flags & 4) != 0);
    }

    private static int? Level(int nibble) => nibble <= 10 ? nibble * 10 : null;

    /// <summary>What the "Geräte" widget shows for AirPods: the emptier earbud (the case is not worn). Null: neither known.</summary>
    public static int? Lowest(int? left, int? right) => left is int l && right is int r ? Math.Min(l, r) : left ?? right;

    /// <summary>Weaker than this is somebody else's AirPods in the next room.</summary>
    public const short MinRssi = -85;

    /// <summary>
    /// Other people's AirPods send the same message. Stay with the sender we have; take another one only when it is
    /// clearly closer (8 dB) or ours has been silent for 15 s (AirPods change their address every few minutes).
    /// </summary>
    public static bool TakeSender(ulong current, short currentRssi, DateTime currentSeen, ulong candidate, short rssi, DateTime now)
    {
        if (rssi < MinRssi) return false;
        if (current == 0 || candidate == current) return true;
        return rssi >= currentRssi + 8 || now - currentSeen > TimeSpan.FromSeconds(15);
    }
}

/// <summary>
/// The "AirPods" widget: battery of each earbud and the case, read from the AirPods' own Bluetooth advertisements
/// (passive scan, nothing is sent to them), plus connect. Also gives the "Geräte" widget the AirPods' lowest level.
/// Scans only while AirPods are connected and one of the two widgets is on a page.
/// </summary>
public sealed partial class AirPodsService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AudioDevicesService _audio;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _stale = new() { Interval = TimeSpan.FromSeconds(5) };
    private BluetoothLEAdvertisementWatcher? _watcher;
    private ulong _sender;
    private short _senderRssi;
    private DateTime _senderSeen;
    private bool _demo;

    private BluetoothService? _bluetooth;

    /// <summary>The paired AirPods as a sound device (for connecting them). Null: not in the sound devices list.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConnect))] private BluetoothAudioItem? _headset;
    /// <summary>Connected, as Bluetooth reports it (also when they have no sound device yet).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText), nameof(CanConnect))] private bool _connected;
    [ObservableProperty] private string _name = "AirPods";
    /// <summary>A reading from the last 30 s: the earbuds are shown one by one.</summary>
    [ObservableProperty] private bool _hasReading;
    [ObservableProperty] private int? _left;
    [ObservableProperty] private int? _right;
    /// <summary>Kept from the last time the lid was open: the case only reports while the earbuds are in it.</summary>
    [ObservableProperty] private int? _case;
    [ObservableProperty] private bool _leftCharging;
    [ObservableProperty] private bool _rightCharging;
    [ObservableProperty] private bool _caseCharging;

    public bool CanConnect => Headset is not null && !Connected;

    /// <summary>Shown instead of the earbuds while there is no reading.</summary>
    public string StateText => Connected ? (Loc.German ? "Akkustand wird gelesen" : "Reading battery") : (Loc.German ? "Nicht verbunden" : "Not connected");

    public AirPodsService(AppSettings settings, AudioDevicesService audio)
    {
        _settings = settings;
        _audio = audio;
        _stale.Tick += (_, _) => { if (DateTime.UtcNow - _senderSeen > TimeSpan.FromSeconds(30)) { HasReading = false; _stale.Stop(); } };
    }

    public static bool IsAirPods(string name) => name.Contains("AirPods", StringComparison.OrdinalIgnoreCase);

    public void Start(BluetoothService bluetooth)
    {
        _bluetooth = bluetooth;
        bluetooth.Devices.CollectionChanged += OnDevicesChanged;
        _audio.Headsets.CollectionChanged += OnDevicesChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        Update();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.HomeWidgets) or nameof(AppSettings.SystemWidgets)) Update();
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update();

    private void Update()
    {
        if (_demo || _bluetooth is null) return;
        // The sound devices list is replaced as a whole on every change: look the AirPods up again each time.
        Headset = _audio.Headsets.FirstOrDefault(h => IsAirPods(h.Name));
        var device = _bluetooth.Devices.FirstOrDefault(d => IsAirPods(d.Name));
        Connected = device is not null;
        Name = device?.Name ?? Headset?.Name ?? "AirPods";
        // The "Geräte" widget shows the AirPods' battery too.
        bool placed = new[] { Widgets.AirPods, Widgets.Devices }.Any(id => _settings.HomeWidgets.Contains(id) || _settings.SystemWidgets.Contains(id));
        if (Connected && placed) StartScan();
        else StopScan();
        if (!Connected) HasReading = false;
    }

    private void StartScan()
    {
        if (_watcher is not null) return;
        try
        {
            var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            // Only Apple's proximity pairing messages reach us (filtered in the Bluetooth stack).
            var prefix = new DataWriter();
            prefix.WriteByte(AirPodsAdvert.ProximityPairing);
            w.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(AirPodsAdvert.AppleCompanyId, prefix.DetachBuffer()));
            w.Received += OnReceived;
            w.Stopped += (s, e) => { if (e.Error != Windows.Devices.Bluetooth.BluetoothError.Success) Log.Info($"AirPods: scan stopped ({e.Error})"); };
            w.Start();
            _watcher = w;
            Log.Info("AirPods: listening for battery levels");
        }
        catch (Exception ex) { Log.Error("AirPods", ex); }
    }

    private void StopScan()
    {
        if (_watcher is null) return;
        _watcher.Received -= OnReceived;
        try { _watcher.Stop(); } catch { }
        _watcher = null;
        _stale.Stop();
        _sender = 0;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs a)
    {
        foreach (var m in a.Advertisement.ManufacturerData)
        {
            if (m.CompanyId != AirPodsAdvert.AppleCompanyId) continue;
            var bytes = new byte[m.Data.Length];
            using (var r = DataReader.FromBuffer(m.Data)) r.ReadBytes(bytes);
            if (AirPodsAdvert.Parse(bytes) is not { } reading) continue;
            ulong address = a.BluetoothAddress;
            short rssi = a.RawSignalStrengthInDBm;
            _ui.BeginInvoke(() =>
            {
                var now = DateTime.UtcNow;
                if (_watcher is null || !AirPodsAdvert.TakeSender(_sender, _senderRssi, _senderSeen, address, rssi, now)) return;
                _sender = address; _senderRssi = rssi; _senderSeen = now;
                Apply(reading);
            });
            return;
        }
    }

    private void Apply(AirPodsReading r)
    {
        Left = r.Left; Right = r.Right;
        if (r.Case is not null) Case = r.Case;
        LeftCharging = r.LeftCharging; RightCharging = r.RightCharging; CaseCharging = r.CaseCharging;
        HasReading = true;
        _stale.Start();
        // Windows knows no battery level for AirPods: the "Geräte" widget gets the emptier earbud.
        if (AirPodsAdvert.Lowest(r.Left, r.Right) is int lowest && _bluetooth?.Devices.FirstOrDefault(d => IsAirPods(d.Name)) is { } device) device.Battery = lowest;
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo(bool connected = true)
    {
        _demo = true;
        StopScan();
        Headset = new BluetoothAudioItem { Name = "AirPods", Headphones = true, Connected = connected };
        Connected = connected;
        Name = "AirPods";
        if (!connected) { HasReading = false; return; }
        Apply(new AirPodsReading(0x201B, 80, 70, 40, false, false, true));
    }
}
