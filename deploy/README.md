# PalPanel deployment

Scripts and steps for building and installing PalPanel on the Palworld host
(192.168.1.50). For exposing it to the internet via Cloudflare, see
[`docs/setup-cloudflare.md`](../docs/setup-cloudflare.md).

## Contents

- `publish.ps1` -- builds a self-contained win-x64 publish output.
- `install-service.ps1` -- installs/starts the `PalPanel` Windows service
  (run as Administrator on the target machine). Use for a **first** install.
- `update-service.ps1` -- **updates an already-installed** service in place:
  stops it, snapshots `C:\PalPanel\app` to `app.bak-<timestamp>`, copies the new
  build over it while preserving live data (`palpanel.db`, `dp-keys\`,
  `appsettings.Local.json`), restarts, and health-checks `http://localhost:5080`.
  Self-elevates via UAC. This is the routine "ship a new version" path.

## Updating an existing install

```powershell
.\deploy\publish.ps1          # build the new version
.\deploy\update-service.ps1   # stop -> back up -> copy -> start -> health-check (self-elevates)
```

`update-service.ps1` never touches `palpanel.db`, `dp-keys\`, or
`appsettings.Local.json` (it excludes them from the copy and never uses
`robocopy /MIR`), and always snapshots the old install first, so a bad deploy
rolls back with `robocopy <app.bak-...> C:\PalPanel\app /MIR`. Restarting the
panel service does **not** restart the Palworld game server (the panel re-adopts
`PalServer.exe` on startup), so there is no player-facing downtime.

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
   `Panel:BackupDirectory` (and, if using Google sign-in,
   `Panel:GoogleClientId` / `Panel:GoogleClientSecret`) -- see
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
- [ ] **First-run setup creates the owner**: on a fresh install (empty
      database), open `https://panel.iamfatness.us`. Confirm you land on
      `/setup`, not `/login` or the dashboard. Create the owner account
      (email + password). Confirm you're signed in immediately afterward and
      land on the dashboard in full **Admin** mode -- and that browsing to
      `/setup` again now redirects straight to `/login` instead of offering
      to create a second owner.
- [ ] **Owner adds a Google-only friend and a password friend**: from the
      owner's Admin session, go to Settings -> Users and add two accounts:
      one with a Google account's email and **no** password (Google-only),
      one with an email and an initial password (password path). Both should
      default to **Viewer**.
- [ ] **Google path**: from a phone on cellular data (not the home Wi-Fi),
      open `https://panel.iamfatness.us`, click "Sign in with Google", and
      complete the flow with the Google account added above. Confirm it
      signs straight in (no separate Cloudflare login screen -- PalPanel's
      own login is the only gate) and the dashboard loads in **read-only**
      Viewer mode -- no Start/Stop/Restart/Kick/Ban/Settings controls.
- [ ] **Password path**: sign out, then sign in as the password friend using
      the initial password. Confirm you're forced to `/change-password`
      before reaching the dashboard, and that after setting a new password
      you land on the dashboard as Viewer.
- [ ] **Promote to Admin**: from the owner's own (Admin) session, go to
      Settings -> Users and promote one of the two friend accounts to Admin.
- [ ] **Admin controls work**: on the promoted (or owner's) Admin session,
      exercise each mutating control at least once: Announce (message shows
      in-game), Save, Kick a connected test player if available, and confirm
      each action is recorded in the History/event log with the acting
      user's email.
- [ ] **Block revokes access**: from the owner's Admin session, set one
      friend's role to **Blocked**. Confirm: if that friend has the panel
      open in another browser/tab right now, their session loses the UI
      immediately (no page reload needed); and if they try to sign in again
      (either Google or password), the login is refused and they land back
      on `/login?denied=1`.
- [ ] **Unknown Google account denied**: from a browser signed into a Google
      account that was never added under Settings -> Users, click "Sign in
      with Google" on the login page. Confirm the flow completes with Google
      but PalPanel denies the sign-in and redirects to `/login?denied=1` --
      no session is created and no new user row is silently added.
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
