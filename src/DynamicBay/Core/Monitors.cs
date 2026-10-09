using System.Runtime.InteropServices;

namespace DynamicBay.Core;

public sealed record MonitorInfo(string Device, Native.RECT Bounds, Native.RECT Work, double Scale, bool IsPrimary);

/// <summary>Enumerates displays in physical pixels (the app is per-monitor DPI aware).</summary>
public static class Monitors
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            list.Add(Describe(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo Describe(IntPtr hMon)
    {
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(hMon, ref info);
        uint dpi = 96;
        try { Native.GetDpiForMonitor(hMon, 0, out dpi, out _); } catch { }
        return new MonitorInfo(info.szDevice, info.rcMonitor, info.rcWork, dpi / 96.0, (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0);
    }

    public static MonitorInfo Primary() =>
        Describe(Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTOPRIMARY));

    public static MonitorInfo FromPoint(int x, int y) =>
        Describe(Native.MonitorFromPoint(new Native.POINT { X = x, Y = y }, Native.MONITOR_DEFAULTTONEAREST));

    /// <summary>The monitor with this device name, or the primary one if it is not connected.</summary>
    public static MonitorInfo ByDevice(string? device)
    {
        if (!string.IsNullOrEmpty(device))
        {
            var match = All().FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return Primary();
    }
}
