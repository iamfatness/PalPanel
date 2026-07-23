# PalPanel setup: Palworld REST API, Cloudflare Tunnel, and Google OAuth

Operator runbook for exposing PalPanel (running on 192.168.1.50 as the
`PalPanel` Windows service, listening on `http://localhost:5080`) to the
internet at `https://panel.iamfatness.us`.

PalPanel gates itself now: it has its own app-native login (password + optional
Google sign-in, cookie sessions) with a first-run `/setup` flow that creates
the owner account. **The Cloudflare Access application that used to sit in
front of the tunnel is gone** -- do not create one (or, if this box still has
one from before app-native login shipped, delete it: Zero Trust -> Access ->
Applications -> the `panel.iamfatness.us` application -> Delete). PalPanel's
own login is the gate now; leaving an Access application in place would just
add a redundant, unmaintained second login in front of it. The Cloudflare
Tunnel itself (Parts A/B below) stays -- it's still how the box reaches the
internet without an inbound port.

Security note: `AdminPassword` and (if using Google sign-in) `GoogleClientId`/
`GoogleClientSecret` are secrets/environment-specific config. They live
**only** in `appsettings.Local.json` (gitignored, never committed) or a
DPAPI-protected store -- never in `appsettings.json` or any file checked into
the repo. This document uses placeholders (`<...>`) for all secret values; do
not paste real secrets into any committed doc, issue, or commit message.

---

## Part A -- Palworld REST API prerequisites

PalPanel talks to the Palworld dedicated server over its built-in REST API
on `localhost:8212`. This must be enabled before PalPanel can supervise or
poll the server.

1. Find the config file on this box:

   ```
   D:\SteamLibrary\steamapps\common\PalServer\Pal\Saved\Config\WindowsServer\PalWorldSettings.ini
   ```

2. In the `[/Script/Pal.PalGameWorldSettings]` section, ensure these keys are
   set (they are already set on this box, but verify after any server
   reinstall or settings reset):

   ```
   RESTAPIEnabled=True
   RESTAPIPort=8212
   AdminPassword="<strong password>"
   ```

3. Restart PalServer.exe for the settings to take effect (or let PalPanel do
   this later once installed).

4. Verify the API responds, from the same machine:

   ```
   curl -u admin:<strong password> http://127.0.0.1:8212/v1/api/info
   ```

   A successful call returns JSON with the server name/version (this box
   reports `"The Fat Shack"`, version `v1.0.1.100619`). If you get a
   connection refused or 401, re-check `RESTAPIEnabled`/`RESTAPIPort`/
   `AdminPassword` and that the server has been restarted since editing the
   ini.

   For reference, the full set of endpoints PalPanel uses:

   | Method | Endpoint | Purpose |
   |---|---|---|
   | GET | `/v1/api/info` | server name/version, used for health/adoption checks |
   | GET | `/v1/api/players` | connected players |
   | GET | `/v1/api/metrics` | FPS, frame time, memory, uptime |
   | GET | `/v1/api/settings` | current world settings |
   | POST | `/v1/api/announce` | in-game broadcast message |
   | POST | `/v1/api/save` | force a world save |
   | POST | `/v1/api/shutdown` | graceful shutdown with a wait time |
   | POST | `/v1/api/stop` | immediate stop |
   | POST | `/v1/api/kick` | kick a player |
   | POST | `/v1/api/ban` / `/v1/api/unban` | ban/unban a player |

   All calls use HTTP Basic auth with username `admin` and the
   `AdminPassword` above.

