using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DynamicBay.Core;
using DynamicBay.Island;

namespace DynamicBay.Services;

/// <summary>
/// Looks for a newer GitHub release at startup and <b>asks</b> before installing: the island shows
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

    public static async Task RunAsync(IslandManager island, Action shutdown)
    {
        _island = island;
        _shutdown = shutdown;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            var release = await GetLatestAsync();
            if (release is null || !IsNewer(release.Version, App.Version)) return;
            Log.Info($"Update available: {release.Tag}");
            Pending = release;
            PendingChanged?.Invoke();
            Ask();
        }
        catch (Exception ex) { Log.Error("Update", ex); } // offline / rate limited: try next start
    }

    /// <summary>Manual check from the settings. Returns true if a newer version was found.</summary>
    public static async Task<bool> CheckNowAsync()
    {
        var release = await GetLatestAsync();
        Pending = release is not null && IsNewer(release.Version, App.Version) ? release : null;
        PendingChanged?.Invoke();
        return Pending is not null;
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
            _island?.ShowPeek(Info(Loc.F("Update.Downloading", release.Version.ToString(3)), Loc.T("Update.InstallingHint"), 6));
            var setup = await DownloadAsync(release);
            try
            {
                var setupLog = Path.Combine(Path.GetDirectoryName(setup)!, "setup.log");
                var proc = Process.Start(new ProcessStartInfo(setup, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /TASKS=notifications /LOG=\"{setupLog}\"")
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
            _island?.ShowPeek(Info(Loc.T("Update.Failed"), ex.Message, 6));
        }
        finally { _installing = false; }
    }

    public static async Task<Release?> GetLatestAsync()
    {
        using var http = Client();
        // /releases/latest = newest non-prerelease, non-draft release
        var json = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        string tag = json?["tag_name"]?.ToString() ?? "";
        if (!TryParse(tag, out var version)) return null;
        var asset = (json!["assets"] as JsonArray)?
            .FirstOrDefault(a => a?["name"]?.ToString() is { } n && n.StartsWith("DynamicBay-Setup", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe"));
        string? digest = asset?["digest"]?.ToString();
        return new Release(version, tag, json["html_url"]?.ToString() ?? $"https://github.com/{Repo}/releases",
            asset?["browser_download_url"]?.ToString(),
            digest?.StartsWith("sha256:") == true ? digest[7..] : null,
            asset?["size"]?.GetValue<long>() ?? 0);
    }

    private static async Task<string> DownloadAsync(Release r)
    {
        string dir = Path.Combine(Path.GetTempPath(), "DynamicBay-Update");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"DynamicBay-Setup-{r.Version.ToString(3)}.exe");
        using (var http = Client())
        {
            http.Timeout = TimeSpan.FromMinutes(10);
            using var src = await http.GetStreamAsync(r.SetupUrl!);
            using var dst = File.Create(file);
            await src.CopyToAsync(dst);
        }
        var info = new FileInfo(file);
        if (r.Size > 0 && info.Length != r.Size) throw new InvalidDataException(Loc.German ? "Download unvollständig" : "Incomplete download");
        if (r.Sha256 is not null)
        {
            using var fs = File.OpenRead(file);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.German ? "Prüfsumme stimmt nicht" : "Checksum mismatch");
        }
        Log.Info($"Update downloaded ({info.Length / 1048576.0:0.0} MB, checksum {(r.Sha256 is null ? "n/a" : "ok")})");
        return file;
    }

    /// <summary>"v1.2.3", "1.2.3-beta.1" -> 1.2.3 (pre-release suffix ignored).</summary>
    public static bool TryParse(string tag, out Version version)
    {
        var core = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(core, out var v)) { version = new Version(v.Major, v.Minor, Math.Max(0, v.Build)); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsNewer(Version candidate, string current) =>
        TryParse(current, out var cur) && candidate > cur;

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
