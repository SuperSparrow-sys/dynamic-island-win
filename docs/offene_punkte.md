# Offene Punkte

Aufgaben, die noch zu erledigen sind. Was erledigt ist, wird entfernt; Messwerte gehören nach
[performance/](performance/README.md), Entscheidungen nach [ENTSCHEIDUNGEN.md](ENTSCHEIDUNGEN.md).

Die Nummern laufen durch und werden nicht neu vergeben. Der nächste Punkt bekommt die **24**.

## Ein neuer Punkt

```markdown
## 13. Kurz, was nicht stimmt

**Gemeldet:** Datum, wo es auffiel, Version.

**Beobachtet:** Was passiert ist und was stattdessen zu erwarten war.

**Ursache:** noch offen - oder nachgewiesen, mit Datei und Zeile.

**Zu tun:** Was geändert werden muss und woran das Erledigtsein zu erkennen ist.
```

---

# Offen

## 6. Synchrone Arbeit hält den UI-Thread fest (Rest)

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** `Slow UI operation` im Log. Zwischenablage und Startleiste sind seit 16.10.2026 erledigt.

**Ursache:** Offen sind der `AudioService`-Timer (COM-Endpunkte, bis 3,1 s, meist kurz nach dem Aufwachen oder einem
Hänger des ganzen PCs) und das Laden der Screenshot-Datei aus dem Bilder-Ordner (`OnScreenshotFile`).

**Zu tun:** Beides in den Hintergrund verlagern. Erledigt, wenn diese Einträge nicht mehr als `Slow UI operation` im
Log stehen. `UpdateCheck` bleibt bewusst, wie es ist (siehe Entscheidungen).

## 7. Start dauert bis zu 8 s

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** Nach Punkt 6 nachgemessen (16.10.2026, Testinstanz): Laufzeit 0,5–2,2 s, Dienste 1,4–5,1 s, der
langsamste Start war kalt. Übrig sind Schritte in `App.OnStartup` mit 180–615 ms.

**Zu tun:** Nach einigen Starts der installierten App das Log auswerten, dann entscheiden. Geringe Priorität.

## 9. Deinstallation mit einem anderen Admin-Konto räumt beim falschen Benutzer auf

**Gemeldet:** 11.10.2026, Fehleranalyse (Befund 35), 1.0.15.

**Beobachtet:** Der Installer läuft für alle Benutzer mit Adminrechten (`PrivilegesRequired=admin`, `{autopf}`). Wer mit
einem anderen Admin-Konto deinstalliert (UAC über die Schulter), bereinigt Banner-Werte, Autostart, Paket und
`{userappdata}` beim Admin statt beim eigentlichen Benutzer.

**Zu tun:** Installation pro Benutzer. Das ist ein Umbau von Installer, Identitäts-Paket und Update-Test.

## 10. Skript-Widgets hinter einem System-Proxy: DNS-Rebinding möglich

**Gemeldet:** 11.10.2026, Fehleranalyse Runde 4.

**Beobachtet:** Ohne Proxy prüft die Skript-Engine jedes Ziel beim Verbindungsaufbau. Mit System-Proxy löst der Proxy
den Namen selbst noch einmal auf, und ein Skript könnte so ein lokales Ziel erreichen.

**Zu tun:** Eigener Verbindungsaufbau über den Proxy (CONNECT mit der geprüften Adresse). Betrifft nur Netze mit
System-Proxy.

## 11. Live-Prüfungen mit echten Geräten und Konten

**Gemeldet:** laufend, zuletzt 17.10.2026.

**Beobachtet:** Gebaut und mit Tests oder Testinstanzen geprüft, aber nicht im echten Betrieb:

- Rubus mit dem echten Rubus-Client (Stand, Frage, Erlauben, Ablehnen, In Rubus öffnen), ab 1.1.10.
- Koppeln mit zwei echten Geräten: Hauptgerät, Ton an anderes Gerät.
- „Über allem“ über Spielen im echten Vollbild auf dem PC mit NVIDIA (auf dem Laptop mit Intel geht es).
- Mitteilungen vom Handy über Smartphone-Link, Handy-Akku im Geräte-Widget, Apple-Music-Cover, Mitteilungen auf dem
  anderen Gerät ausblenden.
