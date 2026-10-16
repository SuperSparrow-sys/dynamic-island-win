# Offene Punkte

Aufgaben, die noch zu erledigen sind. Was erledigt ist, wird hier mit Ursache und Änderung als erledigt markiert oder
entfernt; die Messwerte dazu gehören nach [performance/](performance/README.md).

Die Nummern laufen durch und werden nicht neu vergeben. Der nächste Punkt bekommt die **9**.

## Ein neuer Punkt

```markdown
## 9. Kurz, was nicht stimmt

**Gemeldet:** Datum, wo es auffiel, Version.

**Beobachtet:** Was passiert ist und was stattdessen zu erwarten war.

**Ursache:** noch offen - oder nachgewiesen, mit Datei und Zeile.

**Zu tun:** Was geändert werden muss und woran das Erledigtsein zu erkennen ist.
```

---

# Offen

Leistungsprüfung vom 15.10.2026 (Version 1.1.8), nach Wirkung sortiert. CPU-Werte in Prozent **eines** Kerns
(i7-13700H, 60 Hz, Release-Build). Gemessen an der installierten App (nur beobachtet) und an Testinstanzen
(`DYNAMICBAY_DEV`) mit einer Kopie der echten Einstellungen, ohne Konten und ohne Link; je ein Dienst abgeschaltet.

## 1. Mitteilungsabfrage verursacht den Großteil der Grundlast

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** Alle Dienste 4,86 %, nur Mitteilungen und Claude aus 1,42 %; nur die Abfrage an 4,90 %, alles aus
0,75 %. Die Abfrage kostet also 3,4–4,1 Punkte (70–85 % der Grundlast), dazu 1,7 Punkte im Windows-Dienst
`WpnUserService` (svchost) und 359 Kontextwechsel pro Sekunde. Das war bei 13 Einträgen im Info-Center; die Kosten
wachsen mit der Liste (bis 29 Einträge im Log gesehen). Läuft auch bei ausgeblendeter Insel und gesperrtem PC.

**Ursache:** `Services/NotificationService.cs:46` fragt alle 1,2 s `GetNotificationsAsync` ab. In `ReadAsync` und
`ToItemAsync` läuft der Code nach dem `await` auf einem WinRT-Thread (Start in SHCORE.dll) weiter und erzeugt dort mit
`ImageTools.Load` ein WPF-Bild. Dadurch hat dieser Thread einen eigenen WPF-Dispatcher (`HwndWrapper`-Fenster), und die
Aufrufe gehen über Apartment-Grenzen.

**Erledigt 16.10.2026:** `NotificationChanged` funktioniert mit der Paket-Identität. Gelesen wird nur noch nach diesem
Ereignis und einmal pro Minute zur Sicherheit; ohne Ereignis bleibt der 1,2-s-Takt. Mehrere Ereignisse während einer
Lesung ergeben genau eine weitere (`Core/CoalescedRun.cs`, Test `PerformanceTests`). Logos entstehen auf dem UI-Thread
und werden pro App zwischengespeichert. Gemessen (Medien aus, Musik lief): mit Mitteilungen 3,02 %, ohne 2,83 %, die
Abfrage kostet also noch etwa 0,2 statt 3,4–4,1 Punkte. SHCORE-Threads 0,1 % statt 2,6 %. Das Kriterium „alle Dienste
unter 2 %“ ist damit nicht erreicht, weil der Claude-Dienst bei laufender Sitzung 5,4 Punkte kostet (Punkt 8).

## 2. Kein Dienst pausiert bei ausgeblendeter Insel, Sperre, Bildschirm aus oder Akku

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** Mit ausgeblendeter Insel (`Hidden=True`) braucht die installierte App weiter 6,6–7,4 %. Ohne Dienste
wären es 0,75 %.

**Ursache:** Es gibt keinen zentralen Aktivitätszustand. Sperre, Bildschirm aus und Energiesparmodus werden nirgends
ausgewertet; `SessionSwitch` nutzen nur die Zeiterfassung und die Link-Sperre. Timer laufen weiter, bis ihr Dienst
abgeschaltet wird.

**Erledigt 16.10.2026:** `Core/ActivityState.cs` wertet Sperre und Bildschirm aus. Solange niemand hinsieht, halten
`Waveform` und `c:Loop` an, und die Stummschaltungs-Abfrage (500 ms) sowie der Claude-Scan lassen ihre Arbeit aus.
Die Mikrofon-Nutzung bleibt abgefragt, weil sie den Anrufmodus steuert, dem das gekoppelte Gerät folgt. Gemessen an
einer Testinstanz bei laufender Musik und arbeitender Claude-Sitzung, mit der echten Windows-Meldung „Bildschirm aus“
an ihr Meldungsfenster: sichtbar 17,78 %, Bildschirm aus 1,72 % (je 60 s). Ausgeblendete Insel: 2,95 % statt
6,6–7,4 %, hauptsächlich dank Punkt 1. Test `PerformanceTests` (Zustand, Meldung lesen, Gegenprobe rot).

