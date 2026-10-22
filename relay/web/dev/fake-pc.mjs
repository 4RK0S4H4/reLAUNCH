// A fake PC for developing and screenshotting the PWA against `wrangler dev`.
// Usage: node web/dev/fake-pc.mjs [relayUrl] [pcKey]   (defaults: http://127.0.0.1:8787, dev-key)
import { createHash } from "node:crypto";
import zlib from "node:zlib";
import http from "node:http";
import WebSocket from "ws";

const base = process.argv[2] ?? "http://127.0.0.1:8787";
const key = process.argv[3] ?? "dev-key";
const CODE = process.env.PAIR_CODE ?? "SHOT123456";

function png(color) {
  const w = 96, h = 96, raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (w * 4 + 1)] = 0;
    for (let x = 0; x < w; x++) {
      const dx = Math.max(Math.abs(x - w / 2) - 28, 0), dy = Math.max(Math.abs(y - h / 2) - 28, 0);
      const inside = Math.hypot(dx, dy) < 14;
      const o = y * (w * 4 + 1) + 1 + x * 4;
      raw[o] = color[0]; raw[o + 1] = color[1]; raw[o + 2] = color[2]; raw[o + 3] = inside ? 255 : 0;
    }
  }
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = (buf) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type), data]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([len, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", zlib.deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

const names = ["Jellyfin Server", "Notepad", "Calculator", "Google Chrome", "Visual Studio Code", "Steam", "Spotify", "Plex Media Server", "Microsoft Edge", "Windows Terminal", "Paint", "7-Zip File Manager", "Discord", "OBS Studio"];
const colors = [[20, 184, 166], [96, 165, 250], [244, 114, 182], [250, 204, 21], [129, 140, 248], [74, 222, 128], [251, 146, 60], [248, 113, 113]];
const apps = names.map((name, i) => ({
  id: createHash("sha1").update(name).digest("hex").slice(0, 12),
  name, favorite: i < 5, trackable: name !== "7-Zip File Manager", iconHash: `ic${i}`,
  status: { running: i % 3 === 0, processCount: i % 3 === 0 ? 2 : 0, uptimeSeconds: i % 3 === 0 ? 4000 + i * 977 : 0, workingSetBytes: i % 3 === 0 ? 180e6 + i * 12e6 : 0 },
  phase: "idle",
}));
let favorites = apps.filter((a) => a.favorite).map((a) => a.id);
const icons = Object.fromEntries(apps.map((a, i) => [a.id, png(colors[i % colors.length])]));
let session = "active";

const wsUrl = base.replace(/^http/, "ws") + "/pc/connect";
let ws;
const send = (o) => ws && ws.readyState === 1 && ws.send(JSON.stringify(o));
const event = (name, data) => send({ type: "event", name, data });
const pushCatalogue = () => event("catalogue", { apps: apps.map((a) => ({ ...a, favorite: favorites.includes(a.id) })), favorites });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function connect() {
ws = new WebSocket(wsUrl, { headers: { Authorization: `Bearer ${key}` } });
ws.on("open", async () => {
  console.log("fake PC connected");
  event("hello", { pcName: "LIVINGROOM-PC", session });
  for (const code of [CODE, ...Array.from({ length: 8 }, (_, i) => `SHOT00000${i + 1}`)]) {
    const hash = createHash("sha256").update(code).digest("hex");
    send({ id: "reg" + code, type: "request", method: "registerPairingCode", params: { codeHash: hash, expiresAt: Date.now() + 9 * 60_000 } });
  }
  pushCatalogue();
  setInterval(() => event("heartbeat", { pcName: "LIVINGROOM-PC", session }), 30_000);
});

ws.on("message", async (raw) => {
  const f = JSON.parse(String(raw));
  if (f.type !== "request") return;
  const reply = (result) => send({ id: f.id, type: "response", ok: true, result });
  const app = apps.find((a) => a.id === f.params?.appId);
  switch (f.method) {
    case "getIcon": return reply({ png: icons[f.params.id]?.toString("base64") });
    case "setFavorites": favorites = f.params.ids; reply({ ok: true }); return pushCatalogue();
    case "start": case "stop": case "restart": {
      const emit = (phase) => { app.phase = phase; event("action", { appId: app.id, kind: f.method, phase, message: null }); };
      if (f.method !== "start") { emit("stopping"); await sleep(700); app.status = { ...app.status, running: false, processCount: 0 }; event("status", { appId: app.id, status: app.status }); }
      if (f.method !== "stop") { emit("starting"); await sleep(700); app.status = { running: true, processCount: 2, uptimeSeconds: 1, workingSetBytes: 150e6 }; event("status", { appId: app.id, status: app.status }); }
      emit(f.method === "stop" ? "stopped" : "running");
      return reply({ ok: true, phase: app.phase, errorCode: null, message: null });
    }
  }
  send({ id: f.id, type: "response", ok: false, error: "unknown", code: "unknownMethod" });
});
}
connect();
// Dev controls over stdin: "lock", "unlock", "nosession", "quit".
process.stdin.on("data", (d) => {
  const cmd = String(d).trim();
  if (cmd === "lock" || cmd === "unlock" || cmd === "nosession") { session = cmd === "lock" ? "locked" : cmd === "unlock" ? "active" : "noSession"; event("session", { state: session }); }
  if (cmd === "quit") process.exit(0);
});


// Control server for screenshot runs: /session/<state>, /disconnect, /connect.
http.createServer((req, res) => {
  const [, what, arg] = req.url.split("/");
  if (what === "session") { session = arg; event("session", { state: session }); }
  if (what === "disconnect") ws.close();
  if (what === "connect") connect();
  res.end("ok");
}).listen(8799, "127.0.0.1");
