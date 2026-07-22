# PalPanel — Palworld Server Control Panel & Dashboard

**Date:** 2026-07-21
**Status:** Approved design, pre-implementation
**Owner:** John Wallace

## Purpose

A self-hosted control panel and live dashboard for the Palworld dedicated server
running on 192.168.1.50 (Windows). One installable Windows service that supervises
the game server process, exposes a web dashboard to the internet through a
Cloudflare Tunnel, and gives role-based control: the owner and trusted friends
administer the server from anywhere; everyone else who logs in sees a read-only
live view.

Out of scope for v1 (explicitly deprioritized by owner): Discord bot, mobile app.
The web dashboard is responsive, so phones get the web UI.

## Environment facts (verified 2026-07-20)

- Palworld dedicated server host: **192.168.1.50**, Windows (TTL 128, host up).
- Game port: **UDP 8211**, forwarded on the UDM Pro ("Palworld" rule, TCP/UDP
  8211 → 192.168.1.50:8211, active). TCP 8211 is closed on the host — the game
  protocol is UDP-only; port scans showing 8211 "closed" are expected.
- WAN: 96.27.16.112 (WideOpenWest, residential — may change; DDNS is a future
  consideration).
- DNS: `palworld.iamfatness.us` → 96.27.16.112 (A record, DNS-only, created
  2026-07-20 in Cloudflare zone `iamfatness.us`).
- Cloudflare account: wallace.john.w@gmail.com, free plan, zone active.

## Architecture (Approach A — single service)

One .NET 8 ASP.NET Core application, installed as a Windows service
(`UseWindowsService`) **on the same machine as the Palworld server (192.168.1.50)**.
It contains four cooperating parts in one process:

1. **Process supervisor** — owns the PalServer.exe lifecycle.
2. **REST integration** — client + poller for Palworld's built-in REST API
   (localhost:8212).
3. **Data layer** — SQLite for history, sessions, audit log, users, schedules.
4. **Web dashboard** — Blazor Server UI, pushed live via its built-in SignalR
   circuit. No separate frontend build chain, no JSON API contract, no CORS.

Exposure: `cloudflared` tunnel maps `panel.iamfatness.us` → `http://localhost:5080`.
Cloudflare Access sits in front of the hostname. **No new open ports on the UDM.**

Rationale: fewest moving parts, one deployable, C# end-to-end (owner's daily
stack). Rejected alternatives: (B) ASP.NET API + React SPA — nicer component
ecosystem but two build systems and an API contract to maintain; (C) cloud-hosted
dashboard + local agent — second deployable and version skew for zero functional
gain.

## 1. Process supervisor

- Config: full path to `PalServer.exe`, launch args, working directory, graceful
  stop timeout, restart backoff parameters.
- State machine: `Stopped → Starting → Running → Stopping → Stopped`, plus
  `Crashed` (unexpected exit, restart pending) and `Held` (flap protection
  tripped; manual intervention required).
- **Start:** launch the process, hold the `Process` handle, transition to
  `Running` once the REST API answers `/v1/api/info` (with a startup timeout).
- **Adoption:** if a `PalServer` process is already running when the service
  starts, adopt it (attach by process name) instead of failing or double-starting.
- **Graceful stop (always the default path):** in-game announce → REST `save` →
  REST `shutdown` with wait time → wait for process exit → force-kill only after
  the configured timeout. Force-kill is loudly logged as an event.
- **Auto-restart on crash:** process exit without a requested stop → log `crash`
  event → restart with exponential backoff. Flap protection: 3 crashes within
  10 minutes → enter `Held`, stop retrying, surface prominently in the UI.
  Thresholds configurable.
- **Restart:** graceful stop + start, with pre-restart backup (see §4).

## 2. Palworld REST API integration

- `HttpClient` to `http://localhost:8212`, HTTP Basic auth (`admin` +
  AdminPassword from config). The panel runs on the same host, so the API can
  stay bound to localhost — it is never exposed off-machine.
- Prerequisite (documented in setup): `PalWorldSettings.ini` must have
  `RESTAPIEnabled=True`, `RESTAPIPort=8212`, and an `AdminPassword` set.
  First-run setup check calls `/v1/api/info` and reports exactly what is
  missing/wrong if it fails.
- **Poller** (hosted background service, ~10 s interval): `/v1/api/info`,
  `/v1/api/players`, `/v1/api/metrics` → in-memory `ServerSnapshot` → broadcast
  to dashboard circuits + appended to history.
- **Actions** (wrapped, audited): `announce`, `kick`, `ban`, `unban`, `save`,
  `shutdown`. (`start` is the supervisor's job — the REST API cannot start a
  server.)
- Distinct degraded state: process alive but API not answering →
  **"Running — API unreachable"**. Never silently shown as offline. Failures are
  loud, not silent (standing owner preference).

## 3. Data layer (SQLite)

Single SQLite file next to the service (`palpanel.db`), via EF Core.

