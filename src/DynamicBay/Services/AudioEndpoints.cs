using System.Runtime.InteropServices;

namespace DynamicBay.Services;

public enum AudioFlow { Output = 0, Input = 1 }

/// <summary>One Windows sound device (an "endpoint"), as Core Audio sees it.</summary>
/// <param name="Connectable">Owned by the Bluetooth driver: can be asked to connect the headset.</param>
public sealed record AudioEndpointInfo(string Id, AudioFlow Flow, string Name, string Device, bool Active, bool Bluetooth, bool HandsFree, bool Connectable);

/// <summary>
/// Windows sound devices: list them, make one the default (what the sound settings do), and connect or disconnect
/// a paired Bluetooth headset (what the "Verbinden" button in the sound settings does - a one-shot request to the
/// Bluetooth audio driver, so it also works when the AirPods are in their case nearby or were last used on the phone).
/// </summary>
public static class AudioEndpoints
{
    private const int StateActive = 1, StateAll = 0xF;

    /// <summary>All output and input devices, including paired Bluetooth headsets that are not connected right now.</summary>
    public static List<AudioEndpointInfo> List()
    {
        var list = new List<AudioEndpointInfo>();
        try
        {
            var en = (IMMDeviceEnumerator)CreateEnumerator();
            foreach (var flow in new[] { AudioFlow.Output, AudioFlow.Input })
            {
                en.EnumAudioEndpoints((int)flow, StateAll, out var coll);
                coll.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    try
                    {
                        coll.Item(i, out var dev);
                        dev.GetId(out string id);
                        dev.GetState(out int state);
                        string kind = KsDeviceId(dev) ?? "";
                        bool bt = IsBluetoothPath(kind);
                        // Not connected and not Bluetooth: an unplugged jack or a removed USB device - not worth listing.
                        if (state != StateActive && !bt) continue;
                        dev.OpenPropertyStore(0, out var props);
                        string name = ReadString(props, FriendlyName) ?? id;
                        string device = ReadString(props, InterfaceName) ?? name;
                        // A Bluetooth microphone always means the hands-free (call) mode: mono, telephone quality also for what you hear.
                        bool handsFree = bt && (flow == AudioFlow.Input || kind.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                                                || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase));
                        list.Add(new AudioEndpointInfo(id, flow, name, device, state == StateActive, bt, handsFree, IsBluetoothDriverPath(kind)));
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { Core.Log.Info($"Audio endpoints: {ex.Message}"); }
        return list;
    }

    /// <summary>
    /// Classic Bluetooth audio ("bthenum", "bthhfenum", LE audio "bthleenum") - and Intel's Bluetooth offload ("intcbt…"),
    /// which many laptops use instead: there the connected headset plays through the Intel audio driver.
    /// </summary>
    public static bool IsBluetoothPath(string path) =>
        path.Contains("bthenum", StringComparison.OrdinalIgnoreCase) || path.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase)
        || path.Contains("bthleenum", StringComparison.OrdinalIgnoreCase) || path.Contains("intcbt", StringComparison.OrdinalIgnoreCase);

    /// <summary>The endpoint the Bluetooth driver itself owns (the one that can be asked to connect).</summary>
    public static bool IsBluetoothDriverPath(string path) =>
        path.Contains("bthenum", StringComparison.OrdinalIgnoreCase) || path.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase);

    public static string? DefaultId(AudioFlow flow, int role = 1 /* multimedia */)
    {
        try
        {
            var en = (IMMDeviceEnumerator)CreateEnumerator();
            if (en.GetDefaultAudioEndpoint((int)flow, role, out var dev) != 0 || dev is null) return null;
            dev.GetId(out string id);
            return id;
        }
        catch { return null; }
    }