- Medienstapel mit Spotify und YouTube gleichzeitig (Wechsel per Mausrad und Touchpad, Vorrang beim Start), Klick auf
  eine Gmail- oder Claude-Mitteilung vom Handy öffnet Outlook bzw. Claude.
- Lautstärke auf mehreren Geräten: „Alle“ und Einzelregler bei „Ton am Laptop“ (beide Seiten 1.1.13, Regler auf beiden
  Seiten), bei zwei gleichzeitig spielenden Ausgaben, Ziehen ab beliebiger Stelle, Stumm aufheben; Abfrage jede
  Sekunde nicht gemessen; Windows „Geteiltes Audio“ nur mit LE-Audio-Hardware (dieser PC hat keine).
- iPhone-Fokus mit echten Kurzbefehlen (ab 1.1.14): läuft „Sofort ausführen“ ohne Rückfrage, was zeigt das iPhone
  bei ausgeschaltetem PC, kommen die neuen Firewall-Regeln mit dem Update, Laptop über die Kopplung, Teams-Status
  „Beschäftigt“ bei Arbeit.
- Der Abschluss-Check mit den echten Konten: OneDrive, Spotify, Mitteilungen, Kalender, Bluetooth, Claude, Microsoft 365.

**Zu tun:** Gemeinsam mit dem Besitzer durchgehen. Gefundene Fehler werden eigene Punkte.

## 12. Apple Music mit Anmeldung

**Gemeldet:** Planung, vor 1.0.

**Beobachtet:** Anzeige und Steuerung gehen über die Windows-Medienschnittstelle. Bibliothek und Playlists bräuchten
die Apple-Music-API.

**Zu tun:** Erst mit einem eigenen Apple-Entwicklerkonto möglich (siehe [INTEGRATIONS.md](INTEGRATIONS.md)).

## 13. Server, Anzeigetafel und Rubus anbinden

**Gemeldet:** 24.10.2026, Besitzer.

