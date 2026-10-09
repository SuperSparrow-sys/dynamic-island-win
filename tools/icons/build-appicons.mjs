// Builds the launcher icon palette from Simple Icons (CC0, https://simpleicons.org):
// a gzip'd JSON list of { t: title, h: hex color, p: WPF path data, a: aliases } loaded lazily by the app.
// Usage: node build-appicons.mjs <node_modules dir containing simple-icons and svgpath> <out.json.gz>
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";
import { createRequire } from "node:module";

const [, , modules, outFile] = process.argv;
const require = createRequire(path.join(modules, "noop.js"));
const si = require("simple-icons");
const svgpath = require("svgpath");
const data = JSON.parse(fs.readFileSync(path.join(modules, "simple-icons", "data", "simple-icons.json"), "utf8"));
const meta = new Map((Array.isArray(data) ? data : data.icons).map((i) => [i.title, i]));

const num = (n) => +(+n).toFixed(2);
function toWpf(d) {
  const out = [];
  svgpath(d).abs().unshort().iterate((seg) => {
    const [cmd, ...a] = seg;
    if (cmd === "A") out.push(`A${num(a[0])},${num(a[1])} ${num(a[2])} ${a[3] ? 1 : 0} ${a[4] ? 1 : 0} ${num(a[5])},${num(a[6])}`);
    else if (cmd === "H" || cmd === "V") out.push(cmd + num(a[0]));
    else if (cmd === "Z") out.push("Z");
    else {
      const pts = [];
      for (let i = 0; i < a.length; i += 2) pts.push(`${num(a[i])},${num(a[i + 1])}`);
      out.push(cmd + pts.join(" "));
    }
  });
  return "F1 " + out.join(" ");
}

const list = [];
for (const icon of Object.values(si)) {
  if (!icon || !icon.path) continue;
  const m = meta.get(icon.title);
  const aliases = [];
  if (m?.aliases?.aka) aliases.push(...m.aliases.aka);
  if (m?.aliases?.dup) aliases.push(...m.aliases.dup.map((d) => d.title));
  if (m?.aliases?.loc) aliases.push(...Object.values(m.aliases.loc));
  list.push({ t: icon.title, h: icon.hex, p: toWpf(icon.path), ...(aliases.length ? { a: aliases } : {}) });
}
const gz = zlib.gzipSync(Buffer.from(JSON.stringify(list)), { level: 9 });
fs.writeFileSync(outFile, gz);
console.log(`wrote ${list.length} icons, ${(gz.length / 1024).toFixed(0)} KB`);
