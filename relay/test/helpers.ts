import { SELF, env } from "cloudflare:test";
import { runInDurableObject } from "cloudflare:test";

export const BASE = "https://relaunch.test";
export const PC_KEY = "test-pc-key";

let ipCounter = 0;
export const freshIp = () => `10.0.${Math.floor(++ipCounter / 250)}.${ipCounter % 250}`;

export type Handler = (method: string, params: any) => unknown | Promise<unknown>;

/** A fake PC: connects to /pc/connect and answers relay requests with the supplied handler. */
export class FakePc {
  requests: { method: string; params: any }[] = [];
  handler: Handler = () => ({ ok: true, phase: "running" });
  respond = true;
  private waiters = new Map<string, (f: any) => void>();
  private nextId = 1;

  private constructor(public ws: WebSocket) {
    ws.addEventListener("message", (e) => void this.onMessage(ws, e));
  }

  private async onMessage(ws: WebSocket, e: MessageEvent) {
    {
      const frame = JSON.parse(String(e.data));
      if (frame.type === "request") {
        this.requests.push({ method: frame.method, params: frame.params });
        if (!this.respond) return;
        try {
          const result = await this.handler(frame.method, frame.params);
          ws.send(JSON.stringify({ id: frame.id, type: "response", ok: true, result }));
        } catch (err) {
          ws.send(JSON.stringify({ id: frame.id, type: "response", ok: false, error: String(err), code: "boom" }));
        }
      } else if (frame.type === "response") {
        this.waiters.get(frame.id)?.(frame);
        this.waiters.delete(frame.id);
      } else if (frame.type === "event") {
        this.events.push(frame);
      }
    }
  }

  events: any[] = [];

  static async connect(key = PC_KEY): Promise<FakePc> {
    const res = await SELF.fetch(`${BASE}/pc/connect`, { headers: { Upgrade: "websocket", Authorization: `Bearer ${key}` } });
    if (res.status !== 101 || !res.webSocket) throw new Error(`connect failed: ${res.status}`);
    res.webSocket.accept();
    return new FakePc(res.webSocket);
  }

  event(name: string, data: unknown) {
    this.ws.send(JSON.stringify({ type: "event", name, data }));
  }

  /** PC → relay request, resolves with the response frame. */
  request(method: string, params: unknown): Promise<any> {
    const id = `pc-${this.nextId++}`;
    return new Promise((resolve) => {
      this.waiters.set(id, resolve);
      this.ws.send(JSON.stringify({ id, type: "request", method, params }));
    });
  }

  close() {
    try { this.ws.close(1000, "test over"); } catch { /* already closed */ }
  }
}

export async function sha256Hex(s: string): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Registers a pairing code through the fake PC and exchanges it as a phone would. */
export async function pairDevice(pc: FakePc, name = "Test phone", ip = freshIp()): Promise<{ token: string; deviceId: string }> {
  const code = `T${Math.random().toString(36).slice(2, 11).toUpperCase()}`;
  const reg = await pc.request("registerPairingCode", { codeHash: await sha256Hex(code), expiresAt: Date.now() + 300_000 });
  if (!reg.ok) throw new Error("register failed");
  const res = await SELF.fetch(`${BASE}/api/pair`, {
    method: "POST",
    headers: { "content-type": "application/json", "cf-connecting-ip": ip },
    body: JSON.stringify({ code, name }),
  });
  if (res.status !== 200) throw new Error(`pair failed: ${res.status}`);
  return res.json();
}

export const api = (token: string, path: string, init: RequestInit = {}) =>
  SELF.fetch(`${BASE}${path}`, { ...init, headers: { authorization: `Bearer ${token}`, ...(init.headers ?? {}) } });

export const sampleCatalogue = (iconHash: string | null = null) => ({
  apps: [
    { id: "aaa", name: "Jellyfin Server", favorite: true, trackable: true, iconHash, status: { running: true, processCount: 2, uptimeSeconds: 10, workingSetBytes: 1000 }, phase: "idle" },
    { id: "bbb", name: "Notepad", favorite: false, trackable: true, iconHash: null, status: { running: false, processCount: 0, uptimeSeconds: 0, workingSetBytes: 0 }, phase: "idle" },
  ],
  favorites: ["aaa"],
});

export async function hub() {
  const ns = (env as unknown as Env).PC_HUB;
  return ns.get(ns.idFromName("home"));
}

export async function withHub<T>(fn: (instance: any) => T | Promise<T>): Promise<T> {
  const stub = (await hub()) as any;
  const run = runInDurableObject as unknown as (s: any, cb: (i: any) => unknown) => Promise<T>;
  return run(stub, (instance) => fn(instance));
}

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export async function waitFor(cond: () => boolean | Promise<boolean>, timeoutMs = 2000): Promise<void> {
  const start = Date.now();
  while (!(await cond())) {
    if (Date.now() - start > timeoutMs) throw new Error("waitFor timed out");
    await sleep(10);
  }
}
