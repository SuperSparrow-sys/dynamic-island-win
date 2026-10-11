using System.Runtime.InteropServices;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// One NVIDIA driver setting for one game: "Vulkan/OpenGL present method" = "Prefer layered on DXGI Swapchain". OpenGL
/// games in fullscreen (Minecraft with F11) then present through DXGI, and Windows draws the island above them like over
/// a borderless game. Same as the NVIDIA Control Panel, per program profile. NVAPI from nvapi64.dll (the driver ships
/// it); IDs, values and structure layouts from NVIDIA's open headers (github.com/NVIDIA/nvapi).
/// </summary>
public static class NvidiaProfile
{
    public const int Ok = 0, NoNvidia = -6, NeedsAdmin = -137, SettingNotFound = -160, ProfileNotFound = -163, AppNotFound = -166;
    public const uint PresentMethodId = 0x20D690F8; // OGL_CPL_PREFER_DXPRESENT_ID
    public const uint PreferLayered = 1;            // OGL_CPL_PREFER_DXPRESENT_PREFER_ENABLED

    // Structure sizes and field offsets (x64, NvAPI_UnicodeString = 2048 UTF-16 units).
    internal const int StringBytes = 4096;
    internal const int SettingSize = 12328, SettingIdOffset = 4100, SettingTypeOffset = 4104, SettingCurrentOffset = 8224;
    internal const int AppSize = 20492, AppNameOffset = 8;
    internal const int ProfileSize = 4116, ProfileNameOffset = 4;
    internal static uint Version(int size, int ver) => (uint)size | (uint)(ver << 16);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fn0();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnOut(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fn1(IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fn2(IntPtr a, IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fn2Out(IntPtr a, IntPtr b, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fn3(IntPtr a, IntPtr b, IntPtr c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnFindApp(IntPtr session, IntPtr name, out IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSetting(IntPtr session, IntPtr profile, uint id);

    /// <summary>
    /// Turns the setting on or back to the driver default for <paramref name="exe"/> (file name, e.g. javaw.exe). Returns
    /// an NVAPI status: <see cref="NeedsAdmin"/> when the driver wants an elevated caller, <see cref="NoNvidia"/> without one.
    /// </summary>
    public static int SetPresentLayered(string exe, bool on)
    {
        if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib)) return NoNvidia;
        var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(lib, "nvapi_QueryInterface"));
        T F<T>(uint id) where T : Delegate
        {
            var p = query(id);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"NVAPI 0x{id:X8}");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        int st = F<Fn0>(0x0150E828)(); // Initialize
        if (st != Ok) return st;
        st = F<FnOut>(0x0694D52E)(out var session); // DRS_CreateSession
        if (st != Ok) return st;
        var name = Marshal.AllocHGlobal(StringBytes);
        var app = Marshal.AllocHGlobal(AppSize);
        var profileInfo = Marshal.AllocHGlobal(ProfileSize);
        var setting = Marshal.AllocHGlobal(SettingSize);
        try
        {
            st = F<Fn1>(0x375DBD6B)(session); // DRS_LoadSettings
            if (st != Ok) return st;
            WriteString(name, exe);
            Clear(app, AppSize);
            Marshal.WriteInt32(app, 0, (int)Version(AppSize, 4));
            st = F<FnFindApp>(0xEEE566B2)(session, name, out var profile, app); // DRS_FindApplicationByName
            if (st == AppNotFound || st == ProfileNotFound)
            {
                if (!on) return Ok; // nothing set for this game
                profile = OwnProfile(F<Fn2Out>(0xCC176068), F<Fn2Out>(0x7E4A9A0B), session, profileInfo, exe, out st);
                if (st != Ok) return st;
                Clear(app, AppSize);
                Marshal.WriteInt32(app, 0, (int)Version(AppSize, 4));
                WriteString(app + AppNameOffset, exe);
                st = F<Fn3>(0x4347A9DE)(session, profile, app); // DRS_CreateApplication
                if (st != Ok) return st;
            }
            else if (st != Ok) return st;

            if (on)
            {
                Clear(setting, SettingSize);
                Marshal.WriteInt32(setting, 0, (int)Version(SettingSize, 1));
                Marshal.WriteInt32(setting, SettingIdOffset, unchecked((int)PresentMethodId));
                Marshal.WriteInt32(setting, SettingTypeOffset, 0); // NVDRS_DWORD_TYPE
                Marshal.WriteInt32(setting, SettingCurrentOffset, (int)PreferLayered);
                st = F<Fn3>(0x577DD202)(session, profile, setting); // DRS_SetSetting
            }
            else
            {
                st = F<FnSetting>(0xE4A26362)(session, profile, PresentMethodId); // DRS_DeleteProfileSetting
                if (st == SettingNotFound) st = Ok;
            }
            if (st != Ok) return st;
            return F<Fn1>(0xFCBC7E14)(session); // DRS_SaveSettings
        }
        finally
        {
            try { F<Fn1>(0xDAD9CFF8)(session); } catch { } // DRS_DestroySession
            Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); Marshal.FreeHGlobal(profileInfo); Marshal.FreeHGlobal(setting);
        }
    }

    /// <summary>Our own profile for a game the driver does not know ("DynamicBay javaw.exe"), reused when it exists.</summary>
    private static IntPtr OwnProfile(Delegate create, Delegate find, IntPtr session, IntPtr info, string exe, out int st)
    {
        string profileName = "DynamicBay " + exe;
        var name = Marshal.AllocHGlobal(StringBytes);
        try
        {
            WriteString(name, profileName);
            st = ((Fn2Out)find)(session, name, out var profile);
            if (st == Ok) return profile;
            Clear(info, ProfileSize);
            Marshal.WriteInt32(info, 0, (int)Version(ProfileSize, 1));
            WriteString(info + ProfileNameOffset, profileName);
            st = ((Fn2Out)create)(session, info, out profile);
            return profile;
        }
        finally { Marshal.FreeHGlobal(name); }
    }

    private static void Clear(IntPtr p, int size)
    {
        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
    }

    private static void WriteString(IntPtr p, string s)
    {
        Clear(p, StringBytes);
        var chars = s.AsSpan(0, Math.Min(s.Length, StringBytes / 2 - 1));
        for (int i = 0; i < chars.Length; i++) Marshal.WriteInt16(p, i * 2, chars[i]);
    }

    /// <summary>
    /// Runs <see cref="SetPresentLayered"/>, and once more elevated (Windows asks) when the driver wants an administrator.
    /// Returns the status; -1 when the user declined the Windows prompt.
    /// </summary>
    public static async Task<int> ApplyAsync(string exe, bool on)
    {
        int st;
        try { st = await Task.Run(() => SetPresentLayered(exe, on)); }
        catch (Exception ex) { Log.Error("NVIDIA profile", ex); return int.MinValue; }
        Log.Info($"NVIDIA profile: {exe} present method {(on ? "layered" : "default")} -> {st}");
        if (st != NeedsAdmin) return st;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, UseShellExecute = true, Verb = "runas",
                Arguments = $"--nvidia-present \"{exe}\" {(on ? "on" : "off")}",
            })!;
            await p.WaitForExitAsync();
            Log.Info($"NVIDIA profile (elevated): {exe} -> {p.ExitCode}");
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return -1; } // declined
    }
}
