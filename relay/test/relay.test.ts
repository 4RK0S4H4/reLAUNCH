import { SELF } from "cloudflare:test";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { BASE, FakePc, api, freshIp, pairDevice, sampleCatalogue, sha256Hex, sleep, waitFor, withHub } from "./helpers";

const open: FakePc[] = [];
async function connectPc(): Promise<FakePc> {
  const pc = await FakePc.connect();
  open.push(pc);
  return pc;
}

beforeEach(async () => {
  await withHub((h) => {
    h.requestTimeoutMs = 25_000;
    h.heartbeatTimeoutMs = 90_000;
  });
});

afterEach(async () => {
  while (open.length) open.pop()!.close();
  await sleep(20);
});

describe("basics and auth", () => {
  it("answers the health check without auth", async () => {
    const res = await SELF.fetch(`${BASE}/api/health`);
    expect(res.status).toBe(200);
  });

  it("rejects phone calls without a valid token", async () => {
    expect((await SELF.fetch(`${BASE}/api/status`)).status).toBe(401);
    expect((await api("nope", "/api/status")).status).toBe(401);
  });

  it("rejects a PC connection with the wrong key and without upgrade", async () => {
    const bad = await SELF.fetch(`${BASE}/pc/connect`, { headers: { Upgrade: "websocket", Authorization: "Bearer wrong" } });
    expect(bad.status).toBe(401);
    const none = await SELF.fetch(`${BASE}/pc/connect`, { headers: { Authorization: "Bearer test-pc-key" } });
    expect(none.status).toBe(426);
  });
});

describe("pairing", () => {
  it("exchanges a one-time code for a working token, once", async () => {
    const pc = await connectPc();
    const code = "ABCD2345EF";
    await pc.request("registerPairingCode", { codeHash: await sha256Hex(code), expiresAt: Date.now() + 300_000 });
    const ip = freshIp();
    const pair = () => SELF.fetch(`${BASE}/api/pair`, { method: "POST", headers: { "cf-connecting-ip": ip }, body: JSON.stringify({ code: "abcd-2345-ef", name: "Pixel" }) });

    const ok = await pair();
    expect(ok.status).toBe(200);
    const { token, deviceId } = (await ok.json()) as any;
    expect(token.length).toBeGreaterThan(30);
    expect((await api(token, "/api/status")).status).toBe(200);
    await waitFor(() => pc.events.some((e) => e.name === "devicePaired" && e.data.deviceId === deviceId));

    expect((await pair()).status).toBe(403);
  });

  it("rejects expired codes", async () => {
    const pc = await connectPc();
    await pc.request("registerPairingCode", { codeHash: await sha256Hex("OLDCODE1"), expiresAt: Date.now() - 1 });
    const res = await SELF.fetch(`${BASE}/api/pair`, { method: "POST", headers: { "cf-connecting-ip": freshIp() }, body: JSON.stringify({ code: "OLDCODE1" }) });
    expect(res.status).toBe(403);
  });

  it("limits pairing attempts to 5 per IP", async () => {
    const ip = freshIp();
    const attempt = () => SELF.fetch(`${BASE}/api/pair`, { method: "POST", headers: { "cf-connecting-ip": ip }, body: JSON.stringify({ code: "WRONG123" }) });
    for (let i = 0; i < 5; i++) expect((await attempt()).status).toBe(403);
    expect((await attempt()).status).toBe(429);
    // another IP is unaffected
    const other = await SELF.fetch(`${BASE}/api/pair`, { method: "POST", headers: { "cf-connecting-ip": freshIp() }, body: JSON.stringify({ code: "WRONG123" }) });
    expect(other.status).toBe(403);
  });

  it("revoking a device invalidates its token and lists devices", async () => {
    const pc = await connectPc();
    const { token, deviceId } = await pairDevice(pc, "Old phone");
    const list = await pc.request("listDevices", {});
    expect(list.result.some((d: any) => d.id === deviceId && d.name === "Old phone")).toBe(true);

    await pc.request("revokeDevice", { deviceId });
    expect((await api(token, "/api/status")).status).toBe(401);
  });
});

