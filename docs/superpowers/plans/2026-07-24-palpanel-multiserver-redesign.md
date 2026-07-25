# PalPanel Multi-Server + UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline, batch with checkpoints) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the single-server PalPanel into a reliable multi-server fleet manager with a cohesive design system, keeping Blazor Server and the existing 135 tests green.

**Architecture:** Per-server services (`ProcessSupervisor`, `PalApiClient`, `BackupService`, event sink, `ServerOrchestrator`) are bundled into a `ServerRuntime` owned by a singleton `ServerManager` keyed by `Guid`. One shared `PollerService`/`SchedulerService` iterate all runtimes. `SnapshotService` becomes a keyed `SnapshotStore`. Per-server config lives in a DB-backed `ServerConfig` table (DPAPI secrets); `PanelOptions` keeps only panel-global fields. Routing gains a fleet home + `/s/{id}` drill-in. CSS is replaced by a token design system.

**Tech Stack:** .NET 8, Blazor Server (InteractiveServer), EF Core + SQLite, xUnit, Chart.js, Cloudflare Tunnel.

## Global Constraints

- Target framework **net8.0**; C# nullable enabled (existing style).
- Secrets (server admin passwords) encrypted at rest with **DPAPI** (`ProtectedData`, `CurrentUser` scope) exactly as auth secrets are today.
- Roles stay **global** (Admin/Viewer/Blocked); no per-server permissions.
- **Single host** only — never assume remote agents.
- All lifecycle/error paths stay **loud, not silent** (log via `IEventSink`/`ILogger`, never swallow into a dead UI) — repo convention.
- **One shared** `PollerService` and `SchedulerService`, not per-server hosted services.
- The existing 135 tests MUST stay green (this is a refactor). Run `dotnet test` after each phase.
- Commit after every task with a `feat:`/`refactor:`/`test:`/`docs:` message.

---

## Phase 0 — Transport hardening (do first)

### Task 0.1: Circuit + reconnect options

**Files:**
- Modify: `src/PalPanel/Program.cs` (add `AddInteractiveServerComponents` circuit options)
- Modify: `src/PalPanel/Components/App.razor` (reconnect script config)
- Modify: `src/PalPanel/Components/Layout/ReconnectModal.razor` + `.razor.css`

**Interfaces:**
- Produces: no code contract; behavioral — the panel recovers from a dropped circuit.

- [ ] **Step 1:** In `Program.cs`, configure server circuit retention:
  ```csharp
  builder.Services.AddRazorComponents()
      .AddInteractiveServerComponents(o =>
      {
          o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
          o.DisconnectedCircuitMaxRetained = 100;
      });
  ```
- [ ] **Step 2:** In `App.razor`, after `blazor.web.js`, configure autostart reconnect with a bounded retry + reload fallback:
  ```html
  <script src="_framework/blazor.web.js" autostart="false"></script>
  <script>
    Blazor.start({
      circuit: {
        reconnectionOptions: { maxRetries: 30, retryIntervalMilliseconds: 2000 },
        reconnectionHandler: {
          onConnectionDown: () => document.getElementById('reconnect-modal')?.classList.add('show'),
          onConnectionUp:   () => document.getElementById('reconnect-modal')?.classList.remove('show')
        }
      }
    });
  </script>
  ```
- [ ] **Step 3:** Update `ReconnectModal.razor` to show "Reconnecting…" with an explicit **Reload** button (`onclick="location.reload()"`) so a failed reconnect never leaves dead buttons.
- [ ] **Step 4:** `dotnet build` — verify it compiles.
- [ ] **Step 5:** Commit: `refactor(transport): bounded circuit reconnect + explicit reload fallback`.

### Task 0.2: Tunnel WebSocket runbook

**Files:**
- Create: `docs/runbooks/tunnel-websocket.md`

- [ ] **Step 1:** Document the diagnosis + fix: reproduce through `panel.iamfatness.us` with devtools Network (WS) + `cloudflared` logs; identify negotiate vs upgrade vs mid-session drop; pin the tunnel ingress origin to `http1.1` (the prime suspect — cloudflared HTTP/2 origin breaks the WS upgrade); confirm SignalR long-polling fallback is permitted. Include the acceptance test: kill the WS mid-session and confirm recovery/reload.
- [ ] **Step 2:** Commit: `docs: tunnel websocket diagnosis + fix runbook`.

