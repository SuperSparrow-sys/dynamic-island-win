# Entscheidungen

Getroffene Entscheidungen mit der verworfenen Alternative und dem Grund. Neue Entscheidungen kommen oben in ihren
Abschnitt; was nicht mehr gilt, wird entfernt. Offene Aufgaben stehen in [offene_punkte.md](offene_punkte.md).

## Darstellung und Bewegung

- **Claude-Zeichen statt Funken, es atmet statt sich zu drehen** (17.10.2026, Besitzer). Das Zeichen kommt aus Simple
  Icons und kennzeichnet nur die Claude-Anbindung. Verworfen: das drehende Lucide-Symbol. Die Kosten bleiben gleich,
  weil jede Dauer-Animation mit 60 Bildern/s etwa gleich viel kostet (siehe unten).
- **Sichtbare Animationen behalten ihre Bildrate** (zweimal vom Besitzer bestätigt). Verworfen: 30 Bilder/s für die
  Waveform (ruckelte) und eine Umdrehung mit Pause für das Claude-Symbol. Gespart wird nur, indem verborgene
  Animationen anhalten (`ActivityState`, `c:Loop`).
- **Software-Rendering bleibt** (15.10.2026). Verworfen: Grafikkarte (doppelte CPU, 50–70 MB mehr Speicher) und
  `Windows.UI.Composition` (16.10.2026 gemessen: verlagert die Arbeit nur in den DWM, im System 11,1 gegen 12,1
  Punkte, innerhalb der Streuung). Ebenso verworfen: ein kleineres Fenster (kostet nichts extra) und die Waveform über
  den Render-Thread (keine Ersparnis).

## Funktionen

- **Automationen, das iPhone meldet nur den Ort** (24.10.2026, Besitzer). „Zu Hause“ kommt aus zwei WLAN-Automationen
  (`Zuhause|an`/`aus`), nicht aus dem iPhone-Fokus: Ein Wechsel zu „Nicht stören“ am iPhone beendete Zuhause, das Panel
  ging aus, obwohl er da war, und nach 16 h ohne Wechsel blieb es beim Gehen an. Alles Weitere sind Automationen in
  DynamicBay (Auslöser, was sie schalten, die obere gewinnt). Weg gewinnt am Panel immer, nur das Widget von Hand
  nicht. Bei laufender Kamera 10 s Ausblenden; ausgelöst nur von der Kamera, nicht von Terminen, Besprechungen
  oder Anrufen (Besitzer: „nur mit der Kamera“). Der Server ist allgemein: Er führt die Regeltabelle aus, die DynamicBay
  schickt, und reicht `regel`-Befehle durch, damit für neue Automationen nur DynamicBay geändert wird. Verworfen:
  Anwesenheit und Vorrang im Server fest eingebaut (jede neue Automation hätte einen Server-Umbau gebraucht),
  „die neuere gewinnt“ und „Liste entscheidet“ für das Panel, Fokus-Profile getrennt von Automationen.
- **Fokus aus drei Quellen, die neuere Wahl gewinnt** (24.10.2026, Besitzer). Ein Fokus kommt vom iPhone, aus dem
  Fokus-Menü der Insel oder aus einem Szenario (Auslöser: Termin, Teams-Besprechung oder Anruf, Fokus-Sitzung oder
  Pomodoro) und wirkt jedes Mal gleich (Profil, Panel, iPhone holt ihn ab). Wer zuletzt schaltet, gewinnt. Verworfen:
  „von Hand vor Szenario“, Uhrzeit-Szenarien und Bildschirm teilen oder Vollbild als Auslöser.