describe("status, catalogue and offline handling", () => {
  it("reports online while the PC socket is open and offline after it closes", async () => {
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    pc.event("hello", { pcName: "HOMEPC", session: "locked" });
    await waitFor(async () => ((await (await api(token, "/api/status")).json()) as any).session === "locked");

    const online = (await (await api(token, "/api/status")).json()) as any;
    expect(online).toMatchObject({ pc: "online", session: "locked", pcName: "HOMEPC" });

    pc.close();
    await waitFor(async () => ((await (await api(token, "/api/status")).json()) as any).pc === "offline");
  });

  it("marks the PC offline after missed heartbeats", async () => {
    await withHub((h) => (h.heartbeatTimeoutMs = 250));
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    expect(((await (await api(token, "/api/status")).json()) as any).pc).toBe("online");
    await sleep(400);
    expect(((await (await api(token, "/api/status")).json()) as any).pc).toBe("offline");
    pc.event("heartbeat", { session: "active" });
    await waitFor(async () => ((await (await api(token, "/api/status")).json()) as any).pc === "online");
  });

  it("serves the cached catalogue, flagged stale, when the PC is offline", async () => {
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue());
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).apps.length === 2);

    const live = (await (await api(token, "/api/apps?favorites=true")).json()) as any;
    expect(live).toMatchObject({ pc: "online", stale: false });
    expect(live.apps.map((a: any) => a.name)).toEqual(["Jellyfin Server"]);

    pc.close();
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).stale === true);
    const stale = (await (await api(token, "/api/apps")).json()) as any;
    expect(stale.apps).toHaveLength(2);
  });

  it("applies status and action events to the cached catalogue", async () => {
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue());
    pc.event("status", { appId: "bbb", status: { running: true, processCount: 1, uptimeSeconds: 1, workingSetBytes: 5 } });
    pc.event("action", { appId: "bbb", kind: "restart", phase: "stopping", message: null });
    await waitFor(async () => {
      const b = ((await (await api(token, "/api/apps")).json()) as any).apps.find((a: any) => a.id === "bbb");
      return b?.status.running === true && b.phase === "stopping";
    });
  });
});

describe("icons", () => {
  const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]);
  const b64 = (u: Uint8Array) => btoa(String.fromCharCode(...u));

  it("fetches missing icons from the PC once and serves them with long cache headers", async () => {
    const pc = await connectPc();
    pc.handler = (method, params) => (method === "getIcon" ? { png: b64(png) } : { ok: true });
    const { token } = await pairDevice(pc);
    const hash = `h${Math.random().toString(16).slice(2, 10)}`;

    pc.event("catalogue", sampleCatalogue(hash));
    await waitFor(async () => (await api(token, "/api/apps/aaa/icon")).status === 200);

    const res = await api(token, "/api/apps/aaa/icon");
    expect(res.headers.get("content-type")).toBe("image/png");
    expect(res.headers.get("cache-control")).toContain("max-age=31536000");
    expect(new Uint8Array(await res.arrayBuffer())).toEqual(png);

    const cached = await api(token, "/api/apps/aaa/icon", { headers: { "if-none-match": `"${hash}"` } });
    expect(cached.status).toBe(304);

    // A second catalogue push with the same hash does not ask for the icon again.
    const before = pc.requests.filter((r) => r.method === "getIcon").length;
    pc.event("catalogue", sampleCatalogue(hash));
    await sleep(100);
    expect(pc.requests.filter((r) => r.method === "getIcon").length).toBe(before);
  });

  it("drops icons larger than 64 KB", async () => {
    const pc = await connectPc();
    pc.handler = (method) => (method === "getIcon" ? { png: b64(new Uint8Array(70_000)) } : { ok: true });
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue(`big${Math.random().toString(16).slice(2, 8)}`));
    await waitFor(() => pc.requests.some((r) => r.method === "getIcon"));
    await sleep(100);
    expect((await api(token, "/api/apps/aaa/icon")).status).toBe(404);
  });
});

