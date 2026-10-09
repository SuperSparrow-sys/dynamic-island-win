using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using DynamicBay.Core;
using DynamicBay.Island;

namespace DynamicBay.Services;

/// <summary>Checks GitHub Releases once at startup and announces a newer version as a peek.</summary>
public static class UpdateCheck
{
    public const string Repo = "SuperSparrow-sys/dynamic-island-win";

    public static async Task RunAsync(IslandManager island)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicBay/" + App.Version);
            var json = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            string tag = json?["tag_name"]?.ToString()?.TrimStart('v') ?? "";
            string url = json?["html_url"]?.ToString() ?? $"https://github.com/{Repo}/releases";
            if (Version.TryParse(tag, out var latest) && Version.TryParse(App.Version, out var current) && latest > current)
            {
                island.ShowPeek(new PeekItem
                {
                    Icon = (System.Windows.Media.Geometry)System.Windows.Application.Current.FindResource("Icon.Download"),
                    Title = Loc.T("Update.Available"),
                    Subtitle = Loc.F("Update.Hint", tag),
                    Seconds = 8,
                    OnClick = () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
                });
            }
        }
        catch { /* offline or rate-limited: try next start */ }
    }
}
