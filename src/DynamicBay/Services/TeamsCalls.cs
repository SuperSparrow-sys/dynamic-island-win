using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Answers a ringing Teams call from the island: Teams' own shortcuts (Strg+Umschalt+S accept with audio,
/// Strg+Umschalt+D decline) go to the Teams window. Only on a click of the user, never by itself.
/// </summary>
public static class TeamsCalls
{
    /// <summary>New Teams and classic Teams.</summary>
    private static readonly string[] Processes = { "ms-teams", "Teams" };

    /// <summary>The key that goes with Strg+Umschalt: S accepts with audio, D declines.</summary>
    internal static ushort KeyFor(bool accept) => accept ? (ushort)'S' : (ushort)'D';

    /// <summary>False when Teams is not running (the caller opens the notification instead).</summary>
    public static bool Answer(bool accept)
    {
        var window = Processes.SelectMany(Process.GetProcessesByName)
            .Select(p => { try { return p.MainWindowHandle; } catch { return IntPtr.Zero; } finally { p.Dispose(); } })
            .FirstOrDefault(h => h != IntPtr.Zero);
        if (window == IntPtr.Zero) { Log.Info("Teams call: no Teams window"); return false; }
        if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
        SetForegroundWindow(window);
        // Teams takes the shortcut once its window is in front.
        Thread.Sleep(150);
        SendChord(KeyFor(accept));
        Log.Info(accept ? "Teams call: accepted from the island" : "Teams call: declined from the island");
        return true;
    }

    private const ushort Ctrl = 0x11, Shift = 0x10;

    private static void SendChord(ushort key)
    {
        var down = new[] { Ctrl, Shift, key };
        var inputs = down.Select(k => Key(k, false)).Concat(down.Reverse().Select(k => Key(k, true))).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort vk, bool up) => new() { Type = 1, Ki = new KeybdInput { Vk = vk, Flags = up ? 2u : 0u } };

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra; }

    // The union of INPUT is as large as its mouse member: pad the keyboard one to that size.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeybdInput Ki; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}