- **DynamicBay-Server als optionale Verwaltung** (24.10.2026, Besitzer). Ein eigenes Rubus-Projekt
  `dynamicbay-server` (Python, FastAPI) auf dem Homeserver, nur im Tailnet über `tailscale serve`. Es hält gemeinsame
  Einstellungen (Werte undurchsichtig je Schlüssel, die neuere gewinnt, jedes Gerät darf überschreiben), Fokus,
  Nicht stören und den Panel-Zustand, nimmt den iPhone-Fokus an und schickt Anweisungen an die Anzeigetafel. Ton,
  Dateien, Zwischenablage und Maus bleiben auf der direkten Kopplung. Ohne Server läuft alles wie vorher. Verworfen:
  ASP.NET Core (Rubus liefert nur Python aus), der Windows-PC als Server (kein gültiges HTTPS, nicht immer an),
  Rubus-Anfragen für prod und Feldgeräte in der Insel bestätigen (bleibt „In Rubus öffnen“ mit Passwort).
  Gemeinsam sind Fokus-Profile, Nicht stören, Mitteilungsregeln, stumme Apps, Anschluss der Zentrale; je Gerät
  bleiben Position, Bildschirm, Darstellung, Bildrate, Audio, Bluetooth, Autostart. Auftrag und Protokoll:
  `C:\Users\jonat\Documents\DynamicBay-Auftraege\dynamicbay-server`.
- **Anzeigetafel: DynamicBay weist an, die Zentrale führt aus** (24.10.2026, Besitzer). Das Fokus-Profil sagt, was das
  WAGO-Panel tut (nichts, dimmen, aus); die Zentrale (`dashboard-zentrale`) merkt es sich als Vorrang. Das Widget gilt
  bis zum nächsten Planpunkt, ein Fokus bis zu seinem Ende. Verworfen: Fokus-Verhalten in der Zentrale einstellen,
  Monitor-Helligkeit über DDC/CI (war ein Missverständnis, zurückgebaut).
- **iPhone-Fokus über eine Kurzbefehle-Automation, Profile frei einstellbar** (24.10.2026, Besitzer). Arbeit,
  Schlafen, Schule und Zuhause schalten je Verschiedenes; vier Profile als Vorschlag, jedes änderbar, unbekannte
  Namen legen ein leeres an. Der Laptop übernimmt über die Kopplung, LAN und Tailscale mit Schlüssel. Verworfen:
  Smartphone-Link und Bluetooth (übertragen keinen Fokus), fest verdrahtete Verhalten, `HttpListener` (braucht für
  andere Adressen als localhost eine URL-Reservierung mit Adminrechten), HTTPS (Kurzbefehle vertraut keinem selbst
  signierten Zertifikat). Der aktive Fokus wird nicht gespeichert, damit nach einem Neustart kein altes „Schlafen“
  die Insel versteckt. Ohne „Aus“ endet er nach 16 h. Teams folgt nur auf dem Gerät, das den Fokus direkt bekommt.

- **Mehrere Player: Stapel statt Wettstreit** (19.10.2026, Besitzer). Eine Seite pro Player, Scrollen über der Karte
  wechselt, eine Seite pro Geste. Die Wahl hält, bis der Player verschwindet oder ein anderer nach mindestens 3 s Pause
  zu spielen beginnt (`MediaPick`, `MediaStarts`). Verworfen: zwei Karten nebeneinander (zu breit für die Insel) und
  der alte Vorrang „spielendes Spotify zuerst“ allein, bei dem YouTube neben laufendem Spotify nie zu sehen war.
- **Handy-Mitteilungen öffnen die Windows-App** (19.10.2026, Besitzer). Gmail, Mail und Kalender gehen nach Outlook,
  sonst gleicher Name im Startmenü, Programm vor Web-App (`PhoneLinkApps.WindowsApp`). Gilt immer, ohne Schalter.
  Die einzelne Nachricht lässt sich nicht öffnen, Smartphone-Link liefert keine ID. Klicks auf Windows-Banner von
  Smartphone-Link öffnen weiter Smartphone-Link; das Klickziel fremder Toasts lässt sich nicht ändern.
