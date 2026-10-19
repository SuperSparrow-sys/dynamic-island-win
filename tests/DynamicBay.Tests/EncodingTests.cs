using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicBay.Tests;

/// <summary>
/// Guards against broken umlauts (an a-umlaut showing up as two characters, A-tilde + currency sign): happens when a UTF-8 file is read as ANSI and saved again
/// (e.g. Windows PowerShell 5), or when Inno Setup reads a UTF-8 script without BOM.
/// </summary>
public class EncodingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DynamicBay.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    private static readonly string[] TextExtensions = { ".cs", ".xaml", ".md", ".iss", ".ps1", ".mjs", ".json", ".yml", ".xml", ".csproj", ".manifest" };

    private static IEnumerable<string> TextFiles()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|artifacts|node_modules|\.git)[\\/]"));
    }

    // UTF-8 bytes of umlauts, sharp s, multiplication sign, dashes and quotes when read as Windows-1252.
    // Written as escapes so this file does not trip its own check.
    private static readonly Regex Mojibake = new(
        Pattern(0xC3, new[] { 0xA4, 0xB6, 0xBC, 0x178, 0x201E, 0x2013, 0x153, 0x2014, 0xA9 }) + "|" + Pattern(0xE2, 0x20AC, new[] { 0x201C, 0x201D, 0x17E, 0x153, 0x2122 }),
        RegexOptions.Compiled);

    // Built from code points so this source file stays plain ASCII and cannot match itself.
    private static string Pattern(int lead, int[] follow) => Regex.Escape(((char)lead).ToString()) + "[" + string.Concat(follow.Select(c => Regex.Escape(((char)c).ToString()))) + "]";
    private static string Pattern(int lead1, int lead2, int[] follow) => Regex.Escape(((char)lead1).ToString()) + Pattern(lead2, follow);

    [Fact]
    public void No_file_contains_broken_umlauts()
    {
        var broken = new List<string>();
        foreach (var f in TextFiles())
        {
            var text = File.ReadAllText(f, Encoding.UTF8);
            var m = Mojibake.Match(text);
            if (m.Success) broken.Add($"{Path.GetRelativePath(RepoRoot(), f)}: '{m.Value}'");
        }
        Assert.True(broken.Count == 0, "Broken characters in:\n" + string.Join("\n", broken));
    }

    [Fact]
    public void All_text_files_are_valid_utf8()
    {
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        foreach (var f in TextFiles())
        {
            var ex = Record.Exception(() => strict.GetString(File.ReadAllBytes(f)));
            Assert.True(ex is null, $"{Path.GetRelativePath(RepoRoot(), f)} is not valid UTF-8");
        }
    }

    [Fact]
    public void Installer_script_has_utf8_bom_so_inno_setup_reads_umlauts_correctly()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "installer", "DynamicBay.iss"));
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "installer/DynamicBay.iss must be saved as UTF-8 with BOM (Inno Setup reads files without BOM as ANSI)");
    }

    [Fact]
    public void Installer_closes_a_copy_started_during_the_update_before_it_registers_the_identity_package()
    {
        // 1.1.12: a click on the taskbar during the update started the app before Add-AppxPackage, Windows refused
        // with 0x80073D02 and left the package broken (docs/postmortems/2026-10-19-identity-package-in-use.md).
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DynamicBay.iss"));
        var register = iss[iss.IndexOf("function RegisterIdentity", StringComparison.Ordinal)..];
        register = register[..register.IndexOf("end;", StringComparison.Ordinal)];
        Assert.True(register.IndexOf("CloseRunningApp", StringComparison.Ordinal) is >= 0 and var close
            && close < register.IndexOf("Add-AppxPackage", StringComparison.Ordinal), "CloseRunningApp must run before Add-AppxPackage");
        Assert.Contains("not RegisterIdentity(App) and not RegisterIdentity(App)", iss); // second try repairs it
        Assert.Single(Regex.Matches(iss, "Add-AppxPackage")); // no other registration path skips the close
    }

    [Fact]
    public void German_strings_keep_their_umlauts()
    {
        Assert.Contains("ö", Core.Loc.TableForTests["Clip.Delete"].de);   // "Löschen"
        Assert.Contains("ä", Core.Loc.TableForTests["Media.NowPlaying"].de); // "Jetzt läuft"
    }
}