5. Put the **same** password into PalPanel's local config on the install
   machine, `appsettings.Local.json` next to `PalPanel.exe` (create the file
   if it doesn't exist -- it is gitignored and never committed):

   ```json
   {
     "Panel": {
       "AdminPassword": "<same strong password as above>",
       "ServerExePath": "D:\\SteamLibrary\\steamapps\\common\\PalServer\\PalServer.exe",
       "SaveDirectory": "D:\\SteamLibrary\\steamapps\\common\\PalServer\\Pal\\Saved",
       "BackupDirectory": "C:\\PalPanel\\Backups"
     }
   }
   ```

   These four values plus, optionally, `GoogleClientId`/`GoogleClientSecret`
   (Part C) are the only settings that typically need to be filled in
   per-install; everything else has a sane default in `appsettings.json`.

---

## Part B -- Cloudflare Tunnel (cloudflared)

This exposes the panel without opening any inbound port on the router/UDM.

1. Install `cloudflared`:

   ```
   winget install Cloudflare.cloudflared
   ```

2. Authenticate to the Cloudflare account that owns the zone:

   ```
   cloudflared tunnel login
   ```

   This opens a browser -- sign in as `wallace.john.w@gmail.com` and select
   the `iamfatness.us` zone when prompted. A certificate is saved to
   `%USERPROFILE%\.cloudflared\cert.pem`.

3. Create the tunnel:

   ```
   cloudflared tunnel create palpanel
   ```

   This prints a tunnel ID and writes a credentials JSON file (typically
   `%USERPROFILE%\.cloudflared\<tunnel-id>.json`) -- note the path, it's
   referenced in the config below.

4. Create `%USERPROFILE%\.cloudflared\config.yml`:

   ```yaml
   tunnel: palpanel
   credentials-file: C:\Users\<user>\.cloudflared\<tunnel-id>.json

   ingress:
     - hostname: panel.iamfatness.us
       service: http://localhost:5080
     - service: http_status:404
   ```

   The catch-all `http_status:404` rule at the end is required -- cloudflared
   refuses to run without a final rule that matches any hostname.

5. Route DNS for the hostname to this tunnel (this creates/updates the CNAME
   for `panel.iamfatness.us` in the `iamfatness.us` zone -- distinct from the
   existing `palworld.iamfatness.us` A record, which is unrelated and stays
   as-is):

   ```
   cloudflared tunnel route dns palpanel panel.iamfatness.us
   ```

6. Install cloudflared as a Windows service so it survives reboot.

   ```
   cloudflared service install
   ```

   **Important -- config file placement.** The Windows service runs as
   `LocalSystem`, which reads its config from
   `C:\Windows\System32\config\systemprofile\.cloudflared\`, **not** from
   `%USERPROFILE%\.cloudflared\` where `cloudflared tunnel login` /
   `tunnel create` wrote `cert.pem`, the `<tunnel-id>.json` credentials
   file, and where you created `config.yml`. Copy them over before starting
   the service:

   ```powershell
   $dst = "C:\Windows\System32\config\systemprofile\.cloudflared"
   New-Item -ItemType Directory -Force -Path $dst | Out-Null
   Copy-Item "$env:USERPROFILE\.cloudflared\config.yml"        $dst
   Copy-Item "$env:USERPROFILE\.cloudflared\cert.pem"          $dst
   Copy-Item "$env:USERPROFILE\.cloudflared\<tunnel-id>.json"  $dst
   ```

   (Also make sure `credentials-file:` in the copied `config.yml` points at
   the credentials JSON in this `systemprofile` path, or use an absolute path
   that resolves the same from both locations.)

   Then start the service:

   ```
   sc start cloudflared
   ```

   Simpler alternative: token-based service install avoids the file-placement
   step entirely. Copy the tunnel's token from the Cloudflare Zero Trust
   dashboard (Networks -> Tunnels -> your tunnel -> Configure) and run
   `cloudflared service install <TOKEN>`. In that mode the tunnel config
   (ingress rules) is managed in the dashboard rather than the local
   `config.yml`, so set the `panel.iamfatness.us -> http://localhost:5080`
   public hostname there instead. This runbook keeps the config-file path as
   primary because it keeps the ingress rules in version-controllable local
   files.

7. Verify: from a machine off the LAN (e.g. phone on cellular data), browse
   to `https://panel.iamfatness.us`. You should land on PalPanel's own
   `/setup` page (first run) or `/login` page -- see "First-run setup" below.
   PalPanel itself is the only gate now; there is no separate Cloudflare
   Access login screen in front of it.

   PalPanel runs behind the tunnel over plain HTTP on localhost, so it needs
   to trust Cloudflare's `X-Forwarded-*` headers to know the original request
   was HTTPS (for secure cookies, correct redirect URIs, etc.) -- this is
   already handled by the app itself (`app.UseForwardedHeaders()` in
   `Program.cs`); there is nothing to configure here.

---

## Part C -- Create a Google OAuth client (optional)

PalPanel's login page always offers password sign-in. Google sign-in is
**optional** but recommended for friends/family so they don't need to
remember a password -- it requires a Google OAuth client, set up once per
install.

1. Go to the [Google Cloud Console](https://console.cloud.google.com/) and
   create a new project (or reuse an existing personal one) -- e.g.
   "PalPanel".

2. **APIs & Services -> OAuth consent screen**:
   - User type: **External** (this is a personal Google account, not a
     Google Workspace org).
   - App name: `PalPanel`. Support email: your own.
   - Scopes: the default `openid`, `email`, `profile` scopes are sufficient
     -- PalPanel only needs the verified email address.
   - Test users: while the consent screen is in "Testing" status, add every
     Google account that should be able to sign in (yourself + friends) as a
     test user, or click **Publish app** to make it available to any Google
     account without the test-user list (fine for a small personal app;
     Google's "unverified app" warning screen still appears either way since
     this app is never submitted for Google's verification review, but the
     user can click through it).

3. **Credentials -> Create Credentials -> OAuth client ID**:
   - Application type: **Web application**.
   - Name: e.g. "PalPanel web".
   - Authorized redirect URIs: add exactly
     `https://panel.iamfatness.us/signin-google`.
   - Create, then copy the **Client ID** and **Client secret** shown.

4. Put both into `appsettings.Local.json` on the install machine:

   ```json
   {
     "Panel": {
       "GoogleClientId": "<client id>.apps.googleusercontent.com",
       "GoogleClientSecret": "<client secret>"
     }
   }
   ```

   Restart the PalPanel service after editing `appsettings.Local.json` for
   the change to take effect. If these are left blank, the "Sign in with
   Google" link on the login page simply won't work -- password login is
   unaffected either way.

---

## First-run setup

Browse to `https://panel.iamfatness.us` (or `http://localhost:5080` from the
box itself). With no users in the database yet, PalPanel redirects you to
`/setup` to create the **owner** account (email + password) -- this first
account is always created with the Admin role, no allow-list step needed.

Once the owner account exists, `/setup` refuses to create a second one
(it redirects straight to `/login` instead); everyone else is added by the
owner under **Settings -> Users**: email, role (Admin/Viewer/Blocked), and an
optional initial password. Leave the password blank for a Google-only
account (they sign in with "Sign in with Google" and must use the same email
address you added); set an initial password if they'll use password sign-in
instead (they're required to change it on first login). Blocking a user
there revokes their access immediately, including any already-open session.