- **Ton auf mehreren Geräten: ein Regler für alle, einer je Gerät** (19.10.2026, Besitzer). Zeilen bekommen die
  Standard-Ausgabe und jede Ausgabe, die gerade spielt oder in den letzten 10 s gespielt hat, dazu das gekoppelte Gerät
  bei „Ton an Gerät“. „Alle“ steht auf dem lautesten und bewegt alle im selben Verhältnis (`VolumeMix`). Beim
  gekoppelten Gerät ändert der Regler nur den weitergeleiteten Ton, nicht dessen Systemlautstärke: Wer dort sitzt,
  behält seine eigene Einstellung. Der Wert gilt in beide Richtungen (der Laptop zeigt den empfangenen Ton mit eigenem
  Regler, beide Seiten folgen einander) und wird je Gerät gespeichert. Windows „Geteiltes Audio“ (zwei Bluetooth-LE-Kopfhörer) ist für Apps ein einziger
  Endpunkt; er bekommt eine Zeile, die Aufteilung auf die Kopfhörer macht Windows. Verworfen: nur ein Regler mit
  gleichem Wert für alle (zerstört den Ausgleich zwischen leisem Lautsprecher und lauten Kopfhörern).

- **Anrufe bleiben wie in 1.1.4:** Teams- und Telefon-Fenster werden nie versteckt oder verschoben. Die Übernahme der
  Anrufe wurde in 1.1.7 zurückgenommen.
- **AirPods: nur der Akku.** Geräuschsteuerung ist endgültig verworfen, ebenso Wege über Treiber oder Testmodus.

## Einstellungen

Neuordnung vom 11.10.2026; die Gliederung selbst steht im Einstellungsfenster.

- **Jede Einstellung genau an einer Stelle,** Seiten mit Namen statt Nummern; ein Test prüft Seiten und Sprünge.
- **Eigene Seite „Konten“** für alle Verbindungen. Verworfen: Spotify bei Medien, Microsoft und Kalender bei
  „Kalender und Zeit“.
- **Eine Seite „Ablage“** für Zwischenablage und Dateien, wie der gemeinsame Tab in der Insel. Verworfen: zwei Seiten.
- **Eigene Seite „Ruhe und Fokus“.** Verworfen: ein Abschnitt unter „Verhalten“.
- **Kein Schalter „Insel ausblenden“.** Ausblenden geht über Tastenkürzel, Tray und den Knopf in der Insel.
- **Die Uhr steht unter „Darstellung“.** Verworfen: „Kalender und Zeit“.
- **Abschnittsüberschriften in der Seitenleiste** (INHALTE, VERBINDUNGEN).
- **Pfade als Knopf mit Symbol und Namen,** voller Pfad im Tooltip. Eingabefelder nur für eingefügte Werte (Client-IDs,
  ICS-Adresse, CalDAV-Server bei „Andere“).
- **Widget-Tafel ohne zweite Liste;** statt Ziehen geht auch ein Rechtsklick-Menü.

## Claude und Rubus

- **Rubus spricht über denselben Loopback-Listener wie die Hooks** (17.10.2026). Er läuft, wenn die Hooks eingerichtet
  sind oder „Sessions aus Rubus“ an ist; ein abgeschalteter Pfad antwortet 404. Verworfen: ein zweiter Port.
- **Fragen aus Rubus bleiben auf diesem Gerät** (`PeekKind.Local`), weil die Antwort an den Kanal dieses Rechners
  geht. Verworfen: Weiterleiten ans gekoppelte Hauptgerät.
- **Der Wartegrund steht als ein Wort in der Liste** („Freigabe“). „wartet auf Freigabe“ wurde abgeschnitten, der
  orange Punkt zeigt das Warten schon.
- **Der Listener nimmt nur POST ohne `Origin` an**, mit Größengrenze. Webseiten können ihn so nicht ansprechen.

## Koppeln und CursorHop

- **CursorHop wird nur über sein Protokoll mitgelesen.** Verworfen: der interne gRPC-Dienst auf `127.0.0.1:50051`
  (private Schnittstelle, ändert sich mit jeder Version, das Token gehört CursorHop).
- **Eigene Verbindung zwischen zwei DynamicBays:** TLS mit festgepinnten Zertifikaten, Kopplung per Zahlenvergleich mit
  Commitment. Verworfen: Dateien nur über CursorHop.
- **Tailscale-Suche nur bei eigenen Windows- und macOS-Geräten,** damit im geteilten Tailnet keine fremden Geräte
  gefragt werden.

## Sicherheit und Daten

