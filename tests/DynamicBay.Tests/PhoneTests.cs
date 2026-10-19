using System.Collections.ObjectModel;
using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>The phone through Smartphone-Link: its apps by name and icon, its calls to call back, its battery.</summary>
public class PhoneTests
{
    private const string PhoneLink = "Microsoft.YourPhone_8wekyb3d8bbwe!App";
    private const string Calling = "Microsoft.YourPhone_8wekyb3d8bbwe!YourPhoneCalling";

    [Fact]
    public void Snapchat_from_the_phone_shows_as_Snapchat_with_the_sender_as_title()
    {
        Assert.True(PhoneLinkApps.IsPhoneNotification(PhoneLink));
        Assert.False(PhoneLinkApps.IsPhoneNotification(Calling));
        Assert.False(PhoneLinkApps.IsPhoneNotification("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"));

        // The toast as it arrives: "Snapchat", "Tim", "sent you a Snap".
        var (app, title, body) = PhoneLinkApps.Unwrap(new[] { "Snapchat", "Tim", "sent you a Snap" })!.Value;
        Assert.Equal(("Snapchat", "Tim", "sent you a Snap"), (app, title, body));
        Assert.Null(PhoneLinkApps.Unwrap(new[] { "Nur eine Zeile" }));
        // Smartphone-Link's own call: two lines, no app line. A WhatsApp call: three lines with the app first.
        Assert.Null(PhoneLinkApps.Unwrap(new[] { "Philipp", "Eingehender Anruf" }));
        Assert.Equal(("WhatsApp", "Philipp Seeger", "Verpasster Sprachanruf"), PhoneLinkApps.Unwrap(new[] { "WhatsApp", "Philipp Seeger", "Verpasster Sprachanruf" })!.Value);
        Assert.Equal(("Snapchat", "Tim", ""), PhoneLinkApps.Unwrap(new[] { "Snapchat", "Tim" })!.Value);
    }

    [Fact]
    public void The_icon_Smartphone_Link_keeps_of_the_phone_app_is_found()
    {
        var files = new[] { @"C:\x\app_icon_net.whatsapp.WhatsApp1.0.jpg", @"C:\x\app_icon_net.whatsapp.WhatsAppSMB1.0.jpg",
                            @"C:\x\app_icon_com.toyopagroup.picaboo13.2.jpg", @"C:\x\app_icon_com.anthropic.claude1.0.jpg" };
        Assert.Equal("app_icon_com.toyopagroup.picaboo13.2.jpg", PhoneLinkApps.IconFile("Snapchat", files));
        Assert.Equal("app_icon_net.whatsapp.WhatsApp1.0.jpg", PhoneLinkApps.IconFile("WhatsApp", files));
        Assert.Equal("app_icon_net.whatsapp.WhatsAppSMB1.0.jpg", PhoneLinkApps.IconFile("WhatsApp Business", files));
        Assert.Equal("app_icon_com.anthropic.claude1.0.jpg", PhoneLinkApps.IconFile("Claude", files)); // not in the list, by its id
        Assert.Null(PhoneLinkApps.IconFile("Instagram", files));
    }

