using System.IO;
using DynamicBay.Core;

namespace DynamicBay.Tests;

public class SettingsTransferTests : IDisposable
{
    private const int FastIterations = 10_000; // the real default is slow on purpose
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dynamicbay-transfer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (AppSettings Settings, Dictionary<string, string> Secrets, string Scripts) Source()
    {
        var s = new AppSettings { SpotifyClientId = "spotify-client", MicrosoftClientId = "ms-client", Language = "de" };
        s.CalendarAccounts.Add(new CalendarAccount { Id = "g1", Kind = CalendarKind.Google, User = "google-client.apps.googleusercontent.com" });
        s.CalendarAccounts.Add(new CalendarAccount { Id = "i1", Kind = CalendarKind.ICloud, User = "me@icloud.com" });
        var secrets = new Dictionary<string, string>
        {
            ["cal:g1:secret"] = "google-secret",
            ["cal:g1:refresh"] = "google-refresh-token",
            ["cal:i1:password"] = "app-password",
            ["ms:refresh"] = "ms-refresh-token",
        };
        string scripts = Path.Combine(_dir, "source-scripts");
        Directory.CreateDirectory(Path.Combine(scripts, "data", "w1"));
        File.WriteAllText(Path.Combine(scripts, "Wetter.js"), "Script.setWidget(new ListWidget())");
        File.WriteAllText(Path.Combine(scripts, "data", "w1", "cache.json"), "{}");
        return (s, secrets, scripts);
    }

    [Fact]
    public void Round_trip_brings_settings_client_ids_and_scripts_but_no_sign_in()
    {
        var (s, secrets, scripts) = Source();
        var bundle = SettingsTransfer.Collect(s, "1.0.19", scripts, k => secrets.GetValueOrDefault(k));
        var file = SettingsTransfer.Encrypt(bundle, "482913", FastIterations);

        var back = SettingsTransfer.Decrypt(file, "482913");
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var written = new Dictionary<string, string?>();
        SettingsTransfer.Apply(back, target, (k, v) => written[k] = v);

        var imported = AppSettings.FromJson(File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("spotify-client", imported.SpotifyClientId);
        Assert.Equal("ms-client", imported.MicrosoftClientId);
        Assert.Equal("de", imported.Language);
        Assert.Equal(2, imported.CalendarAccounts.Count);
        Assert.Equal("google-client.apps.googleusercontent.com", imported.CalendarAccounts[0].User);
        Assert.Equal("google-secret", written["cal:g1:secret"]);
        Assert.Single(written); // no password, no refresh token
        Assert.Equal("Script.setWidget(new ListWidget())", File.ReadAllText(Path.Combine(target, "scripts", "Wetter.js")));
        Assert.True(File.Exists(Path.Combine(target, "scripts", "data", "w1", "cache.json")));
    }

    [Fact]
    public void File_contains_no_readable_settings()
    {
        var (s, secrets, scripts) = Source();
        var file = SettingsTransfer.Encrypt(SettingsTransfer.Collect(s, "1.0.19", scripts, k => secrets.GetValueOrDefault(k)), "482913", FastIterations);
        string raw = System.Text.Encoding.Latin1.GetString(file);
        Assert.DoesNotContain("spotify-client", raw);
        Assert.DoesNotContain("google-secret", raw);
    }

    [Fact]
    public void Wrong_code_and_changed_bytes_are_rejected()
    {
        var (s, secrets, scripts) = Source();
        var file = SettingsTransfer.Encrypt(SettingsTransfer.Collect(s, "1.0.19", scripts, k => secrets.GetValueOrDefault(k)), "482913", FastIterations);
        Assert.Throws<SettingsTransfer.WrongPinException>(() => SettingsTransfer.Decrypt(file, "482914"));
        var changed = (byte[])file.Clone();
        changed[^1] ^= 1;
        Assert.Throws<SettingsTransfer.WrongPinException>(() => SettingsTransfer.Decrypt(changed, "482913"));
        var header = (byte[])file.Clone();
        header[8] ^= 1; // iteration count is authenticated too
        Assert.ThrowsAny<Exception>(() => SettingsTransfer.Decrypt(header, "482913"));
    }

    [Fact]
    public void Other_files_are_not_exports()
    {
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Decrypt("{\"settings\":1}"u8.ToArray(), "482913"));
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Decrypt(Array.Empty<byte>(), "482913"));
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("123456789012", true)]
    [InlineData("12345", false)]
    [InlineData("1234567890123", false)]
    [InlineData("12a456", false)]
    [InlineData("１２３４５６", false)] // full-width digits
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Code_is_6_to_12_digits(string? pin, bool valid) => Assert.Equal(valid, SettingsTransfer.IsValidPin(pin));

    [Fact]
    public void Script_paths_outside_the_scripts_folder_change_nothing()
    {
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
        var bundle = new SettingsTransfer.Bundle { Settings = new AppSettings().ToJson(), Scripts = { ["../evil.txt"] = Convert.ToBase64String("x"u8.ToArray()) } };
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Apply(bundle, target, (_, _) => { }));
        Assert.False(File.Exists(Path.Combine(target, "evil.txt")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
    }

    [Fact]
    public void Broken_settings_change_nothing()
    {
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
        var bundle = new SettingsTransfer.Bundle { Settings = "{ not json" };
        Assert.ThrowsAny<Exception>(() => SettingsTransfer.Apply(bundle, target, (_, _) => { }));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
    }

    [Fact]
    public void Only_client_secrets_are_transferable()
    {
        Assert.True(SettingsTransfer.IsTransferableSecret("cal:g1:secret"));
        Assert.False(SettingsTransfer.IsTransferableSecret("cal:g1:refresh"));
        Assert.False(SettingsTransfer.IsTransferableSecret("cal:i1:password"));
        Assert.False(SettingsTransfer.IsTransferableSecret("ms:refresh"));
    }
}
