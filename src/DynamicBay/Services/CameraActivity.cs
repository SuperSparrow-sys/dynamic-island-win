using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Which apps stream a camera right now, from Media Foundation's sensor activity monitor (the documented source of
/// camera indicators, no admin rights). Since the Windows updates of 14.10.2026 (26H2) the privacy records in the
/// registry are no longer written on every PC (docs/offene_punkte.md, point 15); this works on those too. Reports
/// come on a Media Foundation thread; <see cref="Changed"/> is raised there.
/// </summary>
public sealed class CameraActivity : IDisposable
{
    private readonly object _gate = new();
    private IMFSensorActivityMonitor? _monitor;
    private Callback? _callback;
    private IReadOnlyList<string> _apps = Array.Empty<string>();
    private bool _started;

    /// <summary>Names of the processes streaming a camera now (this app never counts).</summary>
    public IReadOnlyList<string> Apps { get { lock (_gate) return _apps; } }
    public bool InUse => Apps.Count > 0;
    /// <summary>Why <see cref="Start"/> failed ("" when it runs), for the log and the diagnosis.</summary>
    public string Failure { get; private set; } = "";
    public event Action? Changed;

    /// <summary>Starts listening; false when Media Foundation has no monitor here (older Windows): then only the registry counts.</summary>
    public bool Start()
    {
        if (_monitor is not null) return true;
        try
        {
            int hr = MFStartup(MF_VERSION, MFSTARTUP_LITE); // the monitor answers MF_E_SHUTDOWN without it
            if (hr < 0) { Failure = $"startup 0x{hr:X8}"; Log.Info($"Camera activity: Media Foundation did not start (0x{hr:X8})"); return false; }
            _started = true;
            _callback = new Callback(this);
            hr = MFCreateSensorActivityMonitor(_callback, out var monitor);
            if (hr < 0 || monitor is null) { Failure = $"create 0x{hr:X8}"; Log.Info($"Camera activity: no monitor (0x{hr:X8})"); return false; }
            hr = monitor.Start();
            if (hr < 0) { Failure = $"start 0x{hr:X8}"; Log.Info($"Camera activity: start failed (0x{hr:X8})"); Marshal.ReleaseComObject(monitor); return false; }
            _monitor = monitor;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidCastException)
        {
            Failure = $"{ex.GetType().Name}: {ex.Message}";
            Log.Info($"Camera activity: not available ({ex.GetType().Name})");
            return false;
        }
    }

    private void OnReport(IMFSensorActivitiesReport report)
    {
        var apps = new List<string>();
        int own = Environment.ProcessId;
        try
        {
            if (report.GetCount(out uint devices) < 0) return;
            for (uint d = 0; d < devices; d++)
            {
                if (report.GetActivityReport(d, out var device) < 0 || device is null) continue;
                try
                {
                    if (device.GetProcessCount(out uint processes) < 0) continue;
                    for (uint p = 0; p < processes; p++)
                    {
                        if (device.GetProcessActivity(p, out var activity) < 0 || activity is null) continue;
                        try
                        {
                            if (activity.GetStreamingState(out int streaming) >= 0 && streaming != 0
                                && activity.GetProcessId(out uint pid) >= 0 && pid != own)
                                apps.Add(ProcessName((int)pid));
                        }
                        finally { Marshal.ReleaseComObject(activity); }
                    }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        catch (COMException ex) { Log.Info($"Camera activity: report failed (0x{ex.HResult:X8})"); return; }
        var now = apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool changed;
        lock (_gate)
        {
            changed = !now.SequenceEqual(_apps, StringComparer.OrdinalIgnoreCase);
            _apps = now;
        }
        if (changed) Changed?.Invoke();
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException) { return $"pid {pid}"; } // ended meanwhile
        catch (InvalidOperationException) { return $"pid {pid}"; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _monitor, null) is { } m)
        {
            try { m.Stop(); } catch (COMException) { }
            Marshal.ReleaseComObject(m);
        }
        if (_started) { _started = false; MFShutdown(); }
    }

    // ---- interop (mfidl.h, Windows SDK 10.0.26100) ----

    private const uint MF_VERSION = 0x00020070, MFSTARTUP_LITE = 1; // mfapi.h: MF_SDK_VERSION 2, MF_API_VERSION 0x70

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();

    [DllImport("mfsensorgroup.dll", ExactSpelling = true)]
    private static extern int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback, out IMFSensorActivityMonitor monitor);

    [ComVisible(true)]
    private sealed class Callback(CameraActivity owner) : IMFSensorActivitiesReportCallback
    {
        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            owner.OnReport(report);
            return 0;
        }
    }

    [ComImport, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivityMonitor
    {
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
    }

    [ComImport, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivitiesReportCallback
    {
        [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
    }

    [ComImport, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivitiesReport
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport report);
        [PreserveSig] int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string name, out IMFSensorActivityReport report);
    }

    [ComImport, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivityReport
    {
        [PreserveSig] int GetFriendlyName(IntPtr name, uint size, out uint written);
        [PreserveSig] int GetSymbolicLink(IntPtr link, uint size, out uint written);
        [PreserveSig] int GetProcessCount(out uint count);
        [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
    }

    [ComImport, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorProcessActivity
    {
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int GetStreamingState(out int streaming);
        [PreserveSig] int GetStreamingMode(out int mode);
        [PreserveSig] int GetReportTime(out long fileTime);
    }
}
