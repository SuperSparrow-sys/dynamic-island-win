using System.IO;
using System.Runtime.CompilerServices;

namespace DynamicBay.Tests;

/// <summary>
/// Runs before any test: DynamicBay's data folder (settings, log, secrets, link identity) points to a folder of the
/// test run, never to the user's %AppData%\DynamicBay. Without it test runs wrote their errors into the real log.
/// </summary>
internal static class TestSetup
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "DynamicBayTests", "data-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("DYNAMICBAY_DATA_DIR", DataDir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(DataDir, recursive: true); } catch { } };
    }
}

public class TestSetupTests
{
    [Fact]
    public void Tests_never_use_the_users_data_folder()
    {
        Assert.Equal(TestSetup.DataDir, DynamicBay.Core.AppSettings.Folder);
        Assert.DoesNotContain(Path.Combine("AppData", "Roaming", "DynamicBay"), DynamicBay.Core.AppSettings.Folder, StringComparison.OrdinalIgnoreCase);
    }
}
