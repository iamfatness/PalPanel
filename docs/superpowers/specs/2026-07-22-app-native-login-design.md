# PalPanel App-Native Login (password + Google) — Design

**Date:** 2026-07-22
**Status:** Approved design, pre-implementation
**Owner:** John Wallace
**Supersedes:** the Cloudflare-Access authentication portion of
`2026-07-21-palpanel-design.md` §5. The tunnel/exposure model is unchanged;
only the *login* mechanism changes.

## Why

The original design delegated authentication to Cloudflare Access. The owner
wants friends to log in **without creating any third-party (Cloudflare) account**
and wants to **manage who can log in from inside the app**. This replaces the
Cloudflare-Access JWT login with app-native authentication offering two paths —
email+password and Google sign-in — both feeding the existing PanelUser/role
model. Cloudflare Tunnel is retained purely to expose `localhost:5080` at
`panel.iamfatness.us` with no open ports and edge HTTPS.

## What changes / what stays

- **Removed:** `AccessJwtMiddleware`, the Cloudflare Access JWT validation path,
  `AccessJwksConfigurationRetriever`, the JWKS-rotation logic, and the
  `AccessTeamDomain`/`AccessAud` options. The Cloudflare Access *application*
  (dashboard) is deleted at go-live.
- **Kept unchanged:** `PanelUser` + roles (Admin/Viewer/Blocked), `RoleService`,
  `IAdminGuard` and all server-side action guards, `RoleChangeNotifier`
  live-revocation, `HttpContextAuthStateProvider`, all pages, the supervisor,
  poller, orchestrator, backups, scheduler, retention. Downstream authorization
  is identical — only the *source* of `HttpContext.User` changes from a validated
  JWT to a validated auth cookie.
- **Kept:** Cloudflare Tunnel `palpanel` + route `panel.iamfatness.us →
  http://localhost:5080`.

## Architecture

ASP.NET Core **cookie authentication** (`AddAuthentication().AddCookie()`) is the
session mechanism. Two credential paths both end in `HttpContext.SignInAsync`
issuing the same cookie (claims: email + role):

1. **Password** — `/login` email+password → verify against `PasswordHasher<PanelUser>`.
2. **Google** — `.AddGoogle()` OIDC → Google returns a verified email → look up the
   `PanelUser` by email → allow iff it exists and role != Blocked.

The cookie sets `HttpContext.User`; the existing `HttpContextAuthStateProvider`
(captures the principal at circuit start) feeds Blazor `AuthorizeView` and the
`AuthenticationStateProvider` exactly as before. `IAdminGuard` continues to check
the actor's role from the DB on every mutating action, so authorization is
unchanged.

## 1. Data model

`PanelUser` gains:

| Field | Type | Meaning |
|---|---|---|
| `PasswordHash` | `string?` | PBKDF2 hash; null ⇒ this user cannot use password login (Google-only) |
| `MustChangePassword` | `bool` | set when an admin sets an initial/reset password; user is forced to change on next password login |
| `FailedLoginCount` | `int` | consecutive failed password attempts |
| `LockedUntil` | `DateTimeOffset?` | password login refused until this time |

Existing fields (`Email` unique, `Role`, `FirstSeen`, `LastSeen`) unchanged.
`EnsureCreated` is still used; because it is not a migration system, the new
columns require deleting any existing dev `palpanel.db` (documented) — production
has no prior DB.

## 2. Authentication wiring

- `builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
  .AddCookie(...).AddGoogle(...)`.
- Cookie options: `HttpOnly=true`, `SecurePolicy=Always`, `SameSite=Lax`,
  sliding expiration (default 7 days, configurable), `LoginPath=/login`,
  `AccessDeniedPath=/login`.
- `app.UseForwardedHeaders(...)` (trust `X-Forwarded-Proto` from the tunnel) BEFORE
  auth, so the app treats requests as HTTPS and Secure cookies are honored. Only
  the loopback/Cloudflare source is trusted (KnownProxies/KnownNetworks left to
  the loopback default since cloudflared connects from localhost).
- `app.UseAuthentication(); app.UseAuthorization();` replace the removed
  `AccessJwtMiddleware`.
- `AuthDisabled=true` (dev only) short-circuits to sign in a `dev@localhost` Admin
  principal (must also GetOrCreate that user so `IAdminGuard` — which reads the DB —
  honors it; this mirrors the existing AdminGuard `AuthDisabled` bypass so both
  agree). Documented as never-in-production; default false.

### PanelOptions additions
`GoogleClientId` (string, ""), `GoogleClientSecret` (string, "", secret → local
config only), `CookieDaysValid` (int, 7). Removed: `AccessTeamDomain`, `AccessAud`.

## 3. Login paths

### Password (`AuthController`/minimal endpoints + `/login` page)
- `/login` Razor page: email + password form (antiforgery token), plus a
  "Sign in with Google" button. Generic error "Invalid email or password" on any
  failure (no user enumeration).
- POST `/auth/login`: find user by email (case-insensitive; store emails
  lowercased). If `LockedUntil > now` → refuse ("account temporarily locked"),
  log `login-locked`. If `PasswordHash` null → generic failure. Verify hash:
  - success → reset `FailedLoginCount`/`LockedUntil`, update `LastSeen`, sign in,
    log `login-success` (method=password); if `MustChangePassword` redirect to
    `/change-password`.
  - failure → increment `FailedLoginCount`; at ≥5 set `LockedUntil = now+15min`
    and log `login-locked`; else log `login-failed`. Generic error to user.
