# Status

**Current phase:** 2 — Agent + service core (awaiting CI)

## Done
- Phase 1: solution skeleton, relay Worker skeleton, CI (both jobs green, artifact uploaded).
- Phase 2 code written: Core (catalogue, action runner/restart state machine, PNG encoder, pipe RPC), Agent (shell enumeration, icons, launch/stop, lock detection), Service (agent supervisor, catalogue service, SQLite, control service, debug HTTP API). Unit tests for restart state machine and catalogue matching.

## Last CI result
- Phase 1 (cdd5e87): success (windows + relay).

## Open problems
- None.

## Notes
- Solution file is `reLAUNCH.slnx` (the .NET 10 SDK's default format).
- The debug API is off by default (`settings.json` `debugApi`, or env `RELAUNCH_DEBUG_API=1`); CI runs a smoke test with it on.
