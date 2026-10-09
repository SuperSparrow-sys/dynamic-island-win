# Anbindungen

## Spotify

Ohne Anmeldung zeigt DynamicBay Titel, Cover und Steuerung für Spotify (Desktop und Web-App), Apple Music und jeden
anderen Player. Die optionale Anmeldung ergänzt Lieblingssongs, Gerätewahl, Shuffle und Wiederholen:

1. <https://developer.spotify.com/dashboard> → *Create app* (beliebiger Name).
2. Redirect URI: `http://127.0.0.1:43821/callback`, API: *Web API*.
3. Client ID in Einstellungen → Medien und Spotify eintragen → *Verbinden*.

Seit Februar 2026 verlangt Spotify für solche Apps ein Premium-Konto des Erstellers (bis zu 5 Nutzer pro App).
Die Anmeldung bleibt gespeichert (Refresh-Token, DPAPI-verschlüsselt), bis du sie trennst.

## Apple Music

Anzeige und Steuerung funktionieren ohne Anmeldung über die Windows-Medienschnittstelle. Eine Anmeldung (Lieblingssongs,
Mediathek) würde ein kostenpflichtiges Apple-Entwicklerkonto mit geheimem Schlüssel voraussetzen und ist deshalb nicht
eingebaut.

## Kalender

Einstellungen → Timer und Kalender → *Kalender-Konten*. Mehrere Konten gleichzeitig möglich.

- **iCloud / GMX / web.de (CalDAV):** Server-Feld (iCloud vorgegeben, GMX `https://caldav.gmx.net`, web.de `https://caldav.web.de`). iCloud: Apple-ID + **app-spezifisches Passwort** (appleid.apple.com → Anmeldung und Sicherheit →
  App-spezifische Passwörter). Zugriff über CalDAV (`caldav.icloud.com`), alle Kalender werden gefunden.
- **Google:** Einmalig in der [Google Cloud Console](https://console.cloud.google.com) ein Projekt anlegen,
  *Google Calendar API* aktivieren, OAuth-Zustimmungsbildschirm einrichten (extern, dich selbst als Testnutzer),
  unter *Anmeldedaten* eine OAuth-Client-ID vom Typ *Desktop-App* erstellen. Client-ID und Clientschlüssel eintragen
  → *Mit Google anmelden*. Nur Leserechte.
- **ICS-Link:** Outlook (Kalender veröffentlichen → ICS-Link), Google ohne Anmeldung (Privatadresse im iCal-Format)
  oder öffentliche iCloud-Kalender.

## Mitteilungen

Windows gibt Mitteilungen fremder Apps nur an Programme mit *Paket-Identität* heraus. Der Installer richtet das ein
(Zertifikat in *Vertrauenswürdige Personen*, Registrierung beim ersten Start). Danach erscheinen WhatsApp, Outlook,
Teams usw. als Peek, im Mitteilungs-Tab und im Nachrichten-Widget. Antworten aus der Insel ist technisch nicht möglich.

**Nur in der Insel:** Solange DynamicBay läuft, zeigt Windows unten rechts keine Banner und spielt keinen
Mitteilungston (Einstellungen → Mitteilungen → *Windows-Mitteilungen*). Alles landet weiter in der Mitteilungszentrale,
von dort liest die Insel. Einzelne Apps lassen sich als Ausnahme eintragen. Ohne Zugriff auf die Mitteilungen bleibt
Windows unverändert. Die ursprünglichen Einstellungen werden beim Beenden wiederhergestellt.

**Nicht stören:** Die Insel bleibt klein und zeigt nur Musik und einen laufenden Timer. Mitteilungen sammeln sich
still im Mitteilungs-Tab, eingeblendet wird nur das Ende eines Timers.

## Cloud-Speicher

Ablage und Zwischenablage können Einträge direkt in **OneDrive** (alle angemeldeten Konten), **iCloud Drive**,
**Google Drive** und **Dropbox** speichern – in den Unterordner `DynamicBay`. Voraussetzung ist der jeweilige
Sync-Client. Screenshot-Ordner aller OneDrive-Konten werden automatisch überwacht.

## Claude

- **Sitzungen:** Die letzten Claude-Code-Sitzungen aus `%UserProfile%\.claude\projects` mit Titel, Projekt und Zeit.
  Klick setzt sie im Terminal fort (`claude --resume`).
- **Remote Control:** Sitzungen mit Remote Control zeigen einen Globus – öffnet `claude.ai/code/session_…` im Browser
  oder in der Claude-App, auf jedem Gerät.
- **Live-Status:** Einstellungen → Widgets und Module → Claude → *Einrichten* ergänzt Claude-Code-Hooks
  (`UserPromptSubmit`, `Notification`, `Stop`), die an `127.0.0.1:43822` melden. Die Insel zeigt dann
  „Claude arbeitet“, „Claude ist fertig“ und „Claude braucht dich“.
- **Alle PCs:** Mit einem gewählten Cloud-Ordner legt jeder PC eine kleine Übersicht seiner Sitzungen ab
  (Titel, Projekt, Status, Remote-Link – keine Zugangsdaten); die Insel zeigt dann die Sitzungen aller PCs.
- Chats aus Claude Desktop/claude.ai werden nicht gelesen: Dafür gibt es keine öffentliche Schnittstelle.
- Das ganze Modul ist mit einem Schalter abschaltbar.