- **Updates fragen immer,** der Besitzer installiert selbst. Verworfen: automatisches Installieren.
- **Das Identitäts-Paket registriert nur der Installer,** solange die App geschlossen ist. Verworfen: verzögerte
  Registrierung aus der laufenden App (brach den Start, siehe [postmortems](postmortems/2026-10-11-identity-package.md)).
- **CalDAV nur über https.**
- **Getrennte Bluetooth-Geräte werden weiter abgefragt,** weil getrennte Headsets den Pfad brauchen. Verworfen: das
  Abfragen nicht vorhandener Endpunkte abzuschalten.
- **Ein Einfachklick aufs Tray-Symbol wartet die Doppelklickzeit ab,** damit ein Doppelklick die Insel nicht versteckt.

## Leistung

- **Übrige Abfragen bleiben Abfragen** (16.10.2026, zusammen etwa 0,5 Punkte). Ereignisse bräuchten COM-Rückrufe, die
  bei jedem Neustart des Audiodiensts neu angemeldet werden müssen; das Risiko ist größer als der Gewinn.
- **Energiesparmodus und Leerlauf sind keine eigenen Zustände.** Kein gemessener Gewinn, und bei sichtbarer Insel
  würde sich die Bewegung ändern.
- **`UpdateCheck` bleibt auf dem UI-Thread.** Die Wartezeit fällt nur beim Update an, während die
  Windows-Bestätigung den Bildschirm verdeckt, und der Weg lässt sich ohne Installation nicht testen.

## Kamera und Mikrofon auf Windows 26H2

