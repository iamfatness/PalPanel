# PalPanel deployment

Scripts and steps for building and installing PalPanel on the Palworld host
(192.168.1.50). For exposing it to the internet via Cloudflare, see
[`docs/setup-cloudflare.md`](../docs/setup-cloudflare.md).

## Contents

- `publish.ps1` -- builds a self-contained win-x64 publish output.
- `install-service.ps1` -- installs/starts the `PalPanel` Windows service
  (run as Administrator on the target machine).

## Install steps

1. On a build machine (or the target machine itself), from the repo root:

   ```powershell
   .\deploy\publish.ps1
   ```

   This publishes to `.\publish` by default (`-OutDir` to override).

2. Copy the contents of the output directory to the target machine, e.g.
   `C:\PalPanel\app` (the default `install-service.ps1` expects).

3. Create `C:\PalPanel\app\appsettings.Local.json` with at least
   `Panel:AdminPassword`, `Panel:ServerExePath`, `Panel:SaveDirectory`,
   `Panel:BackupDirectory` (and, once the Cloudflare Access app exists,
   `Panel:AccessTeamDomain` / `Panel:AccessAud`) -- see
   `docs/setup-cloudflare.md` for the exact values for this box. This file
   is gitignored and must never be committed; it is the only place secrets
   live (no secrets in `appsettings.json`, ever).

4. On the target machine, in an **Administrator** PowerShell:

   ```powershell
   .\deploy\install-service.ps1
   ```

   This creates the `PalPanel` service (`start=auto`), configures failure
   recovery (restart at 5s/15s/60s backoff), and starts it.

   **It is safe to run this while PalServer.exe is already running.**
   PalPanel adopts an already-running Palworld server on startup instead of
   launching a second copy, so there's no need to stop the game server
   first, and no player-facing downtime from installing the panel itself.

5. Confirm `http://localhost:5080` responds on the target machine before
   moving on to the Cloudflare tunnel/Access setup.

## Manual acceptance checklist

Run this end-to-end on 192.168.1.50 before considering a release done. Each
step should be verified against the live dashboard, not assumed.

- [ ] **Install**: `install-service.ps1` completes without error; `sc query
      PalPanel` shows `RUNNING`; `http://localhost:5080` loads.
- [ ] **Adopt/start**: with PalServer.exe already running before install,
      the Overview page shows it as `Running` (adopted, not restarted) once
      the panel comes up. If PalServer.exe was stopped, use the dashboard's
      Start control and confirm it transitions `Stopped -> Starting ->
      Running`.
- [ ] **Fresh login lands as Viewer**: from a phone on cellular data (not
      the home Wi-Fi), open `https://panel.iamfatness.us` and sign in with a
      Google account (or email OTP) that has never logged in before.
      Confirm: Cloudflare Access login screen appears, login succeeds, and
      the dashboard loads in **read-only** mode -- status/players/charts
      visible, no Start/Stop/Restart/Kick/Ban/Settings controls.
- [ ] **Promote to Admin**: from the owner's own (Admin) session, go to
      Settings -> Users and promote that Viewer account to Admin.
- [ ] **Admin controls work**: on the promoted (or owner's) Admin session,
      exercise each mutating control at least once: Announce (message shows
      in-game), Save, Kick a connected test player if available, and confirm
      each action is recorded in the History/event log with the acting
      user's email.
- [ ] **Crash + auto-restart**: end `PalServer.exe` via Task Manager (kill
      the whole process tree, including the `PalServer-Win64-Shipping-Cmd`
      child). Confirm: a `crash` event appears in the log within a few
      seconds, the state machine shows `Crashed` then cycles back through
      `Starting -> Running`, and the REST API answers again once restarted.
- [ ] **Flap protection**: repeat the kill three times within 10 minutes.
      Confirm: after the 3rd crash the server enters `Held`, stops
      auto-retrying, and this is surfaced prominently (banner/badge) on the
      Overview page -- not just buried in the log.
- [ ] **Scheduled restart with warnings**: trigger (or wait for) a scheduled
      restart. Confirm: in-game announce warnings appear at T-10, T-5, T-1
      minutes, followed by save -> graceful shutdown -> relaunch -> a
      `restart-complete` event once the API responds again.
- [ ] **Pre-restart backup appears**: after the scheduled restart above,
      confirm a new backup entry appears on the Backups page timestamped
      just before the restart, with a reasonable file size (not 0 bytes).
- [ ] **Download + restore round-trip**: download that backup from the
      Backups page. Stop the server from the dashboard, use Restore on a
      (different, older) backup, confirm it snapshots the current save
      first, then restores; start the server back up and confirm it comes
      back healthy on the restored save.

All boxes checked = release accepted.
