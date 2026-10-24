using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicBay.Services;

/// <summary>
/// Which apps record from a microphone right now: active sessions on the active capture endpoints (WASAPI). Since
/// the Windows updates of 14.10.2026 (26H2) the privacy records in the registry are no longer written on every PC
/// (docs/offene_punkte.md, point 15); this works on those too. COM calls that can take a while: never on the UI thread.
/// </summary>
public static class MicSessions
{
    public static List<string> Active()
    {
        var names = new List<string>();
        int own = Environment.ProcessId;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var s = sessions[i];
                        if (s.State != AudioSessionState.AudioSessionStateActive || s.IsSystemSoundsSession) continue;
                        uint pid = s.GetProcessID;
                        if (pid == 0 || pid == own) continue;
                        names.Add(Name((int)pid));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            Core.Log.Info($"Microphone sessions: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The apps that count as recording: seen now, or less than <paramref name="hold"/> ago. Voice input drops its
    /// session for a second in every pause; without the hold each pause would be a new start ("Stumm?").
    /// </summary>
    public static List<string> Held(Dictionary<string, DateTime> lastSeen, IEnumerable<string> now, DateTime t, TimeSpan hold)
    {
        foreach (var a in now) lastSeen[a] = t;
        foreach (var gone in lastSeen.Where(kv => t - kv.Value >= hold).Select(kv => kv.Key).ToList()) lastSeen.Remove(gone);
        return lastSeen.Keys.ToList();
    }

    private static string Name(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException) { return $"pid {pid}"; }
        catch (InvalidOperationException) { return $"pid {pid}"; }
    }
}
