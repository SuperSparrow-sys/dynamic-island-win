# Anbindungen

## Spotify

Ohne Anmeldung zeigt DynamicBay Titel, Cover und Steuerung für Spotify (Desktop und Web-App), Apple Music und jeden
anderen Player. Die optionale Anmeldung ergänzt Lieblingssongs, Gerätewahl, Shuffle und Wiederholen:

1. <https://developer.spotify.com/dashboard> → *Create app* (beliebiger Name).
2. Redirect URI: `http://127.0.0.1:43821/callback`, API: *Web API*.
3. Einstellungen → Konten → Spotify → *Verbinden* öffnet die Einrichtung. Client ID eintragen → *Verbinden*.

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

Einstellungen → Konten → *Kalender*. Mehrere Konten gleichzeitig möglich.

- **iCloud / GMX / web.de (CalDAV):** Anbieter wählen: iCloud, GMX, web.de oder *Andere* (eigene https-Adresse). iCloud: Apple-ID + **app-spezifisches Passwort** (appleid.apple.com → Anmeldung und Sicherheit →
  App-spezifische Passwörter). Zugriff über CalDAV (`caldav.icloud.com`), alle Kalender werden gefunden.
- **Google:** Einmalig in der [Google Cloud Console](https://console.cloud.google.com) ein Projekt anlegen,
  *Google Calendar API* aktivieren, OAuth-Zustimmungsbildschirm einrichten (extern, dich selbst als Testnutzer),
  unter *Anmeldedaten* eine OAuth-Client-ID vom Typ *Desktop-App* erstellen. Client-ID und Clientschlüssel eintragen
  → *Mit Google anmelden*. Nur Leserechte.
- **ICS-Link:** Outlook (Kalender veröffentlichen → ICS-Link), Google ohne Anmeldung (Privatadresse im iCal-Format)
  oder öffentliche iCloud-Kalender.

## Mitteilungen

Windows gibt Mitteilungen fremder Apps nur an Programme mit *Paket-Identität* heraus. Der Installer richtet das ein
(Zertifikat in *Vertrauenswürdige Personen*, Registrierung des Identitäts-Pakets, auch bei jedem Update). Danach erscheinen WhatsApp, Outlook,
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

## CursorHop

CursorHop teilt eine Maus und eine Tastatur zwischen mehreren PCs. Es hat keine öffentliche
Schnittstelle; seinen internen gRPC-Dienst (`127.0.0.1:50051`, eigenes Token) nutzt DynamicBay bewusst nicht.
`CursorHopService` liest nur das Protokoll `%LocalAppData%\CursorHop\logs\core.log.<Datum>` mit (eine Datei pro Tag,
Datum in UTC, geöffnet mit Lese-, Schreib- und Lösch-Freigabe, damit CursorHop nie gestört wird).

- **Karte „CursorHop“** (System-Seite): verbundenes Gerät, ob CursorHop läuft, wo die Maus gerade ist („Maus ist hier“
  oder „Maus am PC-Büro“), Knopf zum Öffnen oder Starten. Wird einmal hinzugefügt, wenn CursorHop installiert ist.
- **Einblendungen:** „PC-Büro verbunden“ und „PC-Büro getrennt“. CursorHop verbindet sich nach dem Aufwachen oft
  mehrmals neu; gemeldet wird eine Verbindung erst, wenn sie 3 s hält, eine Trennung erst nach 40 s.
  „Zwischenablage von PC-Büro“, und der Eintrag in der Zwischenablage zeigt das Gerät als Quelle.
- **Mitstarten:** Läuft CursorHop nicht, startet DynamicBay es (wie der Windows-Autostart, `CursorHop.exe`): beim
  eigenen Start, nach dem Aufwachen und wenn das Netzwerk zurückkommt. Nie eine zweite Instanz.
- Einstellungen: `CursorHopEnabled`, `CursorHopAutoStart`, `CursorHopPeeks` (alle an; ohne CursorHop passiert nichts).

Gelesene Zeilen (CursorHop 0.12, als Fixtures in `tests/DynamicBay.Tests/Fixtures/CursorHop`):

| Zeile | Bedeutung |
|---|---|
| `[PEER] PeerHello processed — connection ready peer=… name=… synthetic_id=… peer_is_primary_over_us=…` | Gerät verbunden |
| `cleared peer keyboard presence after disconnect … peer=…` | Gerät getrennt (Geräte-ID oder Verbindungs-ID) |
| `room beacon started on UDP port …` | kein Gerät mehr verbunden (auch beim Start) |
| `CursorHop core starting` | Neustart, alle Verbindungen weg |
| `[ROLE] peer claimed Primary role` / `peer released Primary role` | Zweit-PC: die Maus des Primary kommt herüber / geht zurück |
| `[CLIP_RECV] received … clipboard frame … format=… peer=…` | Zwischenablage empfangen |

Noch nicht in einem echten Protokoll gesehen (dieser PC ist Zweit-PC), aus den Meldungstexten in `cursorhop.exe`,
Felder geraten: `[SWITCH] now controlling remote device` / `returned to local control` (Primary: Maus geht hinüber /
kommt zurück), `[CLIP_TRANSFER] files delivered to paste directory|fallback folder` (Dateien empfangen; Zielordner
unbekannt, die Einblendung öffnet ihn nur, wenn die Zeile einen Ordner nennt). Unbekannte Zeilen werden ignoriert.

## Geräte koppeln

Zwei PCs mit DynamicBay (zum Beispiel PC und Laptop) lassen sich koppeln, im selben Netz oder über Tailscale. Wer
nur einen PC hat, merkt davon nichts: Ohne den Schalter ist der PC für andere unsichtbar und teilt nichts.

**Koppeln** (Einstellungen → Geräte):

1. Auf beiden Geräten „Geräte koppeln“ einschalten. Unter „Gerätename“ steht, wie das andere Gerät diesen PC sieht
   (leer: der Rechnername).
2. Auf dem einen Gerät „Koppeln erlauben“ drücken. Das Fenster bleibt zwei Minuten offen, die Restzeit steht daneben.
3. Auf dem anderen Gerät unter „Gefundene Geräte“ beim ersten auf „Koppeln“ drücken. Es findet sich im selben Netz
   und über Tailscale (dort nur deine eigenen Windows- und Mac-Geräte: Laptops, NAS und Handys anderer Leute im
   geteilten Tailnet werden nie gefragt); sonst unter „Adresse“ die IP, den Rechnernamen oder den Tailscale-Namen eintippen (optional mit
   `:Port`).
4. Beide Geräte zeigen denselben sechsstelligen Code: das gefragte Gerät in der Insel (mit „Bestätigen“ und
   „Ablehnen“), ein Gerät mit offenen Einstellungen zusätzlich groß in einem kleinen Fenster. Stimmt er, auf beiden
   bestätigen. Gespeichert wird erst, wenn beide bestätigt haben; das Ergebnis (gekoppelt, abgelehnt, Zeit abgelaufen)
   steht kurz in der Insel und unter „Gerät hinzufügen“.

Danach verbinden sich die beiden von selbst, auch nach dem Aufwachen, bei einem Netzwechsel und wenn Tailscale angeht.
Die Verbindung ist verschlüsselt, und nur gekoppelte Geräte kommen durch. „Gekoppelte Geräte“ zeigt jedes mit Punkt
(grün verbunden, grau nicht verbunden, rot abgelehnt), Weg (LAN oder Tailscale) und Akku. „Entkoppeln“ fragt direkt in
der Zeile noch einmal nach. Entkoppelt ein Gerät das andere, zeigt das andere die Kopplung als abgelehnt und muss neu
gekoppelt werden. Ein anderes Gerät, das inzwischen unter der alten Adresse erreichbar ist, ändert daran nichts.

**Teilen mit gekoppelten Geräten** (jeder Schalter gilt auf beiden Seiten: Was hier aus ist, wird weder gesendet noch
angenommen):

| Schalter | Was passiert |
|---|---|
| Dateien | Auf einer Datei in der Ablage schickt der Laptop-Knopf sie an das verbundene Gerät (bei mehreren: kleines Menü). Beim Herausziehen einer Datei erscheinen die verbundenen Geräte unter der Ablage, Loslassen schickt sie hin; Dateien aus dem Explorer lassen sich auch auf die Karte „Gekoppelt“ ziehen. Empfangen wird ohne Nachfrage nach `Downloads\DynamicBay`, die Datei kommt in die Ablage und als Einblendung „Datei von …“; große Dateien zeigen ihren Fortschritt. Nichts wird automatisch geöffnet. |
| Zwischenablage | Kopierte Texte und Bilder erscheinen im Verlauf des anderen Geräts mit dessen Namen. Die Windows-Zwischenablage dort ändert sich nicht (das macht CursorHop); ein Klick auf den Eintrag kopiert ihn. Inhalte aus Passwort-Managern, Einträge vom anderen Gerät und was CursorHop gebracht hat, werden nie weitergeschickt. Bilder bis 5 MB. |
| Mitteilungen | Die Windows-Mitteilungen, die die Insel hier zeigt, erscheinen auch auf dem anderen Gerät, markiert „von …“. Dort gelten „Nicht stören“ und stummgeschaltete Apps wie für eigene. Eine Mitteilung vom anderen Gerät wird nie weitergeleitet; dieselbe Nachricht von beiden Geräten erscheint einmal. |
| Musik | Läuft hier nichts, zeigt die Medienkarte die Wiedergabe des anderen Geräts mit Cover; Zurück, Pause und Weiter steuern es. Läuft auf beiden etwas, wechselt der Laptop-Knopf neben dem Titel. Musik startet nie von selbst: Ohne Player auf dem anderen Gerät passiert nichts. |
| Timer | Timer und Pomodoro laufen auf beiden Geräten: Starten, Pause, Verlängern und Stoppen auf einem gilt auf dem anderen. Der Laptop-Knopf am laufenden Timer übergibt ihn ans andere Gerät oder holt ihn her. Das Ende erscheint einmal: auf dem Hauptgerät, ohne Hauptgerät dort, wo der Timer hingehört (gestartet oder übergeben). Fokus-Sitzungen zählen nur dort. |
| Ruhe und Fokus | „Nicht stören“ gilt auf beiden Geräten, egal wo es geschaltet wird (die letzte Änderung gewinnt, auch nach einer Trennung). Ein Anruf oder eine Fokus-Sitzung auf einem Gerät macht das andere still, solange sie dauert. |
| Fragen | Einblendungen mit Knöpfen (Mikrofon aktiv – stumm?, Beitreten, eingehender Anruf, Kopfhörer verbinden …) des stillen Geräts erscheinen auf dem Hauptgerät, mit dessen Namen. Der Knopf dort wirkt auf dem Gerät, von dem die Frage kommt. Läuft die Zeit ab, gilt sie als abgelehnt; ist das Hauptgerät vorher weg, erscheint die Frage auf dem eigenen Gerät. |
| Akku | Akku schwach, Ladegerät ein oder aus und voll erscheinen auf dem Hauptgerät als „Laptop: Akku 15 %“. Das stille Gerät zeigt sie dann nicht noch einmal selbst. |
| Gemeinsam sperren | Aus, bis man es einschaltet (auf beiden Geräten). Wird ein Gerät gesperrt (Win+L), sperrt sich das andere mit, aber nur, wenn dort seit einer Minute niemand Maus oder Tastatur benutzt hat. Entsperrt wird nie über die Kopplung. |
| Status | Name, Akku und Version für die Karte „Gekoppelt“ (System-Seite, kommt beim ersten Koppeln dazu). Verbunden und getrennt kommen als Einblendung (verbunden nach 3 s, getrennt nach 30 s; hat CursorHop dasselbe Gerät gerade gemeldet, nur einmal). |

**Hauptgerät:** „Alles zeigt“ legt fest, welches Gerät die Einblendungen zeigt, solange beide verbunden sind: „Beide“
(wie ohne Wahl), „Dieses Gerät“, das andere Gerät oder „Automatisch“ (wo die Maus ist, laut CursorHop; ohne CursorHop
das zuletzt benutzte Gerät). Das andere Gerät bleibt still und schickt seine Einblendungen (Termine, Timer, Akku …)
und Mitteilungen ans Hauptgerät, das jede Nachricht nur einmal zeigt. Die Wahl gilt auf beiden Geräten, die letzte
Änderung gewinnt; gespeichert wird sie als „dieses“ oder „anderes“ Gerät, ein Einstellungs-Export bleibt also richtig.
„Insel auf dem anderen Gerät ausblenden“ blendet dort zusätzlich die ganze Insel aus. Ist die Verbindung weg, zeigt
jedes Gerät wieder alles selbst. Fragen mit Knöpfen gehen mit dem Schalter „Fragen“ ebenfalls ans Hauptgerät, ohne
ihn bleiben sie auf ihrem Gerät.

Der Installer gibt in der Windows-Firewall die Ports 43823 (TCP) und 43824 (UDP) frei: in privaten Netzen und im
Domänennetz, dazu auf dem Tailscale-Adapter (in jedem Netzprofil, nur für Tailscale-Adressen). Ohne Tailscale beim
Installieren fehlt nur die zweite Regel.

## Audio und Bluetooth

Das Widget „Audio“ wechselt Ausgabe und Mikrofon (Windows-Standardgerät für alles) und verbindet gekoppelte
Bluetooth-Kopfhörer und -Lautsprecher mit einem Klick (wie „Verbinden“ in den Windows-Soundeinstellungen; klappt auch
mit Intels Bluetooth-Audio-Offload). Einstellungen → Audio und Geräte → *Kopfhörer und Mikrofon*:

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
- **Live-Status:** Einstellungen → Apps und Skripte → Claude → *Einrichten* ergänzt Claude-Code-Hooks
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
6. In DynamicBay: Einstellungen → Konten → Microsoft 365 → *Verbinden* → Anwendungs-ID einfügen →
   **Mit Microsoft anmelden**. Im Browser das Konto wählen und zustimmen.

Danach erscheint der Outlook-Kalender in der Kalender-Karte. Die Widgets **Aufgaben** und **Teams-Status** fügst du
unter Insel-Inhalte hinzu. Den Teams-Status musst du einmal extra freischalten (Knopf „Freischalten“), weil
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
