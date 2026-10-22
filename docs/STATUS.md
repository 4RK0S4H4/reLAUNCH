# Status

**Current phase:** 4 — PWA (awaiting CI)

## Done
- Phase 1: solution skeleton, relay Worker skeleton, CI (both jobs green, MSI-less zip artifact uploaded).
- Phase 2: Core (catalogue, restart state machine, PNG encoder, pipe RPC), Agent (shell enumeration, icons, launch/stop, lock detection), Service (agent supervisor, catalogue, SQLite, control service, debug HTTP API). 21 unit tests. CI smoke test on the Windows runner passes: 175 apps enumerated, 37+ icons extracted, Calculator started / restarted / stopped through `127.0.0.1:5480`.

- Phase 3: relay Worker (PcHub Durable Object: PC socket via Hibernation API, pairing, device tokens, rate limits, icon cache, offline detection, live socket) with 21 vitest tests using a fake PC; service relay client (backoff, heartbeat, request handling), pairing/device endpoints; CI end-to-end on the Windows runner (local `wrangler dev` + published service + fake phone) passes.
- Phase 4: phone PWA in S4 Flat (`relay/web`, built into `relay/public`): pairing sheet / QR link, favorites tiles → card, all apps with search / swipe-to-start / star, activity, offline and locked states, live updates, self-hosted Inter, manifest + service worker. Screenshots checked at 400px and 1440px in both themes; `check_page.py` clean. The S4 Flat kit is vendored in `relay/web/s4-flat`; `python3 relay/web/build.py` rebuilds (CI verifies `relay/public` is current).

## Last CI result
- Phase 3: success (windows incl. smoke test + relay e2e, relay).

## Open problems
- None.

## Notes
- Solution file is `reLAUNCH.slnx` (the .NET 10 SDK default format).
- The debug API is off by default (`settings.json` `debugApi`, or env `RELAUNCH_DEBUG_API=1`); CI smoke test turns it on.
