using System.Diagnostics;
using System.IO;
using Windows.Management.Deployment;

namespace DynamicBay.Core;

/// <summary>
/// Registers the identity package (DynamicBay.msix next to the exe) for the current user. With identity, Windows lets
/// DynamicBay read other apps' notifications. The installer trusts the signing certificate; registration itself needs
/// no admin rights. See docs/INTEGRATIONS.md.
/// </summary>
public static class SparsePackage
{
    public const string Name = "DynamicBay";
    public const string Publisher = "CN=DynamicBay Open Source";

    public static bool HasIdentity
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current is not null; }
            catch { return false; }
        }
    }

    private static string AppDir => AppContext.BaseDirectory.TrimEnd('\\');
    private static string MsixPath => Path.Combine(AppDir, "DynamicBay.msix");

    /// <summary>
    /// Returns true if the app should restart to pick up the freshly registered identity.
    /// Attempts once per version so a missing certificate never causes a restart loop.
    /// </summary>
    public static async Task<bool> EnsureRegisteredAsync(AppSettings settings)
    {
        if (!File.Exists(MsixPath)) return false;
        if (HasIdentity)
        {
            _ = UpdateIfOutdatedAsync(); // in the background: the island must not wait for the package manager
            return false;
        }
        if (settings.SparseAttemptedVersion == App.Version) return false;
        settings.SparseAttemptedVersion = App.Version;
        settings.Save();
        try
        {
            var pm = new PackageManager();
            var options = new AddPackageOptions { ExternalLocationUri = new Uri(AppDir + "\\"), ForceUpdateFromAnyVersion = true };
            var result = await pm.AddPackageByUriAsync(new Uri(MsixPath), options);
            if (result.ExtendedErrorCode is not null) throw result.ExtendedErrorCode;
            Log.Info("Identity package registered");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("SparsePackage", ex);
            return false;
        }
    }

    private static string AttemptFile => Path.Combine(AppSettings.Folder, "identity-attempt.txt");

    /// <summary>
    /// After an update the registered identity package still has the old version (and the old manifest). The new
    /// DynamicBay.msix is registered over it; Windows applies it once this process has exited, so the next start runs
    /// with it - no restart here. Tried once per version (remembered in a file of its own), so a failure never loops.
    /// </summary>
    private static async Task UpdateIfOutdatedAsync()
    {
        try
        {
            var asm = typeof(SparsePackage).Assembly.GetName().Version;
            if (asm is null) return;
            var own = new Version(asm.Major, asm.Minor, Math.Max(asm.Build, 0), 0); // like build-sparse.ps1: app version + .0
            var p = Windows.ApplicationModel.Package.Current.Id.Version;
            if (new Version(p.Major, p.Minor, p.Build, p.Revision) >= own) return;
            string attempt = own.ToString();
            try { if (File.Exists(AttemptFile) && File.ReadAllText(AttemptFile).Trim() == attempt) return; } catch { }
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(AttemptFile, attempt);

            var pm = new PackageManager();
            var options = new AddPackageOptions
            {
                ExternalLocationUri = new Uri(AppDir + "\\"),
                ForceUpdateFromAnyVersion = true,
                DeferRegistrationWhenPackagesAreInUse = true, // we are running with it right now
            };
            var result = await pm.AddPackageByUriAsync(new Uri(MsixPath), options);
            if (result.ExtendedErrorCode is not null) throw result.ExtendedErrorCode;
            Log.Info($"Identity package {p.Major}.{p.Minor}.{p.Build}.{p.Revision} -> {attempt} registered (applies after exit)");
        }
        catch (Exception ex)
        {
            Log.Error("SparseUpdate", ex);
        }
    }

    public static void Restart()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Removes the identity package for this user (uninstall).</summary>
    public static async Task RemoveAsync()
    {
        try
        {
            var pm = new PackageManager();
            foreach (var p in pm.FindPackagesForUser("", Name, Publisher))
                await pm.RemovePackageAsync(p.Id.FullName);
        }
        catch (Exception ex) { Log.Error("SparseRemove", ex); }
    }
}
