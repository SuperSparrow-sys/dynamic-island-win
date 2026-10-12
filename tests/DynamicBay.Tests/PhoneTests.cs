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
    public void Calls_of_the_phone_are_recognized_with_number_and_missed()
    {
        const string SL = "Smartphone-Link";
        // As it arrives from an iPhone: "Philipp" / "Eingehender Anruf", name only.
        Assert.Equal(("Philipp", "", false), RecentCalls.ParseCall(PhoneLink, SL, "Philipp", "Eingehender Anruf"));
        Assert.True(RecentCalls.IsRinging(PhoneLink, SL, "Philipp", "Eingehender Anruf"));
        Assert.True(RecentCalls.IsRinging("", SL, "Philipp", "Eingehender Anruf")); // forwarded by the paired device
        Assert.Equal(("+49 176 41511149", "+4917641511149", true), RecentCalls.ParseCall(PhoneLink, SL, "+49 176 41511149", "Verpasster Anruf"));
        Assert.Equal(("Anna", "015123456789", false), RecentCalls.ParseCall(Calling, SL, "Anna", "0151 23456789"));
        // A WhatsApp call through Smartphone-Link is called back in WhatsApp, not by phone.
        Assert.Null(RecentCalls.ParseCall(PhoneLink, "WhatsApp", "Philipp Seeger", "Verpasster Sprachanruf"));
        Assert.False(RecentCalls.IsRinging(PhoneLink, "WhatsApp", "Philipp Seeger", "Eingehender Anruf"));
        // Messages are no calls, Teams calls are called back in Teams.
        Assert.Null(RecentCalls.ParseCall(PhoneLink, "Snapchat", "Lotte", "sent you a Snap"));
        Assert.Null(RecentCalls.ParseCall("MSTeams_8wekyb3d8bbwe!MSTeams", "Microsoft Teams", "Anna", "ruft an"));
        Assert.Null(RecentCalls.ParseCall(PhoneLink, SL, "Max", "Anruf morgen um 10?"));
    }

    [Fact]
    public void Calls_of_one_caller_fold_into_one_line()
    {
        var calls = new RecentCalls(load: false);
        var t = new DateTime(2026, 10, 11, 19, 0, 0);
        calls.Record("+49 176 41511149", "+4917641511149", missed: true, t);
        calls.Record("Chung", "", missed: false, t.AddMinutes(5));
        calls.Record("0176 41511149", "017641511149", missed: false, t.AddMinutes(30)); // same number, written differently
        calls.Record("+49 176 41511149", "+4917641511149", missed: true, t.AddMinutes(30.5)); // the same call again (incoming, then missed)

        Assert.Equal(2, calls.Items.Count);
        var top = calls.Items[0];
        Assert.Equal("+4917641511149", top.Number);
        Assert.Equal((2, 2), (top.Count, top.Missed));
        Assert.Equal("Chung", calls.Items[1].Who);
        Assert.False(calls.IsEmpty);
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

    [Fact]
    public void Teams_call_window_is_found_and_moved_under_the_island()
    {
        // As seen on the laptop: the call toast 465x315 bottom right, the main window carries chat and tenant in its title.
        Assert.True(TeamsCalls.IsToast("Microsoft Teams", 465, 315));
        Assert.True(TeamsCalls.IsToast("Microsoft Teams", 465, 170));
        Assert.False(TeamsCalls.IsToast("Chat | Jonathan Seeger | Regenery GmbH | Microsoft Teams", 1536, 864));
        Assert.False(TeamsCalls.IsToast("Microsoft Teams", 1458, 911));

        var work = new DynamicBay.Core.Native.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1032 };
        Assert.Equal((727, 64), TeamsCalls.TopCenter(work, 465, 1.0));
        Assert.Equal((727, 96), TeamsCalls.TopCenter(work, 465, 1.5));

        Assert.Equal("Anna Schmidt", TeamsCalls.CallerFrom(new[] { "Microsoft Teams", "", "Anna Schmidt", "Eingehender Anruf" }));
        Assert.Equal("", TeamsCalls.CallerFrom(new[] { "Mit Audio annehmen", "Anruf ablehnen" }));
        Assert.Contains("Mit Audio annehmen", TeamsCalls.AcceptNames);
        Assert.Contains("Anruf ablehnen", TeamsCalls.DeclineNames);
    }

    [Fact]
    public void A_phone_call_is_answered_with_the_buttons_of_its_notification()
    {
        // As Smartphone-Link shows it: "Auf dem PC zulassen", "Nachricht", "Ablehnen".
        Assert.Contains("Auf dem PC zulassen", PhoneLinkCalls.AcceptNames);
        Assert.Contains("Ablehnen", PhoneLinkCalls.DeclineNames);
        Assert.DoesNotContain("Nachricht", PhoneLinkCalls.AcceptNames.Concat(PhoneLinkCalls.DeclineNames));
        Assert.True(PhoneLinkCalls.IsNotificationCenter("Benachrichtigungscenter"));
        Assert.True(PhoneLinkCalls.IsNotificationCenter("Notification Center"));
        Assert.False(PhoneLinkCalls.IsNotificationCenter("Smartphone-Link"));
        // A question that runs out on the other device declines nothing for calls; other questions keep "timeout = no".
        Assert.True(new DynamicBay.Island.PeekItem().TimeoutIsNo);
    }
}
