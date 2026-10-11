using System.IO;
using System.Text.RegularExpressions;
using DynamicBay.Core;

namespace DynamicBay.Tests;

[CollectionDefinition("Loc", DisableParallelization = true)]
public class LocCollection { }

/// <summary>
/// The settings window as the user sees it: one sidebar entry per page, and every jump (gear on a widget, links between
/// pages) lands on a heading that exists on that page, in German and English.
/// </summary>
[Collection("Loc")]
public class SettingsLayoutTests
{
    private static string Xaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DynamicBay.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "DynamicBay", "Settings", "SettingsWindow.xaml"));
    }

    private static readonly Regex PageStart = new(@"<StackPanel Visibility=""\{Binding Page, Converter=\{StaticResource EqVis\}, ConverterParameter=(\d+)\}"">");
    private static readonly Regex Heading = new(@"Style=""\{StaticResource ST\.Group\}""[^>]*?Text=""(?:\{core:L De='(?<de>[^']*)', En='(?<en>[^']*)'\}|(?<plain>[^""{]*))""");

    /// <summary>Group headings of each page: (German, English).</summary>
    private static Dictionary<int, List<(string de, string en)>> Headings()
    {
        var xaml = Xaml();
        var starts = PageStart.Matches(xaml).ToList();
        var pages = new Dictionary<int, List<(string, string)>>();
        for (int i = 0; i < starts.Count; i++)
        {
            int page = int.Parse(starts[i].Groups[1].Value);
            Assert.False(pages.ContainsKey(page), $"page {page} twice");
            int end = i + 1 < starts.Count ? starts[i + 1].Index : xaml.Length;
            pages[page] = Heading.Matches(xaml[starts[i].Index..end])
                .Select(m => m.Groups["plain"].Success ? (m.Groups["plain"].Value, m.Groups["plain"].Value) : (m.Groups["de"].Value, m.Groups["en"].Value))
                .ToList();
        }
        return pages;
    }

    [Fact]
    public void Every_page_has_one_block_and_one_sidebar_entry()
    {
        var xaml = Xaml();
        var pages = Headings();
        int count = Enum.GetValues<SettingsPage>().Length;
        Assert.Equal(Enumerable.Range(0, count), pages.Keys.OrderBy(k => k));
        var nav = xaml[xaml.IndexOf("<ListBox x:Name=\"Nav\"", StringComparison.Ordinal)..xaml.IndexOf("</ListBox>", StringComparison.Ordinal)];
        Assert.Equal(count, Regex.Matches(nav, @"<StackPanel Orientation=""Horizontal""><c:Icon").Count);
    }

    public static IEnumerable<object[]> Targets()
    {
        var ids = Widgets.AllHome.Concat(new[] { Widgets.Notes, Widgets.Audio, Widgets.ScriptPrefix + "demo" }).Distinct();
        foreach (var id in ids)
            foreach (var lang in new[] { "de", "en" })
                yield return new object[] { id, lang };
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Widget_settings_jump_to_an_existing_heading(string id, string lang)
    {
        try
        {
            Loc.Init(lang);
            var (page, group) = Widgets.SettingsOf(id);
            if (group is null) return;
            var headings = Headings()[(int)page];
            Assert.Contains(headings, h => (lang == "de" ? h.de : h.en).StartsWith(group, StringComparison.OrdinalIgnoreCase));
        }
        finally { Loc.Init("de"); }
    }

    [Theory]
    [InlineData(SettingsPage.Accounts, "Spotify", "Spotify")]
    [InlineData(SettingsPage.Accounts, "Kalender", "Calendars")]
    [InlineData(SettingsPage.Accounts, "Microsoft 365", "Microsoft 365")]
    [InlineData(SettingsPage.Content, "Kompakte", "Compact")]
    public void Links_between_pages_land_on_a_heading(SettingsPage page, string de, string en)
    {
        var headings = Headings()[(int)page];
        Assert.Contains(headings, h => h.de.StartsWith(de, StringComparison.Ordinal));
        Assert.Contains(headings, h => h.en.StartsWith(en, StringComparison.Ordinal));
    }
}
