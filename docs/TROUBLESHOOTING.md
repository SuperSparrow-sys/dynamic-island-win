# Fehlersuche

Erste Anlaufstelle ist immer das **Protokoll**: `%AppData%\DynamicBay\logs\dynamicbay.log`
(Einstellungen → Über → *Ordner öffnen*). Jeder Start schreibt Version und Identität, jede Platzierung Position,
Skalierung und Arbeitsbereich, Fehler mit Stacktrace.

## Die Insel ist nicht zu sehen

1. Tray-Symbol prüfen: Linksklick blendet ein/aus, Rechtsklick → *Insel einblenden*. Oder Tastenkürzel (Standard `Strg+Alt+I`).
2. Im Log nach `Island suppressed (foreground: …)` suchen: dann hält DynamicBay das aktive Fenster für Vollbild oder es
   steht auf der Ausnahmeliste. Einstellungen → Verhalten → *Im Vollbild ausblenden* testweise ausschalten.
3. Position zurücksetzen: Tray → *Position zurücksetzen*. Bei nicht mehr angeschlossenem Bildschirm wandert die Insel
   automatisch auf den Hauptbildschirm.
4. Ebene *Nur Desktop* gewählt? Dann liegt die Insel hinter allen Fenstern (Win + D zeigt sie).

## Taskleiste verschwindet / Bildschirm-Probleme

Das Inselfenster wird immer auf seinen Bildschirm begrenzt. Zur Diagnose:
```powershell
powershell -File tools\window-probe.ps1            # alle DynamicBay-Fenster + Taskleisten mit Position
powershell -File tools\taskbar-watch.ps1 -Seconds 120 -Out taskbar.log   # protokolliert jede Taskleisten-Änderung
```

## Musik wird nicht angezeigt

- DynamicBay nutzt die Windows-Medienschnittstelle. Erscheint der Titel im Lautstärke-Flyout von Windows, sieht ihn auch DynamicBay.
- Log-Zeile `Media session: <AppId>` zeigt, welche App erkannt wurde.
- Spotify-Login-Probleme: Redirect URI muss exakt `http://127.0.0.1:43821/callback` sein, die App im Spotify-Dashboard
  braucht *Web API*. Seit 2026 verlangt Spotify ein Premium-Konto des App-Erstellers.

## Screenshots erscheinen nicht

- Win + Umschalt + S kopiert in die Zwischenablage → muss unter Einstellungen → Zwischenablage aktiviert sein.
- Überwachte Screenshot-Ordner stehen im Log (`Watching screenshots in …`) und im Tooltip der Einstellung.
- Passwort-Manager-Inhalte werden absichtlich ignoriert.

## Mitteilungen anderer Apps fehlen

- Im Mitteilungs-Tab steht *nicht verfügbar*: Die App läuft ohne Paket-Identität. Über den Installer installieren
  (Aufgabe *Mitteilungen anderer Apps …* angehakt lassen). Log: `starting (identity: True)` muss erscheinen.
- *Zugriff verweigert*: Windows-Einstellungen → Datenschutz → Benachrichtigungen → Zugriff für DynamicBay erlauben.
- Prüfen ob das Paket registriert ist: `Get-AppxPackage DynamicBay`.

## Windows-Banner erscheinen trotzdem / bleiben weg

DynamicBay schaltet Banner nur, solange es läuft. Nach einem Absturz stellt `DynamicBay.exe --restore-banners` alles
wieder her (macht auch die Deinstallation). Gesichert wird in `%AppData%\DynamicBay\banner-backup.json`.

## Kalender

Der Status jedes Kontos steht in Einstellungen → Timer und Kalender (z. B. `OK · 4 Termine in 48 h` oder die
Fehlermeldung). iCloud braucht ein **app-spezifisches** Passwort, nicht das Apple-ID-Passwort.

## Claude

- Keine Sitzungen: Es werden nur lokale Claude-Code-Sitzungen aus `%UserProfile%\.claude\projects` gelesen.
- Live-Status fehlt: Einstellungen → Widgets und Module → Claude → *Einrichten*. Danach muss in
  `%UserProfile%\.claude\settings.json` ein Hook mit `127.0.0.1:43822/claude` stehen. Test:
  `curl -X POST http://127.0.0.1:43822/claude/ -d "{\"session_id\":\"x\",\"hook_event_name\":\"Notification\"}"`
- Eine Sicherung der vorherigen Datei liegt als `settings.json.dynamicbay-backup` daneben.

## Design prüfen / Fehler melden

`tools\snapshot.ps1` rendert alle Zustände als PNG (siehe [DEVELOPMENT.md](DEVELOPMENT.md)). Beim Melden eines
Fehlers bitte Log-Datei, Windows-Version und ggf. Screenshot anhängen:
<https://github.com/SuperSparrow-sys/dynamic-island-win/issues>
