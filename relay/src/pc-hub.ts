import { DurableObject } from "cloudflare:workers";
import type { AppInfo, Catalogue, Frame, SessionState } from "./protocol";
import { HttpError, base64ToBytes, json, randomHex, randomToken, safeEqual, sha256Hex } from "./util";

const PC_TAG = "pc";
const LIVE_TAG = "live";
const MAX_ICON_BYTES = 64 * 1024;
const PAIR_ATTEMPTS = 5;
const PAIR_WINDOW_MS = 10 * 60_000;
const ACTION_LIMIT = 30;
const ACTION_WINDOW_MS = 60_000;
const AUTH_TIMEOUT_MS = 15_000;
const LAST_SEEN_WRITE_INTERVAL_MS = 60_000;
const ACTIONS = new Set(["start", "stop", "restart"]);

interface Pending {
  resolve: (frame: Frame) => void;
  reject: (err: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

interface DeviceRow {
  id: string;
  name: string;
  last_seen: number | null;
  [key: string]: SqlStorageValue;
}

/** Single instance ("home") owning all relay state and the PC WebSocket. */
export class PcHub extends DurableObject<Env> {
  /** Silence after which the PC counts as offline. Mutable so tests can shorten it. */
  heartbeatTimeoutMs = 90_000;
  requestTimeoutMs = 25_000;

  private pending = new Map<string, Pending>();
  private lastBroadcastPc: string | null = null;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
    ctx.blockConcurrencyWhile(async () => this.migrate());
  }

  // ───────────────────────────── storage ─────────────────────────────

  private get sql(): SqlStorage {
    return this.ctx.storage.sql;
  }

  private migrate(): void {
    this.sql.exec(`
      CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, created_at INTEGER NOT NULL, last_seen INTEGER);
      CREATE TABLE IF NOT EXISTS pairing (code_hash TEXT PRIMARY KEY, expires_at INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS icons (hash TEXT PRIMARY KEY, data BLOB NOT NULL);
      CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, device_id TEXT NOT NULL, app_id TEXT NOT NULL, action TEXT NOT NULL, result TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS rate (key TEXT PRIMARY KEY, window_start INTEGER NOT NULL, count INTEGER NOT NULL);
    `);
  }

  private kvGet<T>(key: string): T | null {
    const rows = this.sql.exec<{ value: string }>("SELECT value FROM kv WHERE key = ?", key).toArray();
    return rows.length ? (JSON.parse(rows[0].value) as T) : null;
  }

  private kvSet(key: string, value: unknown): void {
    this.sql.exec("INSERT INTO kv (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, JSON.stringify(value));
  }

  /** Fixed-window rate limit. Returns false when the limit is exceeded. */
  private hit(key: string, limit: number, windowMs: number): boolean {
    const now = Date.now();
    const rows = this.sql.exec<{ window_start: number; count: number }>("SELECT window_start, count FROM rate WHERE key = ?", key).toArray();
    if (!rows.length || now - rows[0].window_start >= windowMs) {
      this.sql.exec("INSERT INTO rate (key, window_start, count) VALUES (?, ?, 1) ON CONFLICT(key) DO UPDATE SET window_start = excluded.window_start, count = 1", key, now);
      return true;
    }
    if (rows[0].count >= limit) return false;
    this.sql.exec("UPDATE rate SET count = count + 1 WHERE key = ?", key);
    return true;
  }

  // ───────────────────────────── state ─────────────────────────────

  private pcSocket(): WebSocket | undefined {
    return this.ctx.getWebSockets(PC_TAG).find((s) => s.readyState === WebSocket.OPEN);
  }

  private lastSeen(): number {
    return this.kvGet<number>("lastSeen") ?? 0;
  }

  private pcState(): "online" | "offline" {
    return this.pcSocket() && Date.now() - this.lastSeen() < this.heartbeatTimeoutMs ? "online" : "offline";
  }

  private catalogue(): Catalogue {
    return this.kvGet<Catalogue>("catalogue") ?? { apps: [], favorites: [] };
  }

  private session(): SessionState {
    return this.kvGet<SessionState>("session") ?? "unknown";
  }

  private statusBody() {
    return { pc: this.pcState(), session: this.session(), pcName: this.kvGet<string>("pcName") ?? "", lastSeen: this.lastSeen() || null };
  }

  private async touch(): Promise<void> {
    this.kvSet("lastSeen", Date.now());
    await this.ctx.storage.setAlarm(Date.now() + this.heartbeatTimeoutMs + 500);
    this.refreshPcState();
  }

  /** Broadcast online/offline transitions to live sockets. */
  private refreshPcState(): void {
    const state = this.pcState();
    if (state === this.lastBroadcastPc) return;
    this.lastBroadcastPc = state;
    this.broadcast({ type: "pc", state, session: this.session(), pcName: this.kvGet<string>("pcName") ?? "" });
  }

  async alarm(): Promise<void> {
    this.refreshPcState();
  }

  // ───────────────────────────── HTTP entry ─────────────────────────────

  async fetch(request: Request): Promise<Response> {
    try {
      return await this.route(request);
    } catch (e) {
      if (e instanceof HttpError) return json({ error: e.code, message: e.message }, e.status);
      console.error("relay error", e);
      return json({ error: "internal" }, 500);
    }
  }

  private async route(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    if (path === "/pc/connect") return this.pcConnect(request);
    if (path === "/api/live") return this.liveConnect(request);
    if (path === "/api/pair" && method === "POST") return this.pair(request);

    const device = await this.authenticate(request);
    if (!device) return json({ error: "unauthorized" }, 401);

    if (path === "/api/status" && method === "GET") return json(this.statusBody());
    if (path === "/api/apps" && method === "GET") return this.listApps(url);
    if (path === "/api/activity" && method === "GET") return this.listActivity(url);
    if (path === "/api/favorites" && method === "PUT") return this.putFavorites(request);

    const m = /^\/api\/apps\/([A-Za-z0-9_-]{1,64})\/(icon|start|stop|restart)$/.exec(path);
    if (m) {
      const [, appId, what] = m;
      if (what === "icon" && method === "GET") return this.icon(appId, request);
      if (ACTIONS.has(what) && method === "POST") return this.action(device, appId, what);
    }
    return json({ error: "notFound" }, 404);
  }

  // ───────────────────────────── phone auth ─────────────────────────────

  private bearer(request: Request): string | null {
    const h = request.headers.get("authorization");
    const m = h && /^Bearer\s+(.+)$/i.exec(h);
    return m ? m[1].trim() : null;
  }

  private async deviceForToken(token: string): Promise<DeviceRow | null> {
    const hash = await sha256Hex(token);
    const rows = this.sql.exec<DeviceRow>("SELECT id, name, last_seen FROM devices WHERE token_hash = ?", hash).toArray();
    if (!rows.length) return null;
    const row = rows[0];
    const now = Date.now();
    if (!row.last_seen || now - row.last_seen > LAST_SEEN_WRITE_INTERVAL_MS) {
      this.sql.exec("UPDATE devices SET last_seen = ? WHERE id = ?", now, row.id);
    }
    return row;
  }

  private async authenticate(request: Request): Promise<DeviceRow | null> {
    const token = this.bearer(request);
    return token ? this.deviceForToken(token) : null;
  }

  // ───────────────────────────── pairing ─────────────────────────────

  private async pair(request: Request): Promise<Response> {
    const ip = request.headers.get("cf-connecting-ip") ?? "unknown";
    if (!this.hit(`pair:${ip}`, PAIR_ATTEMPTS, PAIR_WINDOW_MS)) throw new HttpError(429, "tooManyAttempts", "Too many pairing attempts. Try again later.");

    const body = (await request.json().catch(() => null)) as { code?: string; name?: string } | null;
    const code = (body?.code ?? "").toUpperCase().replace(/[^A-Z0-9]/g, "");
    if (!code) throw new HttpError(400, "badRequest", "Missing code.");

    const now = Date.now();
    this.sql.exec("DELETE FROM pairing WHERE expires_at < ?", now);
    const codeHash = await sha256Hex(code);
    const rows = this.sql.exec("SELECT code_hash FROM pairing WHERE code_hash = ? AND expires_at >= ?", codeHash, now).toArray();
    if (!rows.length) throw new HttpError(403, "invalidCode", "That code is wrong or has expired.");
    this.sql.exec("DELETE FROM pairing WHERE code_hash = ?", codeHash);

    const token = randomToken(32);
    const deviceId = randomHex(8);
    const name = (body?.name ?? "").trim().slice(0, 60) || "Phone";
    this.sql.exec("INSERT INTO devices (id, name, token_hash, created_at, last_seen) VALUES (?, ?, ?, ?, ?)", deviceId, name, await sha256Hex(token), now, now);

    this.sendToPc({ type: "event", name: "devicePaired", data: { deviceId, name } });
    return json({ token, deviceId });
  }

  // ───────────────────────────── phone API ─────────────────────────────

  private listApps(url: URL): Response {
    const cat = this.catalogue();
    let apps: AppInfo[] = cat.apps;
    if (url.searchParams.get("favorites") === "true") {
      const byId = new Map(cat.apps.map((a) => [a.id, a]));
      apps = cat.favorites.map((id) => byId.get(id)).filter((a): a is AppInfo => !!a);
    }
    const pc = this.pcState();
    return json({ pc, stale: pc !== "online", apps });
  }

  private icon(appId: string, request: Request): Response {
    const app = this.catalogue().apps.find((a) => a.id === appId);
    if (!app?.iconHash) return json({ error: "notFound" }, 404);
    const etag = `"${app.iconHash}"`;
    const headers = { etag, "cache-control": "private, max-age=31536000, immutable" };
    if (request.headers.get("if-none-match") === etag) return new Response(null, { status: 304, headers });
    const rows = this.sql.exec<{ data: ArrayBuffer }>("SELECT data FROM icons WHERE hash = ?", app.iconHash).toArray();
    if (!rows.length) return json({ error: "notFound" }, 404);
    return new Response(rows[0].data, { headers: { ...headers, "content-type": "image/png" } });
  }

  private listActivity(url: URL): Response {
    const limit = Math.min(Math.max(parseInt(url.searchParams.get("limit") ?? "50", 10) || 50, 1), 200);
    const names = new Map(this.catalogue().apps.map((a) => [a.id, a.name]));
    const rows = this.sql
      .exec<{ ts: number; device_id: string; device_name: string | null; app_id: string; action: string; result: string }>(
        `SELECT a.ts, a.device_id, d.name AS device_name, a.app_id, a.action, a.result
         FROM activity a LEFT JOIN devices d ON d.id = a.device_id ORDER BY a.id DESC LIMIT ?`,
        limit,
      )
      .toArray();
    return json({
      activity: rows.map((r) => ({
        time: r.ts,
        deviceId: r.device_id,
        deviceName: r.device_name ?? "Unknown device",
        appId: r.app_id,
        appName: names.get(r.app_id) ?? r.app_id,
        action: r.action,
        result: r.result,
      })),
    });
  }

  private async putFavorites(request: Request): Promise<Response> {
    const body = (await request.json().catch(() => null)) as { ids?: unknown } | null;
    if (!body || !Array.isArray(body.ids) || !body.ids.every((i) => typeof i === "string")) throw new HttpError(400, "badRequest", "Expected { ids: string[] }.");
    const ids = body.ids as string[];
    await this.callPc("setFavorites", { ids });
    const cat = this.catalogue();
    const known = new Set(cat.apps.map((a) => a.id));
    cat.favorites = [...new Set(ids)].filter((id) => known.has(id));
    for (const a of cat.apps) a.favorite = cat.favorites.includes(a.id);
    this.kvSet("catalogue", cat);
    this.broadcast({ type: "catalogue" });
    return json({ ok: true });
  }

  private async action(device: DeviceRow, appId: string, action: string): Promise<Response> {
    if (!this.catalogue().apps.some((a) => a.id === appId)) throw new HttpError(404, "unknownApp", "Unknown app.");
    if (!this.hit(`act:${device.id}`, ACTION_LIMIT, ACTION_WINDOW_MS)) throw new HttpError(429, "tooManyActions", "Slow down a little.");

    let result: { ok: boolean; phase?: string; errorCode?: string | null; message?: string | null };
    try {
      result = await this.callPc(action, { appId, device: device.name });
    } catch (e) {
      if (e instanceof HttpError) {
        this.logActivity(device.id, appId, action, e.code);
        throw e;
      }
      throw e;
    }
    this.logActivity(device.id, appId, action, result.ok ? "ok" : (result.errorCode ?? "failed"));
    const status = result.ok ? 200 : result.errorCode === "busy" ? 409 : 422;
    return json(result, status);
  }

  private logActivity(deviceId: string, appId: string, action: string, result: string): void {
    this.sql.exec("INSERT INTO activity (ts, device_id, app_id, action, result) VALUES (?, ?, ?, ?, ?)", Date.now(), deviceId, appId, action, result);
    this.sql.exec("DELETE FROM activity WHERE id <= (SELECT MAX(id) FROM activity) - 500");
  }

  // ───────────────────────────── PC socket ─────────────────────────────

  private async pcConnect(request: Request): Promise<Response> {
    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") throw new HttpError(426, "upgradeRequired");
    const key = this.env.PC_KEY;
    if (!key) throw new HttpError(503, "notConfigured", "PC_KEY is not set on the Worker.");
    const presented = this.bearer(request);
    if (!presented || !(await safeEqual(presented, key))) throw new HttpError(401, "unauthorized");

    for (const old of this.ctx.getWebSockets(PC_TAG)) {
      try { old.close(1000, "replaced"); } catch { /* already closed */ }
    }
    this.rejectAllPending("pcOffline");

    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], [PC_TAG]);
    await this.touch();
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  private sendToPc(frame: Frame): boolean {
    const ws = this.pcSocket();
    if (!ws) return false;
    try {
      ws.send(JSON.stringify(frame));
      return true;
    } catch {
      return false;
    }
  }

  /** Relay a request to the PC and wait for its response. Throws HttpError 503 / 504 / 502. */
  private callPc(method: string, params: unknown): Promise<any> {
    if (this.pcState() !== "online") throw new HttpError(503, "pcOffline", "The PC is offline.");
    const id = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new HttpError(504, "timeout", "The PC did not answer in time."));
      }, this.requestTimeoutMs);
      this.pending.set(id, {
        timer,
        resolve: (frame) => {
          if (frame.ok) resolve(frame.result);
          else reject(new HttpError(502, frame.code ?? "pcError", frame.error ?? "The PC reported an error."));
        },
        reject,
      });
      if (!this.sendToPc({ id, type: "request", method, params })) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(new HttpError(503, "pcOffline", "The PC is offline."));
      }
    });
  }

  private rejectAllPending(code: string): void {
    for (const [id, p] of this.pending) {
      clearTimeout(p.timer);
      p.reject(new HttpError(503, code, "The PC went offline."));
      this.pending.delete(id);
    }
  }

  // ───────────────────────────── live sockets ─────────────────────────────

  private liveConnect(request: Request): Response {
    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") throw new HttpError(426, "upgradeRequired");
    const now = Date.now();
    // Sockets that never authenticated are dropped.
    for (const ws of this.ctx.getWebSockets(LIVE_TAG)) {
      const att = ws.deserializeAttachment() as { deviceId?: string; createdAt: number } | null;
      if (!att?.deviceId && now - (att?.createdAt ?? 0) > AUTH_TIMEOUT_MS) {
        try { ws.close(4401, "unauthorized"); } catch { /* ignore */ }
      }
    }
    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], [LIVE_TAG]);
    pair[1].serializeAttachment({ createdAt: now });
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  private broadcast(message: unknown): void {
    const text = JSON.stringify(message);
    for (const ws of this.ctx.getWebSockets(LIVE_TAG)) {
      const att = ws.deserializeAttachment() as { deviceId?: string } | null;
      if (!att?.deviceId) continue;
      try { ws.send(text); } catch { /* socket closing */ }
    }
  }

  private closeLiveFor(deviceId: string): void {
    for (const ws of this.ctx.getWebSockets(LIVE_TAG)) {
      const att = ws.deserializeAttachment() as { deviceId?: string } | null;
      if (att?.deviceId === deviceId) {
        try { ws.close(4401, "revoked"); } catch { /* ignore */ }
      }
    }
  }

  // ───────────────────────────── hibernation handlers ─────────────────────────────

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string") return;
    if (this.ctx.getTags(ws).includes(PC_TAG)) await this.onPcMessage(ws, message);
    else await this.onLiveMessage(ws, message);
  }

  async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    try { ws.close(code, reason); } catch { /* already closed */ }
    this.afterSocketGone(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    this.afterSocketGone(ws);
  }

  private afterSocketGone(ws: WebSocket): void {
    if (!this.ctx.getTags(ws).includes(PC_TAG)) return;
    // The closing socket may still be listed; only open sockets count.
    const others = this.ctx.getWebSockets(PC_TAG).filter((s) => s.readyState === WebSocket.OPEN);
    if (others.length === 0) {
      this.rejectAllPending("pcOffline");
      this.lastBroadcastPc = "offline";
      this.broadcast({ type: "pc", state: "offline", session: this.session(), pcName: this.kvGet<string>("pcName") ?? "" });
    }
  }

  private async onLiveMessage(ws: WebSocket, text: string): Promise<void> {
    const att = ws.deserializeAttachment() as { deviceId?: string; createdAt: number } | null;
    if (att?.deviceId) return; // already authenticated; nothing is expected from the phone
    let msg: { token?: string } | null = null;
    try { msg = JSON.parse(text); } catch { /* handled below */ }
    const device = msg?.token ? await this.deviceForToken(msg.token) : null;
    if (!device) {
      ws.close(4401, "unauthorized");
      return;
    }
    ws.serializeAttachment({ deviceId: device.id, createdAt: att?.createdAt ?? Date.now() });
    ws.send(JSON.stringify({ type: "hello", ...this.statusBody() }));
  }

  private async onPcMessage(ws: WebSocket, text: string): Promise<void> {
    let frame: Frame;
    try { frame = JSON.parse(text); } catch { return; }
    await this.touch();

    if (frame.type === "response" && frame.id) {
      const p = this.pending.get(frame.id);
      if (p) {
        clearTimeout(p.timer);
        this.pending.delete(frame.id);
        p.resolve(frame);
      }
    } else if (frame.type === "request" && frame.id) {
      await this.onPcRequest(ws, frame);
    } else if (frame.type === "event" && frame.name) {
      await this.onPcEvent(frame.name, frame.data);
    }
  }

  private async onPcRequest(ws: WebSocket, frame: Frame): Promise<void> {
    const reply = (ok: boolean, payload: { result?: unknown; error?: string; code?: string }) =>
      ws.send(JSON.stringify({ id: frame.id, type: "response", ok, ...payload }));
    try {
      const p = frame.params ?? {};
      switch (frame.method) {
        case "registerPairingCode": {
          const hash = String(p.codeHash ?? "");
          const expiresAt = Number(p.expiresAt);
          if (!/^[0-9a-f]{64}$/.test(hash) || !Number.isFinite(expiresAt)) throw new HttpError(400, "badRequest");
          const capped = Math.min(expiresAt, Date.now() + 10 * 60_000);
          this.sql.exec("INSERT INTO pairing (code_hash, expires_at) VALUES (?, ?) ON CONFLICT(code_hash) DO UPDATE SET expires_at = excluded.expires_at", hash, capped);
          reply(true, { result: { ok: true } });
          break;
        }
        case "revokeDevice": {
          const id = String(p.deviceId ?? "");
          this.sql.exec("DELETE FROM devices WHERE id = ?", id);
          this.closeLiveFor(id);
          reply(true, { result: { ok: true } });
          break;
        }
        case "listDevices": {
          const rows = this.sql.exec<{ id: string; name: string; created_at: number; last_seen: number | null }>("SELECT id, name, created_at, last_seen FROM devices ORDER BY created_at").toArray();
          reply(true, { result: rows.map((r) => ({ id: r.id, name: r.name, createdAt: r.created_at, lastSeen: r.last_seen })) });
          break;
        }
        default:
          reply(false, { error: `Unknown method '${frame.method}'.`, code: "unknownMethod" });
      }
    } catch (e) {
      reply(false, { error: e instanceof Error ? e.message : "error", code: e instanceof HttpError ? e.code : "error" });
    }
  }

  private async onPcEvent(name: string, data: any): Promise<void> {
    switch (name) {
      case "hello":
      case "heartbeat":
        if (data?.pcName) this.kvSet("pcName", String(data.pcName));
        if (data?.session) this.setSession(data.session);
        break;
      case "catalogue":
        await this.onCatalogue(data as Catalogue);
        break;
      case "status": {
        const cat = this.catalogue();
        const app = cat.apps.find((a) => a.id === data?.appId);
        if (app && data.status) {
          app.status = data.status;
          this.kvSet("catalogue", cat);
        }
        this.broadcast({ type: "status", appId: data?.appId, status: data?.status });
        break;
      }
      case "action": {
        const cat = this.catalogue();
        const app = cat.apps.find((a) => a.id === data?.appId);
        if (app && data.phase) {
          app.phase = data.phase;
          this.kvSet("catalogue", cat);
        }
        this.broadcast({ type: "action", appId: data?.appId, kind: data?.kind, phase: data?.phase, message: data?.message ?? null });
        break;
      }
      case "session":
        if (data?.state) this.setSession(data.state);
        break;
    }
  }

  private setSession(state: SessionState): void {
    if (this.session() === state) return;
    this.kvSet("session", state);
    this.broadcast({ type: "session", state });
  }

  private async onCatalogue(cat: Catalogue): Promise<void> {
    if (!cat || !Array.isArray(cat.apps)) return;
    this.kvSet("catalogue", { apps: cat.apps, favorites: Array.isArray(cat.favorites) ? cat.favorites : [] });
    this.broadcast({ type: "catalogue" });
    await this.fetchMissingIcons(cat.apps);
  }

  private async fetchMissingIcons(apps: AppInfo[]): Promise<void> {
    const have = new Set(this.sql.exec<{ hash: string }>("SELECT hash FROM icons").toArray().map((r) => r.hash));
    const wanted = new Map<string, string>(); // hash -> app id
    for (const a of apps) if (a.iconHash && !have.has(a.iconHash) && !wanted.has(a.iconHash)) wanted.set(a.iconHash, a.id);

    const queue = [...wanted.entries()];
    const worker = async () => {
      for (let item = queue.shift(); item; item = queue.shift()) {
        const [hash, appId] = item;
        try {
          const res = await this.callPc("getIcon", { id: appId });
          const bytes = res?.png ? base64ToBytes(String(res.png)) : null;
          if (bytes && bytes.length > 0 && bytes.length <= MAX_ICON_BYTES) {
            this.sql.exec("INSERT OR REPLACE INTO icons (hash, data) VALUES (?, ?)", hash, bytes);
          }
        } catch (e) {
          if (e instanceof HttpError && e.code === "pcOffline") return;
          // An icon that cannot be fetched is simply missing.
        }
      }
    };
    await Promise.all([worker(), worker(), worker()]);
    this.broadcast({ type: "icons" });
  }
}