    [Fact]
    public void A_phone_notification_opens_the_app_on_Windows_and_Gmail_opens_Outlook()
    {
        // As the Start menu lists them on the owner's PC.
        var apps = new[]
        {
            new InstalledApp("Claude", @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude"),
            new InstalledApp("Microsoft Teams", @"shell:AppsFolder\MSTeams_8wekyb3d8bbwe!MSTeams"),
            new InstalledApp("Outlook (classic)", @"shell:AppsFolder\Microsoft.Office.OUTLOOK.EXE.15"),
            new InstalledApp("Outlook", @"shell:AppsFolder\Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows"),
            new InstalledApp("Smartphone-Link", @"shell:AppsFolder\Microsoft.YourPhone_8wekyb3d8bbwe!App"),
            new InstalledApp("Spotify (Web-App)", @"shell:AppsFolder\Chrome._crx_pjibgcllelbfgfagdaldikeohf"),
            new InstalledApp("Spotify", @"shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            new InstalledApp("WhatsApp", @"shell:AppsFolder\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"),
        };
        string outlook = @"shell:AppsFolder\Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows";
        Assert.Equal(outlook, PhoneLinkApps.WindowsApp("Gmail", apps));
        Assert.Equal(outlook, PhoneLinkApps.WindowsApp("Mail", apps));
        Assert.Equal(@"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude", PhoneLinkApps.WindowsApp("Claude", apps));
        Assert.Equal(@"shell:AppsFolder\MSTeams_8wekyb3d8bbwe!MSTeams", PhoneLinkApps.WindowsApp("Teams", apps));
        Assert.Equal(@"shell:AppsFolder\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", PhoneLinkApps.WindowsApp("WhatsApp", apps));
        Assert.Equal(@"shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", PhoneLinkApps.WindowsApp("Spotify", apps)); // program before web app
        // No such app here: Smartphone-Link opens it, never itself through the mapping.
        Assert.Null(PhoneLinkApps.WindowsApp("Snapchat", apps));
        Assert.Null(PhoneLinkApps.WindowsApp("Smartphone-Link", apps));
        Assert.Equal(outlook, PhoneLinkApps.WindowsApp("Gmail", apps.Where(a => a.Name != "Outlook (classic)")));
        Assert.Equal(@"shell:AppsFolder\Microsoft.Office.OUTLOOK.EXE.15", PhoneLinkApps.WindowsApp("Gmail", apps.Where(a => a.Name != "Outlook")));
    }

    [Fact]
    public void The_phone_listed_once_takes_the_battery_of_its_second_connection()
    {
        var devices = new ObservableCollection<BluetoothDeviceInfo>();
        Assert.True(BluetoothService.Merge(devices, new BluetoothDeviceInfo { Id = "le", Name = "iPhone", Kind = DeviceKind.Phone }));
        Assert.False(BluetoothService.Merge(devices, new BluetoothDeviceInfo { Id = "classic", Name = "iPhone", Kind = DeviceKind.Phone, Battery = 85 }));
        Assert.Single(devices);
        Assert.Equal(85, devices[0].Battery);
        Assert.Equal(3, BluetoothService.BatteryRetrySeconds[0]);
    }

    [Fact]
    public void An_iPhone_under_two_names_shows_once_with_its_battery()
    {
        Assert.True(BluetoothService.SameDevice("iPhone", DeviceKind.Phone, "iPhone von Philipp", DeviceKind.Phone));
        Assert.False(BluetoothService.SameDevice("iPhone", DeviceKind.Phone, "iPhoneX", DeviceKind.Phone));
        Assert.False(BluetoothService.SameDevice("JBL", DeviceKind.Speaker, "JBL Charge 5", DeviceKind.Speaker));

        var devices = new ObservableCollection<BluetoothDeviceInfo>();
        BluetoothService.Merge(devices, new BluetoothDeviceInfo { Id = "le", Name = "iPhone von Philipp", Kind = DeviceKind.Phone });
        BluetoothService.Merge(devices, new BluetoothDeviceInfo { Id = "classic", Name = "iPhone", Kind = DeviceKind.Phone, Battery = 94 });
        Assert.Equal(94, Assert.Single(devices).Battery);
    }

    [Fact]
    public void A_phone_that_keeps_dropping_is_announced_once_a_minute()
    {
        var last = new Dictionary<string, DateTime>();
        var t = new DateTime(2026, 10, 12, 13, 12, 0);
        Assert.True(BluetoothService.Calm(last, "iPhone", t));
        Assert.False(BluetoothService.Calm(last, "iPhone", t.AddSeconds(9)));   // dropped
        Assert.False(BluetoothService.Calm(last, "iPhone", t.AddSeconds(10)));  // back
        Assert.False(BluetoothService.Calm(last, "iPhone", t.AddSeconds(55)));  // still flapping: the minute starts again
        Assert.True(BluetoothService.Calm(last, "iPhone", t.AddMinutes(3)));
        Assert.True(BluetoothService.Calm(last, "AirPods", t.AddSeconds(10)));
    }

    [Fact]
    public void Album_covers_of_Apple_Music_are_no_app_icon()
    {
        var seen = new Dictionary<string, HashSet<string>>();
        // Two songs of one album send the same cover: still a cover.
        Assert.False(MediaService.SeenForOthers(seen, "COVER", MediaService.ArtOwner("Habits", "Pastel Ghost", "Habits - EP")));
        Assert.False(MediaService.SeenForOthers(seen, "COVER", MediaService.ArtOwner("Dark Red", "Pastel Ghost", "Habits - EP")));
        // The same image for songs of different albums: the player's icon.
        Assert.False(MediaService.SeenForOthers(seen, "ICON", MediaService.ArtOwner("Self Care", "Mac Miller", "Swimming")));
        Assert.True(MediaService.SeenForOthers(seen, "ICON", MediaService.ArtOwner("Midnight City", "M83", "Hurry Up, We're Dreaming")));
        // Without album (browser web apps) every title counts on its own, as before.
        Assert.False(MediaService.SeenForOthers(seen, "WEB", MediaService.ArtOwner("A", "x", "")));
        Assert.True(MediaService.SeenForOthers(seen, "WEB", MediaService.ArtOwner("B", "x", "")));
    }

    [Fact]
    public void The_cover_search_uses_the_artist_without_the_album_Apple_Music_adds()
    {
        Assert.Equal("Pastel Ghost", CoverLookup.MainArtist("Pastel Ghost — Habits"));
        Assert.Equal("M83", CoverLookup.MainArtist("M83"));
        Assert.Contains("term=Pastel%20Ghost%20Habits&", CoverLookup.SearchUrl("Habits (Feat. X)", "Pastel Ghost — Habits - EP"));
    }

    [Fact]
    public void On_the_other_device_notifications_stay_out_completely()
    {
        var n = new NotificationService(new DynamicBay.Core.AppSettings { NotificationsEnabled = true });
        Assert.True(n.AddRemote("Laptop", "WhatsApp", "Lena", "Hallo", null));
        n.HiddenHere = true;
        Assert.Empty(n.Items);
        Assert.False(n.AddRemote("Laptop", "WhatsApp", "Lena", "Noch da?", null));
        Assert.Empty(n.Items);
        n.HiddenHere = false;
        Assert.True(n.AddRemote("Laptop", "WhatsApp", "Lena", "Jetzt hier", null));
    }
}