describe("actions", () => {
  it("relays start / stop / restart to the PC and records activity", async () => {
    const pc = await connectPc();
    pc.handler = (method, params) => ({ ok: true, phase: method === "stop" ? "stopped" : "running", errorCode: null, message: null });
    const { token } = await pairDevice(pc, "Kitchen tablet");
    pc.event("catalogue", sampleCatalogue());
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).apps.length === 2);

    const res = await api(token, "/api/apps/aaa/restart", { method: "POST" });
    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ ok: true, phase: "running" });
    expect(pc.requests.at(-1)).toEqual({ method: "restart", params: { appId: "aaa", device: "Kitchen tablet" } });

    await api(token, "/api/apps/bbb/stop", { method: "POST" });
    const activity = (await (await api(token, "/api/activity?limit=5")).json()) as any;
    expect(activity.activity[0]).toMatchObject({ action: "stop", appName: "Notepad", deviceName: "Kitchen tablet", result: "ok" });
    expect(activity.activity[1]).toMatchObject({ action: "restart", appName: "Jellyfin Server" });
  });

  it("passes PC-side failures through with a 422", async () => {
    const pc = await connectPc();
    pc.handler = () => ({ ok: false, phase: "failed", errorCode: "noSession", message: "Nobody is logged in on the PC." });
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue());
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).apps.length === 2);
    const res = await api(token, "/api/apps/aaa/start", { method: "POST" });
    expect(res.status).toBe(422);
    expect(await res.json()).toMatchObject({ errorCode: "noSession" });
  });

  it("answers 503 pcOffline when no PC is connected", async () => {
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue());
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).apps.length === 2);
    pc.close();
    await waitFor(async () => ((await (await api(token, "/api/status")).json()) as any).pc === "offline");

    const res = await api(token, "/api/apps/aaa/start", { method: "POST" });
    expect(res.status).toBe(503);
    expect(await res.json()).toMatchObject({ error: "pcOffline" });
  });

  it("answers 504 when the PC does not respond in time", async () => {
    await withHub((h) => (h.requestTimeoutMs = 150));
    const pc = await connectPc();
    pc.respond = false;
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue());
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).apps.length === 2);

    const res = await api(token, "/api/apps/aaa/start", { method: "POST" });
    expect(res.status).toBe(504);
    expect(await res.json()).toMatchObject({ error: "timeout" });
  });

  it("rejects unknown apps and limits actions to 30 per device per minute", async () => {
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue());
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).apps.length === 2);

    expect((await api(token, "/api/apps/zzz/start", { method: "POST" })).status).toBe(404);
    for (let i = 0; i < 30; i++) expect((await api(token, "/api/apps/aaa/start", { method: "POST" })).status).toBe(200);
    expect((await api(token, "/api/apps/aaa/start", { method: "POST" })).status).toBe(429);
  });

  it("relays favorites to the PC and updates the cache", async () => {
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    pc.event("catalogue", sampleCatalogue());
    await waitFor(async () => ((await (await api(token, "/api/apps")).json()) as any).apps.length === 2);

    const res = await api(token, "/api/favorites", { method: "PUT", body: JSON.stringify({ ids: ["bbb", "aaa", "nope"] }) });
    expect(res.status).toBe(200);
    expect(pc.requests.at(-1)).toEqual({ method: "setFavorites", params: { ids: ["bbb", "aaa", "nope"] } });
    const favs = (await (await api(token, "/api/apps?favorites=true")).json()) as any;
    expect(favs.apps.map((a: any) => a.id)).toEqual(["bbb", "aaa"]);

    expect((await api(token, "/api/favorites", { method: "PUT", body: "{}" })).status).toBe(400);
  });
});

describe("live socket", () => {
  async function liveConnect(token: string | null) {
    const res = await SELF.fetch(`${BASE}/api/live`, { headers: { Upgrade: "websocket" } });
    expect(res.status).toBe(101);
    const ws = res.webSocket!;
    ws.accept();
    const messages: any[] = [];
    let closed: CloseEvent | null = null;
    ws.addEventListener("message", (e) => { messages.push(JSON.parse(String(e.data))); });
    ws.addEventListener("close", (e) => { closed = e; });
    if (token) ws.send(JSON.stringify({ token }));
    return { ws, messages, closed: () => closed };
  }

  it("authenticates with the first message, then streams PC events and online/offline", async () => {
    const pc = await connectPc();
    const { token } = await pairDevice(pc);
    const live = await liveConnect(token);
    await waitFor(() => live.messages.some((m) => m.type === "hello"));

    pc.event("status", { appId: "aaa", status: { running: false, processCount: 0, uptimeSeconds: 0, workingSetBytes: 0 } });
    await waitFor(() => live.messages.some((m) => m.type === "status" && m.appId === "aaa"));

    pc.event("session", { state: "locked" });
    await waitFor(() => live.messages.some((m) => m.type === "session" && m.state === "locked"));

    pc.close();
    await waitFor(() => live.messages.some((m) => m.type === "pc" && m.state === "offline"));
    live.ws.close();
  });

  it("closes sockets with an invalid token and after a device is revoked", async () => {
    const bad = await liveConnect("bogus");
    await waitFor(() => bad.closed() !== null);
    expect(bad.closed()!.code).toBe(4401);

    const pc = await connectPc();
    const { token, deviceId } = await pairDevice(pc);
    const live = await liveConnect(token);
    await waitFor(() => live.messages.some((m) => m.type === "hello"));
    await pc.request("revokeDevice", { deviceId });
    await waitFor(() => live.closed() !== null);
    expect(live.closed()!.code).toBe(4401);
  });
});
