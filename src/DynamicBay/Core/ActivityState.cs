using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicBay.Core;

/// <summary>
/// Whether anyone can see the island: not while the session is locked or the display is off. Endless animations
/// (waveform, loops) stop then and frequent polls skip their work; both pick up at once when the user is back.
/// Measured 15.10.2026: everything kept running when nobody looked, the waveform alone 4-6 % of a core.
/// </summary>
public sealed class ActivityState
{
    public static ActivityState Current { get; } = new();

    private bool _locked, _displayOff;

    public bool Away => _locked || _displayOff;

    /// <summary>Away changed (on the thread that called <see cref="Set"/>, the UI thread in the app).</summary>
    public event Action? Changed;

    public void Set(bool? locked = null, bool? displayOff = null)
    {
        bool before = Away;
        if (locked is { } l) _locked = l;
        if (displayOff is { } d) _displayOff = d;
        if (Away == before) return;
        Log.Info(Away ? $"Away ({(_locked ? "locked" : "display off")}): animations and polls pause" : "Back: animations and polls resume");
        Changed?.Invoke();
    }

    public static bool ShouldAnimate(bool visible, bool loaded, bool away) => visible && loaded && !away;

    /// <summary>Whether this element's endless animation may run now.</summary>
    public static bool ShouldAnimate(FrameworkElement el) => ShouldAnimate(el.IsVisible, el.IsLoaded, Current.Away);

    private readonly List<(WeakReference<FrameworkElement> Element, Action<FrameworkElement> Update)> _watched = new();

    /// <summary>
    /// Calls <paramref name="update"/> for the element whenever Away changes. Held weakly: a closed window's elements
    /// are not kept alive. <paramref name="update"/> must not capture the element.
    /// </summary>
    public void Watch(FrameworkElement el, Action<FrameworkElement> update)
    {
        _watched.RemoveAll(w => !w.Element.TryGetTarget(out _));
        foreach (var w in _watched)
            if (w.Element.TryGetTarget(out var e) && ReferenceEquals(e, el)) return;
        _watched.Add((new WeakReference<FrameworkElement>(el), update));
    }

    private void UpdateWatched()
    {
        foreach (var (element, update) in _watched.ToList())
            if (element.TryGetTarget(out var el)) update(el);
    }

    /// <summary>Lock and unlock from Windows, the display state from a power setting notification to the message window.</summary>
    public void Start(MessageWindow msg, Dispatcher ui)
    {
        Changed += UpdateWatched;
        SystemEvents.SessionSwitch += (_, e) =>
        {
            bool? locked = e.Reason switch
            {
                SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect => true,
                SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect => false,
                _ => null,
            };
            if (locked is not null) ui.BeginInvoke(() => Set(locked: locked));
        };
        msg.Message += (m, w, l) =>
        {
            if (m == WM_POWERBROADCAST && w == PBT_POWERSETTINGCHANGE && DisplayOff(l) is { } off) Set(displayOff: off);
            return false;
        };
        var guid = ConsoleDisplayState;
        // Windows answers at once with the current state, then on every change.
        if (RegisterPowerSettingNotification(msg.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE) == IntPtr.Zero)
            Log.Info("Display state notifications unavailable");
    }

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: 0 off, 1 on, 2 dimmed.</summary>
    internal static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    /// <summary>The display state of a POWERBROADCAST_SETTING, null for another setting.</summary>
    internal static bool? DisplayOff(IntPtr setting)
    {
        if (setting == IntPtr.Zero) return null;
        var bytes = new byte[16];
        Marshal.Copy(setting, bytes, 0, 16);
        if (new Guid(bytes) != ConsoleDisplayState || Marshal.ReadInt32(setting, 16) < 4) return null;
        return Marshal.ReadInt32(setting, 20) == 0;
    }

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);
}
