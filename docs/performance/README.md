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
