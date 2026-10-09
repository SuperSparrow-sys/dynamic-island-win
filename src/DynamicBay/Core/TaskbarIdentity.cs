using System.Runtime.InteropServices;

namespace DynamicBay.Core;

/// <summary>
/// Gives a window its own taskbar identity (AppUserModelID + relaunch icon). With the identity package the process
/// otherwise inherits the package's app id, whose logo the taskbar cannot resolve for a sparse package (AppListEntry
/// is "none"), so it shows a grey square. With an own id the taskbar uses the icon given here.
/// </summary>
public static class TaskbarIdentity
{
    public const string SettingsAppId = "SuperSparrow.DynamicBay.Settings";

    public static void Apply(IntPtr hwnd, string appId, string displayName)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return;
            try
            {
                var exe = Environment.ProcessPath ?? "";
                Set(store, PKEY(5), appId);                        // System.AppUserModel.ID
                Set(store, PKEY(3), $"\"{exe}\"");                 // RelaunchCommand (a second start opens the settings)
                Set(store, PKEY(4), displayName);                  // RelaunchDisplayNameResource
                Set(store, PKEY(2), exe + ",0");                   // RelaunchIconResource
                store.Commit();
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex) { Log.Error("TaskbarIdentity", ex); }
    }

    /// <summary>
    /// AppUserModelID of a window: the per-window id (browsers, Electron apps) or, for Store apps, the package app id.
    /// </summary>
    public static string? Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) == 0 && store is not null)
            {
                try
                {
                    var key = PKEY(5);
                    store.GetValue(ref key, out var pv);
                    try { if (pv.vt == 31 && pv.pointer != IntPtr.Zero) return Marshal.PtrToStringUni(pv.pointer); }
                    finally { PropVariantClear(ref pv); }
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            GetWindowThreadProcessId(hwnd, out uint pid);
            var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                int len = 256;
                var sb = new System.Text.StringBuilder(len);
                return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref int length, System.Text.StringBuilder id);

        private static PropertyKey PKEY(int pid) => new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = pid };

    private static void Set(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, pointer = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref pv); }
        finally { PropVariantClear(ref pv); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(16)] private IntPtr _pad;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);
}
