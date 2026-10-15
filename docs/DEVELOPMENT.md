# Entwicklung

## Voraussetzungen

- .NET 9 SDK (`winget install Microsoft.DotNet.SDK.9`)
- Für Installer: Inno Setup 6 (`winget install JRSoftware.InnoSetup`) und Windows SDK (`winget install Microsoft.WindowsSDK.10.0.26100`)
- Für Icon-Generierung: Node.js

## Befehle

```powershell
dotnet run --project src/DynamicBay                 # App starten
dotnet test tests/DynamicBay.Tests                  # Unit-Tests (Platzierung, Federn, Icons, Einstellungen, Kalender …)
powershell -File tools/build-release.ps1            # self-contained publish + Identitäts-Paket + Setup.exe
```

## Design-Prüfung mit Screenshots

```powershell
powershell -File tools/snapshot.ps1 -Out artifacts/snapshots          # echte Bildschirmaufnahmen aller Zustände
$env:DYNAMICBAY_SNAPSHOT="settings"; powershell -File tools/snapshot.ps1   # alle Einstellungsseiten
$env:DYNAMICBAY_SNAPSHOT="readme";   powershell -File tools/snapshot.ps1   # saubere 2x-Renderings für die Doku
$env:DYNAMICBAY_SNAPSHOT="compact";  powershell -File tools/snapshot.ps1   # kleine Insel: Symbole mit langen Namen
$env:DYNAMICBAY_SNAPSHOT_CURSORHOP="1"; powershell -File tools/snapshot.ps1   # CursorHop-Karte und -Einblendungen (Demo)
$env:DYNAMICBAY_SNAPSHOT_LINK="1"; powershell -File tools/snapshot.ps1        # Karte „Gekoppelt“, Musik des Laptops, Koppel-Code (Demo)
$env:DYNAMICBAY_SNAPSHOT_DEVICES="1"; powershell -File tools/snapshot.ps1     # Karten „Geräte“ und „AirPods“ mit den echten Bluetooth-Geräten (nur lesen)
```

Der Snapshot-Modus (`DynamicBay.exe --snapshot <dir>`) lädt Demo-Daten, durchläuft Idle, Compact, Peek, Panel,
Ablage, Mitteilungen, Drop und eigene Widget-Layouts an allen Kanten und speichert PNGs. Er verändert die echten
Einstellungen nicht.

`DYNAMICBAY_SNAPSHOT=frames` rendert jedes Bild, während die minimale Ruhelinie auf- und zugeht (auch Linie, Compact und Peek zu Minimal und zurück; Größe, Lage und Bildabstand pro Bild im Log), als PNG
(`DYNAMICBAY_SNAPSHOT_SCALE`, Standard 1.5 = 150 %) und schreibt ins Log, in wie vielen Bildern WPF die Form
abgeschnitten hat (`FRAMES: … clipped by layout in 0` ist richtig). Mit `DYNAMICBAY_DEV=f` landet das Log in
`%AppData%\DynamicBay-dev-f`.

Speicher und Lecks: `powershell -File tools/memory-check.ps1` (siehe [performance/README.md](performance/README.md)).

`tools/live-test.ps1` testet die echte App mit Maus: Hover, Screenshot-Peek (Testbild im Screenshot-Ordner), Ziehen an
den linken und unteren Rand. `tools/toast-test.ps1` schickt Test-Benachrichtigungen im Namen einer App.
`tools/media-key.ps1` drückt eine Medientaste (weiter, zurück, Play/Pause), `tools/skip-test.ps1` nimmt die Insel beim
Titelwechsel alle 150 ms auf, `tools/screen-grab.ps1` fotografiert einen Bildschirmbereich samt Insel.

## Icons

- UI-Icons: `tools/icons/build-icons.mjs` erzeugt `Themes/Icons.Lucide.xaml` aus `lucide-static` (Liste im Skript).
- App-Palette: `tools/icons/build-appicons.mjs` erzeugt `Assets/AppIcons.json.gz` aus `simple-icons`.
- App-Icon: `tools/icons/make-app-icon.ps1`.

## Release

Tag pushen: `git tag v1.0.1 && git push --tags` – der Workflow `release.yml` testet, baut den Installer, führt den
Update-Test aus und legt ihn erst dann als GitHub-Release ab. Version kommt aus dem Tag.

- **Update-Test** (`tools/update-smoke.ps1`): installiert 1.0.15 und 1.0.16, aktualisiert wie der In-App-Updater auf
  den neuen Installer und prüft nach jedem Schritt, dass die App mit Paket-Identität startet. Läuft nur auf einem
  Wegwerf-Windows (CI, VM), nie auf dem eigenen PC. Von Hand: Workflow „Update smoke test“ (Eingabe `release`, um
  ein vorhandenes Release als neue Version zu prüfen).
