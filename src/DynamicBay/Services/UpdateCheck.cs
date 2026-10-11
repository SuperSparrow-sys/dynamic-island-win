using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Island;

namespace DynamicBay.Services;

/// <summary>
/// Looks for a newer GitHub release at startup and once a day and <b>asks</b> before installing: the island shows
/// "Update x.y.z verfügbar" with "Installieren" / "Später"; the tray menu and the settings offer it too.
/// Installing downloads the setup, verifies GitHub's SHA-256 digest, asks Windows for elevation and runs the
/// installer silently (/UPDATE) - it waits for DynamicBay to exit and starts the new version afterwards.
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "SuperSparrow-sys/dynamic-island-win";

    public sealed record Release(Version Version, string Tag, string PageUrl, string? SetupUrl, string? Sha256, long Size);

    /// <summary>A newer release that the user hasn't installed yet (null = up to date / unknown).</summary>
    public static Release? Pending { get; private set; }
    public static event Action? PendingChanged;

    private static IslandManager? _island;
    private static Action? _shutdown;
    private static bool _installing;

    private static DispatcherTimer? _daily;

    /// <summary>
    /// Checks shortly after the start and then once a day while the setting is on (it only asks, it never installs by
    /// itself). The daily timer always runs, so switching the setting on later works without a restart.
    /// </summary>
    public static async Task RunAsync(IslandManager island, Action shutdown, Func<bool> enabled)
    {
        if (Core.DevMode.On) return;
        _island = island;
        _shutdown = shutdown;
        if (_daily is null)
        {
            _daily = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
            _daily.Tick += async (_, _) => { if (enabled()) await CheckAndAskAsync(); };
            _daily.Start();
        }
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (enabled()) await CheckAndAskAsync();
    }

    private static async Task CheckAndAskAsync()
    {
        if (_installing) return;
        try
        {
            var release = await GetLatestAsync();
            bool newer = release is not null && IsNewer(release.Tag, App.Version);
            CleanupDownloads(newer ? release : null);
            if (!newer) return;
            Log.Info($"Update available: {release!.Tag}");
            Pending = release;
            PendingChanged?.Invoke();
            Ask();
        }
        catch (Exception ex) { Log.Error("Update", ex); } // offline / rate limited: try again tomorrow or next start
    }

    /// <summary>Manual check from the settings. Returns true if a newer version was found.</summary>
    public static async Task<bool> CheckNowAsync()
    {
        var release = await GetLatestAsync();
        Pending = release is not null && IsNewer(release.Tag, App.Version) ? release : null;
        CleanupDownloads(Pending);
        PendingChanged?.Invoke();
        return Pending is not null;
    }

    private static string DownloadFolder => Path.Combine(Path.GetTempPath(), "DynamicBay-Update");

    private static string SetupPath(Release r) => Path.Combine(DownloadFolder, $"DynamicBay-Setup-{r.Version.ToString(3)}.exe");

    /// <summary>Removes setups of earlier updates from %TEMP%\DynamicBay-Update (keeps the one still to install).</summary>
    private static void CleanupDownloads(Release? keep)
    {
        try
        {
            if (!Directory.Exists(DownloadFolder)) return;
            string? keepFile = keep is null ? null : SetupPath(keep);
            foreach (var f in Directory.EnumerateFiles(DownloadFolder, "DynamicBay-Setup-*.exe"))
                if (!string.Equals(f, keepFile, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { } // still running or locked: next time
        }
        catch (Exception ex) { Log.Error("Update.Cleanup", ex); }
    }

    /// <summary>Shows the question in the island.</summary>
    public static void Ask()
    {
        if (Pending is null || _island is null) return;
        _island.ShowPeek(new PeekItem
        {
            Icon = Icon(),
            IconBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("B.Blue"),
            Title = Loc.F("Update.Question", Pending.Version.ToString(3)),
            Subtitle = Loc.T("Update.QuestionHint"),
            ActionText = Loc.T("Update.Install"),
            DismissText = Loc.T("Update.Later"),
            Action = () => _ = InstallAsync(),
            Priority = PeekPriority.High,
            Seconds = 30,
        });
    }

    /// <summary>
    /// Safety net for the restart after an update: a hidden, non-elevated PowerShell waits for the setup to finish and
    /// starts DynamicBay again unless the installer already did (a second start would only open the settings window).
    /// </summary>
    private static void StartRelaunchWatcher(int setupPid)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var script =
                $"$t=0; while ((Get-Process -Id {setupPid} -ErrorAction SilentlyContinue) -and $t -lt 900) {{ Start-Sleep 1; $t++ }}; " +
                "Start-Sleep 4; " +
                $"if (-not (Get-Process DynamicBay -ErrorAction SilentlyContinue)) {{ Start-Process -FilePath '{exe.Replace("'", "''")}' }}";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log.Error("Update.Relaunch", ex); }
    }

    /// <summary>Downloads, verifies and launches the installer - only ever called after the user said yes.</summary>
    public static async Task InstallAsync()
    {
        var release = Pending;
        if (release is null || _installing) return;
        if (release.SetupUrl is null)
        {
            Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
            return;
        }
        _installing = true;
        try
        {
            // Without GitHub's checksum the download cannot be verified: don't run it.
            if (release.Sha256 is null) throw new UserMessageException(Loc.German ? "Update ohne Prüfsumme, nicht installiert" : "Update has no checksum, not installed");
            _island?.ShowPeek(Info(Loc.F("Update.Downloading", release.Version.ToString(3)), Loc.T("Update.InstallingHint"), 6));
            // Stays open (others may only read) until the installer runs: the checked file is the one that starts.
            using var verified = await DownloadAsync(release);
            string setup = verified.Name;
            try
            {
                var setupLog = Path.Combine(Path.GetDirectoryName(setup)!, "setup.log");
                // No /TASKS: Inno keeps the tasks chosen at the first install (UsePreviousTasks).
                var proc = Process.Start(new ProcessStartInfo(setup, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /LOG=\"{setupLog}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas", // Windows confirmation: the app is installed for all users
                });
                Log.Info($"Installing update {release.Tag}");
                if (proc is not null) StartRelaunchWatcher(proc.Id);
                _shutdown?.Invoke(); // the installer waits for this process, then starts the new version
            }
            catch (Win32Exception)
            {
                Log.Info("Update cancelled at the Windows confirmation");
                _island?.ShowPeek(Info(Loc.T("Update.Cancelled"), Loc.T("Update.CancelledHint"), 5));
            }
        }
        catch (Exception ex)
        {
            Log.Error("UpdateInstall", ex);
            _island?.ShowPeek(Info(Loc.T("Update.Failed"), ErrorText.For(ex), 6));
        }
        finally { _installing = false; }
    }

    public static async Task<Release?> GetLatestAsync()
    {
        using var http = Client();
        // /releases/latest = newest non-prerelease, non-draft release
        return ParseRelease(JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest")));
    }

    /// <summary>
    /// One release of the GitHub API. No setup asset: SetupUrl is null (the release page opens instead); no digest:
    /// Sha256 is null (never installed). A draft or a tag that is no version gives null.
    /// </summary>
    internal static Release? ParseRelease(JsonNode? json)
    {
        string tag = json?["tag_name"]?.ToString() ?? "";
        if (json is null || !TryParse(tag, out var version)) return null;
        if (json["draft"] is JsonValue d && d.TryGetValue<bool>(out var draft) && draft) return null;
        var asset = (json["assets"] as JsonArray)?
            .FirstOrDefault(a => a?["name"]?.ToString() is { } n && n.StartsWith("DynamicBay-Setup", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe"));
        string? digest = asset?["digest"]?.ToString();
        return new Release(version, tag, json["html_url"]?.ToString() ?? $"https://github.com/{Repo}/releases",
            asset?["browser_download_url"]?.ToString(),
            digest?.StartsWith("sha256:") == true ? digest[7..] : null,
            asset?["size"] is JsonValue s && s.TryGetValue<long>(out var size) ? size : 0);
    }

    /// <summary>
    /// Downloads the setup and checks size and SHA-256. Returns the file opened for reading with FileShare.Read: while
    /// it is open nobody can change or swap it, and the hash is computed over exactly this handle.
    /// </summary>
    private static async Task<FileStream> DownloadAsync(Release r)
    {
        Directory.CreateDirectory(DownloadFolder);
        string file = SetupPath(r);
        using (var http = Client())
        {
            http.Timeout = TimeSpan.FromMinutes(10);
            using var src = await http.GetStreamAsync(r.SetupUrl!);
            using var dst = File.Create(file);
            await src.CopyToAsync(dst);
        }
        var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (r.Size > 0 && fs.Length != r.Size) throw new UserMessageException(Loc.German ? "Download unvollständig" : "Incomplete download");
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (r.Sha256 is null || !hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UserMessageException(Loc.German ? "Prüfsumme stimmt nicht" : "Checksum mismatch");
            Log.Info($"Update downloaded ({fs.Length / 1048576.0:0.0} MB, checksum ok)");
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>"v1.2.3", "1.2.3.4", "1.2.3-beta.1" -> 1.2.3(.4) (the pre-release suffix is compared in <see cref="Compare"/>).</summary>
    public static bool TryParse(string tag, out Version version)
    {
        var core = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(core, out var v))
        {
            version = v.Revision > 0 ? new Version(v.Major, v.Minor, Math.Max(0, v.Build), v.Revision) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsNewer(Version candidate, string current) => IsNewer(candidate.ToString(), current);

    /// <summary>True if the tag <paramref name="candidate"/> is a later version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out _) && TryParse(current, out _) && Compare(candidate, current) > 0;

    /// <summary>
    /// Compares two version tags: the numbers first (missing parts count as 0, so 1.0.16 = 1.0.16.0), then a release
    /// beats a pre-release of the same number (1.0.16 > 1.0.16-beta), pre-releases among themselves part by part
    /// (beta.2 > beta.1, rc > beta). Build metadata after '+' is ignored.
    /// </summary>
    public static int Compare(string a, string b)
    {
        TryParse(a, out var va);
        TryParse(b, out var vb);
        int c = Full(va).CompareTo(Full(vb));
        if (c != 0) return c;
        string pa = PreRelease(a), pb = PreRelease(b);
        if (pa.Length == 0 || pb.Length == 0) return pa.Length == pb.Length ? 0 : pa.Length == 0 ? 1 : -1;
        var xa = pa.Split('.');
        var xb = pb.Split('.');
        for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
        {
            bool na = int.TryParse(xa[i], out int ia), nb = int.TryParse(xb[i], out int ib);
            int p = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(xa[i], xb[i]);
            if (p != 0) return Math.Sign(p);
        }
        return xa.Length.CompareTo(xb.Length);

        static Version Full(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        static string PreRelease(string tag)
        {
            var t = tag.Trim().Split('+')[0];
            int dash = t.IndexOf('-');
            return dash < 0 ? "" : t[(dash + 1)..].ToLowerInvariant();
        }
    }

    private static HttpClient Client()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicBay/" + App.Version);
        return http;
    }

    private static System.Windows.Media.Geometry Icon() =>
        (System.Windows.Media.Geometry)System.Windows.Application.Current.FindResource("Icon.Download");

    private static PeekItem Info(string title, string subtitle, double seconds) => new()
    {
        Icon = Icon(),
        IconBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("B.Blue"),
        Title = title,
        Subtitle = subtitle,
        Seconds = seconds,
        Priority = PeekPriority.High,
    };
}
