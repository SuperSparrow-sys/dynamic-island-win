using System.Globalization;
using System.Windows.Markup;

namespace DynamicBay.Core;

/// <summary>Tiny DE/EN string table. Language is fixed at startup; a change applies after restarting the app.</summary>
public static class Loc
{
    public static bool German { get; private set; } = true;

    public static void Init(string setting)
    {
        German = setting switch
        {
            "de" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de",
        };
    }

    public static string T(string key) =>
        Table.TryGetValue(key, out var v) ? (German ? v.de : v.en) : key;

    public static string F(string key, params object[] args) => string.Format(T(key), args);

    internal static IReadOnlyDictionary<string, (string de, string en)> TableForTests => Table;

    private static readonly Dictionary<string, (string de, string en)> Table = new()
    {
        ["App.Name"] = ("DynamicBay", "DynamicBay"),
        ["Tab.Home"] = ("Nook", "Nook"),
        ["Tab.Tray"] = ("Ablage", "Tray"),
        ["Tab.Notifications"] = ("Mitteilungen", "Notifications"),
        ["Tab.Timer"] = ("Timer", "Timer"),

        ["Media.Nothing"] = ("Keine Wiedergabe", "Not playing"),
        ["Media.NothingHint"] = ("Starte Spotify oder einen anderen Player", "Start Spotify or another player"),
        ["Media.Connect"] = ("Mit Spotify verbinden", "Connect Spotify"),
        ["Media.Like"] = ("Zu Lieblingssongs", "Like"),
        ["Media.Queue"] = ("Warteschlange", "Queue"),
        ["Media.Devices"] = ("Gerät wählen", "Choose device"),
        ["Media.NowPlaying"] = ("Jetzt läuft", "Now playing"),
        ["Media.UpNext"] = ("Als Nächstes", "Up next"),

        ["Shelf.Title"] = ("Dateiablage", "Shelf"),
        ["Shelf.Empty"] = ("Dateien hierher ziehen", "Drop files here"),
        ["Shelf.EmptyHint"] = ("Später einfach wieder herausziehen", "Drag them out again later"),
        ["Shelf.Clear"] = ("Leeren", "Clear"),
        ["Drop.Title"] = ("Loslassen zum Ablegen", "Release to drop"),
        ["Drop.Subtitle"] = ("Dateien landen in der Ablage", "Files go to the shelf"),

        ["Clip.Title"] = ("Zwischenablage", "Clipboard"),
        ["Clip.Empty"] = ("Noch nichts kopiert", "Nothing copied yet"),
        ["Clip.EmptyHint"] = ("Win + Umschalt + S für einen Screenshot", "Win + Shift + S for a screenshot"),
        ["Clip.Screenshot"] = ("Screenshot", "Screenshot"),
        ["Clip.Copied"] = ("Kopiert", "Copied"),
        ["Clip.Image"] = ("Bild", "Image"),
        ["Clip.Text"] = ("Text", "Text"),
        ["Clip.Copy"] = ("Kopieren", "Copy"),
        ["Clip.Pin"] = ("Anheften", "Pin"),
        ["Clip.Save"] = ("Speichern unter", "Save as"),
        ["Clip.Delete"] = ("Löschen", "Delete"),
        ["Clip.Clear"] = ("Verlauf leeren", "Clear history"),
        ["Peek.Screenshot"] = ("Screenshot aufgenommen", "Screenshot captured"),
        ["Peek.ScreenshotHint"] = ("In der Zwischenablage · ziehen zum Teilen", "On the clipboard · drag to share"),


        ["Notif.Empty"] = ("Keine Mitteilungen", "No notifications"),
        ["Notif.Unavailable"] = ("Mitteilungen anderer Apps sind nicht verfügbar", "Notifications from other apps are unavailable"),
        ["Notif.UnavailableHint"] = ("Installiere DynamicBay über das Setup, um sie zu aktivieren", "Install DynamicBay with the setup to enable them"),
        ["Notif.Denied"] = ("Zugriff auf Mitteilungen verweigert", "Notification access denied"),
        ["Notif.Clear"] = ("Alle löschen", "Clear all"),

        ["Battery.Charging"] = ("Lädt", "Charging"),
        ["Battery.Unplugged"] = ("Akkubetrieb", "On battery"),
        ["Battery.Low"] = ("Akku schwach", "Low battery"),
        ["Battery.Full"] = ("Vollständig geladen", "Fully charged"),
        ["Bt.Connected"] = ("Verbunden", "Connected"),
        ["Bt.Disconnected"] = ("Getrennt", "Disconnected"),

        ["Timer.Title"] = ("Timer", "Timer"),
        ["Timer.Pomodoro"] = ("Fokus", "Focus"),
        ["Timer.Break"] = ("Pause", "Break"),
        ["Timer.Done"] = ("Timer abgelaufen", "Timer finished"),
        ["Timer.FocusDone"] = ("Fokuszeit vorbei – Zeit für eine Pause", "Focus session done – take a break"),
        ["Timer.BreakDone"] = ("Pause vorbei – weiter geht’s", "Break is over – back to it"),
        ["Timer.Start"] = ("Start", "Start"),
        ["Timer.Pause"] = ("Pause", "Pause"),
        ["Timer.Resume"] = ("Weiter", "Resume"),
        ["Timer.Reset"] = ("Zurücksetzen", "Reset"),

        ["Cal.Today"] = ("Heute", "Today"),
        ["Cal.NoEvents"] = ("Keine Termine mehr heute", "No more events today"),
        ["Cal.Setup"] = ("Kalender in den Einstellungen verbinden", "Connect a calendar in settings"),
        ["Cal.InMinutes"] = ("in {0} Min.", "in {0} min"),
        ["Cal.Now"] = ("jetzt", "now"),
        ["Cal.Starting"] = ("Termin beginnt bald", "Event starting soon"),
        ["Cal.AllDay"] = ("Ganztägig", "All day"),

        ["Update.Available"] = ("Update verfügbar", "Update available"),
        ["Update.Question"] = ("Update {0} verfügbar", "Update {0} available"),
        ["Update.QuestionHint"] = ("Jetzt installieren?", "Install now?"),
        ["Update.Install"] = ("Installieren", "Install"),
        ["Update.Later"] = ("Später", "Later"),
        ["Update.Downloading"] = ("Update {0} wird geladen", "Downloading update {0}"),
        ["Update.InstallingHint"] = ("Danach kurz bestätigen – DynamicBay startet neu", "Confirm once – DynamicBay restarts"),
        ["Update.Cancelled"] = ("Update abgebrochen", "Update cancelled"),
        ["Update.CancelledHint"] = ("Später über das Symbol in der Taskleiste nachholen", "Install later from the taskbar icon"),
        ["Update.Failed"] = ("Update fehlgeschlagen", "Update failed"),
        ["Tray.Update"] = ("Update {0} installieren", "Install update {0}"),
        ["Update.Hint"] = ("Version {0} · klicken zum Herunterladen", "Version {0} · click to download"),

        ["Tray.Show"] = ("Insel einblenden", "Show island"),
        ["Tray.Hide"] = ("Insel ausblenden", "Hide island"),
        ["Tray.Settings"] = ("Einstellungen", "Settings"),
        ["Tray.DND"] = ("Nicht stören", "Do not disturb"),
        ["Tray.Reset"] = ("Position zurücksetzen", "Reset position"),
        ["Tray.Quit"] = ("Beenden", "Quit"),

        ["Action.Hide"] = ("Ausblenden", "Hide"),
        ["Action.Settings"] = ("Einstellungen", "Settings"),
        ["Action.Pin"] = ("Offen halten", "Keep open"),
        ["Action.Open"] = ("Öffnen", "Open"),
        ["Action.ShowInFolder"] = ("Im Ordner zeigen", "Show in folder"),
        ["Action.Remove"] = ("Entfernen", "Remove"),
    };
}

/// <summary>Inline bilingual text for the settings UI: Text="{core:L De='Allgemein', En='General'}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LExtension : MarkupExtension
{
    public string De { get; set; } = "";
    public string En { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.German ? De : En;
}

/// <summary>XAML: Text="{core:T Tab.Home}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension(string key) => Key = key;
    public string Key { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
