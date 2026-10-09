// Beispiel-Widget für DynamicBay: lädt Werte von einer eigenen API (JSON) und zeigt sie in allen drei Größen.
// Erwartete Antwort, z. B. von einem eigenen Server:
// { "titel": "Raumklima", "wert": 21.4, "einheit": "°C", "trend": "steigend",
//   "zeilen": [ { "name": "Wohnzimmer", "wert": 21.4 }, { "name": "Büro", "wert": 19.8 } ], "stand": "09.10. 21:00" }

const API = "https://example.com/api/werte";   // eigene Adresse eintragen
const REFRESH_MIN = 15;
const FAMILY = config.widgetFamily || "medium";   // "medium" = Groß, "small" = Klein, "accessoryInline" = Mini

const C = {
  bg: new Color("#141414"), text: new Color("#EDEDED"), dim: new Color("#8A8A8A"),
  good: new Color("#2ECC71"), warn: new Color("#E74C3C"), line: new Color("#8A8A8A", 0.3),
};
const TREND = { steigend: "↗", fallend: "↘", gleich: "→" };
const fmt = x => x.toFixed(1).replace(".", ",");

// Laden mit Cache: ohne Netz wird der letzte Stand gezeigt
const fm = FileManager.local();
const CACHE = fm.joinPath(fm.documentsDirectory(), "werte_cache.json");
async function load() {
  try {
    const r = new Request(API);
    r.timeoutInterval = 10;
    const d = await r.loadJSON();
    fm.writeString(CACHE, JSON.stringify(d));
    return { d, offline: false };
  } catch (e) {
    if (fm.fileExists(CACHE)) return { d: JSON.parse(fm.readString(CACHE)), offline: true };
    return { d: null, offline: true, err: String(e) };
  }
}

function text(parent, value, font, color) {
  const t = parent.addText(value);
  t.font = font; t.textColor = color; t.lineLimit = 1;
  return t;
}

const { d, offline, err } = await load();
const w = new ListWidget();
w.backgroundColor = C.bg;

if (FAMILY === "accessoryInline") {
  // Mini: eine Zeile in der kompakten Insel
  const row = w.addStack(); row.centerAlignContent();
  text(row, d ? `${fmt(d.wert)} ${d.einheit}` : "–", Font.semiboldSystemFont(12), d ? C.text : C.dim);
  if (d) { row.addSpacer(5); text(row, TREND[d.trend] || "", Font.semiboldSystemFont(12), C.dim); }
} else if (!d) {
  w.setPadding(14, 16, 14, 16);
  text(w, "Keine Daten", Font.boldSystemFont(13), C.warn);
  w.addSpacer(4);
  text(w, err || "", Font.systemFont(9), C.dim);
} else {
  w.setPadding(14, 16, 14, 16);
  const head = w.addStack(); head.centerAlignContent();
  text(head, d.titel, Font.semiboldSystemFont(12), C.dim);
  head.addSpacer();
  const dot = head.addStack(); dot.size = new Size(7, 7); dot.cornerRadius = 3.5; dot.backgroundColor = offline ? C.warn : C.good;
  w.addSpacer(6);

  const body = w.addStack(); body.centerAlignContent();
  if (FAMILY === "medium") {
    // Groß: Liste links, aktueller Wert rechts
    const list = body.addStack(); list.layoutVertically(); list.spacing = 3;
    for (const z of d.zeilen.slice(0, 5)) {
      const r = list.addStack();
      text(r, z.name.padEnd(12, " "), Font.regularMonospacedSystemFont(12), C.text);
      text(r, fmt(z.wert).padStart(5, " "), Font.mediumMonospacedSystemFont(13), C.text);
    }
    body.addSpacer();
    const div = body.addStack(); div.size = new Size(1, 70); div.backgroundColor = C.line;
    body.addSpacer();
  }
  const now = body.addStack(); now.layoutVertically();
  text(now, "Jetzt", Font.semiboldSystemFont(10), C.dim);
  text(now, fmt(d.wert), Font.boldRoundedSystemFont(28), C.text);
  text(now, `${d.einheit} ${TREND[d.trend] || ""}`, Font.systemFont(10), C.dim);
  if (FAMILY === "small") body.addSpacer();

  w.addSpacer();
  text(w, (offline ? "OFFLINE · " : "") + "Stand " + d.stand, Font.systemFont(9), offline ? C.warn : C.dim);
}

w.refreshAfterDate = new Date(Date.now() + REFRESH_MIN * 60 * 1000);
Script.setWidget(w);
Script.complete();
