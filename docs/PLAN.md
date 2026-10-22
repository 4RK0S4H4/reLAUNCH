# reLAUNCH — Build Plan

Launch, stop and restart apps on a Windows 11 PC from a phone, including while the PC is locked and when away from home.

Motivating case: Infuse on the TV can't reach the Jellyfin server → open reLAUNCH on the phone → tap Jellyfin → Restart.

## Decisions (agreed with the owner)

| Topic | Decision |
|---|---|
| Desktop app | Native **WinUI 3** (Windows App SDK, latest stable), Fluent 2: Mica backdrop, `NavigationView`, light/dark following the system. |
| Backend | **.NET 10** Windows Service hosting ASP.NET Core (Kestrel). |
| Phone UI | Installable **PWA**, designed with the **S4 Flat** skill (`anthropic-skills:s4-flat`), hosted on Cloudflare by the relay Worker. |
| Remote access | **Cloudflare Worker relay** on the free plan at `relaunch.<subdomain>.workers.dev`: no domain, no VPN on the phone, no port-forwarding, no `cloudflared`. The PC keeps an outbound WebSocket open to a Durable Object; phone requests are relayed down it. Fallback if this proves unworkable: **Tailscale Funnel** (see the end of this file). |
| Jellyfin | Runs as the **tray app** (`Jellyfin.Windows.Tray.exe`, which spawns `jellyfin.exe`). Restart must handle both processes. |
| Nobody logged in / PC asleep | **Out of scope.** The owner has their own Wake-on-LAN tool. If no user session exists, the API reports `"noSession"` and the UI says so. |
| Builds | The dev environment is Linux, so **GitHub Actions on `windows-latest`** builds, tests and produces the installer on every push. CI is the compile feedback loop. |

## Architecture

```
Phone PWA ──HTTPS / WSS──► Cloudflare Worker  relaunch.<sub>.workers.dev   (free plan)
                             ├─ static assets: the PWA
                             ├─ /api/*  → Durable Object "PcHub" (single instance, SQLite storage)
                             │            ├─ device tokens (hashed), pairing codes, cached catalogue + icons, activity
                             │            └─ holds the PC WebSocket (Hibernation API), relays requests by id
                             └─ /pc/connect (WSS, PC key) ◄──── outbound from the PC
                                                                    │
                                         ReLaunch.Service (LocalSystem, auto-start)
                                           ├─ relay client: reconnect with backoff, heartbeat, request handler
                                           ├─ SQLite: favorites, overrides, activity log
                                           ├─ optional debug HTTP API on 127.0.0.1:5480 (off by default)
                                           ├─ spawns + supervises ReLaunch.Agent in the active console session
                                           └─ named pipe ◄────► ReLaunch.Agent (runs as the logged-in user)
                                                                  ├─ enumerates shell:AppsFolder (+ icons)
                                                                  ├─ launches apps (ShellExecute / AUMID activation)
                                                                  └─ process status, close / kill
ReLaunch.App (WinUI 3) ──named pipe──► ReLaunch.Service   (settings, favorites, pairing, devices, logs)
```

Why the split:
- A **service** survives crashes (recovery options) and is reachable while the session is locked.
- Enumerating the Start menu app list, extracting icons and launching via the shell must happen **as the user, inside their session**. A SYSTEM process sees the wrong profile. So the service starts a small headless **agent** in the active console session with `WTSGetActiveConsoleSessionId` → `WTSQueryUserToken` → `CreateEnvironmentBlock` → `CreateProcessAsUser` (desktop `winsta0\default`), and restarts it if it dies or the session changes (`OnSessionChange`: logon/logoff/lock/unlock).
- A locked session keeps running, so apps launched into it run normally behind the lock screen.

## Repository layout

