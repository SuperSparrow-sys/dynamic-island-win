// Converts Lucide SVG icons (ISC) into a WPF ResourceDictionary of Geometry resources.
// Usage: node build-icons.mjs <lucide-icons-dir> <output.xaml>
// Requires: npm i svgpath  (run from a folder where it's installed, or set NODE_PATH)
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(path.join(process.cwd(), "noop.js"));
const svgpath = require("svgpath");

const [, , iconDir, outFile] = process.argv;

// Lucide name -> resource key suffix
const ICONS = {
  "heart": "Heart", "list-music": "Queue", "shuffle": "Shuffle", "repeat": "Repeat", "repeat-1": "RepeatOne",
  "monitor-speaker": "Devices", "music": "Music", "folder": "Folder", "file": "File", "image": "Image",
  "clipboard": "Clipboard", "copy": "Copy", "trash-2": "Trash", "pin": "Pin", "pin-off": "PinOff",
  "save": "Save", "x": "Close", "check": "Check", "plus": "Plus", "scissors": "Scissors", "type": "Text",
  "bell": "Bell", "bell-off": "BellOff", "moon": "Moon", "eye": "Eye", "eye-off": "EyeOff",
  "battery": "Battery", "battery-charging": "BatteryCharging", "battery-low": "BatteryLow",
  "battery-medium": "BatteryMedium", "battery-full": "BatteryFull", "plug-zap": "Plug", "zap": "Zap",
  "bluetooth": "Bluetooth", "headphones": "Headphones", "timer": "Timer", "rotate-ccw": "Reset",
  "coffee": "Coffee", "calendar": "Calendar", "clock": "Clock", "map-pin": "MapPin", "video": "Video",
  "settings": "Settings", "grip-horizontal": "Grip", "move": "Move", "palette": "Palette",
  "monitor": "Monitor", "keyboard": "Keyboard", "info": "Info", "log-in": "LogIn", "log-out": "LogOut",
  "external-link": "External", "chevron-left": "ChevronLeft", "chevron-right": "ChevronRight",
  "chevron-down": "ChevronDown", "chevron-up": "ChevronUp", "power": "Power", "refresh-cw": "Refresh",
  "layout-grid": "Modules", "sliders-horizontal": "Sliders", "app-window": "AppWindow",
  "volume-2": "Volume", "inbox": "Inbox", "arrow-down-to-line": "DropIn", "sparkles": "Sparkles",
  "square-dashed-mouse-pointer": "Snip", "file-text": "FileText", "folder-open": "FolderOpen",
  "brain": "Focus", "minus": "Minus", "laptop": "Laptop", "smartphone": "Phone", "speaker": "Speaker",
  "download": "Download", "layers": "Layers", "languages": "Languages", "github": "Github", "mouse-pointer-2": "Pointer",
  "cloud-upload": "CloudUpload", "cloud": "Cloud", "link": "Link", "key-round": "Key", "calendar-plus": "CalendarPlus", "user-round": "User", "mail": "Mail",
  "calculator": "Calculator", "notebook-pen": "Notepad", "square-terminal": "Terminal", "camera": "Camera", "globe": "Globe",
  "images": "Photos", "paintbrush": "Paint", "gamepad-2": "Game", "map": "Map", "store": "Store", "shield-check": "Shield", "cog": "Cog",
  "mic": "Mic", "mic-off": "MicOff", "volume-x": "VolumeOff",
  "magnet": "Magnet", "maximize": "Fullscreen", "app-window-mac": "Window", "lock": "Lock", "flask-conical": "Lab", "scan-text": "ScanText", "mouse": "Mouse", "code": "Code", "pencil": "Pencil",
};

const num = (n) => +(+n).toFixed(3);

