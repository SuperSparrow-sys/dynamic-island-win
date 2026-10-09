# Architektur

DynamicBay ist eine WPF-Anwendung (.NET 9, C#) ohne externe UI-Frameworks. Ziel: native Windows-APIs direkt nutzen
(Medien, Zwischenablage, Drag & Drop, Fenster ohne Fokus) und Apple-ähnliche Bewegung über echte Federphysik.

## Ordner

```
src/DynamicBay/
├─ App.xaml(.cs)          Start, Single-Instance, Dienste verdrahten, Peeks auslösen, Wartungsbefehle (--uninstall …)
├─ TrayIcon.cs            Symbol im Infobereich mit dunklem Menü
├─ Snapshots.cs           Design-Prüfung: --snapshot rendert jeden Zustand als PNG
├─ Core/                  Querschnitt ohne UI
│  ├─ AppSettings.cs      alle Einstellungen (eine JSON-Datei), Enums, Widget-IDs, Kalender-Konten
│  ├─ PlacementMath.cs    reine Positions-Mathematik (wo dockt die Insel, wo liegt das Fenster) – getestet
│  ├─ Monitors.cs         Bildschirme in physischen Pixeln (Per-Monitor-DPI v2)
│  ├─ Native.cs           Win32-P/Invoke
│  ├─ SecretStore.cs      Passwörter/Tokens mit DPAPI
│  ├─ SparsePackage.cs    Paket-Identität registrieren (für Mitteilungen)
│  ├─ Loc.cs              Deutsch/Englisch, {core:T Key} und {core:L De=…, En=…}
│  └─ Log.cs, Hotkey.cs, Autostart.cs, ShellThumbnail.cs, ImageTools.cs, MessageWindow.cs
├─ Motion/                Spring (SwiftUI-artig: response + damping), SpringGroup (Render-Loop), SpringEase (für XAML)
├─ Island/
│  ├─ IslandWindow        transparentes Topmost-Fenster: Morph, Verschieben/Andocken, Hover, Peeks, Verstecken
│  ├─ IslandManager       Haupt-Insel + Spiegel-Inseln pro Bildschirm, verteilt Peeks
│  ├─ IslandViewModel     Daten für alle Ansichten, entscheidet welche Live Activities sichtbar sind
│  └─ Views/              CompactView, PeekView, DropView, ExpandedView (Widgets, Ablage, Mitteilungen)
├─ Services/              je ein Dienst pro Funktion (siehe unten)
├─ Settings/              Einstellungsfenster (Mica), App-Auswahl, Theme hell/dunkel
├─ Controls/              Icon, Waveform, ProgressRing, SegmentStack, FitGrid, Converter
└─ Themes/                Farben/Typo/Stile, Icon-Geometrien (generiert aus Lucide), eigene Glyphen
tests/DynamicBay.Tests/   xUnit-Tests
packaging/                Identitäts-Paket (AppxManifest + Build-Skript)
installer/                Inno-Setup-Skript
tools/                    Snapshot-, Live-Test-, Diagnose- und Icon-Skripte
```

## Die Insel

**Ein Fenster, mehrere Ebenen.** `IslandWindow` ist ein rahmenloses, transparentes Fenster
(`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`: nicht in Alt+Tab, stiehlt nie den Fokus). Darin liegt die schwarze Form
(`Shape`) mit vier Ebenen: Compact, Peek, Drop, Expanded. Transparente Pixel lassen Klicks durch.

**Zustände.** `DesiredMode()` wählt nach Priorität: Drop › Expanded › Peek › Minimized › Compact › Idle.
`Refresh()` setzt Zielgröße und -radius und startet die Federn.

**Bewegung.** Breite, Höhe und Radius sind je eine `Spring` (Response/Dämpfung wie SwiftUI). Aufklappen federt leicht
nach (Dämpfung 0.72), Zuklappen nicht (0.9). Neue Inhalte blenden mit Unschärfe und Skalierung 0.9 → 1 ein, während
die Form wächst. Ein Retarget behält die Geschwindigkeit – unterbrochene Animationen wirken dadurch flüssig.

**Platzierung.** Gespeichert werden Kante, Ausrichtung (Start/Mitte/Ende), Position entlang der Kante (0–1), Abstand
und Bildschirm. `PlacementMath.FromDrop` bestimmt das beim Loslassen (nächste Kante, Ecken-Zone 18 %, magnetisches
Einrasten), `WindowOrigin` berechnet die Fensterposition. Das Fenster wird anschließend auf seinen Bildschirm begrenzt
(`ClampToMonitor`), die Form behält ihre Position über eine Gegen-Verschiebung.

**Konzentrische Rundungen.** Innenradius = Außenradius − Abstand: Panel 32 → Karten 20 (Abstand 12) → Kacheln/Cover 10
(Abstand 10). Peek 22 → Cover 13 (Abstand 9).

## Dienste

| Dienst | Quelle |
|---|---|
| `MediaService` | Windows System Media Transport Controls (Spotify, Apple Music, Browser … ohne Login) |
| `SpotifyService` | Spotify Web API, OAuth PKCE mit eigener Client-ID, Token in DPAPI |
| `ClipboardService` | `WM_CLIPBOARDUPDATE`, PNG aus der Zwischenablage, Screenshot-Ordner aller OneDrive-Konten |
| `ShelfService` | Dateiliste mit Explorer-Vorschaubildern |
| `NotificationService` | `UserNotificationListener` (braucht Paket-Identität), Messenger-Filter |
| `BannerSuppressor` | setzt `ShowBanner=0` für alle Apps und schaltet den Mitteilungston aus, stellt beim Beenden wieder her |
| `CalendarService` | ICS, iCloud (`CalDavClient`), Google (`GoogleCalendarClient`) |
| `Scripting/ScriptWidgetsService` | Eigene Skript-Widgets: `ScriptEngine` (Jint-Sandbox, Scriptable-API aus `ScriptPrelude.js`), `ScriptRenderer` (Widget-Baum → WPF), `ScriptWidgetView`; siehe [SCRIPTS.md](SCRIPTS.md) |
| `ClaudeService` | `~/.claude/projects` lesen, Hooks über `127.0.0.1:43822`, Remote-Control-Links, PC-Abgleich |
| `CloudTargets` | erkannte Sync-Ordner (OneDrive, iCloud Drive, Google Drive, Dropbox) |
| `ShortcutsService`, `AppIcons`, `InstalledApps` | Schnellstart, Simple-Icons-Palette (gzip, bei Bedarf geladen), Startmenü-Apps |
| `Battery/Bluetooth/Timer/Clock/SystemService` | Akku, Geräte, Timer/Pomodoro, Uhr, CPU/RAM |

## Daten

`%AppData%\DynamicBay\`: `settings.json`, `clipboard\` (Bilder + `index.json`), `shelf.json`, `secrets\` (DPAPI),
`banner-backup.json`, `logs\dynamicbay.log`.

## Installation und Identität

Der Installer (Inno Setup) legt die self-contained App nach `Programme\DynamicBay`, vertraut dem Paket-Zertifikat
(`TrustedPeople`) und startet die App als normaler Benutzer. Beim ersten Start registriert die App `DynamicBay.msix`
als *Sparse Package* (externe Dateien = Installationsordner) und startet einmal neu – ab dann darf sie Mitteilungen
lesen. Deinstallation: `DynamicBay.exe --uninstall` stellt Banner, Autostart und Claude-Hooks wieder her und entfernt
das Paket.
