# reLAUNCH — Build Plan

Launch, stop and restart apps on a Windows 11 PC from a phone, including while the PC is locked and when away from home.

Motivating case: Infuse on the TV can't reach the Jellyfin server → open reLAUNCH on the phone → tap Jellyfin → Restart.

## Decisions (agreed with the owner)

| Topic | Decision |
|---|---|
| Desktop app | Native **WinUI 3** (Windows App SDK, latest stable), Fluent 2: Mica backdrop, `NavigationView`, light/dark following the system. |
| Backend | **.NET 10** Windows Service hosting ASP.NET Core (Kestrel). |
| Phone UI | Installable **PWA** served by the service, designed with the **S4 Flat** skill (`anthropic-skills:s4-flat`). |
| Remote access | **Cloudflare Tunnel + Cloudflare Access** (free tier). No VPN on the phone. Port-forwarding is not used. |
| Jellyfin | Runs as the **tray app** (`Jellyfin.Windows.Tray.exe`, which spawns `jellyfin.exe`). Restart must handle both processes. |
| Nobody logged in / PC asleep | **Out of scope.** The owner has their own Wake-on-LAN tool. If no user session exists, the API reports `"noSession"` and the UI says so. |
| Builds | The dev environment is Linux, so **GitHub Actions on `windows-latest`** builds, tests and produces the installer on every push. CI is the compile feedback loop. |

## Architecture

```
Phone PWA ──HTTPS──► Cloudflare edge (Access login) ──tunnel──► cloudflared (Windows service)
                                                                    │ http://127.0.0.1:5480
                                                                    ▼
                                         ReLaunch.Service (LocalSystem, auto-start)
                                           ├─ REST API + static PWA
                                           ├─ SQLite: favorites, devices, activity log
                                           ├─ spawns + supervises ReLaunch.Agent in the active console session
                                           └─ named pipe ◄────► ReLaunch.Agent (runs as the logged-in user)
                                                                  ├─ enumerates shell:AppsFolder (+ icons)
                                                                  ├─ launches apps (ShellExecute / AUMID activation)
                                                                  └─ process status, close / kill
ReLaunch.App (WinUI 3) ──named pipe──► ReLaunch.Service   (settings, favorites, pairing, logs)
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
  ReLaunch.Web/         PWA source (S4 Flat) → built into ReLaunch.Service/wwwroot
installer/              WiX v5 bundle: service + agent + app + optional cloudflared install
tests/
  ReLaunch.Core.Tests/  xUnit: catalogue matching, restart state machine, auth tokens
.github/workflows/build.yml
docs/PLAN.md, docs/REMOTE-ACCESS.md
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

## API (Kestrel bound to `127.0.0.1:5480` only — cloudflared is the only way in; optional LAN binding toggle in settings)

```
GET  /api/status                 PC name, session state (active | locked | noSession), agent health
GET  /api/apps?favorites=true    catalogue with status
GET  /api/apps/{id}/icon
POST /api/apps/{id}/start | stop | restart
PUT  /api/favorites              ordered list of app ids
GET  /api/activity?limit=50
GET  /api/events                 SSE: app status changes, action progress
POST /api/pair                   exchange one-time pairing code for a device token
```

### Auth (two layers)
1. **Cloudflare Access** in front of the tunnel (one-time PIN to the owner's email, session lasting up to 1 month).
2. **Device token** from reLAUNCH itself: the desktop app shows a QR code with a one-time pairing URL (`https://<host>/pair#code=…`, valid 5 minutes). The phone exchanges it for a long random token (stored hashed server-side, kept in `localStorage` on the phone, sent as `Authorization: Bearer`). Devices are revocable from the desktop app. Rate-limit the API and pairing endpoint.

## Phone PWA (S4 Flat)

Build with the `anthropic-skills:s4-flat` skill: start from its `starter.src.html`, follow its rules, build with its `build_page.py`, fix every error/warning, screenshot phone dark/light + desktop with its `screenshot.js`.

Deviation for this project: **self-host Inter** (woff2 in `wwwroot/fonts`) instead of Google Fonts, and ship `s4-flat.css` / `s4-flat.js` / the icon sprite as static files, so the app has no third-party runtime dependencies. Run `check_page.py` on the result.

Screens:
- **Top bar pill**: mark logo, PC name, connection pill (Online / Locked / Offline / No Session).
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
- **Remote Access** — cloudflared status (installed / running / tunnel healthy), public hostname, link to `docs/REMOTE-ACCESS.md` steps.
- **Activity** — log table.
- **Settings** — ports, LAN access toggle, accent preview, about.
Talks to the service over a named pipe (ACL: SYSTEM + Administrators + the interactive user). Starts with Windows via a startup task, minimised to the tray is optional (not required — the service does the work).

## Remote access (Cloudflare, free)

Write `docs/REMOTE-ACCESS.md` with step-by-step instructions:
1. A domain on Cloudflare is required for a named tunnel (one the owner already has, or ~$10/yr via Cloudflare Registrar). Quick tunnels (`trycloudflare.com`) are not suitable: random URL, no Access.
2. Zero Trust dashboard → Networks → Tunnels → create tunnel → install `cloudflared` as a Windows service with the token.
3. Public hostname `relaunch.<domain>` → `http://127.0.0.1:5480`.
4. Access → Applications → self-hosted app for that hostname, policy "emails = owner", one-time PIN login, session duration 1 month.
5. Open the URL on the phone, log in, Add to Home Screen, pair with the QR code.
The installer may optionally install cloudflared (`winget install Cloudflare.cloudflared`), but the tunnel token is entered by the owner in the desktop app or by command.

## CI (`.github/workflows/build.yml`)

On push and PR, `windows-latest`:
1. Setup .NET 10, restore, `dotnet build -c Release`, `dotnet test`.
2. Build PWA (Python for the S4 Flat build script) into `ReLaunch.Service/wwwroot`.
3. Publish Service, Agent (self-contained, win-x64) and App (WinUI unpackaged, self-contained, `WindowsAppSDKSelfContained=true`).
4. Build WiX installer (`ReLaunchSetup.msi`): installs to `Program Files\reLAUNCH`, registers the service (auto start, recovery: restart on failure), adds the desktop app Start menu shortcut.
5. Upload the MSI and a zip as workflow artifacts.

## Phases and acceptance criteria

Each phase ends with CI green and a push to the working branch.

1. **Skeleton + CI** — solution, all projects compile, empty tests pass, workflow uploads artifacts.
2. **Agent + service core** — service spawns agent in the active session; catalogue with icons; start/stop/restart/status work through the API (test locally with `curl` against `127.0.0.1:5480`). Jellyfin profile works. Unit tests for restart state machine and catalogue matching.
3. **PWA** — S4 Flat screens above, SSE status, pairing, works at 400px wide in both themes, served from the service.
4. **Desktop app** — all five pages over the named pipe; QR pairing.
5. **Remote access + installer** — `REMOTE-ACCESS.md`, cloudflared status page, auth hardening (rate limits, token revocation), WiX MSI.
6. **Polish** — activity log, error messages, README with install + usage, screenshots.

## Manual test checklist (owner, on the Windows PC)
- Install MSI → service running → desktop app shows the app list.
- Lock the PC (Win+L) → from phone on mobile data: start Notepad, restart Jellyfin tray, stop an app. Unlock: windows are there / gone as expected.
- Revoke the phone in the desktop app → phone gets the pairing sheet.
