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
        if (HasIdentity || !File.Exists(MsixPath)) return false;
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
