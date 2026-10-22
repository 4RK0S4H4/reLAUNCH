import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

describe("relay skeleton", () => {
  it("answers the health check", async () => {
    const res = await SELF.fetch("https://relaunch.test/api/health");
    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ ok: true });
  });
});