- **Kamera über Media Foundation, Mikrofon über die Aufnahme-Sitzungen (WASAPI)** (24.10.2026). Die Einträge unter
  `ConsentStore` in der Registry werden seit den Updates vom 14.10. nicht mehr geschrieben; sie bleiben als zweite
  Quelle. Verworfen: die Nutzungsdaten unter `C:\ProgramData\Microsoft\Windows\CapabilityAccessManager\`, die nur mit
  Adminrechten lesbar sind. Kamera an nach 3 s, aus nach 5 s; Mikrofon-Apps zählen 5 s über die letzte Sitzung hinaus.

## Kopfleiste

- **Echte App-Leiste (`SHAppBarMessage`) statt nur eines Fensters oben** (18.11.2026, Besitzer). Nur so enden
  maximierte Fenster wie Chrome unter der Leiste und öffnen die Insel nicht mehr beim Griff nach den Tabs. Verworfen:
  den Arbeitsbereich mit `SPI_SETWORKAREA` selbst verkleinern (bleibt nach einem Absturz stehen, maximierte Fenster
  folgen nicht von selbst). Die frühere Annahme, `SPI_SETWORKAREA` gelte nur für den Hauptbildschirm, stimmt nicht: am
  18.11.2026 gemessen, es setzt den Arbeitsbereich des Bildschirms, in dem das Rechteck liegt.
- **24 DIP, Insel ragt heraus.** Niedriger als die Notch (32 DIP) wie am Mac.
- **Verlauf aus den Farben direkt unter der Leiste, deckend** (18.11.2026, Besitzer: abhängig vom angrenzenden
  Fenster bzw. Hintergrund, nicht von dem, was darüber liegt). Eine 2-Pixel-Zeile vom Bildschirm, 12 Farben, alle 2 s
  und nach Fensterwechseln; die Spalten unter der Insel bleiben außen vor, die Schrift folgt der Helligkeit. Ohne
  Messung gilt das Systemdesign der Taskleiste. Die Ecken bleiben eckig (Besitzer). Verworfen: Acrylic-Unschärfe über
  `SetWindowCompositionAttribute` (zeigt nur, was hinter der Leiste liegt, also das Hintergrundbild, nie das Fenster
  darunter; neben dem Verlauf kein sichtbarer Gewinn), Mica (wird bei inaktiven Fenstern deckend) und das
  Hintergrundbild aus der Registry lesen (passt nicht zu Fenstern, Diashows und gestreckten Bildern).
- **Angeheftete Apps = die Liste aus Schnellstart** (18.11.2026, Besitzer wollte einen Taskleistenersatz oben links).
  Eine Liste für Insel und Leiste, gepflegt unter Apps und Skripte. Verworfen: eine zweite Liste nur für die Leiste
  (doppelte Pflege, eigene Einstellungsseite).
- **Links Startmenü und Fenstermenü statt der App-Menüs.** Die Menüs der aktiven App (Datei, Bearbeiten …) lassen sich
  unter Windows nicht allgemein auslesen; das Startmenü steht dort, wo am Mac das Apple-Menü sitzt (Glyphe aus vier
  Feldern, kein Windows-Logo).
- **Vollbild je Bildschirm am obersten großen Fenster, nicht am aktiven** (18.11.2026, Besitzer: Filme und Spiele im
  Vollbild verdecken die Leiste). So bleibt ein Film auf dem einen Bildschirm frei, während auf dem anderen gearbeitet
  wird. Verworfen: nur `ABN_FULLSCREENAPP` (kommt bei Videos im Browser nicht zuverlässig) und das aktive Fenster wie bei
  der Insel (die Leiste käme über den Film zurück, sobald der andere Bildschirm den Fokus hat).
- **Kopfleiste in Segoe UI Variable und mit farbigen App-Symbolen** (18.11.2026, Besitzer: Inter gefiel nicht, einfarbige
  Symbole waren nicht zu erkennen, etwa Teams). Verworfen: die einfarbigen Glyphen des Kachelstils der Insel.
- **Kopfleiste je Bildschirm wählbar, gemerkt an Gerätename und Auflösung** (18.11.2026, Besitzer), wie die Insel ihren
  Platz je Anordnung merkt. Verworfen: Monitornamen aus den EDID-Daten (mehr Code, gleicher Nutzen am festen
  Schreibtisch); ein anderer Monitor am selben Anschluss mit anderer Auflösung gilt als neuer Bildschirm.
- **Angeheftete Apps wie in der Taskleiste**: offenes Fenster nach vorn, im Vordergrund minimieren, sonst starten.
- **Gehört dem PC** (Besitzer): Ein Einstellungs-Export nimmt die Kopfleiste nicht mit, am Schreibtisch an, am Laptop
  aus.
- **Taskleiste nur auf Bildschirmen mit Kopfleiste ausblenden** (18.11.2026, Besitzer: die Kopfleiste ersetzt dort die
  Taskleiste, der Laptop behält seine feste). Weg: das Taskleistenfenster dieses Bildschirms verstecken, seinen
  Arbeitsbereich per `SPI_SETWORKAREA` ohne Broadcast bis unten setzen, maximierte Fenster von Hand nachziehen und die
  Taskleiste am unteren Rand wieder zeigen. Vorher gemessen: Mit Broadcast (`SPIF_SENDCHANGE`) legt der Explorer neu aus
  und zeigt die Taskleiste wieder; Zeigen per `SW_SHOWNA` lässt den Arbeitsbereich in Ruhe. Verworfen: das automatische
  Ausblenden von Windows (gilt nur für alle Bildschirme zusammen), die Taskleiste nur verschieben oder per Region
  unsichtbar machen (der Streifen bliebe reserviert) und eine Low-Level-Maus-Hook (`WH_MOUSE_LL` im UI-Thread bremst bei
  jedem Hänger die Maus im ganzen System; ein 50-ms-Timer nur bei versteckter Taskleiste reicht).
- **Fluent UI System Icons** (Besitzer): passen zu Windows 11, MIT-Lizenz. Verworfen: die Lucide-Symbole der Insel,
  denen Akkustufen und WLAN-Balken fehlen.
- **Klick auf ein Symbol öffnet die Schnelleinstellungen von Windows, auf die Uhr das Benachrichtigungscenter.**
  Eigene Menüs wären doppelte Arbeit.

## Wetter

- **Ort eintippen statt Windows-Standort** (25.10.2026, Besitzer). Der Ort wird über die Ortssuche von Open-Meteo
  gewählt und mit Koordinaten gespeichert. Verworfen: der Ortungsdienst von Windows (Freigabe nötig, am PC ungenau).
- **Abruf nur bei offener Insel**, alle 15 Minuten, und °C/°F wird lokal umgerechnet. Verworfen: Abruf im Hintergrund
  (kostet Netz und Akku für ein Widget, das niemand ansieht).
