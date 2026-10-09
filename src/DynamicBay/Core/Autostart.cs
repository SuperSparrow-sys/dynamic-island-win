using Microsoft.Win32;

namespace DynamicBay.Core;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            string exe = Environment.ProcessPath ?? "";
            // Don't register debug builds running from the source tree.
            if (exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)) return;
            if (enabled) key.SetValue("DynamicBay", $"\"{exe}\" --autostart");
            else key.DeleteValue("DynamicBay", false);
        }
        catch (Exception ex) { Log.Error("Autostart", ex); }
    }
}
