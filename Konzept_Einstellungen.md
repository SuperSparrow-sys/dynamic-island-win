# Konzept: Einstellungen neu ordnen

Stand 11.10.2026. Umgesetzt, siehe Abschnitt 6.

## 1. Was aus Nutzersicht stört

Die Seiten sind mit den Funktionen gewachsen. Neue Einstellungen kamen dorthin, wo gerade Platz war.

1. **Zusammengehöriges ist verstreut.**
   - Uhr: Ziffern oder Zifferblatt stehen unter „Darstellung“, 24 Stunden und Sekunden unter „Widgets und Module“.
   - Audio: Mikrofon- und Kamera-Anzeige stehen unter „Widgets und Module“, Kopfhörer und Call-Routing unter „Akku und Bluetooth“.
   - Tastenkürzel: Ein- und Ausblenden steht unter „Allgemein“, Mikrofon stumm unter „Widgets und Module“.
   - Ruhe: „Nicht stören“, Calls und Bildschirmfreigabe stehen unter „Verhalten“, „Teams-Status automatisch“ unter „Timer und Kalender“.
2. **„Widgets und Module“ ist eine Sammelseite.** Sie ist sehr lang und enthält:
   - die Widget-Tafel und darunter dieselben Widgets noch einmal als Liste mit Aus/Nook/System und Pfeilen
   - Skript-Widgets und Schnellstart-Apps
   - Claude, Audio, die kompakte Insel, die Uhr und die Tabs
3. **„Timer und Kalender“ ist die zweite Sammelseite.** Sie enthält Timer, Kalender, Zeiterfassung, Teams und To Do, Microsoft 365 und die Kalender-Konten. Microsoft 365 sucht dort niemand.
4. **Konten liegen an vier Stellen.** Spotify steht bei Medien, Microsoft und die Kalender stehen bei Timer, der Claude-Sync-Ordner steht bei den Widgets. Ob etwas verbunden ist, sieht man nirgends gesammelt.
5. **Zwischenablage und Dateiablage sind getrennte Seiten.** In der Insel sind sie ein gemeinsamer Tab „Ablage“. Den Schalter für diesen Tab gibt es dazu ein drittes Mal, unter „Widgets und Module“.
6. **Pfade sind lange Texte oder Eingabefelder.**
   - Der Pfad der Zeiterfassungs-CSV steht als langer Text neben „Datei wählen…“.
   - Der Taildrop-Ordner und der Claude-Ordner stehen als gekürzter Text neben einem Knopf.
   - Für ausgeschlossene Apps gibt es ein freies Textfeld, in das man einen Programmnamen tippen muss.
   - Die überwachten Screenshot-Ordner sieht man nur im Tooltip einer leeren Zeile.
7. **Technisches steht zwischen Alltäglichem.** Bildrate und Grafikkarte stehen unter „Darstellung“ zwischen Form und Schatten.
8. **Einzelnes passt nicht.**
   - „Insel ausblenden“ ist ein Schalter unter „Verhalten“, obwohl es eine Aktion ist.
   - „Beenden“ steht unter „Über“.
   - Die Seite heißt „Zwischenablage und Screenshots“, der Menüpunkt nur „Zwischenablage“.
   - Der CalDAV-Server ist ein Freitextfeld, die Adressen für GMX und web.de stehen im Hinweistext zum Abtippen.

## 2. Neue Gliederung

Die Seitenleiste bekommt zwei kleine Abschnittsüberschriften. Jede Einstellung steht genau an einer Stelle.

```
DynamicBay

  Allgemein
  Darstellung
  Position
  Verhalten
  Ruhe und Fokus

INHALTE
  Insel-Inhalte
  Medien
  Audio und Geräte
  Mitteilungen
  Ablage
  Kalender und Zeit
  Apps und Skripte

VERBINDUNGEN
  Konten

  Erweitert
  Über
```

### Allgemein
- Mit Windows starten
- Sprache
- **Tastenkürzel** (neu zusammen): Ein- und Ausblenden, Mikrofon stumm
- Updates: Nach Updates suchen, Status, Jetzt suchen

### Darstellung
- Form (Insel/Notch), Größe, Im Leerlauf, Schatten, Animationstempo
- Symbolstil der App-Kacheln (bisher bei Schnellstart)

### Position
Die Seite bleibt wie heute: Ankerpunkte, Bildschirme, Ebene, Einrasten, Position je Bildschirm-Anordnung, Abstand zum Rand.

### Verhalten
- Beim Überfahren aufklappen, Verzögerung beim Auf- und Zuklappen
- Automatisch einklappen
- Im Vollbild ausblenden
- Ausblenden bei diesen Apps (siehe Abschnitt 3, ohne Textfeld)
- Der Schalter „Insel ausblenden“ fällt weg. Ausblenden geht über das Tastenkürzel, das Tray-Symbol und den Knopf in der Insel, und darauf weist der Hinweis bei „Tastenkürzel“ hin.

### Ruhe und Fokus (neu)
- Nicht stören
- In Teams- und Zoom-Calls still
- Beim Teilen des Bildschirms ausblenden
- Fokus-Sitzung mit Nicht stören
- Teams-Status automatisch (bisher bei Timer)

