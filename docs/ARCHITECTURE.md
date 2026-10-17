# Architektur

DynamicBay ist eine WPF-Anwendung (.NET 9, C#) ohne externe UI-Frameworks. Ziel: native Windows-APIs direkt nutzen
(Medien, Zwischenablage, Drag & Drop, Fenster ohne Fokus) und Apple-ähnliche Bewegung über echte Federphysik.

## Ordner

```
src/DynamicBay/
├─ App.xaml(.cs)          Start, Single-Instance, Dienste verdrahten, Peeks auslösen, Wartungsbefehle (--uninstall …)
├─ TrayIcon.cs            Symbol im Infobereich mit dunklem Menü
├─ Snapshots.cs           Design-Prüfung: --snapshot rendert jeden Zustand als PNG
├─ LeakCheck.cs           Speicher-Prüfung (DYNAMICBAY_SNAPSHOT=leak), siehe performance/README.md
├─ Core/                  Querschnitt ohne UI
│  ├─ AppSettings.cs      alle Einstellungen (eine JSON-Datei), Enums, Widget-IDs, Kalender-Konten
│  ├─ PlacementMath.cs    reine Positions-Mathematik (wo dockt die Insel, wo liegt das Fenster) – getestet
│  ├─ Monitors.cs         Bildschirme in physischen Pixeln (Per-Monitor-DPI v2)
│  ├─ Native.cs           Win32-P/Invoke
│  ├─ SecretStore.cs      Passwörter/Tokens mit DPAPI
│  ├─ SparsePackage.cs    Paket-Identität registrieren (für Mitteilungen)
│  ├─ ActivityState.cs    gesperrt oder Bildschirm aus: Dauer-Animationen und reine Anzeige-Abfragen pausieren
│  ├─ Loc.cs              Deutsch/Englisch, {core:T Key} und {core:L De=…, En=…}
│  ├─ ErrorText.cs        Fehler → kurzer Satz für Nutzer (Deutsch/Englisch), Details nur ins Log
│  ├─ PointerGesture.cs   Drücken/Ziehen mit genau einem Ende (Loslassen, Capture verloren, Insel versteckt)
│  ├─ WindowTools.cs      offenes Fenster zurückholen (auch minimiert)
│  └─ Log.cs, Hotkey.cs, Autostart.cs, ShellThumbnail.cs, ImageTools.cs, MessageWindow.cs
├─ Motion/                Spring (SwiftUI-artig: response + damping), SpringGroup (Render-Loop), SpringEase (für XAML)
├─ Island/
│  ├─ IslandWindow        transparentes Topmost-Fenster: Morph, Verschieben/Andocken, Hover, Peeks, Verstecken
│  ├─ IslandManager       Haupt-Insel + Spiegel-Inseln pro Bildschirm, verteilt Peeks
│  ├─ IslandViewModel     Daten für alle Ansichten, entscheidet welche Live Activities sichtbar sind
│  └─ Views/              CompactView, PeekView, DropView, ExpandedView (Widgets, Ablage, Mitteilungen)
├─ Services/              je ein Dienst pro Funktion (siehe unten)
├─ Settings/              Einstellungsfenster (Mica), App-Auswahl, Theme hell/dunkel/wie Windows
├─ Controls/              Icon, Waveform, ProgressRing, SegmentStack, FitGrid, Converter
└─ Themes/                Farben/Typo/Stile, Icon-Geometrien (generiert aus Lucide), eigene Glyphen
tests/DynamicBay.Tests/   xUnit-Tests, Fixtures/Api: echte API-Antworten (Spotify, Graph, Google, CalDAV, GitHub), Fixtures/CursorHop: Protokollzeilen
packaging/                Identitäts-Paket (AppxManifest + Build-Skript)
installer/                Inno-Setup-Skript
tools/                    Snapshot-, Live-Test-, Update-Test-, Speicher-, Diagnose- und Icon-Skripte
licenses/                 Lizenzhinweise der .NET-Laufzeit (mit THIRD-PARTY-NOTICES.txt ausgeliefert)
docs/postmortems/         Ursache und Lehren schwerer Fehler
docs/performance/         Messmethode und Messwerte
docs/offene_punkte.md     offene Aufgaben (zuletzt: Leistungsprüfung 15.10.2026)
CLAUDE.md                 Regeln für Coding-Agenten
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

**Niemand schaut hin.** `ActivityState` merkt sich Sperre (`SessionSwitch`) und Bildschirm aus
(`GUID_CONSOLE_DISPLAY_STATE` an das Meldungsfenster). Dann halten Dauer-Animationen (`Waveform`, `c:Loop`) an, und
Abfragen, deren Ergebnis nur angezeigt wird (Stummschaltung, Claude-Liste), lassen ihre Arbeit aus. Was andere Teile
steuert (Mikrofon-Nutzung für den Anrufmodus, Timer, Zeiterfassung), läuft weiter.

**Konzentrische Rundungen.** Innenradius = Außenradius − Abstand: Panel 32 → Karten 20 (Abstand 12) → Kacheln/Cover 10
(Abstand 10). Peek 22 → Cover 13 (Abstand 9).

## Dienste

| Dienst | Quelle |
|---|---|
| `MediaService` | Windows System Media Transport Controls (Spotify, Apple Music, Browser … ohne Login) |
| `SpotifyService` | Spotify Web API, OAuth PKCE mit eigener Client-ID, Token in DPAPI |
| `ClipboardService` | `WM_CLIPBOARDUPDATE`, PNG aus der Zwischenablage, Screenshot-Ordner aller OneDrive-Konten |
| `ShelfService` | Dateiliste mit Explorer-Vorschaubildern |
| `NotificationService` | `UserNotificationListener` (braucht Paket-Identität), liest bei `NotificationChanged` und jede Minute, Messenger-Filter |
| `BannerSuppressor` | setzt `ShowBanner=0` für alle Apps und schaltet den Mitteilungston aus, stellt beim Beenden wieder her |
| `CalendarService` | ICS, iCloud (`CalDavClient`), Google (`GoogleCalendarClient`) |
| `Scripting/ScriptWidgetsService` | Eigene Skript-Widgets: `ScriptEngine` (Jint-Sandbox, Scriptable-API aus `ScriptPrelude.js`), `ScriptRenderer` (Widget-Baum → WPF), `ScriptWidgetView`; siehe [SCRIPTS.md](SCRIPTS.md) |
| `ClaudeService` | `~/.claude/projects` lesen, Hooks über `127.0.0.1:43822`, Remote-Control-Links, PC-Abgleich, Rubus-Sessions in der Liste |
| `RubusBridge` | Rubus-Protokoll auf `127.0.0.1:43822/rubus/` (`RubusProtocol`: lesen, Übergänge → Einblendungen; `ListenerGate`: Regeln des Listeners), Kanäle mit Ablauf nach 90 s, Klicks je Kanal; siehe [INTEGRATIONS.md](INTEGRATIONS.md#rubus) |
| `CloudTargets` | erkannte Sync-Ordner (OneDrive, iCloud Drive, Google Drive, Dropbox), im Hintergrund gesucht |
| `TaildropService` | Dateien per Tailscale-Taildrop: liest `tailscale debug watch-ipn`, ordnet die Dateien in den Downloads zu |
| `CursorHopService` | CursorHop (eine Maus für mehrere PCs): liest nur sein Protokoll mit (`CursorHopLog`: Parser, Zustand, Entprellung), startet es bei Bedarf; siehe [INTEGRATIONS.md](INTEGRATIONS.md#cursorhop) |
| `Link/LinkService` | DynamicBay mit DynamicBay: finden, koppeln, verschlüsselte Verbindung, Nachrichten und Dateien (siehe unten) |
| `Link/LinkHub` | Startet den Link mit „Geräte koppeln“, hält die Funktionen und die Listen für Einstellungen und Insel |
| `ShortcutsService`, `AppIcons`, `InstalledApps` | Schnellstart, Simple-Icons-Palette (gzip, bei Bedarf geladen), Startmenü-Apps |
| `Battery/Bluetooth/Timer/Clock/SystemService` | Akku, Geräte, Timer/Pomodoro, Uhr, CPU/RAM |
| `AirPodsService` | Akku beider Ohrhörer und des Case aus der Bluetooth-Werbung der AirPods (`AirPodsAdvert`), siehe [INTEGRATIONS.md](INTEGRATIONS.md#airpods) |

## Daten

`%AppData%\DynamicBay\`: `settings.json`, `clipboard\` (Bilder + `index.json`), `shelf.json`, `secrets\` (DPAPI),
`link\identity.bin` (Geräte-Zertifikat, DPAPI), `link-temp\` (große Zwischenablage-Bilder unterwegs),
`banner-backup.json`, `audio-mute-backup.json` (nur solange der Ton auf einem anderen Gerät läuft), `logs\dynamicbay.log`.

## Link (Geräte koppeln)

`Services/Link/` verbindet zwei DynamicBays (PC und Laptop). Läuft nur mit `LinkEnabled` (aus, bis zum ersten Koppeln).

**Identität.** Jedes Gerät erzeugt einmal pro Windows-Benutzer ein selbst signiertes ECDSA-P-256-Zertifikat
(`DeviceIdentity`, DPAPI in `link\identity.bin`). Geräte-ID = die ersten 16 Hex-Zeichen von SHA-256 über den
öffentlichen Schlüssel, Fingerabdruck = SHA-256 über das Zertifikat. Gespeichert wird vom Partner nur der Fingerabdruck
(`AppSettings.PairedDevices`).

**Finden.** UDP 43824: alle 5 s und auf Anfrage eine Ansage `{t, id, name, port, proto}` als Broadcast (keine
Geheimnisse). Tailscale: Online-Geräte aus `tailscale status --json`, per UDP gefragt und auf Port 43823 geprüft, aber
nur Windows- und macOS-Geräte desselben Tailscale-Benutzers (`Self.UserID` = `Peer.UserID`, `LinkDiscovery.OwnDesktops`):
Im geteilten Tailnet werden Geräte anderer Leute, NAS, Handys und Server mit Tags nie gefragt. Dazu
eingetippte Adressen.

**Verbindung.** TCP 43823, `SslStream` mit TLS 1.3 (1.2 als Rückfall), beide Seiten zeigen ihr Zertifikat. Die
Prüfung läuft über den festgepinnten Fingerabdruck, nicht über eine Zertifizierungsstelle: Der Server nimmt nur
gekoppelte Zertifikate an (während „Koppeln erlauben“ auch fremde, die dann nur koppeln dürfen), der Client erwartet
genau den gespeicherten Fingerabdruck. Danach `hello` mit ID (muss zum Zertifikat passen), Name, Port und Boot-ID.
Rahmen: 4 Byte Länge (Big Endian) + UTF-8-JSON `{ "t": Typ, "id": …, "p": Nutzdaten }`, optional gefolgt von einem
Binär-Rahmen (`"bin": true`, für Dateiblöcke); höchstens 1 MiB pro Rahmen. Ping alle 15 s, nach 45 s Stille ist die
Verbindung tot. Neuverbindung mit Backoff 1 s bis 60 s, sofort bei Netzwechsel, nach dem Aufwachen und wenn Tailscale
angeht. Pro Gerät eine Verbindung: Wählen beide gleichzeitig, bleibt die, die das Gerät mit der kleineren ID gewählt
hat (`LinkProtocol.KeepNew`); eine neue Boot-ID (Partner neu gestartet) gewinnt immer.

**Koppeln (Zahlenvergleich).** Nur wenn der Empfänger „Koppeln erlauben“ geöffnet hat (2 min, höchstens 5 Versuche pro
Absender in 10 min). Der Empfänger legt sich zuerst auf eine Zufallszahl fest (Commitment), dann tauschen beide ihre Zufallszahlen.
Code = 6 Ziffern aus SHA-256(„DynamicBay-pair-v1“ ‖ beide Fingerabdrücke sortiert ‖ beide Zufallszahlen). Ein Gerät
in der Mitte sieht andere Zertifikate und kann den Code wegen des Commitments nicht vorausberechnen. Gespeichert wird
erst, wenn beide Nutzer innerhalb von 2 min bestätigt haben. Entkoppeln schickt `unpair`; der Partner zeigt die
Kopplung dann als abgelehnt und wählt nicht mehr. Lehnt ein Gerät das Zertifikat dreimal hintereinander ab, gilt das
genauso.

**Für Funktionen.** `RegisterHandler(typ, …)` und `SendAsync(geräteId, typ, objekt)` (eigene Typen wie `clipboard`,
`notification`, `media`, `status`; `hello`, `ping`, `pair.*`, `file.*` … gehören dem Link). Dateien: `SendFileAsync`,
beim Empfänger `FileOffered` → `Accept(ordner, maxBytes)`/`Decline()`. Lange Ströme (Ton) bekommen eine eigene
TLS-Verbindung: `OpenStreamAsync(geräteId, "audio")` wählt die Adresse der Hauptverbindung mit demselben festgepinnten
Zertifikat (Hello mit `intent: "stream"`, `purpose`), der Empfänger nimmt sie nur von gekoppelten Geräten, die gerade
online sind, und meldet `StreamOpened`. Ein `LinkStream` trägt nackte Rahmen (4 Byte Länge + Bytes) und schließt mit
seinem Gerät (offline, entkoppelt, Link aus). Blöcke à 256 KiB in eine `.part`-Datei,
SHA-256 und Größe werden geprüft, dann umbenannt; Dateinamen bereinigt (`LinkProtocol.SanitizeFileName`), Ziel
`Downloads\DynamicBay`, Mark of the Web wie bei Downloads. Ereignisse kommen auf dem UI-Thread an. Die Leseschleife
wartet nie auf ein eigenes Senden (Antworten gehen ohne `await` raus): Sonst könnten sich zwei Geräte, die gleichzeitig
Dateien schicken, gegenseitig blockieren.

**Härtung.** Nur wer das festgepinnte Zertifikat zeigt und dann unseres ablehnt, zählt als „lehnt ab“ (dreimal →
abgelehnt); lehnen wir ein fremdes Zertifikat ab (anderes Gerät unter der alten Adresse), heißt das `wrong-device` und
zählt nicht. Gefundene Geräte verfallen nach 20 s (LAN) bzw. 90 s (Tailscale), höchstens 64, Änderungen gehen höchstens
zweimal pro Sekunde an die Oberfläche. Koppel-Versuche: 5 pro Absender-Adresse in 10 min, 20 insgesamt. Dateinamen
verlieren auch Unicode-Formatzeichen (RTLO). Den Schlüssel, den Schannel im Schlüsselspeicher braucht, merkt sich
`link\identity.bin.key` mit Namen; bleibt er nach einem harten Beenden liegen, löscht ihn der nächste Start.

**Funktionen** (`Services/Link/*Feature.cs`, Basis `LinkFeature`): je ein Nachrichtentyp und ein eigener Schalter
(`LinkShare*`, `LinkLockTogether`), der beim Senden und beim Empfangen geprüft wird; ohne WPF, damit zwei
Link-Instanzen im Test miteinander reden. `LinkHub` verbindet sie mit den lokalen Diensten (die Zusammenarbeit in
`LinkHub.Together.cs`, die Einblendungen dazu in `App.Link.cs`). Lässt ein Gerät etwas weg, weil das andere es zeigt,
prüft es vorher dessen Schalter (`LinkPeerSwitch`, Typ `<funktion>.share`, beim Verbinden und bei Änderung).

| Funktion | Typ | Inhalt |
|---|---|---|
| `LinkStatusFeature` | `status` | Name, Akku, Version; beim Verbinden und bei Änderung |
| `LinkFilesFeature` | `file.*` | Annahme ohne Nachfrage nach `Downloads\DynamicBay`, dann Ablage; andere Funktionen können Angebote übernehmen (`LinkFileClaim`) |
| `LinkClipboardFeature` | `clipboard` | Text, Bild bis 700 KB in der Nachricht, bis 5 MB als übernommene Datei; dieselben Inhalte nicht innerhalb 60 s zurück |
| `LinkNotificationsFeature` | `notification` | App, Titel, Text, Symbol (PNG); Empfangenes wird nie weitergeleitet |
| `LinkMediaFeature` | `media`, `media.cmd` | Titel, Künstler, Cover (JPEG ≤ 300 px), Position; Befehle `playpause`/`next`/`previous` nur bei vorhandenem Player |
| `LinkAudioFeature` | Strom `audio` | Ton dieses PCs auf dem anderen Gerät (siehe unten) |
| `LinkRoleFeature` | `role`, `peek` | Hauptgerät (Einstellung relativ: `this`/`other`/`auto`, auf der Leitung die Geräte-ID, die neuere Wahl gewinnt), Leerlaufzeit und CursorHop-Maus für „Automatisch“, weitergereichte Einblendungen |
| `LinkTimerFeature` | `timer`, `timer.share` | Zustand des Timers (läuft, Pause, Modus, Restzeit, Besitzer); die neuere Änderung gewinnt über einen Zähler (keine Uhrzeiten), beim Verbinden der laufende Timer; das Ende kommt nicht über die Leitung (`ShowsEnd`) |
| `LinkQuietFeature` | `quiet` | „Nicht stören“ mit Zeitstempel (das neuere gewinnt), Anruf und Fokus als `AppSettings.PeerQuiet` (nie gespeichert, nie zurückgeschickt) |
| `LinkQuestionFeature` | `ask`, `ask.answer`, `ask.share` | Fragen mit Knöpfen vom stillen Gerät; Antwort `yes`/`no`/`timeout` zurück, nur vom gefragten Gerät; Frist = Zeit der Frage + 10 s; Gerät weg = „lost“, die Frage erscheint dann lokal |
| `LinkBatteryFeature` | `battery`, `battery.share` | `low`/`plugged`/`unplugged`/`full` mit Prozent; das stille Gerät lässt die eigene Einblendung weg, wenn das Hauptgerät sie zeigt |
| `LinkLockFeature` | `lock` | Gesperrt (SessionSwitch); das andere Gerät sperrt nur nach 1 min ohne Eingabe, ohne Echo; nie entsperren |

Das stille Gerät leitet Einblendungen über `IslandManager.Divert` ans Hauptgerät (`PeekKind`: `Local` bleibt immer,
`Notification` kommt über die Mitteilungs-Funktion); gleiche Einblendungen (Titel und Untertitel) und gleiche
Mitteilungen innerhalb 10 s erscheinen einmal. Ausblenden läuft über dieselbe Unterdrückung wie bei Vollbild-Apps,
`AppSettings.Hidden` bleibt unberührt.

**Ton an anderes Gerät** (`LinkAudioFeature`, `Services/Link/Audio/`). Sender: WASAPI-Loopback der
Standard-Ausgabe (NAudio, `LoopbackAudioSource`), `AudioConvert` auf 48 kHz Stereo, 20-ms-Blöcke mit Opus (Concentus,
reines C#, 160 kbit/s VBR, Komplexität 5), höchstens 200 ms in der Sende-Warteschlange (älteste fliegen raus).
Empfänger: Opus → `AudioJitterBuffer` → `WasapiAudioSink` (geteilter Modus, 40 ms). Der Puffer startet bei 100 ms (über
Tailscale 150 ms), regelt die Uhrabweichung beider Soundkarten über die Lesegeschwindigkeit (höchstens ±0,5 %,
lineare Interpolation), wirft nur bei mehr als dem Vierfachen ab und blendet beim Leerlaufen aus und beim Neustart
ein (keine Klicks). Rahmen im Strom (`LinkAudioProtocol`): 1 Kopf (JSON: Angebot, Antwort `ok`/`reason`, Ende
`end: off|output|stopped`), 2 Ton (Folgenummer, Millisekunden-Tick, Opus), 3 Lebenszeichen (jede Sekunde in beide
Richtungen, 5 s Stille = tot). Endet der Strom ohne `end`, ist die Verbindung weg.
Messung (11.10.2026, Realtek-Lautsprecher, Windows 11): WASAPI-Loopback liefert den Ton auch bei stummgeschaltetem
Endpunkt und bei Lautstärke 2 % unverändert (ein erzeugter 440-Hz-Ton in den stummen Endpunkt gespielt, im Loopback
wiedergefunden). Deshalb wird der eigene Endpunkt einfach stummgeschaltet (`LocalMuteGuard`, Vorher-Zustand in
`audio-mute-backup.json`, nach einem Absturz beim nächsten Start zurück; was der Nutzer selbst stumm hatte, bleibt
stumm). Läuft nichts, liefert Loopback gar keine Pakete; der Empfänger blendet dann aus und wartet. Treiber mit eigenen
Klangeffekten (Realtek-Lautheit) können den Pegel im Loopback von der Lautstärke abhängig machen. Testinstanzen
(`DYNAMICBAY_DEV`) senden einen erzeugten Ton, spielen nichts (`NullAudioSink`) und schalten nichts stumm, sie loggen
nur, was sie täten.

**Firewall.** Der Installer legt eingehende Regeln für `DynamicBay.exe` an (TCP 43823, UDP 43824): private und
Domänen-Netze per `netsh`, dazu per `New-NetFirewallRule` eine Regel nur auf dem Adapter „Tailscale“ für
Tailscale-Adressen (100.64.0.0/10 allein träfe auch Carrier-Grade-NAT in Hotspots). Die Deinstallation entfernt beide.

## Installation und Identität

Der Installer (Inno Setup) legt die self-contained App nach `Programme\DynamicBay`, vertraut dem Paket-Zertifikat
(`TrustedPeople`) und registriert `DynamicBay.msix` für den angemeldeten Benutzer als *Sparse Package* (externe
Dateien = Installationsordner), solange die App geschlossen ist. Bei Updates genauso. Ab dann darf sie Mitteilungen
lesen. Schlägt das fehl, registriert die App das Paket beim ersten Start selbst und startet einmal neu. Nie aus der
laufenden App mit `DeferRegistrationWhenPackagesAreInUse`: Die verzögerte Registrierung verliert den externen Ort,
scheitert mit 0x80073CF9 und blockiert danach jeden Start der exe. Deinstallation: `DynamicBay.exe --uninstall` stellt Banner, Autostart und Claude-Hooks wieder her und entfernt
das Paket.
