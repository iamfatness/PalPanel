# PalPanel Multi-Server + UI Redesign — Design

**Date:** 2026-07-24
**Status:** Approved design, pre-implementation
**Owner:** John Wallace
**Builds on:** `2026-07-21-palpanel-design.md` (core) and
`2026-07-22-app-native-login-design.md` (auth). Auth model, tunnel exposure, and
the supervisor/orchestrator/poller/backup/scheduler concepts are retained; this
spec makes them **multi-server** and re-skins the UI.

## Why

PalPanel v1/v2 shipped and are live at `panel.iamfatness.us`, but three things
block it from being the panel the owner wants:

1. **Interactivity is unreliable through the tunnel.** Every page is
   `@rendermode InteractiveServer`, so the UI runs over a SignalR WebSocket
   circuit. Through the Cloudflare tunnel that circuit drops and buttons die.
2. **It manages only one server.** The supervisor, orchestrator, poller,
   scheduler, and snapshot service are singletons bound to a single Palworld
   process via `PanelOptions`. The owner runs / wants to run several servers on
   the one host (`192.168.1.50`).
3. **The UI is functional but templated.** The owner wants a deliberate,
   cohesive look with an at-a-glance fleet view.

Sequencing is **transport first**: harden the circuit so the panel is reliable,
then land the multi-server refactor and the visual system on top.

## What changes / what stays

- **Kept:** Blazor Server + `InteractiveServer` render mode; ASP.NET cookie +
  Google auth; `PanelUser` + global roles (Admin/Viewer/Blocked) and all
  server-side action guards; DPAPI secret encryption; Cloudflare Tunnel
  `panel.iamfatness.us → http://localhost:5080`; xUnit/TDD workflow; the existing
  135 tests stay green (this is a refactor, not a rewrite of the domain logic).
- **Changed:** per-server services become per-instance behind a `ServerManager`;
  `PanelOptions` loses its per-server fields to a DB-backed `ServerConfig`;
  routing gains a fleet home + `/s/{id}` scoping; the CSS is replaced by a token
  design system.
- **Removed:** the single-server assumption embedded in DI (direct singleton
  injection of `ServerOrchestrator`/`SnapshotService`/`ProcessSupervisor`).

## Decisions locked in brainstorming

| Question | Decision |
|---|---|
| Primary goal | Both reliability and redesign — **transport first** |
| Multi-server | **Full multi-server now**, multiple instances on the one host |
| Transport | **Keep Blazor Server**, harden the tunnel + circuit (no WASM rewrite) |
| Navigation | **Fleet home + drill-in** (`/`, `/s/{id}/…`) |
| Server config | **UI-managed, DB-backed** (`ServerConfig` table, DPAPI secrets) |
| Roles | **Global**, not per-server (YAGNI) |
| Polling | **One shared poller** iterating all servers |
| Fleet cards | Include **per-server Start/Stop** quick actions for Admins |
| Visual | **Full design-system reskin**, dark-first + light-aware |

---

## Architecture — multi-server core

Today's per-server singletons collapse into per-instance runtimes behind a
manager.

```
ServerManager (singleton)
  ├─ Dictionary<Guid, ServerRuntime>          (one per enabled server)
  ├─ AddServerAsync / UpdateServerAsync / RemoveServerAsync
  │       → creates / reconfigures / tears down a runtime + persists ServerConfig
  ├─ Get(Guid) / All()                        → resolve runtime(s) for the UI
  └─ raises SnapshotStore changes keyed by serverId

ServerRuntime (one per server) owns:
  ├─ ServerConfig        (paths, args, api url, admin pw, backup path, tunables)
  ├─ ProcessSupervisor   (that server's process + CrashTracker)
  ├─ IServerOrchestrator (start/stop/save/announce for that server)
  ├─ IBackupService      (that server's backup dir + retention)
  └─ latest ServerSnapshot
```

- **`SnapshotService` → `SnapshotStore`** (singleton), keyed by `Guid`. `Current`
  becomes `Current(Guid)`; `Changed` becomes `Action<Guid, ServerSnapshot>`. The
  fleet page subscribes to all changes; a `/s/{id}` page filters to its id.
- **One `PollerService`** (hosted) iterates `ServerManager.All()` each tick,
  polls each server's REST API, publishes per-runtime snapshots. A slow/broken
  server must not stall the others (poll them independently; per-server
  try/catch; unreachable → snapshot marked `ApiReachable = false`, not an
  exception).
- **One `SchedulerService`** (hosted), but `Schedule` rows are keyed by
  `ServerId`; each tick evaluates each server's crons against that server's
  runtime.
- **Crash isolation:** each runtime has its own `CrashTracker`; one server's
  crash-loop `Held` state never affects another.
- **DI:** `ServerManager` is the only singleton entry point; per-server services
  are constructed inside each `ServerRuntime` (manager-owned factory), not
  registered as ambient singletons. `PanelOptions` keeps only panel-global
  fields (DB path, auth, Google, cookie, poll interval default).

## Data model

New entity — the Servers table:

```csharp
public class ServerConfig {
    public Guid   Id { get; set; }
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public string LaunchArgs { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public int    BackupsToKeep { get; set; } = 20;
    public string ApiBaseUrl { get; set; } = "";
    public string AdminPasswordEnc { get; set; } = "";   // DPAPI, as today
    public int    GracefulStopTimeoutSeconds { get; set; } = 60;
    public int    CrashWindowMinutes { get; set; } = 10;
    public int    MaxCrashesInWindow { get; set; } = 3;
    public int    PollIntervalSeconds { get; set; } = 10;
    public bool   AutoRestart { get; set; } = true;
    public bool   Enabled { get; set; } = true;
}
```