| Table | Contents |
|---|---|
| `samples` | timestamp, player count, server FPS, frame time, memory, uptime — one row per poll (10 s) |
| `sample_rollups` | per-minute and per-hour aggregates (avg/max) |
| `sessions` | player join/leave intervals, derived by diffing consecutive player polls → per-player playtime |
| `events` | audit log: start/stop/restart/crash/held/backup/restore/kick/ban/announce/setting-change, with acting user's email where applicable |
| `users` | email, role (`Admin` / `Viewer` / `Blocked`), first/last seen |
| `schedules` | cron expression, action type, parameters, enabled flag |

Retention (background job): raw samples 48 h → per-minute rollups 30 days →
hourly rollups kept indefinitely. DB stays small permanently.

## 4. Scheduling, warnings, backups

- Cron-style schedules evaluated by a hosted background service (e.g. nightly
  04:00 restart).
- **Scheduled restart ritual:** `/announce` warnings at T-10, T-5, T-1 min →
  `save` → graceful shutdown → backup → relaunch → verify the API comes back →
  `restart-complete` event (or a loud failure event).
- **Backups:** zip the world save directory (`Pal/Saved/`) on schedule and
  automatically before every restart/stop. Keep last N (configurable, default 20).
  Dashboard: list, create-now, download. **Restore:** admin-only, only allowed
  while the server is `Stopped`; restoring first snapshots the current save.

## 5. Auth & roles

- `cloudflared` (installed as its own Windows service on .50) tunnels
  `panel.iamfatness.us` → `localhost:5080`.
- **Cloudflare Access** (free tier) in front: policy accepts **any successfully
  authenticated identity** (Google login or one-time email PIN). No allowlist
  upkeep — any friend can sign in with their own email.
- The app **validates the `Cf-Access-Jwt-Assertion` JWT** against the team's
  public keys (issuer + audience check) — it does not trust bare headers. Requests
  without a valid Access JWT are rejected (the app is unreachable except through
  the tunnel, but defense in depth is cheap).
- Role mapping is app-side, keyed by verified email:
  - First email ever seen → **Admin** automatically (that's the owner).
  - Every subsequent new email → **Viewer** (live status, players, charts —
    no mutating controls, which are hidden AND enforced server-side).
  - Owner promotes trusted friends to **Admin**, or sets **Blocked** (signed in
    but sees nothing) from Settings → Users.
- Optional break-glass: a LAN-only listener bound to localhost for RDP access if
  Cloudflare is unreachable. Off by default.

## 6. Dashboard (Blazor Server)

Pages (all live-updating; Viewer sees the same pages with mutating controls
hidden and server-side-enforced):

- **Overview** — status card (state machine state, uptime, version, degraded
  warnings), player count, quick actions (Start / Stop / Restart / Save /
  Announce) with confirmation dialogs, 24 h player-count and memory sparkline
  charts.
- **Players** — live list (name, account id, level, ping where the API provides
  it) with kick/ban; per-player session history and total playtime.
- **History** — range-picker charts (players, FPS, memory), uptime %, player
  peaks, filterable event/audit log.
- **Backups** — list with size/date, create-now, download, restore (guarded).
- **Settings** — server paths/args, schedules, backup retention, user role
  management, REST connectivity check.

Charts: a locally bundled JS chart library (no CDN), data fed from Blazor.
Responsive layout so phones work (mobile app explicitly deferred).

## 7. Error handling & robustness principles

- Every failure state is a distinct, visible state in the UI with a timestamped
  event — never a silent retry loop (owner's standing "no brittle paths" rule).
- All mutating actions audited with acting user.
- Service: auto-start on boot, Windows service recovery = restart on failure.
- Poller and scheduler are independent hosted services; one failing does not
  take down the web UI.

## 8. Testing

- **Unit:** supervisor state machine (fake process launcher), scheduler
  (virtual clock), retention/rollups, role mapping, Access JWT validation
  (test keys).
- **Integration:** stub Palworld REST server (in-proc ASP.NET `WebApplicationFactory`
  or WireMock) for poller/actions; a tiny dummy child exe for real
  start/crash/auto-restart/flap tests.
- **Manual acceptance:** full loop on .50 — install service, tunnel up, login
  from phone on cellular as a fresh Google account → lands as Viewer; owner
  promotes → Admin controls work; pull the PalServer process → crash event +
  auto-restart observed.

## 9. Deployment

- `dotnet publish` self-contained win-x64 single service; install script
  (`sc create PalPanel ... start=auto`, recovery options) + documented
  `cloudflared` setup (tunnel create, DNS route `panel.iamfatness.us`, service
  install) + Cloudflare Access app/policy setup steps.
- Config in `appsettings.json` beside the exe; secrets (Palworld AdminPassword)
  in a DPAPI-protected local file or Windows credential store — not committed
  anywhere.

## Phase 2 candidates (not in v1)

Discord bot (status/admin commands), DDNS updater for the residential WAN IP,
public no-login status page, `PalWorldSettings.ini` editor in the UI, RCON
fallback, multi-server support.
