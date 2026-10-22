# Status

**All 7 phases are done. Last CI run: green** (windows + relay) on the latest commit of `claude/windows-remote-app-launcher-7jbgmw`.

## What was built

1. **Skeleton + CI** — solution (`reLAUNCH.slnx`), Core / Service / Agent / WinUI App / tests, relay project, two CI jobs, artifacts.
2. **Agent + service core** — service supervises an agent in the active session (`CreateProcessAsUser`); Start menu catalogue with icons, start / stop / restart / status; Jellyfin profile; restart state machine and catalogue matching unit-tested (21 tests). Verified on the CI runner: 175 apps enumerated, icons extracted, Calculator started, restarted and stopped.
3. **Relay Worker** — `PcHub` Durable Object (Hibernation API): PC socket, request relay with timeouts, one-time pairing codes, device tokens, rate limits, icon cache, offline detection, live socket. 23 vitest tests with a fake PC. Service relay client with backoff and heartbeat. CI runs the whole path locally (`wrangler dev` + service + fake phone) on the Windows runner.
4. **Phone web app** — S4 Flat, installable, live updates, pairing, offline / locked / no-session states, works at 400 px in both themes (screenshots in `docs/screenshots`), self-hosted Inter, service worker caches the shell only.
5. **Desktop app** — five pages over the named pipe, QR pairing, PC key generation. CI starts it against the running service and opens every page (`--selftest`).
6. **Setup + installer** — `docs/SETUP-CLOUDFLARE.md`, deploy step (skips cleanly without secrets), WiX MSI. CI installs the MSI silently, checks the real LocalSystem service runs, the agent starts in the user session, the desktop app talks to it, then uninstalls.
7. **Polish** — README with install and usage, screenshots, clear error messages throughout.

## Last CI result

Success: Windows job (build, 21 unit tests, smoke test, relay end-to-end, WiX build, install / uninstall test) and relay job (PWA build + output check, typecheck, 23 tests).

## Open problems / not verifiable here

- The WinUI app was verified to start and load all pages in CI, but nobody has looked at it on screen (no screenshots of it were possible). Layout polish may be needed after you see it.
- Cloudflare deploy is untested against a real account (needs your secrets). The Worker runs locally under `wrangler dev` and in vitest.
- Cloudflare Access on the `workers.dev` hostname (optional extra layer in the plan) was not evaluated; not documented.
- The solution file is `reLAUNCH.slnx` (the .NET 10 SDK default format), not `.sln`.

## Your next steps

1. **Install on the PC.** Open the latest green run in the repository's *Actions* tab → Artifacts → `reLAUNCH-installer` → download `ReLaunchSetup.msi` (and `reLAUNCH-win-x64.zip` if you want the loose files). Run the MSI.
2. **Cloudflare secrets.** Follow `docs/SETUP-CLOUDFLARE.md`: create the free account and API token; in the desktop app → Remote Access click **Generate Key** and copy it; add the repository secrets `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`, `RELAUNCH_PC_KEY`; push or re-run the workflow; paste `https://relaunch.<subdomain>.workers.dev` into Remote Access → Save URL; the state should read **Connected**.
3. **Pair the phone.** Desktop app → Phones → Pair a Phone → scan the QR → Add to Home Screen.

## Manual test checklist (on the Windows PC)

- [ ] Install the MSI → the **reLAUNCH** service is running → the desktop app shows the app list.
- [ ] Lock the PC (Win+L) → from the phone on mobile data: start Notepad, restart the Jellyfin tray app, stop an app. Unlock: windows are there / gone as expected.
- [ ] Revoke the phone in the desktop app → the phone shows the pairing sheet.
- [ ] Shut the PC down → the phone shows **Offline** with the favorites greyed out; wake it with the Wake-on-LAN app → the phone returns to **Online** without a reload.
- [ ] Jellyfin: if Infuse can't reach the server, Restart Jellyfin from the phone and check Infuse reconnects.
