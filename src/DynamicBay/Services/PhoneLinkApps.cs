using System.IO;
using System.Windows;
using System.Windows.Media;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Notifications of the phone come through Smartphone-Link: Windows names "Smartphone-Link" as the app, the toast's
/// first line is the phone app ("Snapchat"), then the sender and the message. This puts the phone app in front,
/// with its own icon (the one Smartphone-Link keeps of the phone, else its brand mark).
/// </summary>
public static class PhoneLinkApps
{
    /// <summary>Smartphone-Link's notifications of phone apps (not its call window, that one is a call).</summary>
    public static bool IsPhoneNotification(string appId) =>
        appId.StartsWith("Microsoft.YourPhone_", StringComparison.OrdinalIgnoreCase)
        && !appId.Contains("YourPhoneCalling", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The toast's lines as phone app, title and text. Null when there is no app line: Smartphone-Link's own call
    /// notification has two lines, caller and "Eingehender Anruf" (seen on the laptop), and is no app. Apps come with
    /// three lines ("WhatsApp", "Philipp Seeger", "Verpasster Sprachanruf"), a known app also with two.
    /// </summary>
    public static (string App, string Title, string Body)? Unwrap(IReadOnlyList<string> texts)
    {
        if (texts.Count < 2) return null;
        string app = texts[0].Trim();
        if (app.Length == 0 || app.Length > 40 || app.Contains('\n')) return null;
        if (texts.Count == 2 && !Ids.ContainsKey(AppIcons.Normalize(app))) return null;
        return (app, texts[1], string.Join("\n", texts.Skip(2)));
    }

    // Phone app name -> its id on the iPhone and on Android (Smartphone-Link names its icon files after it).
    private static readonly Dictionary<string, string[]> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["snapchat"] = new[] { "com.toyopagroup.picaboo", "com.snapchat.android" },
        ["whatsapp"] = new[] { "net.whatsapp.WhatsApp", "com.whatsapp" },
        ["whatsappbusiness"] = new[] { "net.whatsapp.WhatsAppSMB", "com.whatsapp.w4b" },
        ["instagram"] = new[] { "com.burbn.instagram", "com.instagram.android" },
        ["nachrichten"] = new[] { "com.apple.MobileSMS", "com.google.android.apps.messaging" },
        ["messages"] = new[] { "com.apple.MobileSMS", "com.google.android.apps.messaging" },
        ["telefon"] = new[] { "com.apple.mobilephone", "com.google.android.dialer" },
        ["phone"] = new[] { "com.apple.mobilephone", "com.google.android.dialer" },
        ["facetime"] = new[] { "com.apple.facetime" },
        ["mail"] = new[] { "com.apple.mobilemail" },
        ["kalender"] = new[] { "com.apple.mobilecal" },
        ["calendar"] = new[] { "com.apple.mobilecal" },
        ["erinnerungen"] = new[] { "com.apple.reminders" },
        ["reminders"] = new[] { "com.apple.reminders" },
        ["telegram"] = new[] { "ph.telegra.Telegraph", "org.telegram.messenger" },
        ["signal"] = new[] { "org.whispersystems.signal", "org.thoughtcrime.securesms" },
        ["threema"] = new[] { "ch.threema.iapp", "ch.threema.app" },
        ["messenger"] = new[] { "com.facebook.Messenger", "com.facebook.orca" },
        ["facebook"] = new[] { "com.facebook.Facebook", "com.facebook.katana" },
        ["tiktok"] = new[] { "com.zhiliaoapp.musically" },
        ["discord"] = new[] { "com.hammerandchisel.discord", "com.discord" },
        ["gmail"] = new[] { "com.google.Gmail", "com.google.android.gm" },
        ["outlook"] = new[] { "com.microsoft.Office.Outlook", "com.microsoft.office.outlook" },
        ["teams"] = new[] { "com.microsoft.skype.teams" },
        ["microsoftteams"] = new[] { "com.microsoft.skype.teams" },
        ["x"] = new[] { "com.atebits.Tweetie2", "com.twitter.android" },
        ["youtube"] = new[] { "com.google.ios.youtube", "com.google.android.youtube" },
        ["bereal"] = new[] { "AlexisBarreyat.BeReal" },
        ["spotify"] = new[] { "com.spotify.client", "com.spotify.music" },
        ["linkedin"] = new[] { "com.linkedin.LinkedIn" },
        ["slack"] = new[] { "com.tinyspeck.chatlyio", "com.Slack" },
        ["claude"] = new[] { "com.anthropic.claude" },
    };

    /// <summary>Where Smartphone-Link keeps the phone's app icons ("app_icon_net.whatsapp.WhatsApp1.0.jpg").</summary>
    public static string IconFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", "Microsoft.YourPhone_8wekyb3d8bbwe", "TempState", "app_icons");

    /// <summary>
    /// The icon file of a phone app among <paramref name="files"/> (names only): by its known id, else a file whose id
    /// contains the app's name ("claude" in "com.anthropic.claude"). Null when none fits.
    /// </summary>
    public static string? IconFile(string app, IEnumerable<string> files)
    {
        string key = AppIcons.Normalize(app);
        if (key.Length == 0) return null;
        var names = files.Select(Path.GetFileName).Where(f => f is not null && f.StartsWith("app_icon_", StringComparison.OrdinalIgnoreCase)).Cast<string>().ToList();
        if (Ids.TryGetValue(key, out var ids))
            foreach (var id in ids)
                if (names.FirstOrDefault(f => f.StartsWith("app_icon_" + id, StringComparison.OrdinalIgnoreCase)
                        && f.Length > 9 + id.Length && !char.IsLetter(f[9 + id.Length])) is { } hit)
                    return hit;
        if (key.Length < 4) return null; // "x" or "mail" would be in too many ids
        return names.FirstOrDefault(f => AppIcons.Normalize(Path.GetFileNameWithoutExtension(f)[9..]).Contains(key));
    }

    /// <summary>The phone app's icon: Smartphone-Link's copy, else its brand mark, else null (Smartphone-Link's logo stays).</summary>
    public static ImageSource? Icon(string app)
    {
        try
        {
            var folder = IconFolder;
            if (Directory.Exists(folder) && IconFile(app, Directory.EnumerateFiles(folder)) is { } file)
                return ImageTools.Load(Path.Combine(folder, file), 64);
        }
        catch { }
        return Application.Current?.Dispatcher.Invoke(() => Glyph(app));
    }

    /// <summary>A brand mark (Simple Icons) on a dark rounded square, like an app icon.</summary>
    private static ImageSource? Glyph(string app)
    {
        if (AppIcons.Find(app, "") is not { } g) return null;
        var brand = new SolidColorBrush(g.Brand);
        var bounds = g.Data.Bounds;
        double size = Math.Max(bounds.Width, bounds.Height) * 1.6;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)), null,
            new RectangleGeometry(new Rect(bounds.X + bounds.Width / 2 - size / 2, bounds.Y + bounds.Height / 2 - size / 2, size, size), size * 0.22, size * 0.22)));
        group.Children.Add(g.Stroked
            ? new GeometryDrawing(null, new Pen(brand, Math.Max(bounds.Width, bounds.Height) / 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g.Data)
            : new GeometryDrawing(brand, null, g.Data));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
