import { PcHub } from "./pc-hub";

export { PcHub };

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/api/health") {
      return Response.json({ ok: true, name: "reLAUNCH relay" });
    }
    if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/pc/")) {
      const hub = env.PC_HUB.get(env.PC_HUB.idFromName("home"));
      return hub.fetch(request);
    }
    return env.ASSETS.fetch(request);
  },
} satisfies ExportedHandler<Env>;
