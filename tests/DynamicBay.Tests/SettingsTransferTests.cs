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
        var file = SettingsTransfer.Encrypt(bundle, "4829137705", FastIterations);

        var back = SettingsTransfer.Decrypt(file, "4829137705");
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
        var file = SettingsTransfer.Encrypt(SettingsTransfer.Collect(s, "1.0.19", scripts, k => secrets.GetValueOrDefault(k)), "4829137705", FastIterations);
        string raw = System.Text.Encoding.Latin1.GetString(file);
        Assert.DoesNotContain("spotify-client", raw);
        Assert.DoesNotContain("google-secret", raw);
    }

    [Fact]
    public void Wrong_code_and_changed_bytes_are_rejected()
    {
        var (s, secrets, scripts) = Source();
        var file = SettingsTransfer.Encrypt(SettingsTransfer.Collect(s, "1.0.19", scripts, k => secrets.GetValueOrDefault(k)), "4829137705", FastIterations);
        Assert.Throws<SettingsTransfer.WrongPinException>(() => SettingsTransfer.Decrypt(file, "4829137706"));
        var changed = (byte[])file.Clone();
        changed[^1] ^= 1;
        Assert.Throws<SettingsTransfer.WrongPinException>(() => SettingsTransfer.Decrypt(changed, "4829137705"));
        var header = (byte[])file.Clone();
        header[8] ^= 1; // iteration count is authenticated too
        Assert.ThrowsAny<Exception>(() => SettingsTransfer.Decrypt(header, "4829137705"));
    }

    [Fact]
    public void Other_files_are_not_exports()
    {
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Decrypt("{\"settings\":1}"u8.ToArray(), "4829137705"));
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Decrypt(Array.Empty<byte>(), "4829137705"));
    }

    [Theory]
    [InlineData("1234567890", true)]
    [InlineData("1234567890123456", true)]
    [InlineData("123456789", false)]
    [InlineData("12345678901234567", false)]
    [InlineData("12a4567890", false)]
    [InlineData("１２３４５６７８９０", false)] // full-width digits
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Code_is_10_to_16_digits(string? pin, bool valid) => Assert.Equal(valid, SettingsTransfer.IsValidPin(pin));

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

    [Fact]
    public void Pairing_stays_on_each_pc()
    {
        var (s, secrets, scripts) = Source();
        s.LinkEnabled = true;
        s.LinkDeviceName = "Laptop";
        s.PairedDevices.Add(new PairedDevice { Id = "laptop-peer", Name = "PC", Fingerprint = "AA" });
        var bundle = SettingsTransfer.Collect(s, "1.0.19", scripts, k => secrets.GetValueOrDefault(k));
        Assert.DoesNotContain("laptop-peer", bundle.Settings);
        Assert.DoesNotContain("LinkEnabled", bundle.Settings);

        // The target PC is paired with someone else: it keeps that after the import
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var own = new AppSettings { LinkEnabled = true, LinkDeviceName = "Desktop" };
        own.PairedDevices.Add(new PairedDevice { Id = "desktop-peer", Name = "Laptop", Fingerprint = "BB" });
        File.WriteAllText(Path.Combine(target, "settings.json"), own.ToJson());
        SettingsTransfer.Apply(SettingsTransfer.Decrypt(SettingsTransfer.Encrypt(bundle, "4829137705", FastIterations), "4829137705"), target, (_, _) => { });

        var after = AppSettings.FromJson(File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("spotify-client", after.SpotifyClientId);
        Assert.True(after.LinkEnabled);
        Assert.Equal("Desktop", after.LinkDeviceName);
        Assert.Equal("desktop-peer", Assert.Single(after.PairedDevices).Id);
    }

    [Fact]
    public void Huge_round_counts_are_refused()
    {
        var (s, secrets, scripts) = Source();
        var file = SettingsTransfer.Encrypt(SettingsTransfer.Collect(s, "1.0.19", scripts, k => secrets.GetValueOrDefault(k)), "4829137705", FastIterations);
        BitConverter.GetBytes(50_000_000).CopyTo(file, 8);
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Decrypt(file, "4829137705"));
    }
}