---

## Phase 1 — Data model + migration

### Task 1.1: `ServerConfig` entity

**Files:**
- Modify: `src/PalPanel/Data/Entities.cs`
- Test: `tests/PalPanel.Tests/ServerConfigTests.cs`

**Interfaces:**
- Produces: `class ServerConfig { Guid Id; string Name, ExePath, WorkingDir, LaunchArgs, ProcessName, SaveDirectory, BackupDirectory, ApiBaseUrl, AdminPasswordEnc; int BackupsToKeep, GracefulStopTimeoutSeconds, CrashWindowMinutes, MaxCrashesInWindow, PollIntervalSeconds; bool AutoRestart, Enabled; }`

- [ ] **Step 1:** Add the `ServerConfig` class (fields from the spec) to `Entities.cs`.
- [ ] **Step 2:** Add `public Guid ServerId { get; set; }` to `Sample`, `SampleRollup`, `PlayerSession`, `EventLog`, `Schedule`.
- [ ] **Step 3:** Write test asserting a `ServerConfig` default has `Enabled == true`, `AutoRestart == true`, `BackupsToKeep == 20`.
- [ ] **Step 4:** `dotnet test --filter ServerConfigTests` — expect PASS.
- [ ] **Step 5:** Commit: `feat(data): ServerConfig entity + ServerId on per-server rows`.

### Task 1.2: `PanelDb` DbSet + indexes

**Files:**
- Modify: `src/PalPanel/Data/PanelDb.cs`

- [ ] **Step 1:** Add `public DbSet<ServerConfig> Servers => Set<ServerConfig>();`
- [ ] **Step 2:** In `OnModelCreating`, add composite indexes: `Sample (ServerId, Ts)`, `PlayerSession (ServerId, UserId)`, `EventLog (ServerId, Ts)`, `Schedule (ServerId)`.
- [ ] **Step 3:** `dotnet build`.
- [ ] **Step 4:** Commit: `feat(data): Servers DbSet + per-server indexes`.

### Task 1.3: Startup migration/seed (legacy → server row)

**Files:**
- Create: `src/PalPanel/Data/LegacyServerMigration.cs`
- Test: `tests/PalPanel.Tests/LegacyServerMigrationTests.cs`

**Interfaces:**
- Produces: `static class LegacyServerMigration { static Task<Guid> EnsureSeededAsync(PanelDb db, PanelOptions legacy, IDataProtector protector) }` — returns the (existing or newly seeded) server id; when `Servers` is empty, inserts one row from `legacy` and stamps all rows with `ServerId == Guid.Empty` to the new id. Idempotent.

- [ ] **Step 1:** Write failing test: given an in-memory `PanelDb` with a `Sample`/`EventLog` at `ServerId==Guid.Empty` and empty `Servers`, `EnsureSeededAsync` creates one `ServerConfig` and updates those rows to its id; running twice makes no further change.
- [ ] **Step 2:** Run — expect FAIL.
- [ ] **Step 3:** Implement `EnsureSeededAsync` (encrypt legacy `AdminPassword` into `AdminPasswordEnc`; copy paths/args/tunables; stamp `Guid.Empty` rows via `ExecuteUpdate`).
- [ ] **Step 4:** Run — expect PASS.
- [ ] **Step 5:** Commit: `feat(data): idempotent legacy→server seed migration`.

---

## Phase 2 — Per-server services

### Task 2.1: `PalApiClient` per-server config

**Files:**
- Modify: `src/PalPanel/PalApi/PalApiClient.cs`, `IPalApi.cs`
- Test: `tests/PalPanel.Tests/PalApiClientTests.cs` (extend)

**Interfaces:**
- Produces: `PalApiClient` no longer reads `IOptions<PanelOptions>`; takes `PalApiSettings(string BaseUrl, string AdminPassword)` in its ctor or a `Configure(PalApiSettings)` method so a per-server instance can be built by `ServerRuntime`.