```
reLAUNCH.sln
src/
  ReLaunch.Core/        shared models, pipe protocol (JSON over named pipes), app catalogue logic
  ReLaunch.Service/     Worker Service + Kestrel API + static web root + SQLite (Microsoft.Data.Sqlite)
  ReLaunch.Agent/       headless WinExe in user session: enumeration, icons, launch, kill
  ReLaunch.App/         WinUI 3 desktop app (unpackaged, self-contained)
relay/                  Cloudflare Worker (TypeScript, wrangler): PcHub Durable Object + API routes
  web/                  PWA source (S4 Flat) → built into relay/public (Workers static assets)
  test/                 vitest + @cloudflare/vitest-pool-workers
installer/              WiX v5 MSI: service + agent + app
tests/
  ReLaunch.Core.Tests/  xUnit: catalogue matching, restart state machine, auth tokens
.github/workflows/build.yml
docs/PLAN.md, docs/SETUP-CLOUDFLARE.md
```

## Core behaviour

### App catalogue
- Source: `shell:AppsFolder` (`FOLDERID_AppsFolder` via `IShellItem` enumeration) — the same list as Start → All apps, covering Win32 and packaged apps. Each entry: display name, AUMID or parsing name, icon (extract 256px PNG via `IShellItemImageFactory`, cache on disk, serve at `/api/apps/{id}/icon`).
- Resolve each entry to an **executable path** for status matching: `.lnk` target via `IShellLink`; packaged apps via `PackageManager` install location + manifest.
- Stable app `id` = hash of AUMID/parsing name.
- Per-app overrides (set in the desktop app): extra launch arguments, **extra process names** to treat as part of the app, "close gracefully first" timeout.
- Built-in profile for Jellyfin tray: processes `Jellyfin.Windows.Tray` + `jellyfin`; restart kills both, then launches the tray app (which starts the server). Detect it automatically when the catalogue contains "Jellyfin Server".
- Refresh catalogue on agent start and every 10 minutes, plus on demand.

### Actions
- **Start**: if not running, launch via `ShellExecuteEx` on `shell:AppsFolder\<AUMID>` (or `IApplicationActivationManager` for packaged apps). Return once a matching process appears (timeout 20s).
- **Stop**: `CloseMainWindow`/`WM_CLOSE` to every window of the app's processes → wait (default 5s) → `Kill(entireProcessTree: true)` for anything left, including the extra process names.
- **Restart**: Stop, then Start. Expose state transitions: `stopping → starting → running` (or `failed` with a message).
- **Status**: running/stopped, process count, uptime, working set. Pushed to the PWA via **Server-Sent Events** (`/api/events`); poll fallback.
- Elevated apps: out of scope for v1. If launch fails because elevation is needed, return a clear error.
- The API can only act on catalogue entries. Never accept a raw path or command from the network.

### Data (SQLite in `%ProgramData%\reLAUNCH`)
`favorites(appId, order)`, `overrides(appId, args, extraProcesses, closeTimeoutMs)`, `devices(id, name, tokenHash, createdAt, lastSeen)`, `activity(ts, deviceId, appId, action, result)`.

## Relay protocol and API

Everything lives in one Worker. A single Durable Object instance (`PcHub`, id from name `"home"`) owns all state. Use **SQLite-backed** Durable Objects (`new_sqlite_classes` in the wrangler migration — required on the free plan) and the **WebSocket Hibernation API** (`state.acceptWebSocket`, `setWebSocketAutoResponse` for ping/pong) so idle connections cost nothing.

### PC ↔ relay
- PC connects to `wss://<host>/pc/connect` with `Authorization: Bearer <PC_KEY>`. The Worker compares against the `PC_KEY` secret (constant-time). One PC socket at a time; a new one replaces the old.
- Messages are JSON: `{ "id", "type": "request", "method", "params" }` from relay → PC, `{ "id", "type": "response", "ok", "result" | "error" }` back, and `{ "type": "event", ... }` from PC for status changes / action progress.
- On connect and on catalogue change the PC pushes the catalogue (with icon hashes); the relay requests missing icons (`getIcon`) and stores the PNGs (≤ 64 KB each) in DO storage, served with a long cache header.
- PC sends a heartbeat event every 30 s; relay marks the PC offline after 90 s of silence or on socket close.
- The PC also mirrors pairing codes and revocations to the relay (`registerPairingCode`, `revokeDevice`) and receives `devicePaired` events.

