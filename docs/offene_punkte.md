# Offene Punkte

Aufgaben, die noch zu erledigen sind. Was erledigt ist, wird entfernt; Messwerte gehören nach
[performance/](performance/README.md), Entscheidungen nach [ENTSCHEIDUNGEN.md](ENTSCHEIDUNGEN.md).

Die Nummern laufen durch und werden nicht neu vergeben. Der nächste Punkt bekommt die **16**.

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

# Zurückgestellt

## 4. Übrige Abfragen statt Ereignisse

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** Nach den Korrekturen vom 16.10.2026 kosten Stummschaltung, Freigaben und CursorHop zusammen etwa
0,5 Punkte (ausgeblendete Insel, UI-Thread pro Minute: `GetMute` 124 ms, Freigaben 91 ms, CursorHop 41 ms).

**Zu tun:** Wieder aufnehmen, wenn die Grundlast sonst unter 1 % liegt. Dann `IAudioEndpointVolumeCallback`,
`RegNotifyChangeKeyValue` für die Freigaben und einen FileSystemWatcher für das CursorHop-Log. Warum jetzt nicht:
siehe Entscheidungen.

## Nicht gemessen

- GPU-Auslastung: Die GPU-Zähler lieferten keine Werte.
- Akkuverbrauch in Wh.