### Insel-Inhalte (bisher „Widgets und Module“)
- **Aufgeklappte Insel:** nur noch die Tafel Nook / System / Aus. Die doppelte Liste darunter fällt weg. Ein Tipp auf ein Widget öffnet wie heute seine Einstellungen.
- **Kompakte Insel:** was nebeneinander erscheinen darf (wie heute)
- **Tabs:** Ablage, Mitteilungen
- Hier stehen keine Funktions-Einstellungen mehr. Uhr, Audio und Claude ziehen an ihren Ort um.

### Medien
- Wiedergabe anzeigen, Bei Titelwechsel kurz anzeigen
- Spotify: eine Zeile mit Status und dem Knopf „Verbinden“, der zu „Konten“ führt

### Audio und Geräte (bisher „Akku und Bluetooth“ plus Audio)
- Mikrofon, Kamera und Ton anzeigen; Stummschalten anbieten
- Kopfhörer: sofort als Ausgabe, guter Ton statt Headset-Mikrofon, Musik pausieren beim Trennen, im Call
- Bluetooth-Geräte
- Akku-Hinweise, Warnen ab

### Mitteilungen
Bleibt wie heute: in der Insel, Nachrichtentext, Anzeigedauer, Windows-Banner mit Ausnahmen, stummgeschaltete Apps.

### Ablage (Zwischenablage und Dateiablage zusammen)
- **Zwischenablage:** Verlauf aufzeichnen, Texte speichern, Screenshot kurz anzeigen, Anzahl, nach Neustart behalten, Leeren
- **Screenshot-Ordner:** als Ordner-Knöpfe sichtbar statt nur im Tooltip
- **Dateien:** Ablage aktivieren, Kopieren, nach Herausziehen entfernen, Leeren
- **Downloads:** bleibt in der Liste der kompakten Insel. Hier steht nur ein Hinweis mit Link dorthin.
- **Taildrop:** Empfangen, Speichern in (Ordner-Knopf)

### Kalender und Zeit
- **Kalender:** Termine anzeigen, Vorwarnung; darunter die verbundenen Kalender als Liste mit „Kalender hinzufügen“ (führt zu „Konten“)
- **Timer:** Timer, Fokuszeit, Pause
- **Zeiterfassung:** Datei (Datei-Knopf), Projekte, Erinnern und nachfragen, Zeiterfassung öffnen
- **Uhr:** Ziffern oder Zifferblatt, 24 Stunden, Sekunden
- **Aufgaben:** welche To-Do-Liste

### Apps und Skripte
- Schnellstart-Apps
- Eigene Widgets (Skripte), mit dem Skript-Ordner als Ordner-Knopf
- Claude Code: anzeigen, sofort Bescheid geben, Sitzungen anderer PCs (Ordner-Knopf)

### Konten (neu)
Hier sind alle Verbindungen gesammelt. Jedes Konto ist eine Karte mit Logo, Status („Verbunden als …“ oder „Nicht verbunden“) und einem Knopf. Die Einrichtungsschritte klappen erst auf, wenn man auf „Verbinden“ klickt.
- Microsoft 365: Outlook, Teams, To Do; Teams-Status freischalten
- Spotify
- Google Kalender
- iCloud und CalDAV: Anbieter als Auswahl iCloud / GMX / web.de / Andere. Ein Server-Feld gibt es nur bei „Andere“.
- Kalender-Link (ICS)

### Erweitert (neu)
- Bildrate, Grafikkarte verwenden
- Protokolle und Daten (Ordner-Knopf)
- Beenden

### Über
- Version, GitHub, Danksagungen

## 3. Pfade als Knopf mit Symbol

Es gibt einen gemeinsamen Baustein „Pfad-Knopf“ für alle Orte. Er zeigt keinen langen Pfad, sondern ein Symbol und den Namen.

```
Speichern in                     [ (Ordner) Downloads    ]  (Zurücksetzen)
Datei                            [ (Datei)  Zeiterfassung.csv ]  (Im Explorer zeigen)
Sitzungen anderer PCs            [ (Wolke)  OneDrive     v ]
```

- **Symbol:** `Icon.Folder` für Ordner, `Icon.FileText` für Dateien, `Icon.Cloud` für Sync-Ordner. Alle drei sind schon als SVG vorhanden.
- **Text:** nur der letzte Teil, z. B. „Downloads“ oder „Zeiterfassung.csv“. Bei Standardwerten steht der Name des Standards, z. B. „Downloads“.
- **Tooltip:** der volle Pfad.
- **Klick:** öffnet den Windows-Dialog zum Wählen.
- **Daneben, nur als Symbol mit Tooltip:**
  - „Im Explorer zeigen“ (`Icon.FolderOpen`)
  - „Zurücksetzen“ (`Icon.Reset`), nur wenn ein eigener Ort gewählt ist
- **Ein Ort, der fehlt:** Text in Orange, Tooltip „Ordner nicht gefunden“.