Nicht umgesetzt: Energiesparmodus und Leerlauf als eigene Zustände. Es gibt keine Messung, die einen Gewinn
belegt, und Leerlauf bei sichtbarer Insel würde die sichtbare Bewegung verändern. Nicht live geprüft: die echte Sperre
(dafür hätte der PC gesperrt werden müssen); der Weg über `SessionSwitch` ist derselbe wie bei Zeiterfassung und
Link-Sperre.

## 3. Waveform kostet 2,4 Punkte mehr als nötig

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** `DYNAMICBAY_SNAPSHOT=perf` (Median aus 3 Läufen): Waveform kompakt 6,1 %, Panel mit Musik 8,8 %. Ein
isoliertes WPF-Testprogramm mit derselben Waveform braucht bei 60 Bildern/s 3,7 %.

**Ursache:** noch offen. Die 3,7 % sind die festen Kosten von WPF in Software. Woher die übrigen 2,4 Punkte in der App
kommen, ist ungeklärt.

**Nachtrag 16.10.2026:** Wahrscheinlich keine echte Lücke. Das Testprogramm schwankt mit derselben Waveform zwischen 3,6
und 5,3 % (`harness-results`, sechs Läufe), und in der App laufen pro Bild nur WPFs eigene Render-Aufträge (Zählung
bei Punkt 8). Was bleibt, ist der Umbau auf `Windows.UI.Composition` für alle Dauer-Animationen (Waveform, Claude-Symbol):
erst ein Prototyp mit Messung, dann Entscheidung.

**Zu tun:** Die Bildrate bleibt unverändert. Die Lücke mit einem instrumentierten Build eingrenzen. Als größerer Umbau
denkbar: die Waveform als `Windows.UI.Composition`-Visual, das im DWM läuft; vorher mit einem Prototyp messen.
Geschätzter Gewinn: etwa 2 Punkte über die Lücke, mit Composition bis etwa 5 (unsicher).

## 4. Übrige Abfragen statt Ereignisse

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** Zusammen etwa 0,7 Punkte (1,42 % gegen 0,75 %). Einzelkosten pro Aufruf:

- Prozessliste: 7 ms CPU alle 1,5 s während Anrufen, auf dem UI-Thread (`AudioService.RunningImagePaths`)
- `GetProcessesByName`: 3,9 ms alle 5 s (CursorHop)
- Netzwerkkarten: 5,5 ms alle 5 s, bei 54 Adaptern (`LinkDiscovery.BroadcastAddresses`)
- `tailscale status --json`: 12,5 ms plus ein Kindprozess alle 30 s
- `GetMute`: alle 500 ms

**Ursache:** Abfragen in festen Abständen, teils auf dem UI-Thread.

**Zu tun:** Ereignisse statt Abfragen: `IAudioEndpointVolumeCallback` für Stummschaltung und Lautstärke,
`RegNotifyChangeKeyValue` für die Mikrofon- und Kamera-Freigaben, ein FileSystemWatcher für das CursorHop-Log.
Prozess-Abfragen nur für betroffene PIDs und nicht auf dem UI-Thread. Geschätzter Gewinn: bis 0,7 Punkte, dazu kein
Ruckeln während Anrufen.

## 5. Große Strings fragmentieren den Large Object Heap

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** In der installierten App 29 MB auf dem LOH, davon 26 MB freie Lücken. 1,1 MB große Strings in 90 s.

**Ursache:** `Services/ClaudeService.cs:393` liest bis zu 512 KB als einen String (1 MB, landet auf dem LOH) und
zerlegt ihn mit `Split('\n')`. Das passiert alle 5 s, solange eine Claude-Sitzung arbeitet.

**Erledigt 16.10.2026:** Die letzten 512 KB landen in einem geliehenen Puffer (`ArrayPool`), die Zeilen werden von hinten
als Bytes durchsucht und mit `Utf8JsonReader` gelesen; nur die gesuchten Werte werden zu Strings
(`ClaudeService.ParseTail`). Test `PerformanceTests`: ein Lesevorgang mit zwei 200-KB-Zeilen braucht unter 64 KB
(vorher über 1 MB, Gegenprobe rot). An sechs echten Transcripts geprüft: Titel, Ordner, letzte Eingabe und
Remote-Link stimmen.

