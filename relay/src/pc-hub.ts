import { DurableObject } from "cloudflare:workers";

/** Single instance that owns all relay state and the PC WebSocket. Filled in during phase 3. */
export class PcHub extends DurableObject<Env> {
  async fetch(_request: Request): Promise<Response> {
    return Response.json({ pc: "offline" });
  }
}