- [ ] **Step 1:** Extract a `record PalApiSettings(string BaseUrl, string AdminPassword)`; change `PalApiClient` to accept it (keep the `HttpClient` injection).
- [ ] **Step 2:** Update existing `PalApiClientTests` to pass settings directly; run — expect PASS after impl.
- [ ] **Step 3:** Commit: `refactor(api): PalApiClient takes per-server settings`.

### Task 2.2: `ProcessSupervisor` per-server config

**Files:**
- Modify: `src/PalPanel/Supervisor/ProcessSupervisor.cs`
- Test: `tests/PalPanel.Tests/ProcessSupervisorTests.cs` (existing — keep green)

**Interfaces:**
- Produces: `ProcessSupervisor(IProcessLauncher launcher, SupervisorSettings settings)` where `SupervisorSettings` carries `ServerExePath, ServerArgs, ServerProcessName, GracefulStopTimeoutSeconds, CrashWindowMinutes, MaxCrashesInWindow`. Replaces `IOptions<PanelOptions>`. Behaviour (epoch fencing, crash tracker, adoption) unchanged.

- [ ] **Step 1:** Introduce `record SupervisorSettings(...)`; change ctor to take it; map the six `_o.*` reads to `settings`.
- [ ] **Step 2:** Update existing supervisor tests' construction to pass `SupervisorSettings`; run the full `ProcessSupervisorTests` — expect PASS (behaviour identical).
- [ ] **Step 3:** Commit: `refactor(supervisor): per-server SupervisorSettings, no IOptions`.

### Task 2.3: per-server `BackupService` + event sink

**Files:**
- Modify: `src/PalPanel/Control/BackupService.cs`, `src/PalPanel/Data/PanelDb.cs` (DbEventSink)
- Test: `tests/PalPanel.Tests/BackupServiceTests.cs`, `EventSinkTests.cs`

**Interfaces:**
- Produces: `BackupService(BackupSettings settings)` with `record BackupSettings(string SaveDirectory, string BackupDirectory, int BackupsToKeep)`. `DbEventSink` gains a `Guid serverId` so every `EventLog` row is stamped; add `class ServerEventSink(IDbContextFactory<PanelDb>, Guid serverId) : IEventSink`.

- [ ] **Step 1:** Extract `BackupSettings`; change `BackupService` ctor; update tests.
- [ ] **Step 2:** Add `ServerEventSink` that stamps `ServerId`; keep global `DbEventSink` for panel-level events (`ServerId = Guid.Empty`).
- [ ] **Step 3:** Run `BackupServiceTests` + `EventSinkTests` — expect PASS.
- [ ] **Step 4:** Commit: `refactor(control): per-server backup + event sink`.

### Task 2.4: `ServerRuntime` container

**Files:**
- Create: `src/PalPanel/Servers/ServerRuntime.cs`
- Test: `tests/PalPanel.Tests/ServerRuntimeTests.cs`

**Interfaces:**
- Consumes: `ProcessSupervisor`, `PalApiClient`, `BackupService`, `ServerEventSink`, `ServerOrchestrator`, `ServerConfig`.
- Produces: `class ServerRuntime { Guid Id; ServerConfig Config; ProcessSupervisor Supervisor; IPalApi Api; IServerOrchestrator Orchestrator; IBackupService Backups; ServerState State => Supervisor.State; } ` plus a factory `static ServerRuntime Build(ServerConfig cfg, IProcessLauncher launcher, IHttpClientFactory http, IDbContextFactory<PanelDb> dbf, IAdminGuard guard, IDataProtector protector)`.

- [ ] **Step 1:** Write failing test: `ServerRuntime.Build(cfg, …)` yields a runtime whose `Id == cfg.Id` and whose `Supervisor.State == Stopped`.
- [ ] **Step 2:** Run — FAIL.
- [ ] **Step 3:** Implement `Build`: decrypt `AdminPasswordEnc`, construct `PalApiClient` (via `http.CreateClient`), `ProcessSupervisor`, `ServerEventSink`, `BackupService`, `ServerOrchestrator`, wire `supervisor.OnEvent` to the sink.
- [ ] **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit: `feat(servers): ServerRuntime container + factory`.

