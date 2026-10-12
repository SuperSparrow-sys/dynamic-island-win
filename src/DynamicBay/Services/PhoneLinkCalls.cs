using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Answers a phone call of Smartphone-Link from the island. Windows lets no other app answer it; the only way is the
/// button of its notification ("Auf dem PC zulassen" / "Ablehnen"). Its banner is off while DynamicBay runs, but the
/// notification waits in the notification center: that opens for a moment, the button is pressed, it closes again.
/// Only on a click of the user.
/// </summary>
public static class PhoneLinkCalls
{
    internal static readonly string[] AcceptNames = { "Auf dem PC zulassen", "Allow on PC", "Annehmen", "Accept" };
    internal static readonly string[] DeclineNames = { "Ablehnen", "Decline" };

    /// <summary>False: the button was not found (the call was gone, or the notification center looks different).</summary>
    public static Task<bool> AnswerAsync(bool accept) => Task.Run(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-actioncenter:") { UseShellExecute = true });
            var names = accept ? AcceptNames : DeclineNames;
            // The notification center takes a moment to open and to show its notifications.
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(150);
                if (FindButton(names) is not { } b || !b.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) continue;
                ((InvokePattern)p).Invoke();
                Log.Info(accept ? "Phone call: accepted from the island" : "Phone call: declined from the island");
                Close();
                return true;
            }
            Log.Info("Phone call: button not found in the notification center");
            Close();
            return false;
        }
        catch (Exception ex) { Log.Error("Phone call answer", ex); Close(); return false; }
    });

    /// <summary>The call's button in the open notification center (the first window whose tree has it).</summary>
    private static AutomationElement? FindButton(string[] names)
    {
        var condition = new OrCondition(names.Select(n => (Condition)new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, n))).Append(Condition.FalseCondition).ToArray());
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            string name;
            try { name = w.Current.Name; } catch { continue; }
            if (!IsNotificationCenter(name)) continue;
            if (w.FindFirst(TreeScope.Descendants, condition) is { } b) return b;
        }
        return null;
    }

    /// <summary>The notification center's window ("Benachrichtigungscenter", "Notification Center").</summary>
    internal static bool IsNotificationCenter(string windowName) =>
        windowName.Contains("Benachrichtigung", StringComparison.OrdinalIgnoreCase) || windowName.Contains("Notification", StringComparison.OrdinalIgnoreCase)
        || windowName.Contains("Mitteilung", StringComparison.OrdinalIgnoreCase);

    /// <summary>Escape closes the notification center (it has the focus while open); only when it is open.</summary>
    private static void Close()
    {
        bool open = false;
        try
        {
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                if (IsNotificationCenter(w.Current.Name)) { open = true; break; }
        }
        catch { }
        if (!open) return;
        var inputs = new[] { Key(0x1B, false), Key(0x1B, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort vk, bool up) => new() { Type = 1, Ki = new KeybdInput { Vk = vk, Flags = up ? 2u : 0u } };

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra; }

    // The union of INPUT is as large as its mouse member: pad the keyboard one to that size.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeybdInput Ki; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