**Beobachtet:** Gewünscht ist ein optionaler DynamicBay-Server, der die gekoppelten Geräte verwaltet, dazu ein
Widget für das WAGO-Panel der Anzeigetafel. Entscheidungen und verworfene Wege in
[ENTSCHEIDUNGEN.md](ENTSCHEIDUNGEN.md#funktionen).

**Zu tun,** je Stufe für sich nutzbar. Aufträge für die anderen Repos (mit Beispielen als Vertrag) liegen in
`C:\Users\jonat\Documents\DynamicBay-Auftraege`; dieselben Beispiele werden hier zu Fixtures
(`tests/DynamicBay.Tests/Fixtures/Api/Server`, `.../Anzeigetafel`).

1. Server (seit 24.10.2026 auf prod) und DynamicBay-Client sind gebaut. Offen: im echten Betrieb koppeln, Fokus vom
   iPhone über den Server, Laptop vom Netz und nach etwa 90 s „offline“ in der Rubus-Anzeige.
2. Gemeinsame Einstellungen: geteilt sind Fokus-Profile, stumme Apps und die Panel-Wirkung je Fokus, mit eigener
   Liste je Gerät. Offen: Mitteilungsregeln (welche Schalter genau) und der Anschluss der Zentrale.
3. Anzeigetafel: Zentrale (seit 24.10.2026 auf prod) und DynamicBay direkt sind gebaut. Heim und weg
   (`panel: plan`, `am_ende: aus`, `dauer: offen`) ist überall gebaut, Zentrale und Server mit Nachtrag 1 auf prod,
   DynamicBay ab 1.1.15. Offen: über den Server
   (`anzeigetafel.fokus` aus den Fokus-Profilen), und die Prüfung im echten Betrieb mit dem Schlüssel: ein Fokus
   dimmt das Panel, sein Ende gibt es zurück, das Widget schaltet.
4. Rubus: einmal per Code mit dem Server koppeln, Sessions und Anfragen über ihn; prod und Feldgeräte nur
   „In Rubus öffnen“. Ohne Server bleibt `RubusBridge`.
5. Fokus leise ans iPhone: iOS nimmt von außen keine Befehle an, Tailscale ändert daran nichts. Ohne Mac oder iPad
   (Stand 24.10.2026) ginge es nur verzögert (Automationen auf dem iPhone fragen den Server). Zurückgestellt: Das
   iPhone meldet seit den Automationen nur „zu Hause“ und einzelne Fokus-Modi.
6. Automationen: gebaut (Zu Hause, Unterwegs, Kamera an, Fokus-Sitzung, Nicht stören am PC, iPhone-Fokus; Server
   mit Regeltabelle seit 24.10.2026 auf prod, Zentrale mit `uebergang_s`). Offen: Fokus-Menü in der Insel und der
   Auslöser „Termin“ (Kalender, Titel enthält); Prüfung im echten Betrieb (gehen, kommen, Kamera, Schlafen).

## 17. Apple Music: falsches Titelbild, Vorspulen springt zurück

**Gemeldet:** 25.10.2026, Besitzer, auf einem anderen Rechner mit Apple Music. Titelbilder fehlen oder es bleibt das
des ersten Lieds; die Zeitleiste lässt sich ziehen, danach springt sie zurück.

**Geprüft:** Am Laptop mit Spotify funktioniert beides (Test-Instanz, 25.10.). Apple Music wirft dort beim Auslesen
der Sitzung eine COMException. Das Vorspulen schreibt jetzt `Media: seek to … accepted=…` ins Log.

**Zu tun:** Erst wenn es am betroffenen Rechner zu sehen ist: Log lesen. Nimmt Apple Music das Vorspulen nicht an,
die Zeitleiste dort nur anzeigen (`Controls.IsPlaybackPositionEnabled` bzw. Ergebnis des Vorspulens).

## 18. Doppelte Grundlast nach dem Aufwachen

**Gemeldet:** 17.11.2026, Messung am Laptop, 1.1.20. Nach dem Aufwachen um 12:20 lag die CPU drei Stunden bei 12–15 %
eines Kerns, auch bei pausierter Musik, sonst 3–7 % (nachts 1,2 %). Sie fiel erst, als um 15:00 der zweite Bildschirm
dazukam und die Insel neu platziert wurde (`SnapShapeToTargets`).

**Geprüft:** Ein Profil um 16:50 zeigte nur die atmende Claude-Animation (eine Dauer-Animation, siehe Entscheidungen).
Ebenen, Waveform und `c:Loop` stoppen verborgen richtig. Verdacht: eine `SpringGroup`, die nicht zur Ruhe kommt
(z. B. Ziel NaN), unbewiesen.

**Zu tun:** Beim nächsten Mal das Log lesen: die CPU-Zeile nennt jetzt `animating: springs/loops/waveforms`, und eine
Feder, die nach 30 s noch läuft, schreibt `Springs still moving` mit Empfänger und Werten. Dann die Ursache beheben.

## 19. Ton an anderes Gerät: Latenz

**Gemeldet:** 18.11.2026, Besitzer: Ton vom PC am Laptop spürbar verzögert, beide per Kabel im LAN.

**Beobachtet:** Alle Ton-Sitzungen am 17./18.11. liefen über die LAN-Adresse 192.168.10.25 (Ziel 100 ms), nicht über
Tailscale. Die 193 Aussetzer vom 17.11. fielen in die Zeit ohne Kabel (Ethernet-Link getrennt 15:00 bis 22:04, Laptop
im WLAN der Fritz!Box, Ausgabe AirPods über Bluetooth), gehäuft 15:45 bis 15:54 zwischen zwei WLAN-Wechseln.
Geschätzt (Laptop, AirPods): Loopback ~25 ms, Opus ~10 ms Rest plus 6,5 ms Vorlauf, LAN unter 1 ms, Puffer 100 ms,
Ausgabe 40 ms, Bluetooth 150 bis 250 ms (nicht gemessen).

**Ursache:** Feste 100 ms Puffer im LAN; der Sender liest den Loopback nur alle 50 ms (NAudio, 100-ms-Puffer im
Abfragemodus); AirPods über Bluetooth.

**Zu tun:** Ab dem nächsten Update startet der Puffer im LAN bei 60 ms und wächst bei Lücken. Im echten Betrieb prüfen:
`target` in den Statistikzeilen bleibt per Kabel bei 60, Aussetzer selten. Danach, nur mit Hörtest des Besitzers:
Loopback ereignisgesteuert oder mit 20-ms-Puffer (`LoopbackAudioSource`), Ausgabe 20 statt 40 ms (`WasapiAudioSink`).

## 21. Explorer-Neustart mit Kopfleiste und ausgeblendeter Taskleiste

**Gemeldet:** 18.11.2026, Prüfung vor 1.1.23.

**Beobachtet:** Live nicht geprüft: Den Explorer zu beenden hat die Rechteprüfung der Claude-Session abgelehnt.
Nachgestellt (Taskleiste sichtbar, Streifen reserviert, dann `TaskbarCreated` an die Testinstanz): `TaskbarHider`
versteckt sie wieder und setzt den Arbeitsbereich zurück.

**Ursache:** Die Kopfleiste meldete ihre App-Leiste nach `TaskbarCreated` nicht neu an (`AppBar.Register` kehrte bei
schon gemerktem Fenster sofort zurück). Seit 1.2.0 meldet `MenuBarManager.Renew` jede Leiste neu an (`AppBar.Renew`),
danach versteckt `TaskbarHider` die Taskleisten wieder.

**Zu tun:** Live prüfen: Mit einer Testinstanz den Explorer neu starten (der Besitzer selbst). Erledigt, wenn danach die
Taskleiste auf dem Kopfleisten-Bildschirm versteckt ist und maximierte Fenster oben unter der Leiste und unten am Rand
enden.

## 22. Klick auf die Uhr der Kopfleiste öffnet die Mitteilungszentrale auf dem Laptop

**Gemeldet:** 18.11.2026, Prüfung vor 1.1.23 (Kopfleiste auf DISPLAY2, Laptop Hauptbildschirm).

**Beobachtet:** `ms-actioncenter:` öffnete die Mitteilungszentrale auf DISPLAY1 statt auf dem Bildschirm der Leiste.
Vorne lag dabei ein Fenster auf dem Laptop.

**Ursache:** noch offen. Windows wählt den Bildschirm selbst, vermutlich nach dem Vordergrundfenster. Ob die
ausgeblendete Taskleiste mitspielt, ist nicht geprüft.

**Zu tun:** Mit ein- und ausgeschalteter Taskleiste vergleichen; öffnet sie auf dem falschen Bildschirm, vorher ein
Fenster der Leiste auf ihrem Bildschirm in den Vordergrund holen oder die Mitteilungszentrale anders öffnen.

# Zurückgestellt

## 4. Übrige Abfragen statt Ereignisse

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** Nach den Korrekturen vom 16.10.2026 kosten Stummschaltung, Freigaben und CursorHop zusammen etwa
0,5 Punkte (ausgeblendete Insel, UI-Thread pro Minute: `GetMute` 124 ms, Freigaben 91 ms, CursorHop 41 ms).

**Zu tun:** Wieder aufnehmen, wenn die Grundlast sonst unter 1 % liegt. Dann `IAudioEndpointVolumeCallback`,
`RegNotifyChangeKeyValue` für die Freigaben und einen FileSystemWatcher für das CursorHop-Log. Warum jetzt nicht:
siehe Entscheidungen.

## 23. Suche: was live noch nicht geprüft ist

**Gemeldet:** 18.11.2026, Live-Prüfung der Suche vor 1.1.23.

**Beobachtet:** Geprüft sind Lupe, Windows-Taste, Enter und Strg+Enter, zwei Bildschirme (125 % und 180 %), versteckte
Taskleiste und der Windows-Index. Offen: Everything (nicht installiert; das IPC ist nur gegen ein nachgebautes Fenster
getestet), 100 % Skalierung (an diesem PC nicht eingestellt) und die Windows-Taste, während ein erhöhtes Fenster vorn
liegt (künstliche Tastendrücke kommen dort nicht an).

**Zu tun:** Mit installiertem Everything nach einer Datei suchen, die nicht im Index liegt; an einem Bildschirm mit
100 % öffnen; vor einem Fenster als Administrator die Windows-Taste von Hand drücken.

## Nicht gemessen

- GPU-Auslastung: Die GPU-Zähler lieferten keine Werte.
- Akkuverbrauch in Wh.