### Task 2.5: `SnapshotStore` (keyed)

**Files:**
- Create: `src/PalPanel/Monitoring/SnapshotStore.cs` (replaces `SnapshotService`)
- Modify: consumers of `SnapshotService`
- Test: `tests/PalPanel.Tests/SnapshotStoreTests.cs`

**Interfaces:**
- Produces: `class SnapshotStore { ServerSnapshot Current(Guid id); void Publish(Guid id, ServerSnapshot s); event Action<Guid, ServerSnapshot>? Changed; IReadOnlyDictionary<Guid, ServerSnapshot> All(); }`

- [ ] **Step 1:** Failing test: `Publish(id, s)` then `Current(id) == s`; a subscriber to `Changed` receives `(id, s)`; unknown id returns a default Stopped snapshot.
- [ ] **Step 2:** Run — FAIL.
- [ ] **Step 3:** Implement keyed store (ConcurrentDictionary + event).
- [ ] **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit: `feat(monitoring): keyed SnapshotStore`.

### Task 2.6: `ServerManager`

**Files:**
- Create: `src/PalPanel/Servers/ServerManager.cs`
- Test: `tests/PalPanel.Tests/ServerManagerTests.cs`

**Interfaces:**
- Consumes: `IDbContextFactory<PanelDb>`, `IProcessLauncher`, `IHttpClientFactory`, `IAdminGuard`, `IDataProtector`, `ServerRuntime.Build`.
- Produces: `class ServerManager { Task InitializeAsync(); IReadOnlyCollection<ServerRuntime> All(); ServerRuntime? Get(Guid id); Task<Guid> AddAsync(ServerConfig cfg, string actor); Task UpdateAsync(ServerConfig cfg, string actor); Task RemoveAsync(Guid id, string actor); }`

- [ ] **Step 1:** Failing test: `InitializeAsync` loads enabled `ServerConfig` rows into runtimes; `AddAsync` persists a row and creates a runtime; `RemoveAsync` stops + drops it; `Get(unknown)` == null.
- [ ] **Step 2:** Run — FAIL.
- [ ] **Step 3:** Implement (ConcurrentDictionary of runtimes; persist via db; on remove call `Orchestrator.StopAsync` best-effort then dispose).
- [ ] **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit: `feat(servers): ServerManager lifecycle`.

---

## Phase 3 — Multi-server poller + scheduler

### Task 3.1: `PollerService` iterates runtimes

**Files:**
- Modify: `src/PalPanel/Monitoring/PollerService.cs`
- Test: `tests/PalPanel.Tests/PollerServiceTests.cs`

**Interfaces:**
- Consumes: `ServerManager`, `SnapshotStore`.
- Produces: `PollerService` ticks each runtime independently; per-runtime `_online`/`_apiWasReachable` state keyed by `Guid`; publishes to `SnapshotStore.Publish(id, …)`; per-server try/catch so one server's failure never stalls others. `Sample`/`PlayerSession`/`EventLog` writes stamp `ServerId`.

- [ ] **Step 1:** Failing test: two runtimes, one API reachable and one not; after a tick, store has a snapshot for both, the reachable one has a `Sample` row with the right `ServerId`, and the unreachable one is `ApiReachable == false` (no throw).
- [ ] **Step 2:** Run — FAIL.
- [ ] **Step 3:** Refactor `TickAsync` to loop runtimes, moving per-server fields into a keyed dictionary; stamp `ServerId` on all writes.
- [ ] **Step 4:** Run full `PollerServiceTests` — PASS.
- [ ] **Step 5:** Commit: `refactor(monitoring): poller iterates all server runtimes`.

### Task 3.2: `SchedulerService` per-server

**Files:**
- Modify: `src/PalPanel/Control/SchedulerService.cs`
- Test: `tests/PalPanel.Tests/SchedulerServiceTests.cs`

**Interfaces:**
- Consumes: `ServerManager`. Each `Schedule` row's `ServerId` selects the runtime whose `Orchestrator`/`Backups` the action targets.