- POST `/auth/logout`: `SignOutAsync`, redirect `/login`.
- `/change-password` page: for signed-in users; verifies current (unless
  `MustChangePassword`), sets new hash, clears the flag.

### Google (`/auth/google` challenge + `/signin-google` callback)
- "Sign in with Google" → challenge the Google scheme (redirect to Google).
- On callback, `.AddGoogle()` yields the verified `email` claim. Handler:
  lowercase email → find `PanelUser`. If missing → sign out the external cookie,
  redirect `/login?denied=1` ("This Google account isn't on the allow list — ask
  the owner to add you"), log `login-denied-unknown` with the attempted email.
  If role == Blocked → deny similarly, log `login-denied-blocked`. Else update
  `LastSeen`, sign in with the app cookie, log `login-success` (method=google).
- The external Google correlation cookie also needs `SecurePolicy=Always` +
  SameSite handling for the OAuth round trip (`SameSite=None` for the correlation
  cookie is the framework default under HTTPS; ForwardedHeaders makes this work).

## 4. First-run setup

- `SetupGateMiddleware` (or a Blazor redirect): if `Users` table is empty, all
  routes except `/setup` and static assets redirect to `/setup`.
- `/setup` page: create owner (email + password, confirm). Creates `PanelUser`
  role=Admin with hash, signs them in, redirects `/`. Guarded so that once any
  user exists, `/setup` 404s/redirects to `/login`. Concurrency: the "is empty"
  check + insert runs in a transaction so a double-submit can't create two owners.

## 5. User management (Settings → Users)

Admin-only (page inside `AuthorizeView Roles="Admin"` AND every mutating call
guarded by `IAdminGuard.EnsureAdminAsync`). Operations:
- **Add user:** email (required, lowercased, unique), role (Viewer/Admin), optional
  initial password (blank ⇒ Google-only). If a password is set, `MustChangePassword=true`.
- **Set/Reset password:** set a new initial password (forces change), or clear it
  (revert to Google-only).
- **Change role:** Admin/Viewer/Blocked (existing `SetRoleAsync`; last-admin
  protection preserved; Blocked triggers `RoleChangeNotifier` live revocation).
- **Delete user.**
All operations log audit events with the acting admin's email.

## 6. Security

- **Hashing:** `PasswordHasher<PanelUser>` (PBKDF2, salted, iteration count per
  ASP.NET defaults). Never store or log plaintext.
- **Lockout:** per-account, 5 consecutive failures → 15 min lock (both configurable
  constants). Successful login resets the counter.
- **Enumeration resistance:** identical generic error + comparable timing for
  unknown-email vs wrong-password (perform a dummy hash verify when the user is
  missing).
- **Antiforgery** on all auth POSTs and the setup/user-management forms.
- **Cookies:** HttpOnly, Secure (Always), SameSite=Lax (app cookie).
- **Transport:** ForwardedHeaders trust so the app knows it is HTTPS behind the
  tunnel; the app still binds `http://localhost:5080` only (TLS terminates at
  Cloudflare).
- **Audit:** every login-success/failed/locked/denied and every user-management
  action is an `EventLog` row, visible on the History page.
- **The app login is now the sole gate** (no Cloudflare Access) — these controls
  are load-bearing, not defense-in-depth.

## 7. Deployment changes

- Keep tunnel `palpanel` + public hostname `panel.iamfatness.us → http://localhost:5080`.
- Delete the Cloudflare Access application for `panel.iamfatness.us` (so no
  Cloudflare login screen).
- **Google OAuth client (one-time, owner-driven):** Google Cloud project → OAuth
  consent screen (External; app name + support email; scopes openid/email/profile)
  → OAuth 2.0 Client ID (Web application) with authorized redirect URI
  `https://panel.iamfatness.us/signin-google` → Client ID + Secret into gitignored
  `appsettings.Local.json` (`Panel:GoogleClientId` / `Panel:GoogleClientSecret`).
  Publish the consent screen (basic scopes need no Google review) or add test users.
- `docs/setup-cloudflare.md` updated: remove the Access-application section, add
  the Google OAuth setup section and the ForwardedHeaders/HTTPS note.

## 8. Testing

- **Unit/integration:** password hash round-trip; lockout after N; enumeration-safe
  failure path; first-run redirect + `/setup` closes after first user (incl. the
  concurrency guard); password login endpoint (valid / invalid / no-password-user /
  blocked / locked / must-change); Google callback mapping (known→in, unknown→denied,
  blocked→denied) using a stubbed Google identity (inject the external principal,
  don't call Google); user-management ops (add w/ + w/o password, reset, clear,
  role change, delete; last-admin guard).
- **Manual acceptance (live):** first-run setup creates owner → owner logs in by
  password → owner adds a Google-only friend (Gmail) and a password friend → each
  signs in via their path → block a user and confirm their live session is revoked
  and re-login refused → unknown Google account is denied with the allow-list message.

## Non-goals (v1)

Email-based self-service password reset (needs outbound email — not configured;
reset stays admin-driven), self-registration, invite links, MFA/TOTP, "remember
this device," account lockout by IP (per-account only for v1).
