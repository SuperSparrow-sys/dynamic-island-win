# Plan: PC und Laptop koppeln, CursorHop einbinden

Stand 2026-10-11: Teil A und Teil B 1–5 umgesetzt (Transport, Einstellungsseite, Status, Dateien, Zwischenablage,
Mitteilungen, Musik, dazu das Hauptgerät). Dazu „Zusammen arbeiten“: Timer auf beiden Geräten und übernehmen, Ruhe und
Fokus, Fragen auf dem Hauptgerät, Akku, gemeinsam sperren, Tailscale-Suche nur bei eigenen Geräten. Offen: Live-Test
mit zwei echten Geräten. Beschreibung
in [INTEGRATIONS.md](../INTEGRATIONS.md#geräte-koppeln).

## Ziel

PC und Laptop arbeiten wie ein Gerät: CursorHop läuft sofort nach dem Start und verbindet sich, die Insel zeigt
den Zustand der Verbindung, Dateien und Zwischenablage wandern zwischen den Geräten und landen sichtbar in der
Ablage. Das geht über LAN und über Tailscale.

## Was CursorHop bietet (geprüft auf diesem PC)

- Software-KVM in Rust: eine Maus und eine Tastatur für mehrere PCs, Wechsel am Bildschirmrand, Geräte findet es
  per mDNS, die Verbindung ist mit dem Noise-Protokoll verschlüsselt. Version 0.12.1, Lizenz Pro+ (Game Mode,
  Dateien in der Zwischenablage, Dateiübertragung).
- Startet über den Windows-Autostart (`HKCU\...\Run\com.cursorhop.app`), läuft hier seit dem Hochfahren.
- Keine offizielle Schnittstelle. Intern gibt es einen gRPC-Dienst auf `127.0.0.1:50051` mit Token. Den nutzen wir
  **nicht**: private Schnittstelle, kann sich mit jeder Version ändern, und das Token gehört CursorHop.
- Das Protokoll `%LocalAppData%\CursorHop\logs\core.log.<Datum>` enthält alles, was die Insel braucht:
  - `[PEER] PeerHello processed — connection ready` → Gerät verbunden
  - `[ROLE] peer claimed / released Primary role` → Maus ist drüben / wieder hier
  - `[CLIP_RECV] received ... clipboard frame` → Zwischenablage vom anderen Gerät
  - `[CLIP_FWD] ... file manifest` → Dateien über die Zwischenablage
  - Trennung und Datei-Empfang: Zeilen müssen im Spike noch gefunden werden
- Dateien zieht man bei CursorHop über den Bildschirmrand (OLE-Ziehleisten am Rand). Das passt zum Herausziehen aus
  der Ablage der Insel.

## Teil A: CursorHop in der Insel (klein, zuerst)

1. **Erkennen:** installiert (Startmenü-Eintrag), läuft (Prozess), Autostart an.
2. **Sofort starten:** Schalter „CursorHop mitstarten“. DynamicBay startet CursorHop, wenn es nicht läuft: beim
   eigenen Start, nach dem Aufwachen und wenn das Netzwerk zurückkommt.
3. **Protokoll mitlesen** (`CursorHopService`, wie `TaildropService`): nur lesen, tolerant parsen, jede bekannte
   Zeile als Fixture-Test. Unbekannte Zeilen werden ignoriert, ein neues Format schaltet die Anzeige nur ab.
4. **Insel:**
   - Peek „Laptop verbunden“ / „Laptop getrennt“
   - Peek „Zwischenablage vom Laptop“. Der Eintrag in der Zwischenablage-Historie bekommt das Gerät als Quelle.
   - Karte im System-Tab: läuft / verbunden mit …, Maus gerade hier oder drüben, Knopf „Öffnen“
5. **Datei-Drop über CursorHop (Spike mit beiden Geräten):** Datei aus der Ablage an den Bildschirmrand ziehen und
   prüfen, ob CursorHop sie übernimmt. Herausfinden, wo empfangene Dateien landen. Diesen Ordner beobachten:
   Peek „Datei vom Laptop“ und automatisch in die Ablage.

## Teil B: DynamicBay mit DynamicBay koppeln (groß)

1. **Finden:** im LAN per UDP-Broadcast (Port 43824), über Tailscale die Geräte aus `tailscale status --json`
   (gibt es schon für Taildrop). Port 43823.
2. **Koppeln wie bei Bluetooth:** Auf einem Gerät „Gerät koppeln“, beide Inseln zeigen denselben 6-stelligen Code
   (aus dem Schlüsselaustausch abgeleitet), der Nutzer bestätigt auf beiden. Damit ist ein Angreifer in der Mitte
   ausgeschlossen. Jedes Gerät hat ein eigenes Zertifikat (DPAPI), gespeichert wird der Fingerabdruck des Partners.
3. **Verbindung:** TLS (`SslStream`) mit gegenseitig festgepinnten Zertifikaten. Nachrichten als JSON, Dateien in
   Blöcken. Neuverbindung mit Backoff bei Netzwechsel, Aufwachen, Tailscale an/aus. Nur gekoppelte Geräte.
4. **Firewall:** Der Installer legt eine Regel für den Port an, nur private Netzwerke und Tailscale.
5. **Funktionen in Reihenfolge:**
   1. Status des anderen Geräts: online, Akku des Laptops, CursorHop-Zustand beider Seiten
   2. Ablage: „An Laptop senden“ (Knopf und Ziehen), Empfang mit Peek und Fortschritt. Dateinamen werden bereinigt,
      Ziel ist `Downloads\DynamicBay`, nichts wird automatisch geöffnet.
   3. Zwischenablage-Historie gemeinsam (optional, Passwort-Manager-Inhalte bleiben ausgenommen)
   4. Mitteilungen des Laptops auf dem PC spiegeln und umgekehrt (optional)
   5. Musik des anderen Geräts steuern, Timer übernehmen
6. **Einstellungen:** neue Seite „Geräte“ unter Verbindungen: gekoppelte Geräte, entkoppeln, was geteilt wird.

## Tests

- Protokoll, Kopplung und Dateiübertragung über Test-Streams (zwei Endpunkte im selben Prozess).
- Zwei Instanzen auf einem PC mit verschiedenen Ports im Snapshot-Modus (eigene Daten, kein Single-Instance-Lock).
- CursorHop-Parser mit echten Protokollzeilen als Fixtures.
- Live-Test mit dem Besitzer auf PC und Laptop, im LAN und nur über Tailscale.

## Reihenfolge

| Schritt | Inhalt | Umfang |
|---|---|---|
| 1 | Teil A 1–4: CursorHop erkennen, mitstarten, Peeks, System-Karte | ein Release |
| 2 | Teil A 5: Spike Datei-Drop über CursorHop, zusammen mit dem Besitzer | ein Termin |
| 3 | Teil B 1–4: Finden, Koppeln, Verbindung, Firewall | ein bis zwei Releases |
| 4 | Teil B 5.1–5.2: Status und Dateien senden | ein Release |
| 5 | Teil B 5.3–5.5 nach Wunsch | je ein Release |

## Offene Fragen an den Besitzer

- Sollen Mitteilungen und die Zwischenablage-Historie gespiegelt werden, oder nur Dateien und Status?
- Welches Gerät ist bei CursorHop der „Primary“ (wo Maus und Tastatur hängen)?
- Reicht für den Datei-Drop der Weg über CursorHop, oder soll „An Laptop senden“ auch ohne CursorHop gehen (Teil B)?