## 6. Synchrone Arbeit hält den UI-Thread fest

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** 95 UI-Hänger seit 12.10.2026, Median 726 ms, 90 % unter 1,8 s. Werte, die zeitlich zum Aufwachen aus
dem Standby passen (etwa 28 s und 7 s, ohne gleich langen „UI stall“), sind nicht mitgezählt.

**Ursache:** `Slow UI operation` im Log:

- Zwischenablage-Timer: 16×, bis 1,9 s
- `InstalledApps.WarmUpAsync` beim Start: 11×, bis 2,1 s; COM über `dynamic`, lädt den C#-Binder
- `AudioService`-Timer: bis 3,1 s
- `UpdateCheck.InstallAsync`: bis 11,8 s

**Zu tun:** In Hintergrund-Threads verlagern. `InstalledApps` erst bei Bedarf, über `IShellItem`. Erledigt, wenn diese
Einträge nicht mehr als `Slow UI operation` im Log stehen.

## 7. Start dauert bis zu 8 s

**Gemeldet:** 15.10.2026, Leistungsprüfung, 1.1.8.

**Beobachtet:** Laufzeit 0,6–2,3 s („Runtime start took“), Dienste 1–8 s („Services started in“).

**Ursache:** wie bei Punkt 6, vor allem die synchronen Schritte in `App.OnStartup` und `InstalledApps`.

**Zu tun:** Mit Punkt 6 erledigen, danach neu messen. Geringe Priorität.

## 8. Claude-Dienst kostet 5,4 Punkte, solange eine Sitzung arbeitet

**Gemeldet:** 16.10.2026, beim Nachmessen von Punkt 1, Build nach 1.1.8.

**Beobachtet:** Testinstanz ohne Medien 8,44 %, zusätzlich ohne Claude 3,02 %. Davon `coreclr.dll` 2,2 % und
`wpfgfx_cor3.dll` 1,5 %. Die laufende Sitzung hat ein Transcript von mehreren MB. In der Prüfung vom 15.10. zeigte
dieselbe Ablation fast keinen Unterschied (5,02 % gegen 4,86 %).

**Ursache:** nachgewiesen. Der Scan kostet nur etwa 30 ms CPU alle 5 s (0,6 Punkte). Den Rest kostet das drehende
Sparkles-Symbol in der kompakten Insel (`Island/Views/CompactView.xaml`, `c:Loop`, 4 s pro Umdrehung bei 60 Bildern/s).
Es läuft, solange irgendeine Sitzung arbeitet, also bei viel Claude-Nutzung fast den ganzen Tag. Gezählt pro 20 s:
1198 Render-Aufträge, sonst nur Timer; die App macht pro Bild nichts Überflüssiges. Ein 60-Hz-Bild kostet in WPF mit
Software-Rendering einfach etwa 4 Punkte, egal wie klein das bewegte Element ist (siehe Punkt 3).

**Zu tun:** Eine Entscheidung des Besitzers: das Symbol so lassen, oder die Bewegung anders gestalten (zum Beispiel
eine Umdrehung, dann Pause; oder ein ruhiges Symbol). Die Bildrate wird nicht gesenkt. Gewinn: bis etwa 4–5 Punkte,
solange Claude arbeitet.

# Zurückgestellt

Bewusst nicht jetzt, mit Begründung.

## Geprüft und verworfen (15.10.2026)

- **Kleineres Fenster:** Das dauerhaft 988×700 px große Fenster kostet nichts extra (Waveform 4,9 % gegen 4,6 % bei
  264×100 DIP). WPF zeichnet nur geänderte Bereiche.
- **Grafikkarte statt Software:** Mit der Grafikkarte braucht die Waveform etwa die doppelte CPU (9–10 % statt etwa
  5 %) und 50–70 MB mehr Speicher. Ein DWM-transparentes Fenster ist nicht besser. Software-Zeichnen bleibt.
- **Waveform über den Render-Thread** (Storyboard statt `InvalidateVisual`): keine Ersparnis (3,5–4,4 % gegen
  3,6–3,8 %).
- **Dauerndes JIT-Kompilieren:** Artefakt des Zähler-Anhängens. Ohne Zähler 2 Methoden in 90 s.
- **Grafiktreiber im Prozess:** WPF lädt sie auch im reinen Software-Modus; überwiegend geteilter Speicher.

## Nicht gemessen

- GPU-Auslastung: Die GPU-Zähler lieferten keine Werte.
- Verhalten bei Sperre und Bildschirm aus: Dafür hätte der PC gesperrt werden müssen.
- Akkuverbrauch in Wh.