### Phone ↔ relay (Bearer device token on every call)
```
GET  /api/status                 { pc: online | offline, session: active | locked | noSession, pcName, lastSeen }
GET  /api/apps?favorites=true    catalogue with status (cached copy when the PC is offline, flagged stale)
GET  /api/apps/{id}/icon         from DO storage
POST /api/apps/{id}/start | stop | restart   → relayed; 503 { error: "pcOffline" } when no PC socket; 25 s timeout
PUT  /api/favorites              ordered list of app ids (relayed; PC is the source of truth)
GET  /api/activity?limit=50
GET  /api/live                   WebSocket; first message is { token }; relay broadcasts PC events + online/offline
POST /api/pair                   { code } → { token, deviceId }
```
The relay only forwards the fixed method set above. The PC only acts on catalogue entries; it never accepts a raw path or command.

### Auth
- **PC key:** a 32-byte random key. The desktop app generates it (Remote Access page → "Generate key", copy button). It is stored DPAPI-protected on the PC and set as the Worker secret `PC_KEY` (via the GitHub secret `RELAUNCH_PC_KEY` that CI pushes with `wrangler secret put`, or manually with `npx wrangler secret put PC_KEY`).
- **Device tokens:** the desktop app shows a QR code for `https://<host>/#pair=<code>` (one-time code, valid 5 minutes, sent hashed to the relay). The phone exchanges it at `/api/pair` for a long random token (stored hashed in the DO, kept in `localStorage` on the phone). Devices are listed and revocable in the desktop app.
- Rate limits in the DO: `/api/pair` 5 attempts per IP per 10 minutes; actions 30 per device per minute.
- Optional extra layer: Cloudflare Access on the `workers.dev` hostname if the dashboard offers it (verify during the build; document it in SETUP-CLOUDFLARE.md if it works, skip otherwise).

### Free-plan budget
Workers Free: 100k requests/day; Durable Objects on the free plan have their own daily request and storage limits. Expected use is a few hundred requests/day plus heartbeats (2,880/day), well within limits. Keep heartbeats at 30 s or slower and use auto-response for pings.

## Phone PWA (S4 Flat)

Build with the `anthropic-skills:s4-flat` skill: start from its `starter.src.html`, follow its rules, build with its `build_page.py`, fix every error/warning, screenshot phone dark/light + desktop with its `screenshot.js`.

Deviation for this project: **self-host Inter** (woff2 in `relay/public/fonts`) instead of Google Fonts, and ship `s4-flat.css` / `s4-flat.js` / the icon sprite as static files from the Worker, so the app has no third-party runtime dependencies. Run `check_page.py` on the result.

Screens:
- **Top bar pill**: mark logo, PC name, connection pill (Online / Locked / Offline / No Session).
- **PC offline**: favorites still show from the cached catalogue, greyed, with a banner "PC is offline — wake it with your Wake-on-LAN app"; action buttons disabled.
- **Dock**: Favorites · All Apps · Activity.
- **Favorites**: grid of tiles (icon, name, status pill). Tap → card morphs out of the tile with **Start / Restart / Stop** pills. Stop confirms in a dialog. Progress shows the mark loader; status words roll in place via `S4Flat.text`; toast in past tense ("Jellyfin restarted").
- **All Apps**: search field + list rows (icon avatar, name, status, star ink-chip to favorite). Swipe right to Start.
- **Activity**: recent actions with time, device and result.
- **Pairing sheet**: shown when no token; explains scanning the QR in the desktop app.
- Accent: single accent via `S4Flat.setAccent` (default teal `#14b8a6` unless the owner picks another).
- PWA: `manifest.webmanifest` (standalone, icons from the S4 mark), service worker caching the shell only (never cache API responses).

## Desktop app (WinUI 3)

`NavigationView` (left), Mica, Fluent icons, system theme:
- **Apps** — searchable catalogue, star to favorite, drag to reorder favorites, per-app overrides (arguments, extra processes, close timeout), Start/Stop/Restart buttons for local testing.
- **Phones** — "Pair a phone" shows a QR code (QRCoder); list of devices with last seen + Revoke.
- **Remote Access** — relay URL field, PC key generate/copy, connection state (Connected / Reconnecting / Rejected key), last heartbeat, link to `docs/SETUP-CLOUDFLARE.md`.
- **Activity** — log table.
- **Settings** — debug HTTP API toggle, start with Windows, about.
Talks to the service over a named pipe (ACL: SYSTEM + Administrators + the interactive user). Starts with Windows via a startup task, minimised to the tray is optional (not required — the service does the work).