- [ ] **Step 1:** Failing test: a `Schedule{ServerId=A, Action=restart}` due now invokes runtime A's orchestrator, not B's.
- [ ] **Step 2:** Run — FAIL.
- [ ] **Step 3:** Refactor scheduler to resolve the runtime by `ServerId` per due schedule.
- [ ] **Step 4:** Run — PASS.
- [ ] **Step 5:** Commit: `refactor(control): per-server scheduling`.

---

## Phase 4 — DI rewire

### Task 4.1: Program.cs multi-server wiring

**Files:**
- Modify: `src/PalPanel/Program.cs`, `src/PalPanel/PanelOptions.cs`
- Test: existing `WebApplicationFactory` smoke tests must stay green.

**Interfaces:**
- Consumes: everything above.
- Produces: `ServerManager` singleton (initialized at startup after `EnsureCreated` + `LegacyServerMigration.EnsureSeededAsync`); `SnapshotStore` singleton; poller/scheduler resolve `ServerManager`. `PanelOptions` loses per-server fields (`ServerExePath`, `ServerArgs`, `ServerProcessName`, `SaveDirectory`, `BackupDirectory`, `BackupsToKeep`, `ApiBaseUrl`, `AdminPassword`, crash/graceful tunables) — retained only as the seed source for `LegacyServerMigration`, read once from configuration.