    /// <summary>Makes the device the default for everything (games and music, and calls).</summary>
    public static bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (int role in new[] { 0, 1, 2 }) policy.SetDefaultEndpoint(id, role);
            return true;
        }
        catch (Exception ex) { Core.Log.Info($"Set default audio device: {ex.Message}"); return false; }
    }

    private static IAudioEndpointVolume? Volume(string id)
    {
        var en = (IMMDeviceEnumerator)CreateEnumerator();
        en.GetDevice(id, out var dev);
        var iid = typeof(IAudioEndpointVolume).GUID;
        dev.Activate(ref iid, 23, IntPtr.Zero, out var obj);
        return obj as IAudioEndpointVolume;
    }

    /// <summary>Mute state and volume (0..1) of a device, null if it cannot be read.</summary>
    public static (bool muted, float level)? State(string id)
    {
        try
        {
            if (Volume(id) is not { } vol) return null;
            vol.GetMute(out bool muted);
            vol.GetMasterVolumeLevelScalar(out float level);
            return (muted, level);
        }
        catch { return null; }
    }

    /// <summary>Mutes or unmutes a device (outputs only - microphones are never touched by the switch).</summary>
    public static void SetMute(string id, bool mute)
    {
        try
        {
            if (Volume(id) is not { } vol) return;
            var ctx = Guid.Empty;
            vol.SetMute(mute, ref ctx);
        }
        catch (Exception ex) { Core.Log.Info($"Set mute: {ex.Message}"); }
    }

    /// <summary>
    /// The Bluetooth audio driver's filter, opened directly from its device path: for a
    /// disconnected headset Windows has no topology connection to it, so it cannot be reached through the endpoint.
    /// Sends the one-shot connect/disconnect request like the "Verbinden" button in the sound settings.
    /// </summary>
    public static int ConnectViaFilter(string filterPath, bool connect)
    {
        using var h = CreateFile(filterPath, 0xC0000000 /* read | write */, 3 /* share read | write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
        if (h.IsInvalid) return Marshal.GetHRForLastWin32Error();
        var prop = new KsProperty { Set = KsPropSetBtAudio, Id = connect ? 0u : 1u, Flags = 1 /* GET */ };
        bool ok = DeviceIoControl(h, 0x2F0003 /* IOCTL_KS_PROPERTY */, ref prop, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
        return ok ? 0 : Marshal.GetHRForLastWin32Error();
    }

    /// <summary>Topology id of the driver filter ("{2}." + device path) -> the device path to open.</summary>
    public static string? FilterPathOf(string endpointId)
    {
        try
        {
            var en = (IMMDeviceEnumerator)CreateEnumerator();
            en.GetDevice(endpointId, out var dev);
            var id = KsDeviceId(dev);
            if (id is null) return null;
            int i = id.IndexOf(@"\\?\", StringComparison.Ordinal);
            return i >= 0 ? id[i..] : null;
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, ref KsProperty input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    /// <summary>Why the last connect failed (for the log and tests).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Asks the Bluetooth audio driver to connect (or disconnect) the headset behind this endpoint. Blocks a moment: call off the UI thread.</summary>
    public static bool Connect(string endpointId, bool connect = true)
    {
        try
        {
            var en = (IMMDeviceEnumerator)CreateEnumerator();
            en.GetDevice(endpointId, out var dev);
            var ks = BtAudioControl(dev);
            if (ks is null)
            {
                // Disconnected: no topology connection to the driver - open its filter by device path instead.
                var path = FilterPathOf(endpointId);
                int hrPath = path is null ? -1 : ConnectViaFilter(path, connect);
                Core.Log.Info($"Bluetooth audio {(connect ? "connect" : "disconnect")} (driver path): 0x{hrPath:X8}");
                return hrPath == 0;
            }
            var prop = new KsProperty { Set = KsPropSetBtAudio, Id = connect ? 0u : 1u /* ONESHOT_RECONNECT / ONESHOT_DISCONNECT */, Flags = 1 /* GET */ };
            int hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
            Core.Log.Info($"Bluetooth audio {(connect ? "connect" : "disconnect")}: 0x{hr:X8}");
            return hr >= 0;
        }
        catch (Exception ex) { LastError = ex.ToString(); Core.Log.Info($"Bluetooth audio connect: {ex}"); return false; }
    }

    // ---------- device topology: endpoint → the driver's filter ----------

    private static IConnector? DriverConnector(IMMDevice dev)
    {
        var iid = typeof(IDeviceTopology).GUID;
        dev.Activate(ref iid, 23, IntPtr.Zero, out var obj);
        if (obj is not IDeviceTopology topo) return null;
        topo.GetConnector(0, out var connector);
        // A disconnected headset has no connection here (0x80070003): the caller then tries the driver path.
        if (connector.GetConnectedTo(out var other) != 0) return null;
        return other;
    }

    /// <summary>The driver filter's device path: "...BTHENUM..." for Bluetooth audio, "...BTHHFENUM..." for its hands-free (call) part.</summary>
    public static string? KsDeviceIdOf(string endpointId)
    {
        var en = (IMMDeviceEnumerator)CreateEnumerator();
        en.GetDevice(endpointId, out var dev);
        return KsDeviceId(dev);
    }

    private static string? KsDeviceId(IMMDevice dev)
    {
        try
        {
            var iid = typeof(IDeviceTopology).GUID;
            dev.Activate(ref iid, 23, IntPtr.Zero, out var obj);
            if (obj is not IDeviceTopology topo) return null;
            topo.GetConnector(0, out var connector);
            return connector.GetDeviceIdConnectedTo(out string id) == 0 ? id : null;
        }
        catch { return null; }
    }

    private static IKsControl? BtAudioControl(IMMDevice dev)
    {
        if (DriverConnector(dev) is not IPart part) return null;
        var iid = typeof(IKsControl).GUID;
        part.Activate(1 /* CLSCTX_INPROC_SERVER */, ref iid, out var obj);
        return obj as IKsControl;
    }

    // ---------- properties ----------

    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);   // "Kopfhörer (AirPods von Jona)"
    private static readonly PropertyKey InterfaceName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);   // "AirPods von Jona"

    private static string? ReadString(IPropertyStore props, PropertyKey key)
    {
        var pv = new PropVariant();
        try
        {
            if (props.GetValue(ref key, out pv) != 0) return null;
            return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.pointer) : null;
        }
        catch { return null; }
        finally { try { PropVariantClear(ref pv); } catch { } }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey(Guid fmt, int pid) { public Guid Fmtid = fmt; public int Pid = pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty { public Guid Set; public uint Id; public uint Flags; }

    private static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    /// <summary>
    /// The Core Audio device enumerator, created from its CLSID. Not with "new" on a [ComImport] class: several services
    /// declare one for the same CLSID, the runtime then hands out the first one's wrapper type and the cast fails
    /// (only in the published app, depending on which service starts first).
    /// </summary>
    private static object CreateEnumerator() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    // ---------- Core Audio interop ----------

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    /// <summary>Called by Windows (on its own thread) when a device comes, goes or becomes the default.</summary>
    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    /// <summary>Undocumented but stable since Windows 7: what the sound settings use to set the default device.</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        void GetConnectorCount(out uint count);
        void GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPart
    {
        [PreserveSig] int GetName(out IntPtr name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId(out IntPtr id);
        [PreserveSig] int GetPartType(out int type);
        [PreserveSig] int GetSubType(out Guid subType);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr control);
        [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
        [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
        [PreserveSig] int GetTopologyObject(out IntPtr topology);
        void Activate(uint clsCtx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
    }
}