## Cloudflare setup (free, no domain)

Write `docs/SETUP-CLOUDFLARE.md` for the owner:
1. Create a free Cloudflare account (or use the existing one) and pick a `workers.dev` subdomain.
2. Create an API token with the "Edit Cloudflare Workers" template; note the account ID.
3. In the GitHub repo add secrets `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`, and `RELAUNCH_PC_KEY` (from the desktop app's Remote Access page).
4. Push / re-run the workflow → the relay deploys to `https://relaunch.<subdomain>.workers.dev`.
5. Paste that URL into the desktop app → status shows Connected.
6. On the phone: open the URL, Add to Home Screen, scan the pairing QR from the desktop app.

## CI (`.github/workflows/build.yml`)

On push and PR, `windows-latest`:
1. Setup .NET 10, restore, `dotnet build -c Release`, `dotnet test`.
2. (Separate `ubuntu-latest` job) Relay: `npm ci`, typecheck, `vitest`, build the PWA with the S4 Flat build script into `relay/public`. On pushes, if `CLOUDFLARE_API_TOKEN` is set: `wrangler deploy` and `wrangler secret put PC_KEY` from `RELAUNCH_PC_KEY`. Skip deploy cleanly when secrets are missing.
3. Publish Service, Agent (self-contained, win-x64) and App (WinUI unpackaged, self-contained, `WindowsAppSDKSelfContained=true`).
4. Build WiX installer (`ReLaunchSetup.msi`): installs to `Program Files\reLAUNCH`, registers the service (auto start, recovery: restart on failure), adds the desktop app Start menu shortcut.

Note: the relay and PWA can be built, tested and screenshotted in a Linux dev session (`wrangler dev`, Playwright). Only the .NET Windows projects rely on CI to compile.
5. Upload the MSI and a zip as workflow artifacts.

## Phases and acceptance criteria

Each phase ends with CI green and a push to the working branch.

1. **Skeleton + CI** — solution, all projects compile, empty tests pass, relay project with wrangler config, both CI jobs green, artifacts uploaded.
2. **Agent + service core** — service spawns agent in the active session; catalogue with icons; start/stop/restart/status work through the localhost debug API (`curl 127.0.0.1:5480`). Jellyfin profile works. Unit tests for restart state machine and catalogue matching.
3. **Relay Worker** — PcHub DO, PC socket, request relay with timeouts, pairing, device tokens, rate limits, icon cache, offline detection. vitest coverage with a fake PC socket. Service relay client with reconnect/backoff.
4. **PWA** — S4 Flat screens above, live status over `/api/live`, pairing, offline state, works at 400px in both themes, screenshots checked.
5. **Desktop app** — all five pages over the named pipe; QR pairing; PC key generation.
6. **Setup + installer** — `SETUP-CLOUDFLARE.md`, deploy step in CI, WiX MSI.
7. **Polish** — activity log, error messages, README with install + usage, screenshots.

## Manual test checklist (owner, on the Windows PC)
- Install MSI → service running → desktop app shows the app list.
- Lock the PC (Win+L) → from phone on mobile data: start Notepad, restart Jellyfin tray, stop an app. Unlock: windows are there / gone as expected.
- Revoke the phone in the desktop app → phone gets the pairing sheet.
- Shut the PC down → phone shows PC offline with cached favorites; wake it with the WoL app → status returns to Online without reloading.

## Fallback: Tailscale Funnel

Only if the Worker relay proves unworkable. Keep the phone API contract identical so the switch is small: the service would serve the PWA and the same `/api/*` routes itself on `127.0.0.1:5480`, and `tailscale funnel 5480` publishes it at `https://<pc>.<tailnet>.ts.net` (no app on the phone). Downsides: publicly reachable with only device tokens as protection, no offline page when the PC is off.
