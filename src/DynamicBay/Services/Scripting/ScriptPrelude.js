// DynamicBay script runtime: a Scriptable-compatible subset (ListWidget, stacks, texts, Color, Font, Request,
// FileManager, config). Everything here is plain JavaScript; the only way out of the sandbox are the few
// __host functions (http, files of this script, log), which DynamicBay checks and limits.
"use strict";
(function (global) {
  const host = {
    http: __host_http, read: __host_read, write: __host_write, exists: __host_exists,
    remove: __host_remove, list: __host_list, log: __host_log,
  };
  for (const k of ["__host_http", "__host_read", "__host_write", "__host_exists", "__host_remove", "__host_list", "__host_log"]) delete global[k];

  // ---- values ----
  class Color {
    constructor(hex, alpha) {
      let h = String(hex || "#000000").trim();
      if (h[0] !== "#") h = "#" + h;
      this.hex = h;
      this.alpha = alpha === undefined ? 1 : Number(alpha);
    }
    static dynamic(light, dark) { const c = new Color(dark.hex, dark.alpha); c.light = light; return c; }
    static black() { return new Color("#000000"); } static white() { return new Color("#FFFFFF"); }
    static red() { return new Color("#FF3B30"); } static green() { return new Color("#34C759"); }
    static blue() { return new Color("#007AFF"); } static orange() { return new Color("#FF9500"); }
    static yellow() { return new Color("#FFCC00"); } static purple() { return new Color("#AF52DE"); }
    static gray() { return new Color("#8E8E93"); } static lightGray() { return new Color("#C7C7CC"); }
    static darkGray() { return new Color("#48484A"); } static clear() { return new Color("#000000", 0); }
    static cyan() { return new Color("#32ADE6"); } static magenta() { return new Color("#FF2D55"); }
    static brown() { return new Color("#A2845E"); }
  }
  class Size { constructor(w, h) { this.width = Number(w) || 0; this.height = Number(h) || 0; } }
  class Point { constructor(x, y) { this.x = x; this.y = y; } }
  class Font {
    constructor(name, size) { this.name = String(name); this.size = Number(size) || 12; this.kind = "named"; this.weight = "regular"; }
  }
  const font = (kind, weight) => size => { const f = new Font("", size); f.kind = kind; f.weight = weight; return f; };
  const weights = ["ultraLight", "thin", "light", "regular", "medium", "semibold", "bold", "heavy", "black"];
  for (const w of weights) {
    Font[w === "regular" ? "systemFont" : w + "SystemFont"] = font("system", w);   // systemFont, boldSystemFont, ...
    Font[w + "MonospacedSystemFont"] = font("mono", w);                             // regularMonospacedSystemFont, ...
    Font[w + "RoundedSystemFont"] = font("rounded", w);                             // boldRoundedSystemFont, ...
  }
  Font.italicSystemFont = font("system", "italic");
  const styles = { largeTitle: 34, title1: 28, title2: 22, title3: 20, headline: 17, subheadline: 15, body: 17, callout: 16, footnote: 13, caption1: 12, caption2: 11 };
  for (const [k, s] of Object.entries(styles)) Font[k] = () => { const f = new Font("", s); f.kind = "system"; f.weight = k === "headline" ? "semibold" : "regular"; return f; };

  // ---- widget tree ----
  class WidgetText {
    constructor(text) { this.text = String(text); this.textColor = null; this.font = null; this.lineLimit = 0; this.minimumScaleFactor = 1; this.textOpacity = 1; this.align = "left"; this.url = null; }
    leftAlignText() { this.align = "left"; } centerAlignText() { this.align = "center"; } rightAlignText() { this.align = "right"; }
  }
  class WidgetSpacer { constructor(length) { this.length = length === undefined || length === null ? null : Number(length); } }
  class WidgetStack {
    constructor(vertical) {
      this._vertical = !!vertical; this._align = "top"; this._items = [];
      this.backgroundColor = null; this.cornerRadius = 0; this.size = new Size(0, 0); this.spacing = 0;
      this.borderColor = null; this.borderWidth = 0; this.url = null; this._pad = [0, 0, 0, 0];
    }
    layoutHorizontally() { this._vertical = false; } layoutVertically() { this._vertical = true; }
    topAlignContent() { this._align = "top"; } centerAlignContent() { this._align = "center"; } bottomAlignContent() { this._align = "bottom"; }
    setPadding(t, l, b, r) { this._pad = [t, l, b, r].map(Number); } useDefaultPadding() { this._pad = [0, 0, 0, 0]; }
    addStack() { const s = new WidgetStack(false); this._items.push(s); return s; }
    addText(t) { const x = new WidgetText(t); this._items.push(x); return x; }
    addSpacer(n) { const s = new WidgetSpacer(n); this._items.push(s); return s; }
    addDate(d) { const x = new WidgetText(new Date(d).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" })); this._items.push(x); return x; }
    addImage() { const x = new WidgetText(""); this._items.push(x); return x; } // images are not supported (yet)
  }
  class ListWidget extends WidgetStack {
    constructor() { super(true); this._align = "center"; this._pad = null; this.refreshAfterDate = null; }
    async presentSmall() {} async presentMedium() {} async presentLarge() {} async presentExtraLarge() {}
  }

  const colorOut = c => c ? { hex: c.hex, a: c.alpha } : null;
  function serialize(x) {
    if (x instanceof WidgetText) return {
      t: "text", text: x.text, color: colorOut(x.textColor), font: x.font ? { kind: x.font.kind, weight: x.font.weight, size: x.font.size, name: x.font.name } : null,
      lines: x.lineLimit | 0, minScale: x.minimumScaleFactor, opacity: x.textOpacity, align: x.align, url: x.url,
    };
    if (x instanceof WidgetSpacer) return { t: "spacer", len: x.length };
    if (x instanceof WidgetStack) return {
      t: x instanceof ListWidget ? "widget" : "stack", vertical: x._vertical, align: x._align, pad: x._pad, bg: colorOut(x.backgroundColor),
      radius: x.cornerRadius, w: x.size ? x.size.width : 0, h: x.size ? x.size.height : 0, spacing: x.spacing,
      border: colorOut(x.borderColor), borderWidth: x.borderWidth, url: x.url, items: x._items.map(serialize),
      refresh: x.refreshAfterDate ? new Date(x.refreshAfterDate).getTime() : null,
    };
    return null;
  }

  // ---- network ----
  class Request {
    constructor(url) { this.url = url; this.method = "GET"; this.headers = {}; this.body = null; this.timeoutInterval = 60; this.response = null; }
    async loadString() {
      const r = JSON.parse(host.http(String(this.url), String(this.method), JSON.stringify(this.headers || {}),
        this.body === null || this.body === undefined ? null : String(this.body), Number(this.timeoutInterval) || 60));
      this.response = { statusCode: r.status, headers: r.headers || {} };
      if (r.error) throw new Error(r.error);
      return r.body;
    }
    async loadJSON() { return JSON.parse(await this.loadString()); }
  }

  // ---- files: only inside this script's own folder ----
  const ROOT = "/documents";
  const name = p => String(p).split("/").pop();
  const fm = {
    documentsDirectory: () => ROOT, libraryDirectory: () => ROOT, cacheDirectory: () => ROOT, temporaryDirectory: () => ROOT,
    joinPath: (a, b) => String(a).replace(/\/$/, "") + "/" + String(b),
    fileExists: p => host.exists(name(p)), readString: p => host.read(name(p)), writeString: (p, s) => host.write(name(p), String(s)),
    remove: p => host.remove(name(p)), listContents: () => JSON.parse(host.list()), isDirectory: p => String(p) === ROOT,
    createDirectory: () => {}, isFileStoredIniCloud: () => false, downloadFileFromiCloud: async () => {},
  };
  const FileManager = { local: () => fm, iCloud: () => fm };

  // ---- environment ----
  const result = { widget: null };
  const Script = { setWidget: w => { result.widget = w; }, complete: () => {}, name: () => global.__scriptName || "Skript" };
  const console = { log: (...a) => host.log(a.map(String).join(" ")), warn: (...a) => host.log("WARN " + a.map(String).join(" ")), error: (...a) => host.log("ERROR " + a.map(String).join(" ")) };
  const Device = { locale: () => "de_DE", language: () => "de", isUsingDarkAppearance: () => true, model: () => "DynamicBay", systemName: () => "Windows" };

  Object.assign(global, { Color, Size, Point, Font, ListWidget, WidgetStack, WidgetText, WidgetSpacer, Request, FileManager, Script, console, Device, args: { widgetParameter: null } });
  global.__serializeResult = () => JSON.stringify(result.widget ? serialize(result.widget) : null);
})(globalThis);
