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
