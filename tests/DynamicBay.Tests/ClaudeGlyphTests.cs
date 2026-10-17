using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using DynamicBay.Controls;

namespace DynamicBay.Tests;

/// <summary>The Claude mark (Simple Icons) is a valid filled glyph on the 24x24 grid, and the compact island never turns it.</summary>
public class ClaudeGlyphTests
{
    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DynamicBay.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName, "src", "DynamicBay" }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Claude_glyph_parses_and_fits_the_grid()
    {
        var m = Regex.Match(Source("Themes", "Icons.Custom.xaml"), "x:Key=\"Glyph.Claude\">([^<]+)<");
        Assert.True(m.Success);
        var b = Geometry.Parse(m.Groups[1].Value).Bounds;
        Assert.InRange(b.Left, -0.01, 1);
        Assert.InRange(b.Top, -0.01, 1);
        Assert.InRange(b.Right, 23, 24.01);
        Assert.InRange(b.Bottom, 23, 24.01);
    }

    [Fact]
    public void Glyph_keys_are_drawn_filled()
    {
        Assert.True(Icon.IsGlyph("Glyph.Claude"));
        Assert.False(Icon.IsGlyph("Icon.Sparkles"));
    }

    [Fact]
    public void Compact_Claude_mark_breathes_and_never_rotates()
    {
        string xaml = Source("Island", "Views", "CompactView.xaml");
        Assert.DoesNotContain("Icon.Sparkles", xaml);
        Assert.Contains("Glyph.Claude", xaml);
        Assert.DoesNotContain("RotateTransform", xaml.Substring(xaml.IndexOf("Claude Code agent", StringComparison.Ordinal)));
    }
}