Add `public Guid ServerId { get; set; }` to `Sample`, `SampleRollup`,
`PlayerSession`, `EventLog`, and `Schedule`. `PanelUser` and roles stay global.

**Migration (zero data loss):** on first startup after upgrade, if a
`ServerConfig` table is empty but legacy `PanelOptions` server values exist, seed
one `ServerConfig` row from those values and stamp every existing
`Sample`/`SampleRollup`/`PlayerSession`/`EventLog`/`Schedule` row with that
server's `Id`. Existing history and schedules carry over intact.

---

## Transport hardening (phase 1 — do first)

Keep the Blazor Server circuit but stop relying on luck.

1. **Diagnose the real drop.** Reproduce through the tunnel with browser devtools
   + `cloudflared` logs and identify the failing stage: SignalR `negotiate`, the
   WebSocket upgrade, or a mid-session drop. Prime suspect is the tunnel origin
   defaulting to **HTTP/2**, which breaks WS upgrade — pinning ingress to
   `http1.1` for the app origin is the expected fix.
2. **Configure the tunnel correctly** from that finding (ingress `http1.1`,
   adequate idle/connection timeouts) and **explicitly permit SignalR
   long-polling fallback** so a degraded WebSocket downgrades to a working
   transport instead of a dead UI.
3. **Harden circuit + reconnect UX.** Tune `CircuitOptions`
   (`DisconnectedCircuitRetentionPeriod`) and the Blazor client reconnect (retry
   cadence + full-page reload fallback after N retries). Replace the bare
   `ReconnectModal` with an explicit "Reconnecting… / Reconnect / Reload" state —
   a blip must never leave dead buttons with no cause (loud-not-silent).

**Acceptance:** kill the WebSocket mid-session → the panel visibly recovers or
offers reload; control buttons work end-to-end through `panel.iamfatness.us`.

---

## Information architecture & routing

```
/                     Fleet dashboard — a card per server
/servers/new          Add-server form              (Admin)
/servers/{id}/edit    Edit / remove server config  (Admin)
/s/{id}               Overview     (scoped to server)
/s/{id}/players       Players
/s/{id}/history       History / event log
/s/{id}/backups       Backups
/s/{id}/settings      Per-server settings (schedules, tunables)
/settings             Panel settings (users/roles, Google, cookies — global)
/login /setup /change-password                     (unchanged)
```

- **Fleet cards**: live status (color-coded), player count, memory sparkline,
  uptime, and Admin quick Start/Stop. Unknown/disabled servers render distinctly.
- **Nav**: fleet link always present; inside `/s/{id}` show the server name + a
  switcher dropdown to hop between servers without returning to the fleet.
- Existing pages (Overview/Players/History/Backups) refactor to take an `{id}`
  route param and resolve their `ServerRuntime` from `ServerManager`; an unknown
  id → NotFound.
- **Settings splits:** per-server settings under the server; global panel
  settings (user admin, OAuth, cookies) stay at `/settings`.

---

## Visual design system (full reskin)

A small deliberate token layer, dark-first and light-aware — not a framework.

- **Tokens (CSS custom properties):** color ramp (surface/elevated/border/text
  tiers), semantic status colors (running=green, starting/held=amber,
  stopped/crashed=red — reused by cards, badges, and charts), a type scale, and a
  spacing scale. Light + dark via `prefers-color-scheme` plus an explicit toggle.
- **Fleet card** = signature component: status-tinted edge, name, big player
  count, memory sparkline (mini Chart.js on the shared theme), uptime, Admin
  quick-actions. Explicit **empty state** ("No servers yet — Add one") and
  **loading skeletons**.
- **Restyled Chart.js theme** driven by the same tokens so Overview charts and
  card sparklines match.
- **Unified primitives:** buttons (primary/danger/ghost), inputs, tables
  (Players/History), `ConfirmDialog`, badges, and the existing inline-alert
  error surface — restyled once, reused everywhere. Destructive actions
  (Stop/Restart/Remove) get consistent danger styling + confirm.
- **Every async view** gets explicit loading / empty / error states — no blank
  flashes.
- **Delivery:** replace ad-hoc `app.css` + scattered `.razor.css` with
  `tokens.css` + a small component-style set; keep Blazor CSS isolation where it
  earns its place.

---

## Testing & migration safety

- **Manager lifecycle:** add / update / remove creates and tears down runtimes
  and persists `ServerConfig`.
- **Isolation:** poller iterates multiple runtimes; one unreachable/slow server
  doesn't stall others; one server's crash-loop `Held` doesn't affect another.
- **Per-server routing:** snapshots publish to the right server id; a `/s/{id}`
  page only reacts to its own server's changes.
- **Scheduling:** crons are evaluated per server against the right runtime.
- **Migration:** legacy rows get stamped to the seeded server; zero data loss.
- **Auth/authz:** per-server Admin actions still gated; global roles unchanged;
  the 135 existing tests stay green.
- **Transport:** documented manual acceptance (kill-the-socket recovery through
  the tunnel), since it's environmental.
- **TDD throughout**, consistent with how v1/v2 were built.

## Out of scope (explicit)

- Blazor WASM / REST rewrite (transport decision was to keep Server).
- Servers on **other hosts** / remote-agent control (single host only).
- Per-server roles/permissions (roles stay global).
- Unified cross-server aggregate pages (fleet cards + drill-in only).
