# Anbindungen

## Spotify

Ohne Anmeldung zeigt DynamicBay Titel, Cover und Steuerung für Spotify (Desktop und Web-App), Apple Music und jeden
anderen Player. Die optionale Anmeldung ergänzt Lieblingssongs, Gerätewahl, Shuffle und Wiederholen:

1. <https://developer.spotify.com/dashboard> → *Create app* (beliebiger Name).
2. Redirect URI: `http://127.0.0.1:43821/callback`, API: *Web API*.
3. Client ID in Einstellungen → Medien und Spotify eintragen → *Verbinden*.

Für Warteschlange und Playlists braucht die Anmeldung die Berechtigung für Playlists: Wer vor Version 1.0.15 verbunden
hat, trennt und verbindet Spotify einmal neu.

Seit Februar 2026 verlangt Spotify für solche Apps ein Premium-Konto des Erstellers (bis zu 5 Nutzer pro App).
Die Anmeldung bleibt gespeichert (Refresh-Token, DPAPI-verschlüsselt), bis du sie trennst.

Der Lautstärkeregler unter dem Cover ändert nur die Musik. Mit Anmeldung stellt er Spotifys eigene Lautstärke
(auch wenn Spotify auf dem Handy oder einem Lautsprecher spielt), ohne Anmeldung die Lautstärke der Spotify-App im
Windows-Lautstärkemixer.

## Apple Music

Anzeige und Steuerung funktionieren ohne Anmeldung über die Windows-Medienschnittstelle. Eine Anmeldung (Lieblingssongs,
Mediathek) würde ein kostenpflichtiges Apple-Entwicklerkonto mit geheimem Schlüssel voraussetzen und ist deshalb nicht
eingebaut. Der Lautstärkeregler unter dem Cover stellt die Lautstärke von Apple Music im Windows-Lautstärkemixer,
der Rest des PCs bleibt gleich laut.

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

## Ablage und Taildrop

Jede Datei in der Ablage und jedes Bild in der Zwischenablage hat beim Überfahren einen Ordner-Knopf: Er öffnet den
Explorer dort, wo die Datei liegt, und markiert sie.

**Taildrop:** Dateien, die ein anderes Gerät mit Tailscale (iPhone, iPad, Android, Mac, Linux, Windows) an diesen PC
schickt, erscheinen sofort als Einblendung und in der Ablage. Ein Klick öffnet die Ablage, die Datei lässt sich auch
direkt aus der Einblendung herausziehen. Tailscale legt sie unter Windows immer in den Downloads ab; unter
Einstellungen → Dateiablage → *Speichern in* verschiebt DynamicBay sie in einen eigenen Ordner. Dafür muss „Send Files“
in der Tailscale-Verwaltung eingeschaltet sein.

**Senden:** Der Pfeil-Knopf auf einer Datei in der Ablage schickt sie per Taildrop an ein anderes Gerät (Liste aus
`tailscale file cp --targets`). Auf iPhone und iPad landet sie in der Dateien-App im Ordner Tailscale. Text und Links
kann Taildrop nicht übertragen.

## Audio und Bluetooth

Das Widget „Audio“ wechselt Ausgabe und Mikrofon (Windows-Standardgerät für alles) und verbindet gekoppelte
Bluetooth-Kopfhörer und -Lautsprecher mit einem Klick (wie „Verbinden“ in den Windows-Soundeinstellungen; klappt auch
mit Intels Bluetooth-Audio-Offload). Einstellungen → Akku und Bluetooth → *Kopfhörer und Mikrofon*:

- Kopfhörer werden beim Verbinden sofort die Ausgabe.
- Wird das Mikrofon von Bluetooth-Kopfhörern benutzt, schalten sie in den Telefonmodus (Mono, dumpfer Ton). Die Insel
  nimmt dann das Mikrofon des PCs; für einen Call holt die Einblendung das Headset-Mikrofon mit einem Klick zurück.
  Teams nutzt das, wenn dort als Gerät „Standard“ eingestellt ist.
- Werden die Kopfhörer getrennt, pausiert die Musik.

Screenshot-Ordner aller OneDrive-Konten werden automatisch überwacht.

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

## Microsoft 365 (Outlook, Teams, To Do)

Eine Anmeldung für den Outlook-Kalender (mit Teams-Links für den Beitreten-Knopf), die Aufgaben aus Microsoft To Do,
den Teams-Status und die häufigen Kontakte. Geht mit Firmen- und Schulkonten und mit privaten Microsoft-Konten.

Microsoft verlangt dafür eine eigene App-Kennung (wie bei Spotify). Einmalig, etwa fünf Minuten:

1. [entra.microsoft.com](https://entra.microsoft.com) öffnen und mit dem Konto anmelden, das du in DynamicBay nutzen
   willst (bevorzugt das Firmenkonto). Links: **App-Registrierungen** → **Neue Registrierung**.
2. **Name:** `DynamicBay`.
3. **Unterstützte Kontotypen:** „Konten in einem beliebigen Organisationsverzeichnis … und persönliche
   Microsoft-Konten“. Dann funktionieren Firmen- und private Konten mit derselben Kennung.
4. **Umleitungs-URI:** Plattform **Öffentlicher Client/nativ (mobil und Desktop)**, Adresse `http://localhost`.
5. **Registrieren** klicken und die **Anwendungs-ID (Client-ID)** kopieren.
6. In DynamicBay: Einstellungen → Timer und Kalender → Microsoft 365 → Anwendungs-ID einfügen →
   **Mit Microsoft anmelden**. Im Browser das Konto wählen und zustimmen.

Danach erscheint der Outlook-Kalender in der Kalender-Karte. Die Widgets **Aufgaben** und **Teams-Status** fügst du
unter Widgets und Module hinzu. Den Teams-Status musst du einmal extra freischalten (Knopf „Freischalten“), weil
Microsoft dafür eine eigene Erlaubnis abfragt.

**Firmenkonto:**
- Manche Firmen erlauben es nicht, selbst Apps zu registrieren, oder lassen fremde Apps nur nach Freigabe auf
  Kalender und Aufgaben zugreifen. Dann zeigt Microsoft bei der Anmeldung „Genehmigung erforderlich“. Die IT kann die
  App freigeben oder selbst registrieren und dir die Anwendungs-ID geben.
- Ist die App nur für deine Firma registriert („Nur Konten in diesem Organisationsverzeichnis“), trag unter
  „Verzeichnis“ die Verzeichnis-ID (Mandanten-ID) statt `common` ein.

**Berechtigungen** (Microsoft Graph, delegiert): `User.Read`, `Calendars.Read`, `Tasks.ReadWrite`, `People.Read`,
`Contacts.Read`, `offline_access`, für den Teams-Status zusätzlich `Presence.ReadWrite`. DynamicBay liest Termine,
Aufgaben und Kontakte nur, schreibt nur das Abhaken von Aufgaben und den eigenen Status. Die Anmeldung wird
verschlüsselt auf diesem PC gespeichert; „Trennen“ löscht sie.
