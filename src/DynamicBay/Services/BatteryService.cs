using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DynamicBay.Services;

public enum BatteryEvent { PluggedIn, Unplugged, Low, Full }

public sealed partial class BatteryService : ObservableObject
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool? _lastPlugged;
    private bool _lowNotified, _fullNotified;
    private readonly Core.AppSettings _settings;

    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private int _percent;
    [ObservableProperty] private bool _isPluggedIn;
    [ObservableProperty] private bool _isCharging;
    [ObservableProperty] private bool _isLow;

    public event Action<BatteryEvent>? Changed;

    public BatteryService(Core.AppSettings settings) => _settings = settings;

    public void Start()
    {
        Read(initial: true);
        _poll.Tick += (_, _) => Read(false);
        _poll.Start();
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.StatusChange)
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Read(false));
        };
    }

    private void Read(bool initial)
    {
        var ps = WinForms.SystemInformation.PowerStatus;
        HasBattery = (ps.BatteryChargeStatus & WinForms.BatteryChargeStatus.NoSystemBattery) == 0 &&
                     ps.BatteryChargeStatus != WinForms.BatteryChargeStatus.Unknown;
        if (!HasBattery) return;
        Percent = (int)Math.Round(ps.BatteryLifePercent * 100);
        IsPluggedIn = ps.PowerLineStatus == WinForms.PowerLineStatus.Online;
        IsCharging = (ps.BatteryChargeStatus & WinForms.BatteryChargeStatus.Charging) != 0 || (IsPluggedIn && Percent < 100);
        IsLow = !IsPluggedIn && Percent <= _settings.BatteryLowThreshold;

        if (initial)
        {
            // Don't announce a state that already existed when DynamicBay started.
            _lastPlugged = IsPluggedIn;
            _fullNotified = IsPluggedIn && Percent >= 100;
            return;
        }
        if (!_settings.BatteryEnabled) { _lastPlugged = IsPluggedIn; return; }

        if (_lastPlugged != IsPluggedIn)
        {
            Changed?.Invoke(IsPluggedIn ? BatteryEvent.PluggedIn : BatteryEvent.Unplugged);
            _lowNotified = false;
            _fullNotified = false;
        }
        _lastPlugged = IsPluggedIn;

        if (IsLow && !_lowNotified) { _lowNotified = true; Changed?.Invoke(BatteryEvent.Low); }
        if (IsPluggedIn && Percent >= 100 && !_fullNotified) { _fullNotified = true; Changed?.Invoke(BatteryEvent.Full); }
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo(int percent, bool plugged)
    {
        HasBattery = true; Percent = percent; IsPluggedIn = plugged; IsCharging = plugged; IsLow = percent <= 20 && !plugged;
    }
}
