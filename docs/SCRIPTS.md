# Eigene Widgets (Skripte)

DynamicBay kann eigene Widgets aus JavaScript anzeigen – mit derselben Schnittstelle wie
[Scriptable](https://scriptable.app) auf dem iPhone. Vorhandene Scriptable-Widgets laufen deshalb meist mit kleinen
Anpassungen (Bilder und Symbole werden noch nicht unterstützt).

Einstellungen → **Widgets und Module** → *Eigene Widgets (Skripte)*:

- **Neues Skript** legt eine Vorlage an und öffnet sie im Editor (Notepad). Speichern genügt – die Insel führt das
  Skript sofort neu aus.
- **Skript hinzufügen…** übernimmt eine vorhandene `.js`-Datei (wird in den Skript-Ordner kopiert).
- Pro Skript: Größe **Klein** oder **Groß**, Schalter **Mini** (eine Zeile in der kleinen Insel, immer sichtbar),
  Bearbeiten, Jetzt ausführen, Entfernen. Darunter steht der Status – bei Fehlern mit Zeilennummer.
- Die Karte erscheint zusätzlich in der normalen Widget-Liste: dort ein-/ausschalten und sortieren.
  **Nur Mini:** Karte in der Liste ausschalten, Mini einschalten.

Die Skripte liegen in `%AppData%\DynamicBay\scripts`, ihre Daten (z. B. Caches) in `scripts\data\<id>`.

## Größen

| Größe in DynamicBay | `config.widgetFamily` | Entwurfsgröße |
|---|---|---|
| Groß | `"medium"` | 338 × 158 (wie iOS mittel) |
| Klein | `"small"` | 158 × 158 |
| Mini (in der Insel) | `"accessoryInline"` | eine Zeile, max. 18 px hoch |

Karten werden in der Entwurfsgröße gelayoutet und passend skaliert – ein Skript sieht aus wie auf dem iPhone.
Ein Skript fragt `config.widgetFamily` ab und baut für jede Größe das passende Layout:

```js
const w = new ListWidget();
w.backgroundColor = new Color("#141414");
if (config.widgetFamily === "accessoryInline") {
  const t = w.addText("21,4 °C ↗");          // Mini: nur eine kurze Zeile
  t.font = Font.semiboldSystemFont(12);
} else {
  w.setPadding(14, 16, 14, 16);
  w.addText("Raumklima").font = Font.semiboldSystemFont(12);
  w.addSpacer();
  const big = w.addText("21,4");
  big.font = Font.boldRoundedSystemFont(30);
}
w.refreshAfterDate = new Date(Date.now() + 15 * 60 * 1000);   // nächste Ausführung
Script.setWidget(w);
```

Ein vollständiges Beispiel mit eigener API, Cache und allen drei Größen: [samples/scripts/Beispiel.js](../samples/scripts/Beispiel.js).

## Unterstützte Schnittstelle

- **Widget:** `ListWidget` (`backgroundColor`, `setPadding`, `refreshAfterDate`, `url`, `addText`, `addStack`,
  `addSpacer`, `addDate`), `WidgetStack` (`layoutHorizontally/Vertically`, `top/center/bottomAlignContent`, `spacing`,
  `size`, `cornerRadius`, `backgroundColor`, `borderColor`, `borderWidth`, `setPadding`, `url`),
  `WidgetText` (`textColor`, `font`, `lineLimit`, `minimumScaleFactor`, `textOpacity`, `left/center/rightAlignText`, `url`).
- **Werte:** `Color(hex, alpha)`, `Color.dynamic(hell, dunkel)` (die Insel ist dunkel → dunkle Variante), `Color.red()` …,
  `Size`, `Point`, `Font.systemFont(größe)`, `Font.boldSystemFont`, `…MonospacedSystemFont`, `…RoundedSystemFont`
  (alle Stärken von `ultraLight` bis `black`), `Font.title1()` usw., `new Font(name, größe)`.
- **Netz:** `new Request(url)` mit `method`, `headers`, `body`, `timeoutInterval`, `loadString()`, `loadJSON()`.
- **Dateien:** `FileManager.local()` / `iCloud()` mit `documentsDirectory`, `joinPath`, `fileExists`, `readString`,
  `writeString`, `remove`, `listContents` – immer im eigenen Ordner des Skripts.
- **Umgebung:** `config.widgetFamily`, `config.runsInWidget` (immer `true`), `Script.setWidget`, `Script.complete`,
  `console.log` (landet im DynamicBay-Log), `Device`, `args`.
- Top-Level-`await` funktioniert wie in Scriptable.

## Sicherheit

Skripte laufen in [Jint](https://github.com/sebastienros/jint), einem in .NET geschriebenen JavaScript-Interpreter,
**ohne Zugriff auf .NET, Windows, Programme oder andere Dateien**. Erlaubt ist nur:

- Internetabfragen über `Request` – nur `http`/`https`, höchstens 2 MB Antwort, 20 s pro Anfrage
  (pro Skript abschaltbar: `AllowNetwork` in der `settings.json`);
- Dateien im eigenen Datenordner (Dateinamen ohne Pfad, max. 1 MB je Datei, 50 Dateien);
- Schreiben ins DynamicBay-Log.

Jeder Lauf ist begrenzt: 40 s Laufzeit, 64 MB Speicher, 20 Mio. Rechenschritte, Rekursionstiefe 400. Ein Klick auf ein
Element mit `url` öffnet nur `http`/`https`-Links im Browser. Bearbeiten öffnet die Datei ausdrücklich in Notepad – nie
über die Windows-Verknüpfung für `.js` (die würde das Skript im Windows Script Host ausführen).

## Aktualisierung

Ein Skript läuft beim Start, nach `refreshAfterDate` (zwischen 1 Minute und 6 Stunden, sonst alle 15 Minuten), nach dem
Speichern der Datei und über *Jetzt ausführen*. Ohne Netz zeigt ein Skript mit Cache (siehe Beispiel) den letzten Stand.
