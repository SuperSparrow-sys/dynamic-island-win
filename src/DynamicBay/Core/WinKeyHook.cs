using System.Runtime.InteropServices;

namespace DynamicBay.Core;

/// <summary>
/// Whether a Windows key went down and up again with nothing in between (then the search opens instead of the Start
/// menu). Any other key while it is held (Win+E, Win+Shift+S, Win+arrows) or a modifier already held makes it a
/// combination, which Windows handles as always.
/// </summary>
public sealed class WinKeyGate
{
    public const int LWin = 0x5B, RWin = 0x5C;
    private bool _left, _right, _alone;

    public static bool IsWin(int vk) => vk is LWin or RWin;

    /// <param name="othersHeld">Ctrl, Shift or Alt already down when the Windows key goes down.</param>
    public void Down(int vk, bool othersHeld)
    {
        if (IsWin(vk))
        {
            if (!_left && !_right) _alone = !othersHeld; // key repeat while held keeps the state
            if (vk == LWin) _left = true; else _right = true;
        }
        else if (_left || _right) _alone = false;
    }

    public bool Held => _left || _right;

    /// <summary>A click or wheel turn while the Windows key is held (Win+click, Win+wheel in other tools) is no tap.</summary>
    public void Mouse()
    {
        if (Held) _alone = false;
    }

    /// <summary>True when this release ends a tap of the Windows key alone.</summary>
    public bool Up(int vk)
    {
        if (!IsWin(vk)) return false;
        bool wasDown = vk == LWin ? _left : _right;
        if (vk == LWin) _left = false; else _right = false;
        if (!wasDown || _left || _right) return false; // released without our seeing it go down (hook started meanwhile)
        bool tap = _alone;
        _alone = false;
        return tap;
    }
}

/// <summary>
/// Low-level keyboard hook on its own thread (a busy UI thread would make Windows skip or drop the hook): a Windows key
/// tapped alone raises <see cref="Tapped"/> and the Start menu stays closed. For that the release is held back and sent
/// again behind an unassigned key (0xE8), so Windows sees a combination, like AutoHotkey's menu mask key. Everything
/// else passes untouched. Only while a Windows key is held a mouse hook watches for clicks and the wheel (all the time
/// it would put every mouse move through this process, and a pause here would make the pointer stutter). Dispose
/// removes the hooks; the process ending removes them too.
/// </summary>
public sealed class WinKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105, WmQuit = 0x12;
    private const int MaskKey = 0xE8;
    private static readonly IntPtr Marker = new(0x44594E42); // "DYNB": our own key events pass the hook unseen

    private readonly WinKeyGate _gate = new();
    private readonly Action _tapped;
    private readonly Thread _thread;
    private uint _threadId;
    private HookProc? _proc, _mouseProc;
    private IntPtr _mouseHook;

    /// <param name="tapped">Called on the hook thread: hand it on quickly (Dispatcher.BeginInvoke).</param>
    public WinKeyHook(Action tapped)
    {
        _tapped = tapped;
        var started = new ManualResetEventSlim();
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "Windows key" };
        _thread.Start();
        started.Wait(2000);
    }

    private void Run(ManualResetEventSlim started)
    {
        _threadId = GetCurrentThreadId();
        _proc = OnKey;
        _mouseProc = OnMouse;
        IntPtr hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        started.Set();
        if (hook == IntPtr.Zero) { Log.Info($"Windows key: hook not installed ({Marshal.GetLastWin32Error()})"); return; }
        Log.Info("Windows key: opens the search");
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        }
        finally
        {
            WatchMouse(false);
            UnhookWindowsHookEx(hook);
            Log.Info("Windows key: hook removed");
        }
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            int msg = wParam.ToInt32();
            if (k.ExtraInfo != Marker)
            {
                bool tap = false;
                if (msg is WmKeyDown or WmSysKeyDown) _gate.Down((int)k.VkCode, ModifiersHeld());
                else if (msg is WmKeyUp or WmSysKeyUp) tap = _gate.Up((int)k.VkCode);
                WatchMouse(_gate.Held);
                if (tap)
                {
                    SendMaskedRelease((ushort)k.VkCode);
                    try { _tapped(); } catch (Exception ex) { Log.Error("Windows key", ex); }
                    return 1; // this release is replaced by the one sent behind the mask key
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>The mouse hook exists only while a Windows key is held (runs on the hook thread, like the callbacks).</summary>
    private void WatchMouse(bool on)
    {
        if (on && _mouseHook == IntPtr.Zero) _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc!, GetModuleHandle(null), 0);
        else if (!on && _mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        // Button down (left, right, middle, X) and both wheels; moving the mouse keeps the tap.
        if (code >= 0 && wParam.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20A or 0x20B or 0x20E) _gate.Mouse();
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool ModifiersHeld() =>
        (Native.GetAsyncKeyState(0x10) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;

    private static void SendMaskedRelease(ushort win)
    {
        var inputs = new[] { Key(MaskKey, false), Key(MaskKey, true), Key(win, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            Log.Info($"Windows key: release not sent ({Marshal.GetLastWin32Error()})");
    }

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Ki = new KeybdInput { Vk = vk, Flags = (up ? 2u : 0u) | (WinKeyGate.IsWin(vk) ? 1u : 0u), ExtraInfo = Marker }, // KEYUP, EXTENDEDKEY
    };

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    // The union's largest member (MOUSEINPUT) sets the size: KEYBDINPUT padded to it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public KeybdInput Ki; private readonly long _pad; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
