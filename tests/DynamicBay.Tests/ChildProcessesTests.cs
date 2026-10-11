using System.Diagnostics;
using DynamicBay.Core;

namespace DynamicBay.Tests;

public class ChildProcessesTests
{
    [Fact]
    public void A_helper_process_joins_the_kill_on_exit_job()
    {
        var psi = new ProcessStartInfo("ping", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        try
        {
            Assert.True(ChildProcesses.Adopt(p));
            Assert.True(ChildProcesses.Adopt(p)); // twice is fine (same job)
        }
        finally { try { p.Kill(); } catch { } }
    }
}