- **Signatur des Identitäts-Pakets:** festes Zertifikat aus den Secrets `SIGNING_PFX_BASE64` / `SIGNING_PFX_PASSWORD`,
  einmalig angelegt mit `powershell -File tools/new-signing-cert.ps1`. Ohne die Secrets signiert jeder Build mit
  einem neuen Zertifikat.
- Postmortems schwerer Fehler: `docs/postmortems/`.
- Regeln für Coding-Agenten (Claude Code): `CLAUDE.md` im Hauptordner, bei Umbauten mitpflegen.

## Konventionen

- Keine Emojis in UI/Doku, Icons nur als Geometrie (Lucide, Simple Icons, eigene Glyphen in `Icons.Custom.xaml`).
- Rundungen konzentrisch, keine ovalen Knöpfe (Chips: `Btn.Chip`, Radius 10).
- Texte zweisprachig: Tabelle in `Core/Loc.cs` oder inline `{core:L De='…', En='…'}`.
- Jede Funktion als Dienst in `Services/`, abschaltbar über `AppSettings`.

## Test-Instanzen neben der Installation

`DYNAMICBAY_DEV=a` startet den Build als eigene Instanz: Daten in `%AppData%\DynamicBay-dev-a`, eigene
Single-Instance-Sperre, Insel unten. Sie ändert nichts an Windows (kein Autostart, keine Banner, kein
Identitäts-Paket, kein Update-Check, keine Tastenkürzel). Für Kopplungstests auf einem PC:

```powershell
$env:DYNAMICBAY_DEV="a"; $env:DYNAMICBAY_LINK_PORT="43901"; $env:DYNAMICBAY_LINK_DISCOVERY_PORT="43911"
$env:DYNAMICBAY_DEV_ALLOWPAIRING="1"; $env:DYNAMICBAY_DEV_AUTOCONFIRM="1"; .\DynamicBay.exe
# zweite Konsole: DYNAMICBAY_DEV=b, Port 43902, DYNAMICBAY_DEV_PAIR=127.0.0.1:43901, DYNAMICBAY_DEV_AUTOCONFIRM=1
```

`DYNAMICBAY_DEV_SENDFILE=<Pfad>` schickt die Datei an das erste Gerät, das online kommt. Eine Testinstanz führt Befehle
aus `%AppData%\DynamicBay-dev-<n>\dev-commands.txt` aus (eine Zeile pro Befehl, die Datei wird danach gelöscht):
`timer <min>`, `focus`, `pause`, `stop`, `handover`, `dnd on|off`, `main none|this|other|auto`, `battery low <Prozent>`,
`ask [Sekunden]`, `answer yes|no`, `lock`, `set Link… on|off`, `state` (schreibt Timer, Ruhe und Hauptgerät ins Log),
`media <Anzahl> [ms]` (erfundene Musik mit Cover an das andere Gerät, es spielt nichts).
`DYNAMICBAY_DEV_SETTINGS=Devices` öffnet beim Start die Einstellungen auf dieser Seite (Namen aus `SettingsPage`).

Hänger: `UI stall 878 ms` im Log kommt mit `Slow UI operation 870 ms: Background timer …` oder `await in …`, also
dem, was den UI-Thread so lange hielt (ab 150 ms; Testinstanzen mit `DYNAMICBAY_DEV_SLOWOPS=<ms>` schon früher).
Testinstanzen sperren nie den PC, `lock` steht nur im Log des anderen. Testinstanzen speichern

`DYNAMICBAY_DEV_LOOPBACK=1` lässt den Link nur auf 127.0.0.1 hören, ohne Suche (keine Firewall-Abfrage).
`DYNAMICBAY_DEV_SENDAUDIO=<Sekunden>` schickt so lange Ton an das erste Gerät, das online kommt: Testinstanzen
erzeugen einen 440-Hz-Ton statt aufzunehmen, spielen Empfangenes nirgends ab und schalten nichts stumm. Das Log zeigt
`Audio link: received … frames …, buffer … ms, … level 0,071` (Pegel des Tons) und `sound back here (…)`.
`DYNAMICBAY_DEV_SENDFILE=<Pfad>` schickt die Datei an das erste Gerät, das online kommt. Testinstanzen speichern
Empfangenes in `%AppData%\DynamicBay-dev-<n>\Downloads`, nie in den echten Downloads. Was über den Link geht, steht ohne
Inhalte im Log (`Link: status from …`, `Link: clipboard text (40 chars) …`, `Link: received … bytes`).

