# PalPanel setup: Palworld REST API, Cloudflare Tunnel, and Access

Operator runbook for exposing PalPanel (running on 192.168.1.50 as the
`PalPanel` Windows service, listening on `http://localhost:5080`) to the
internet at `https://panel.iamfatness.us`, gated by Cloudflare Access.

Security note: `AdminPassword`, `AccessTeamDomain`, and `AccessAud` are
secrets/environment-specific config. They live **only** in
`appsettings.Local.json` (gitignored, never committed) or a DPAPI-protected
store -- never in `appsettings.json` or any file checked into the repo. This
document uses placeholders (`<...>`) for all secret values; do not paste the
real `AdminPassword` into any committed doc, issue, or commit message.

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

   These four values plus `AccessTeamDomain`/`AccessAud` (Part C) are the
   only settings that typically need to be filled in per-install;
   everything else has a sane default in `appsettings.json`.

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

6. Install cloudflared as a Windows service so it survives reboot, and start
   it:

   ```
   cloudflared service install
   sc start cloudflared
   ```

7. Verify: from a machine off the LAN (e.g. phone on cellular data), browse
   to `https://panel.iamfatness.us`. At this point, before Access is
   configured (Part C), you should reach PalPanel directly -- lock it down
   next.

---

## Part C -- Cloudflare Access (Zero Trust)

Gates `panel.iamfatness.us` behind login, without PalPanel needing to run
its own user/password system for the front door.

1. In the Cloudflare dashboard, go to **Zero Trust** (free plan covers this).
   On first visit you'll be asked to pick a **team domain**, e.g.
   `iamfatness` (giving `iamfatness.cloudflareaccess.com`). Record this team
   domain.

2. Put the team domain into `appsettings.Local.json` on the install
   machine:

   ```json
   {
     "Panel": {
       "AccessTeamDomain": "iamfatness.cloudflareaccess.com"
     }
   }
   ```

3. Zero Trust -> **Access -> Applications -> Add an application ->
   Self-hosted**:
   - Application domain: `panel.iamfatness.us`
   - Session duration: **24 hours**
   - Login methods: enable **Google** and **One-time PIN** (email OTP) --
     leave other identity providers off so any Google account or any email
     address can authenticate.
   - Policy: name it e.g. "Allow anyone authenticated", action **Allow**,
     include rule **Login Methods -> any** (equivalently, "Everyone" with
     no additional restriction) -- i.e. any identity that successfully
     completes login through one of the enabled methods is let through.
     There's no allowlist to maintain; PalPanel's own role mapping (below)
     is the second gate.
   - Save the application. Cloudflare shows the application's **AUD tag**
     (audience) on the application's overview/settings page -- copy it.

4. Put the AUD tag into `appsettings.Local.json` alongside the team domain:

   ```json
   {
     "Panel": {
       "AccessTeamDomain": "iamfatness.cloudflareaccess.com",
       "AccessAud": "<aud tag from the application settings page>"
     }
   }
   ```

   PalPanel validates the `Cf-Access-Jwt-Assertion` header against this team
   domain's public keys and checks the audience matches this AUD -- it does
   not trust the header blindly. Restart the PalPanel service after editing
   `appsettings.Local.json` for the change to take effect.

5. First login / role mapping: browse to `https://panel.iamfatness.us` and
   sign in (Google or OTP) -- **the very first verified email PalPanel ever
   sees is automatically promoted to Admin**. Do this yourself first, before
   sharing the link with anyone else.

6. Verify role mapping with a second identity: have someone else (or use a
   second Google account / a throwaway email for OTP) sign in. Confirm in
   PalPanel under **Settings -> Users** that this second account shows up
   as **Viewer** (read-only dashboard, no mutating controls visible), while
   your first account shows **Admin**. Promote the second account to Admin
   from that screen if it should also have control, or leave it as Viewer,
   or set it to Blocked to revoke access entirely.

At this point the panel is reachable at `https://panel.iamfatness.us` from
anywhere, requires Cloudflare Access login, and enforces Admin/Viewer roles
inside the app. No inbound ports were opened on the router.
