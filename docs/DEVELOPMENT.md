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
```

Der Snapshot-Modus (`DynamicBay.exe --snapshot <dir>`) lädt Demo-Daten, durchläuft Idle, Compact, Peek, Panel,
Ablage, Mitteilungen, Drop und eigene Widget-Layouts an allen Kanten und speichert PNGs. Er verändert die echten
Einstellungen nicht.

`tools/live-test.ps1` testet die echte App mit Maus: Hover, Screenshot-Peek (Testbild im Screenshot-Ordner), Ziehen an
den linken und unteren Rand. `tools/toast-test.ps1` schickt Test-Benachrichtigungen im Namen einer App.
`tools/media-key.ps1` drückt eine Medientaste (weiter, zurück, Play/Pause), `tools/skip-test.ps1` nimmt die Insel beim
Titelwechsel alle 150 ms auf, `tools/screen-grab.ps1` fotografiert einen Bildschirmbereich samt Insel.

## Icons

- UI-Icons: `tools/icons/build-icons.mjs` erzeugt `Themes/Icons.Lucide.xaml` aus `lucide-static` (Liste im Skript).
- App-Palette: `tools/icons/build-appicons.mjs` erzeugt `Assets/AppIcons.json.gz` aus `simple-icons`.
- App-Icon: `tools/icons/make-app-icon.ps1`.

## Release

Tag pushen: `git tag v1.0.1 && git push --tags` – der Workflow `release.yml` testet, baut den Installer und legt ihn als
GitHub-Release ab. Version kommt aus dem Tag.

## Konventionen

- Keine Emojis in UI/Doku, Icons nur als Geometrie (Lucide, Simple Icons, eigene Glyphen in `Icons.Custom.xaml`).
- Rundungen konzentrisch, keine ovalen Knöpfe (Chips: `Btn.Chip`, Radius 10).
- Texte zweisprachig: Tabelle in `Core/Loc.cs` oder inline `{core:L De='…', En='…'}`.
- Jede Funktion als Dienst in `Services/`, abschaltbar über `AppSettings`.