- [ ] **Step 1:** Remove singleton registrations of `ProcessSupervisor`, `IPalApi`, `IBackupService`, `IServerOrchestrator`, `SnapshotService`; register `SnapshotStore`, `ServerManager`, `IProcessLauncher`, `IDataProtector` (data protection).
- [ ] **Step 2:** After `EnsureCreated()`, call `LegacyServerMigration.EnsureSeededAsync` then `serverManager.InitializeAsync()`. Remove the direct `ProcessSupervisor` adoption block (adoption now runs inside each runtime's supervisor on `InitializeAsync`).
- [ ] **Step 3:** Update the `/backups/download/{file}` endpoint to resolve the server by a `?server={id}` query (or route) and use that runtime's `Backups`.
- [ ] **Step 4:** `dotnet build` + run the full suite — expect green (fix fallout).
- [ ] **Step 5:** Commit: `refactor(di): multi-server composition root`.

---

## Phase 5 — IA / routing / server CRUD

### Task 5.1: Fleet dashboard page

**Files:**
- Create: `src/PalPanel/Components/Pages/Fleet.razor` (`@page "/"`)
- Modify: `src/PalPanel/Components/Pages/Overview.razor` → `@page "/s/{Id:guid}"`
- Test: `tests/PalPanel.Tests/` render/component test if present, else WAF smoke.

- [ ] **Step 1:** Move current `Overview` root route to `/s/{Id:guid}`; resolve runtime via `ServerManager.Get(Id)`, subscribe to `SnapshotStore.Changed` filtered to `Id`.
- [ ] **Step 2:** Create `Fleet.razor` at `/`: list `ServerManager.All()`, one card each (state, players, memory sparkline, uptime, Admin Start/Stop), empty state, links to `/s/{id}`.
- [ ] **Step 3:** `dotnet build`; smoke test `/` renders.
- [ ] **Step 4:** Commit: `feat(ui): fleet dashboard + per-server overview route`.

### Task 5.2: Per-server sub-pages routing

**Files:**
- Modify: `Players.razor`, `History.razor`, `Backups.razor` → `@page "/s/{Id:guid}/…"`; scope queries by `ServerId`.
- Modify: `NavMenu.razor` — fleet link + in-server nav + server switcher dropdown.

- [ ] **Step 1:** Add `{Id:guid}` param + `ServerId` filtering to each sub-page's DB queries.
- [ ] **Step 2:** Update `NavMenu` to show fleet + (when in a server) the server name, sub-nav, and a switcher.
- [ ] **Step 3:** Build + smoke.
- [ ] **Step 4:** Commit: `feat(ui): per-server sub-pages + nav switcher`.

### Task 5.3: Server CRUD pages

**Files:**
- Create: `Components/Pages/ServerNew.razor` (`/servers/new`), `ServerEdit.razor` (`/servers/{Id:guid}/edit`)
- Modify: `Settings.razor` (split per-server vs global)

- [ ] **Step 1:** `ServerNew`: Admin-guarded form → `ServerManager.AddAsync`; validate paths/URL; encrypt admin password.
- [ ] **Step 2:** `ServerEdit`: load config, `UpdateAsync`, and Remove (confirm) → `RemoveAsync`.
- [ ] **Step 3:** Move per-server tunables/schedules to `/s/{id}/settings`; keep users/OAuth/cookies at `/settings`.
- [ ] **Step 4:** Build + smoke (add a second server, see two fleet cards).
- [ ] **Step 5:** Commit: `feat(ui): server add/edit/remove + settings split`.

---

## Phase 6 — Visual design system

### Task 6.1: Design tokens

**Files:**
- Create: `src/PalPanel/wwwroot/tokens.css`
- Modify: `src/PalPanel/wwwroot/app.css`, `Components/App.razor` (link tokens first)

- [ ] **Step 1:** Define CSS custom properties: color ramp (surface/elevated/border/text tiers), status colors (`--ok`/`--warn`/`--bad`), type scale, spacing scale; `prefers-color-scheme` dark default + light override + `[data-theme]` explicit toggle.
- [ ] **Step 2:** Rebuild `app.css` on top of tokens (buttons primary/danger/ghost, inputs, tables, alert, dialog).
- [ ] **Step 3:** Build + eyeball.
- [ ] **Step 4:** Commit: `feat(ui): design tokens + restyled primitives`.

### Task 6.2: Fleet card + chart theme + states

**Files:**
- Modify: `Fleet.razor` + `.razor.css`, `wwwroot/js/charts.js`, `Overview.razor`

- [ ] **Step 1:** Style fleet cards (status edge, sparkline, quick actions); add loading skeleton + empty state.
- [ ] **Step 2:** Drive Chart.js grid/axis/line colors from tokens (read CSS vars in `charts.js`); add card sparkline renderer.
- [ ] **Step 3:** Add explicit loading/empty/error states to Overview/Players/History/Backups.
- [ ] **Step 4:** Build + eyeball light & dark.
- [ ] **Step 5:** Commit: `feat(ui): fleet cards, themed charts, async states`.

### Task 6.3: Theme toggle + polish pass

**Files:**
- Modify: `MainLayout.razor`, `NavMenu.razor`

- [ ] **Step 1:** Add a light/dark toggle persisting to `localStorage` (`data-theme` on `<html>`).
- [ ] **Step 2:** Consistency sweep (spacing, focus states, danger confirms).
- [ ] **Step 3:** Commit: `feat(ui): theme toggle + polish`.

---

## Phase 7 — Verify & publish

### Task 7.1: Full verification

- [ ] `dotnet test` — all green (existing 135 + new).
- [ ] Manual: run locally, add a 2nd server, drive start/stop on both from the fleet, kill the WS and confirm reconnect/reload.
- [ ] Update `CLAUDE.md` (standing directive: docs current with the change).
- [ ] Commit: `docs: CLAUDE.md multi-server + design system`.

### Task 7.2: Publish

- [ ] `dotnet publish -c Release`.
- [ ] Open PR from `feature/multiserver-redesign`.
- [ ] Deploy: stop service, swap `publish/`, apply tunnel `http1.1` ingress fix, start service, verify `panel.iamfatness.us` interactive.

---

## Self-Review

- **Spec coverage:** transport (Ph0), multi-server core + data + migration (Ph1–2), poller/scheduler (Ph3), DI (Ph4), fleet IA + CRUD (Ph5), design system (Ph6), verify/publish (Ph7). All spec sections mapped.
- **Type consistency:** `SnapshotStore.Publish(Guid,ServerSnapshot)`/`Current(Guid)`/`Changed:Action<Guid,ServerSnapshot>` used consistently in 2.5, 3.1, 5.1. `ServerRuntime.Build(...)` signature consistent in 2.4/2.6. `SupervisorSettings`/`PalApiSettings`/`BackupSettings`/`BackupSettings` records introduced before use.
- **Risk note:** Phase 2/4 are the highest-risk (deep supervisor refactor + composition-root surgery); keep the suite green between each task and do not proceed past a red bar.
