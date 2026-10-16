# Plan: Leistung nach der Prüfung vom 15.10.2026

Grundlage: [offene_punkte.md](../offene_punkte.md), Punkte 1–7. Jedes Paket wird einzeln umgesetzt, gemessen und
committet. Gemessen wird wie in der Prüfung: Release-Build als Testinstanz (`DYNAMICBAY_DEV`) mit einer Kopie der
echten Einstellungen ohne Link, 40 s Anlauf, 90 s Messung, CPU in Prozent eines Kerns.

| Paket | Punkt | Erfolgskriterium | Status |
|---|---|---|---|
| 1. Mitteilungen über Ereignis | 1 | Abfrage kostet unter 0,5 Punkte | erledigt 16.10.: 0,2 statt 3,4–4,1 Punkte |
| 2. Aktivitätszustand | 2 | Ausgeblendet, gesperrt oder Bildschirm aus: nahe 0,75 % | offen |
| 3. Claude-Dienst | 5, 8 | Kein String über 85 KB pro Scan | erledigt 16.10.: unter 64 KB statt über 1 MB; Symbol-Animation (Punkt 8) wartet auf Entscheidung |
| 4. Ereignisse statt Abfragen | 4 | Stummschaltung, Freigaben ohne Takt; keine Prozessliste auf dem UI-Thread | offen |
| 5. UI-Thread frei | 6, 7 | Zwischenablage, InstalledApps, AudioService, UpdateCheck nicht mehr als `Slow UI operation` | offen |
| 6. Waveform-Lücke | 3 | Ursache der 2,4 Punkte belegt | offen |

## 1. Mitteilungen über Ereignis

`UserNotificationListener.NotificationChanged` funktioniert mit der Paket-Identität (geprüft 16.10.2026 an einer
Testinstanz: `Added` und `Removed` kommen auf Thread-Pool-Threads an). Gelesen wird nur noch nach einem Ereignis,
dazu eine Sicherheitslesung jede Minute. Ohne Ereignis (Abo schlägt fehl) bleibt der alte 1,2-s-Takt. Logos werden
auf dem UI-Thread erzeugt und pro App zwischengespeichert.
