import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

const BASE = "https://relaunch.test";

describe("static PWA", () => {
  it("serves the app shell with the S4 Flat kit inlined and no third-party hosts", async () => {
    const res = await SELF.fetch(`${BASE}/`);
    expect(res.status).toBe(200);
    const html = await res.text();
    expect(html).toContain("<title>reLAUNCH</title>");
    expect(html).toContain("S4Flat");
    expect(html).not.toMatch(/fonts\.googleapis|fonts\.gstatic/);
  });

  it("serves the manifest, service worker, font and icons", async () => {
    const manifest = (await (await SELF.fetch(`${BASE}/manifest.webmanifest`)).json()) as any;
    expect(manifest).toMatchObject({ display: "standalone", start_url: "/" });
    const sw = await (await SELF.fetch(`${BASE}/sw.js`)).text();
    expect(sw).not.toContain("__VERSION__");
    expect(sw).toContain('startsWith("/api/")');
    for (const path of ["/fonts/inter.woff2", "/icon-192.png", "/icon-512.png", "/favicon.svg"]) {
      expect((await SELF.fetch(`${BASE}${path}`)).status).toBe(200);
    }
  });
});
