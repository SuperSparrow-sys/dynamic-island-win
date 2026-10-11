using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicBay.Core;

/// <summary>
/// Helper processes DynamicBay starts for itself (tailscale.exe watching or asking for status) go into one Windows
/// job that ends them together with DynamicBay - also when it crashes, is killed or restarts for an update. Without
/// it every "tailscale debug watch-ipn" of an ended DynamicBay kept running for good. Apps the user uses
/// (CursorHop, players) are never put in here.
/// </summary>
public static class ChildProcesses
{
    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    /// <summary>Ties the process to DynamicBay's lifetime. False if Windows refused (the process then runs as before).</summary>
    public static bool Adopt(Process process)
    {
        try
        {
            IntPtr job = Job.Value;
            return job != IntPtr.Zero && AssignProcessToJobObject(job, process.Handle);
        }
        catch { return false; }
    }

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ptr, (uint)size)) { CloseHandle(job); return IntPtr.Zero; }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job; // never closed: the handle goes with the process, and that ends the job's processes
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Reads, Writes, Others, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
