# PalPanel — project notes for Claude

Web control panel for one or more **Palworld dedicated servers** on a single Windows host,
exposed at `panel.iamfatness.us` via a Cloudflare Tunnel. .NET 8 Blazor Server, EF Core + SQLite.

## Build / test / run

- Build: `dotnet build`
- Test: `dotnet test` (xUnit; ~154 tests). TDD is the norm — keep the suite green.
- Run locally: `dotnet run --project src/PalPanel` → http://localhost:5080
- Publish: `dotnet publish -c Release` → hosts as a Windows service (`UseWindowsService`).

## Architecture (multi-server)

- **`ServerManager`** (`Servers/`) owns one **`ServerRuntime`** per enabled server, keyed by
  `Guid`. It implements **`IServerRegistry`** (`All()` / `Get(id)`), which the poller, scheduler,
  and UI use to reach a server. This is the single entry point to the multi-server core.
- **`ServerRuntime.Build`** bundles a per-server `ProcessSupervisor`, `PalApiClient`,
  `BackupService`, `ServerOrchestrator`, `SnapshotService`, and a `ServerEventSink`. It maps the
  DB-backed **`ServerConfig`** onto a per-server `PanelOptions`, so the (single-server-shaped)
  services are reused unchanged, one instance per server.
- **Config** lives in the DB (`Servers` table), UI-managed via `/servers/new` and
  `/servers/{id}/edit`. Server admin passwords are encrypted with **`ISecretProtector`**
  (ASP.NET Data Protection, keyring persisted next to the DB — not Windows-only DPAPI).
- **One shared** `PollerService` iterates all runtimes each tick (per-server state keyed by id;
  one server's failure never stalls others). **One shared** `SchedulerService` resolves the
  runtime per `Schedule.ServerId`.
- `PanelUser` + roles (Admin/Viewer/Blocked) are **global** (panel-level), not per-server.

## Data / migrations

- No EF migrations; schema is created with `EnsureCreated()`. Because that is a no-op on an
  existing DB, **`SchemaUpgrade.ApplyAsync`** patches in the `Servers` table + `ServerId` columns
  idempotently at startup. **`LegacyServerMigration.EnsureSeededAsync`** then seeds one server
  from legacy `PanelOptions` and stamps existing rows — the single→multi upgrade path.
- Per-server rows (`Sample`, `SampleRollup`, `PlayerSession`, `EventLog`, `Schedule`) carry a
  `ServerId`. **Always filter DB queries by `ServerId` on per-server pages.**
- EF Core's SQLite provider can't translate `DateTimeOffset` `Where`/`OrderBy` — load then
  filter/sort client-side (retention keeps tables small). See `Overview`/`History`/`Poller`.

## UI / routing

- `/` = **Fleet** dashboard (a card per server). Per-server pages: `/s/{id}`,
  `/s/{id}/players|messages|history|backups|game-settings|logs|settings`. Panel-wide pages:
  `/host` (host CPU/RAM/disk + per-server backup sizes) and `/settings` (users). All admin-gated
  pages still enforce `IAdminGuard` server-side — nav visibility is not the boundary.
- **Logs** (`/s/{id}/logs`) tails `Pal/Saved/Logs/Pal.log` (`LogReader`, shared read); Palworld only
  writes it with the `-log` launch switch, so the page detects its absence and offers to append
  `-log` to the launch args. **Connectivity** (in Server settings, `ReachabilityService`) reports
  public IP, local game-port listener, and DNS-vs-public-IP for the optional `PublicHostname`.
  **Host** (`/host`, `HostStats`) reads whole-machine CPU (GetSystemTimes), RAM (GlobalMemoryStatusEx),
  and fixed disks (DriveInfo) via guarded Win32 — degrades to null/empty, never throws into the UI.
- Schedules support `restart`, `backup`, and `announce` (message in `Schedule.Parameters`, broadcast
  via `Orchestrator.AnnounceAsync(AdminGuard.SchedulerActor, …)`).
- **Game settings** (`/s/{id}/game-settings`, `PalSettingsFile`/`PalGameSettings`): edits
  `Config/WindowsServer/PalWorldSettings.ini`. **Palworld rewrites that ini from memory when the
  server stops**, so a write done while the server is running is clobbered on the next shutdown.
  the page's **single state-aware save** therefore: if the server is Running it restarts, writing
  the ini via `RestartAsync`'s **`beforeStart` hook** (runs *between* stop and start, the only safe
  window; empty server → no warning countdown, populated → 1-min warning); if Stopped/Held it just
  writes the ini in place. There is deliberately no "save without restarting a running server" — that
  write would be discarded on the next shutdown. Never write the ini before a restart — it will be
  lost. Note also that launch args
  (e.g. a `-players` override) can override ini values at runtime — `OverrideNote` surfaces the
  effective `Metrics.MaxPlayerNum` when it differs from the file.
- **Alerts** (`/alerts`, panel-level, `AlertService`): crash/health alerting with an in-panel feed +
  unread nav badge, plus optional email. `AlertingEventSink` decorates each server's event sink and
  maps notable events → alerts (crash/held/restart-failed → `server-down`, api-unreachable/recovered
  → `reachable`, auto-restart/backup-failed → notifications); the poller adds host low-disk alerts.
  Condition alerts dedup/escalate per `(ServerId, Key)` so a loop yields one evolving alert, not a
  storm. Email (`SmtpAlertNotifier`) sends Warning+Critical only; Info is in-panel only. SMTP config
  is **UI-managed** (Alerts page → Email settings, `AlertSettingsService`, single `AlertSettings`
  row) with the password **encrypted via `ISecretProtector`** like server admin passwords — never
  plaintext, never committed; the config `Alerts` section is only a one-time seed. "Save & send
  test email" verifies delivery. All alert DB predicates avoid `DateTimeOffset` comparisons (SQLite
  can't translate them).
- Per-server pages resolve `ServerManager.Get(Id)` and render `<ServerNotFound />` if null.
- Design tokens in `wwwroot/tokens.css` (dark-first + light via `prefers-color-scheme` and an
  explicit `[data-theme]` toggle); component styles in `app.css`. Charts read colors from tokens.

## Transport

- Blazor Server circuit over SignalR. Through the tunnel, pin cloudflared ingress to
  **HTTP/1.1 origin** (`http2Origin: false`) or the WebSocket upgrade breaks — see
  `docs/runbooks/tunnel-websocket.md`. Server retains disconnected circuits 3 min; client retries
  30×3s (built-in reconnect modal kept).

## Conventions

- **Loud, not silent:** surface failures inline / to the event log; never swallow into a dead
  circuit. Lifecycle/poller/scheduler errors are logged and contained, never fatal to the loop.
- **No brittle paths:** prefer robustness over fragile wins (e.g. a bad server config is skipped
  loudly at startup, not fatal; process names must be unique across servers).
- **Never double-launch:** `ProcessSupervisor.StartAsync` first calls `FindExisting` — if a server
  matching the process name is already up (started externally, e.g. Steam, or left over), it
  **adopts** it instead of spawning a rival that would collide on the game/query ports (the
  "couldn't bind 27015" crash). Start is idempotent w.r.t. an already-running server; the poller
  also re-adopts externally-started servers each tick.
- Server-side authorization is DB-authoritative via `IAdminGuard.EnsureAdminAsync` at every
  mutating entry point — `AuthorizeView` only hides UI, it is not a security boundary.
- Keep this file current in the same change as substantive work.
