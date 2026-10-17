# Speicher messen

Der Leak-Check läuft im Snapshot-Modus (Demo-Einstellungen, die echten Einstellungen bleiben unberührt) und spielt
die Insel immer wieder durch. Nach jeder Runde wird der Garbage Collector erzwungen und gemessen.

## Befehl

```powershell
dotnet build src/DynamicBay -c Release
powershell -File tools/memory-check.ps1 -Runs 3 -Rounds 6     # Median aus 3 Läufen zu je 6 Runden
```

Ein Lauf dauert etwa zwei Minuten. Jeder Lauf legt `artifacts/memory/run-N/memory.json` an, die Mediane stehen in
`artifacts/memory/summary.json`. Einzeln geht es auch direkt:
`$env:DYNAMICBAY_SNAPSHOT="leak"; DynamicBay.exe --snapshot <ordner>` (Runden: `DYNAMICBAY_LEAK_ROUNDS`).

## Eine Runde

- Einstellungsfenster öffnen, drei Seiten zeigen, schließen
- drei Widget-Layouts, je viermal Panel auf/zu mit den Tabs Nook, Ablage, Mitteilungen
- Analoguhr an/aus, ein Skript-Widget (lokal, ohne Netz) hinzufügen, laufen lassen, entfernen
- acht neue Cover (300 px) mit Bild-Peek
- eine Spiegel-Insel (wie auf einem zweiten Monitor) öffnen, aufklappen, schließen

## Bedingungen

- Release-Build, Software-Rendering (Standard), sonst nichts Rechenintensives nebenher
- Eine laufende installierte DynamicBay stört nicht: der Snapshot-Modus nimmt die Einzelinstanz-Sperre nicht
- Immer den Median aus mindestens drei Läufen vergleichen; der Working Set schwankt zwischen Läufen um etwa 10 MB

## Was zählt

- `aliveAfterGc`: Fenster, Spiegel-Inseln und Cover, die nach dem GC noch leben. Alles außer `0/n` ist ein Leck.
- `managedGrowthMb`: verwalteter Speicher nach GC, Ende minus erste Runde. Sollte um 0 liegen.
- `workingSetGrowthMb`: wächst er mit mehr Runden weiter, hält etwas nativen Speicher fest (Bitmaps, Fenster).
- `iconPixels`: Shell-Icons müssen in der angefragten Größe ankommen.

Der Snapshot-Modus startet die Dienste nicht (Medien-Sitzung, Mitteilungen, Bluetooth, Microsoft 365, Spotify).
Die installierte App braucht deshalb mehr Working Set als dieser Test zeigt.

Nie Bildraten senken oder sichtbare Animationen drosseln, um Speicher oder CPU zu sparen: nur verborgene Animationen
anhalten, Lecks schließen, cachen.

## Ergebnisse

- [2026-10-11](2026-10-11-memory.json): kein Leck in den durchgespielten Wegen, vor und nach den Korrekturen.
  Verwaltet nach GC 5,2 MB, Working Set 185 MB (Median), Zuwachs über 6 Runden unter 5 MB ohne Trend.

## CPU (Leistungsprüfung 15. und 16.10.2026)

Gemessen in Prozent eines Kerns (i7-13700H, 60 Hz, Release-Build), an Testinstanzen (`DYNAMICBAY_DEV`) mit einer
Kopie der echten Einstellungen ohne Konten und Link, 40 s Anlauf, 90 s Messung; je ein Dienst abgeschaltet.

| Was | vorher (1.1.8) | nachher |
|---|---|---|
| Mitteilungsabfrage | 3,4–4,1 Punkte, alle 1,2 s | etwa 0,2 Punkte, nach Ereignis und einmal pro Minute |
| Bildschirm aus, Musik und Claude laufen | 17,78 % | 1,72 % |
| Insel ausgeblendet | 6,6–7,4 % | 2,95 % |
| Claude-Transcript lesen | über 1 MB Strings pro Scan (LOH) | unter 64 KB |
| Nichts läuft | 0,75 % | 0,75 % |

- Eine Dauer-Animation mit 60 Bildern/s (Waveform, Claude-Zeichen) kostet in WPF mit Software-Rendering etwa
  4–6 Punkte im Prozess und noch einmal so viel im DWM, unabhängig von ihrer Größe. Composition verlagert das nur in
  den DWM (11,1 gegen 12,1 Punkte im System). Sparen lässt sich nur durch Anhalten, wenn niemand hinsieht.
- Der Claude-Scan selbst kostet etwa 30 ms alle 5 s (0,6 Punkte).
