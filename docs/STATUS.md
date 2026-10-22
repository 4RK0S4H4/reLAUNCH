# Status

**Current phase:** 3 — Relay Worker (in progress)

## Done
- Phase 1: solution skeleton, relay Worker skeleton, CI (both jobs green, MSI-less zip artifact uploaded).
- Phase 2: Core (catalogue, restart state machine, PNG encoder, pipe RPC), Agent (shell enumeration, icons, launch/stop, lock detection), Service (agent supervisor, catalogue, SQLite, control service, debug HTTP API). 21 unit tests. CI smoke test on the Windows runner passes: 175 apps enumerated, 37+ icons extracted, Calculator started / restarted / stopped through `127.0.0.1:5480`.

## Last CI result
- Phase 2 (4821a12): success (windows incl. smoke test, relay).

## Open problems
- None.

## Notes
- Solution file is `reLAUNCH.slnx` (the .NET 10 SDK default format).
- The debug API is off by default (`settings.json` `debugApi`, or env `RELAUNCH_DEBUG_API=1`); CI smoke test turns it on.