Hier wird der Baustein eingesetzt:

| Ort | heute | neu |
|---|---|---|
| Zeiterfassungs-Datei | langer Pfad als Text + „Datei wählen…“ + eigener Knopf „Datei im Explorer zeigen“ | Datei-Knopf + Explorer-Symbol |
| Taildrop-Ordner | gekürzter Pfad + „Ordner wählen…“ + „Downloads“ | Ordner-Knopf + Zurücksetzen-Symbol |
| Claude: andere PCs | Pfad-Text + „Ordner wählen“ (Menü) | Wolken-Knopf mit dem Menü wie heute (Aus / OneDrive / Dropbox …) |
| Screenshot-Ordner | nur im Tooltip | ein Ordner-Knopf je Ordner, Klick öffnet ihn |
| Skript-Ordner | Knopf „Ordner öffnen“ | Ordner-Knopf |
| Daten und Protokolle | Knopf „Ordner öffnen“ | Ordner-Knopf |
| Ausgeschlossene Apps | Textfeld + „Hinzufügen“ | Textfeld entfällt; „Apps auswählen“, „Laufende App wählen“ und neu „Programm wählen…“ (Dateidialog für .exe) |
| Spotify Redirect URI | schreibgeschütztes Textfeld + „Kopieren“ | Knopf „Kopieren“ mit `Icon.Copy`, die Adresse klein darunter |

**Bleiben Eingabefelder:** Client-IDs von Spotify, Microsoft und Google, die ICS-Adresse und der CalDAV-Server bei „Andere“. Sie werden eingefügt und sind keine Pfade auf dem PC.

## 4. Technik

- **Gespeicherte Einstellungen ändern sich nicht.** Es zieht nur die Anzeige um, deshalb ist keine Migration nötig.
- **Seitennummern:** Die Seitenleiste bekommt feste Namen statt Nummern. Das ersetzt die Zuordnung `NavToPage = { 0, 1, 11, 2, … }`. Angepasst werden dabei:
  - `Widgets.SettingsOf`, also wohin ein Tipp auf ein Widget führt
  - `ShowSection` und die Sprünge aus der Insel und dem Tray
  - der Snapshot-Modus
- **Neuer Baustein:** `PathButton` als Style plus kleine Hilfsmethode in `SettingsStyles.xaml` und `SettingsWindow.xaml.cs`.
- **Prüfung:**
  - Screenshots aller Seiten im Snapshot-Modus, hell und dunkel, Deutsch und Englisch
  - Prüfen, dass jede bisherige Einstellung genau einmal vorkommt
  - Prüfen, dass jeder Widget-Tipp auf der richtigen Stelle landet
- Keine Live-Tests mit deinen Konten.

## 5. Zu entscheiden

Entschieden am 11.10.2026: 1 ja (eigene Seite Konten), 2 ja (eine Seite Ablage), 3 ja (eigene Seite), 4 ja (Schalter fällt weg), 5 die Uhr kommt unter Darstellung, 6 ja (mit Überschriften).

1. **„Konten“ als eigene Seite?** Die Alternative: Spotify bleibt bei Medien und Microsoft und die Kalender bleiben bei „Kalender und Zeit“, nur sauber getrennt.
2. **Zwischenablage und Dateiablage auf eine Seite „Ablage“?** Oder zwei Seiten wie heute?
3. **„Ruhe und Fokus“ als eigene Seite?** Oder als Abschnitt unter „Verhalten“?
4. **Der Schalter „Insel ausblenden“ fällt weg?**
5. **Die Uhr unter „Kalender und Zeit“?** Oder unter „Darstellung“?
6. **Die Abschnittsüberschriften in der Seitenleiste** (INHALTE, VERBINDUNGEN): ja oder nein?

## 6. Umsetzung

Umgesetzt wie oben beschrieben. Abweichungen und Ergänzungen:

- **Kalender-Konten:** Die Liste der verbundenen Kalender mit ihren Schaltern steht unter „Konten“, zusammen mit dem Hinzufügen. „Kalender und Zeit“ zeigt nur, welche verbunden sind, und einen Knopf dorthin. So liegt alles zu einem Konto an einer Stelle.
- **Widget-Tafel:** Ohne die doppelte Liste gibt es einen anderen Weg als Ziehen: Ein Rechtsklick auf ein Widget öffnet ein Menü mit Nook, System, Aus, Nach oben und Nach unten.
- **Einrichtung:** Neben „Verbinden“ steht bei Microsoft und Spotify ein Link „Einrichtung“, damit man eine falsche Client-ID korrigieren kann.
- **Tests:** Ein neuer Test prüft, dass es jede Seite genau einmal gibt und dass jeder Sprung (Zahnrad am Widget, Links zwischen Seiten) auf eine vorhandene Überschrift trifft, auf Deutsch und Englisch.
- **Snapshot-Modus:** `DYNAMICBAY_SNAPSHOT_LANG` und `DYNAMICBAY_SNAPSHOT_THEME` erzeugen Bilder in der anderen Sprache und im anderen Design. Jede Seite wird über ihre ganze Länge aufgenommen.