function shapeToPath(tag, a) {
  const f = (k, d = 0) => (a[k] !== undefined ? parseFloat(a[k]) : d);
  switch (tag) {
    case "path": return a.d;
    case "line": return `M${f("x1")} ${f("y1")} L${f("x2")} ${f("y2")}`;
    case "polyline":
    case "polygon": {
      const p = a.points.trim().split(/[\s,]+/).map(Number);
      let d = `M${p[0]} ${p[1]}`;
      for (let i = 2; i < p.length; i += 2) d += ` L${p[i]} ${p[i + 1]}`;
      return tag === "polygon" ? d + " Z" : d;
    }
    case "circle": {
      const cx = f("cx"), cy = f("cy"), r = f("r");
      return `M${cx - r} ${cy} A${r} ${r} 0 1 0 ${cx + r} ${cy} A${r} ${r} 0 1 0 ${cx - r} ${cy} Z`;
    }
    case "ellipse": {
      const cx = f("cx"), cy = f("cy"), rx = f("rx"), ry = f("ry");
      return `M${cx - rx} ${cy} A${rx} ${ry} 0 1 0 ${cx + rx} ${cy} A${rx} ${ry} 0 1 0 ${cx - rx} ${cy} Z`;
    }
    case "rect": {
      const x = f("x"), y = f("y"), w = f("width"), h = f("height");
      let rx = a.rx !== undefined ? f("rx") : f("ry"), ry = a.ry !== undefined ? f("ry") : rx;
      if (!rx) return `M${x} ${y} H${x + w} V${y + h} H${x} Z`;
      return `M${x + rx} ${y} H${x + w - rx} A${rx} ${ry} 0 0 1 ${x + w} ${y + ry} V${y + h - ry} ` +
        `A${rx} ${ry} 0 0 1 ${x + w - rx} ${y + h} H${x + rx} A${rx} ${ry} 0 0 1 ${x} ${y + h - ry} ` +
        `V${y + ry} A${rx} ${ry} 0 0 1 ${x + rx} ${y} Z`;
    }
  }
  return "";
}

// Serialize with explicit separators so WPF's path mini-language parses it unambiguously.
function toWpf(d) {
  const out = [];
  svgpath(d).abs().unshort().iterate((seg) => {
    const [cmd, ...args] = seg;
    if (cmd === "A") {
      const [rx, ry, rot, large, sweep, x, y] = args;
      out.push(`A ${num(rx)},${num(ry)} ${num(rot)} ${large ? 1 : 0} ${sweep ? 1 : 0} ${num(x)},${num(y)}`);
    } else if (cmd === "H" || cmd === "V") {
      out.push(`${cmd} ${num(args[0])}`);
    } else if (cmd === "Z") {
      out.push("Z");
    } else {
      const pts = [];
      for (let i = 0; i < args.length; i += 2) pts.push(`${num(args[i])},${num(args[i + 1])}`);
      out.push(`${cmd} ${pts.join(" ")}`);
    }
  });
  return out.join(" ");
}

let xaml = `<!-- Generated by tools/icons/build-icons.mjs from Lucide (ISC License, https://lucide.dev). Do not edit. -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
`;
for (const [name, key] of Object.entries(ICONS)) {
  const file = path.join(iconDir, `${name}.svg`);
  if (!fs.existsSync(file)) { console.warn("missing", name); continue; }
  const svg = fs.readFileSync(file, "utf8");
  const parts = [];
  for (const m of svg.matchAll(/<(path|line|polyline|polygon|circle|ellipse|rect)\b([^>]*?)\/?>/g)) {
    const attrs = {};
    for (const am of m[2].matchAll(/([\w-]+)="([^"]*)"/g)) attrs[am[1]] = am[2];
    const d = shapeToPath(m[1], attrs);
    if (d) parts.push(toWpf(d));
  }
  xaml += `    <Geometry x:Key="Icon.${key}">F0 ${parts.join(" ")}</Geometry>\n`;
}
xaml += "</ResourceDictionary>\n";
fs.writeFileSync(outFile, xaml);
console.log("wrote", outFile);
