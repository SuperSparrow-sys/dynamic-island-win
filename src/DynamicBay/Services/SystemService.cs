using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DynamicBay.Services;

/// <summary>CPU and memory load for the system widget (sampled only while the widget is visible).</summary>
public sealed partial class SystemService : ObservableObject
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private ulong _lastIdle, _lastTotal;

    [ObservableProperty] private double _cpu;      // 0..100
    [ObservableProperty] private double _memory;   // 0..100
    [ObservableProperty] private string _memoryText = "";

    public SystemService() => _tick.Tick += (_, _) => Sample();

    public void SetActive(bool active)
    {
        if (active && !_tick.IsEnabled) { Sample(); _tick.Start(); }
        else if (!active) _tick.Stop();
    }

    private void Sample()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong i = idle.Value, total = kernel.Value + user.Value; // kernel time includes idle
            if (_lastTotal != 0 && total > _lastTotal)
                Cpu = Math.Clamp(100.0 * (1 - (double)(i - _lastIdle) / (total - _lastTotal)), 0, 100);
            _lastIdle = i;
            _lastTotal = total;
        }
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            Memory = mem.dwMemoryLoad;
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0, totalGb = mem.ullTotalPhys / 1073741824.0;
            MemoryText = $"{usedGb:0.0} / {totalGb:0} GB";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME64 { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME64 idle, out FILETIME64 kernel, out FILETIME64 user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
