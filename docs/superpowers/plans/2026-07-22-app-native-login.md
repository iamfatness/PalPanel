# PalPanel App-Native Login Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Cloudflare-Access authentication with app-native login offering two paths — email+password and Google sign-in — both feeding the existing PanelUser/role model, with owner-managed accounts and a first-run setup.

**Architecture:** ASP.NET Core cookie authentication is the session. Two credential paths (`PasswordHasher<PanelUser>` verify; `.AddGoogle()` OIDC → email lookup) both call `SignInAsync` to issue the same cookie (email + role claims). `HttpContext.User` feeds the existing `HttpContextAuthStateProvider`/`AuthorizeView`/`IAdminGuard` unchanged. `AccessJwtMiddleware` and the Cloudflare JWT path are removed.

**Tech Stack:** .NET 8, ASP.NET Core cookie auth + `Microsoft.AspNetCore.Authentication.Google`, `Microsoft.AspNetCore.Identity` (for `PasswordHasher<T>` only), EF Core SQLite, Blazor Server, xunit + WebApplicationFactory.

## Global Constraints

- net8.0 throughout; app binds `http://localhost:5080` only (TLS terminates at Cloudflare; ForwardedHeaders make the app treat requests as HTTPS).
- Roles exactly `Admin` / `Viewer` / `Blocked`. Emails stored and compared lowercased.
- Password login and Google login must BOTH end in the same cookie (claims: `ClaimTypes.Email` + `ClaimTypes.Role`) so downstream authorization is identical to today.
- Secrets (`GoogleClientSecret`) only in gitignored `appsettings.Local.json`; never committed, never logged. Plaintext passwords never stored or logged.
- Every login attempt (success/failed/locked/denied) and every user-management action is an `EventLog` row via `IEventSink`.
- Server-side enforcement unchanged: every mutating action stays behind `IAdminGuard.EnsureAdminAsync`; `AuthDisabled=true` remains a dev-only bypass and disables the guard too (existing behavior).
- Lockout: 5 consecutive failed password attempts → 15-minute lock (constants `MaxFailedLogins=5`, `LockoutMinutes=15`).
- TDD: real RED output pasted in each task report. Full suite green twice before each task's commit. Conventional commits.
- Repo root `C:\Users\walla\GIT\palpanel`, branch `master` (worktree clean at plan start). Prior app is complete and passing (94 tests).

---

### Task 1: Data model + auth options

**Files:**
- Modify: `src/PalPanel/Data/Entities.cs` (PanelUser fields)
- Modify: `src/PalPanel/PanelOptions.cs` (add Google + cookie options; remove Access options)
- Test: `tests/PalPanel.Tests/DataLayerTests.cs` (extend round-trip)

**Interfaces:**
- Produces: `PanelUser` with new `string? PasswordHash`, `bool MustChangePassword`, `int FailedLoginCount`, `DateTimeOffset? LockedUntil` (all `{ get; set; }`, defaults null/false/0/null). `PanelOptions` gains `string GoogleClientId=""`, `string GoogleClientSecret=""`, `int CookieDaysValid=7`; loses `AccessTeamDomain`, `AccessAud` (leave `AuthDisabled`).

- [ ] **Step 1: Failing test** — extend `DataLayerTests` with a test that sets all four new PanelUser fields, saves, reloads, and asserts they round-trip (PasswordHash string, MustChangePassword true, FailedLoginCount 3, LockedUntil a value).
- [ ] **Step 2: Run** — `dotnet test --filter DataLayerTests` → FAIL (members missing).
- [ ] **Step 3: Implement** — add the four properties to `PanelUser`; add the three options to `PanelOptions` and remove `AccessTeamDomain`/`AccessAud`. Grep for existing references to the removed options (`AccessJwtMiddleware`, `appsettings.json`, tests) — leave `AccessJwtMiddleware.cs` compiling for now by NOT removing the options it reads until Task 2 deletes it; to avoid a broken build this task, KEEP `AccessTeamDomain`/`AccessAud` as `[Obsolete]`-free plain properties for now and delete them in Task 2 with the middleware. (Net: this task only ADDS options; it does not remove any.)
- [ ] **Step 4: Run** — `dotnet test` all green.
- [ ] **Step 5: Commit** — `git commit -am "feat(auth): PanelUser password/lockout fields and Google/cookie options"`

---

### Task 2: Password service (hash, verify, lockout) — pure logic

**Files:**
- Create: `src/PalPanel/Auth/IPasswordService.cs`, `src/PalPanel/Auth/PasswordService.cs`
- Test: `tests/PalPanel.Tests/PasswordServiceTests.cs`

**Interfaces:**
- Produces:
```csharp
public interface IPasswordService
{
    string Hash(string password);                 // never returns plaintext
    bool Verify(string hash, string password);     // constant-ish; false on null/empty hash
    // lockout helpers operate on a PanelUser instance (mutate counters), pure w.r.t. clock passed in
    LoginCheck CheckPassword(PanelUser user, string password, DateTimeOffset now);
}
public enum LoginOutcome { Success, BadCredentials, Locked, NoPassword }
public record LoginCheck(LoginOutcome Outcome, bool MutatedUser);
```
`CheckPassword` semantics: if `user.LockedUntil > now` → `Locked` (no mutation). If `user.PasswordHash` null/empty → perform a dummy verify (timing) and return `NoPassword`. Verify hash: success → reset `FailedLoginCount=0`, `LockedUntil=null` → `Success`; failure → `FailedLoginCount++`, and if `>=5` set `LockedUntil=now+15min` → `Locked` else `BadCredentials`. `MutatedUser=true` whenever counters changed (caller persists).

- [ ] **Step 1: Add package** — `dotnet add src/PalPanel package Microsoft.AspNetCore.Identity` (for `PasswordHasher<T>`), or use `Microsoft.AspNetCore.Cryptography.KeyDerivation`. Prefer `PasswordHasher<PanelUser>` from `Microsoft.AspNetCore.Identity` (already transitively available via the framework reference — verify; if not, add the package).
- [ ] **Step 2: Failing tests** — `PasswordServiceTests`:
```csharp
public class PasswordServiceTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    readonly IPasswordService _svc = new PasswordService();

    [Fact] public void Hash_Then_Verify_Roundtrips()
    { var h = _svc.Hash("Test-Passw0rd!"); Assert.NotEqual("Test-Passw0rd!", h); Assert.True(_svc.Verify(h, "Test-Passw0rd!")); Assert.False(_svc.Verify(h, "wrong")); }

    [Fact] public void Verify_NullOrEmptyHash_False()
    { Assert.False(_svc.Verify(null!, "x")); Assert.False(_svc.Verify("", "x")); }

    [Fact] public void CheckPassword_Success_ResetsCounters()
    {
        var u = new PanelUser { Email="a@b.c", PasswordHash=_svc.Hash("pw"), FailedLoginCount=3 };
        var r = _svc.CheckPassword(u, "pw", T0);
        Assert.Equal(LoginOutcome.Success, r.Outcome); Assert.Equal(0, u.FailedLoginCount); Assert.Null(u.LockedUntil);
    }

    [Fact] public void CheckPassword_FifthFailure_Locks()
    {
        var u = new PanelUser { Email="a@b.c", PasswordHash=_svc.Hash("pw"), FailedLoginCount=4 };
        var r = _svc.CheckPassword(u, "wrong", T0);
        Assert.Equal(LoginOutcome.Locked, r.Outcome); Assert.Equal(T0.AddMinutes(15), u.LockedUntil);
    }

    [Fact] public void CheckPassword_WhileLocked_ReturnsLocked_NoMutation()
    {
        var u = new PanelUser { Email="a@b.c", PasswordHash=_svc.Hash("pw"), LockedUntil=T0.AddMinutes(5) };
        var r = _svc.CheckPassword(u, "pw", T0);
        Assert.Equal(LoginOutcome.Locked, r.Outcome); Assert.False(r.MutatedUser);
    }

    [Fact] public void CheckPassword_NoPasswordHash_ReturnsNoPassword()
    { var u = new PanelUser { Email="a@b.c", PasswordHash=null }; Assert.Equal(LoginOutcome.NoPassword, _svc.CheckPassword(u, "pw", T0).Outcome); }
}
```
- [ ] **Step 3: Run** → FAIL (types missing).
- [ ] **Step 4: Implement** `PasswordService` using `PasswordHasher<PanelUser>` internally (pass a throwaway `PanelUser` to Hash/Verify since the hasher signature takes a user). `Verify` returns false on null/empty hash and catches `FormatException`. `CheckPassword` per the semantics above; the dummy verify in the `NoPassword` branch uses a constant precomputed hash so timing is comparable. Register DI `AddSingleton<IPasswordService, PasswordService>()`.
- [ ] **Step 5: Run** → all green.
- [ ] **Step 6: Commit** — `git commit -am "feat(auth): password hash/verify + lockout service"`

---

### Task 3: Remove Cloudflare Access auth; add cookie + Google auth pipeline

**Files:**
- Delete: `src/PalPanel/Auth/AccessJwtMiddleware.cs`, `src/PalPanel/Auth/AccessJwksConfigurationRetriever.cs` (and any Access-only helpers)
- Modify: `src/PalPanel/Program.cs` (auth wiring, ForwardedHeaders, remove Access middleware/options), `src/PalPanel/PanelOptions.cs` (remove `AccessTeamDomain`/`AccessAud`)
- Delete/replace tests: `tests/PalPanel.Tests/AccessJwtTests.cs` (remove — the feature is gone), any `StubJwksServer.cs`
- Test: `tests/PalPanel.Tests/AuthPipelineTests.cs` (new)

**Interfaces:**
- Consumes: `IPasswordService`, `RoleService`, `PanelOptions`.
- Produces: cookie auth scheme is default; `.AddGoogle()` registered (callback path `/signin-google`); `app.UseForwardedHeaders()` + `UseAuthentication()` + `UseAuthorization()` in the pipeline where `AccessJwtMiddleware` was. `AuthDisabled=true` dev path: a small middleware/claims-transformation that, when enabled, ensures a `dev@localhost` Admin `PanelUser` exists (via `RoleService.GetOrCreateAsync` — note it currently makes the FIRST user Admin; for dev, explicitly ensure Admin) and sets `HttpContext.User` to that principal, bypassing login.

- [ ] **Step 1: Packages** — `dotnet add src/PalPanel package Microsoft.AspNetCore.Authentication.Google`.
- [ ] **Step 2: Failing test** — `AuthPipelineTests` via `WebApplicationFactory<Program>`:
  - With `AuthDisabled=true`: `GET /` returns 200 and the dev principal is Admin (page renders Admin controls) — pins the dev bypass still works.
  - With `AuthDisabled=false` and no auth cookie: `GET /` returns a redirect to `/login` (302) — pins that the app is now gated by cookie auth, not open. (Before implementation this fails because the old JWT middleware 401s or the app is misconfigured.)
- [ ] **Step 3: Run** → FAIL.
- [ ] **Step 4: Implement**
  - Delete `AccessJwtMiddleware.cs`, `AccessJwksConfigurationRetriever.cs`, `AccessJwtTests.cs`, `StubJwksServer.cs`. Remove `AccessTeamDomain`/`AccessAud` from `PanelOptions` and `appsettings.json`.
  - Program.cs:
```csharp
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => {
        o.LoginPath = "/login"; o.AccessDeniedPath = "/login"; o.LogoutPath = "/auth/logout";
        o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax; o.Cookie.Name = "PalPanel.Auth";
        o.ExpireTimeSpan = TimeSpan.FromDays(builder.Configuration.GetValue("Panel:CookieDaysValid", 7));
        o.SlidingExpiration = true;
    })
    .AddGoogle(o => {
        o.ClientId = opts.GoogleClientId; o.ClientSecret = opts.GoogleClientSecret;
        o.CallbackPath = "/signin-google"; // maps to the redirect URI
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        o.CorrelationCookie.SameSite = SameSiteMode.None;
    });
builder.Services.AddAuthorization();
// ForwardedHeaders so Secure cookies + scheme are correct behind the tunnel:
builder.Services.Configure<ForwardedHeadersOptions>(o => {
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownNetworks.Clear(); o.KnownProxies.Clear(); // cloudflared is loopback; trust X-Forwarded-* (see security note)
});
```
  In the pipeline: `app.UseForwardedHeaders();` FIRST, then `app.UseStaticFiles();` then the dev-bypass middleware (only when `AuthDisabled`), then `app.UseAuthentication(); app.UseAuthorization();`.
  - **Security note to encode as a comment:** clearing KnownProxies/KnownNetworks trusts `X-Forwarded-Proto` from any immediate caller; acceptable ONLY because the app binds loopback and only cloudflared (localhost) reaches it. Document that the app must never be bound to a routable interface without a real proxy allowlist.
  - Dev bypass: a minimal `UseWhen(_ => opts.AuthDisabled, ...)` branch that calls `RoleService.GetOrCreateAsync("dev@localhost")`, force-sets that user Admin, and assigns `HttpContext.User` a ClaimsPrincipal (Email+Role Admin) so `AuthorizeView` and `IAdminGuard` both see Admin.
- [ ] **Step 5: Run** → green. Manual: `dotnet run` with `AuthDisabled=true` still renders the dashboard.
- [ ] **Step 6: Commit** — `git commit -am "feat(auth): cookie+Google auth pipeline; remove Cloudflare Access path"`

---

### Task 4: Login / logout / setup endpoints + password login flow

**Files:**
- Create: `src/PalPanel/Auth/AuthEndpoints.cs` (minimal-API endpoint group), `src/PalPanel/Components/Pages/Login.razor`, `src/PalPanel/Components/Pages/Setup.razor`
- Create: `src/PalPanel/Auth/SetupGateMiddleware.cs`
- Modify: `src/PalPanel/Program.cs` (map endpoints, add setup gate)
- Test: `tests/PalPanel.Tests/AuthEndpointsTests.cs`, extend `AuthPipelineTests`

**Interfaces:**
- Consumes: `IPasswordService`, `RoleService`, `IDbContextFactory<PanelDb>`, `IEventSink`.
- Produces: endpoints
  - `POST /auth/login` (form: email, password, returnUrl) — validates, signs in cookie, redirects; generic failure back to `/login?error=1`.
  - `POST /auth/logout` — SignOutAsync → `/login`.
  - `POST /auth/setup` (form: email, password) — only when Users empty; creates Admin, signs in, → `/`.
  - `SetupGateMiddleware`: if `Users` empty and path not in {`/setup`, `/auth/setup`, static assets, `/_blazor` handshake} → redirect `/setup`; if Users non-empty and path is `/setup` → redirect `/login`.

- [ ] **Step 1: Failing tests** — `AuthEndpointsTests` (WebApplicationFactory, `AuthDisabled=false`, temp DB):
  - Empty DB: `GET /` → 302 `/setup`; `GET /login` → 302 `/setup` (setup gate wins).
  - `POST /auth/setup` with email+password → 302 `/`, and a subsequent request with the returned auth cookie renders as Admin; the user exists in DB with role Admin and a PasswordHash.
  - After setup, `GET /setup` → 302 `/login`; `POST /auth/setup` again → 400/redirect (closed).
  - `POST /auth/login` wrong password → 302 `/login?error=1`, no auth cookie; correct → 302 `/`, auth cookie set; a `login-success` event logged; 5 wrong → `login-locked` event and further correct attempt within 15 min → still refused.
  - `POST /auth/logout` clears the cookie (subsequent `GET /` → 302 `/login`).
  Use `HttpClient` with `AllowAutoRedirect=false` and a cookie container; seed the DB via the factory's service provider where needed.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** endpoints + pages + setup gate per the Interfaces. `Login.razor` and `Setup.razor` are simple server-rendered forms (they POST to the endpoints; antiforgery token included; these pages are NOT InteractiveServer — they must work pre-authentication, so use static SSR forms). Login page shows an error alert when `?error=1`/`?denied=1`, and a "Sign in with Google" link to `/auth/google` (wired in Task 5; the button can exist now linking to that path). Emails lowercased everywhere. `login-*` audit events via `IEventSink`.
- [ ] **Step 4: Run** → green twice.
- [ ] **Step 5: Commit** — `git commit -am "feat(auth): login/logout/setup endpoints, first-run gate, password flow"`

---

### Task 5: Google sign-in callback + allow-list mapping

**Files:**
- Modify: `src/PalPanel/Auth/AuthEndpoints.cs` (`/auth/google` challenge + `/signin-google` result handling)
- Test: `tests/PalPanel.Tests/GoogleAuthTests.cs`

**Interfaces:**
- Consumes: cookie sign-in, `RoleService`/`IDbContextFactory<PanelDb>`, `IEventSink`.
- Produces: `GET /auth/google?returnUrl=` → `Challenge` the Google scheme. After the Google scheme authenticates (external cookie), an OnTicketReceived/OnCreatingTicket handler (or a `/signin-google`-adjacent result endpoint) resolves the `email` claim → lookup PanelUser (lowercased). Missing → deny (`/login?denied=1`, sign out external), log `login-denied-unknown`. Blocked → deny, log `login-denied-blocked`. Else sign in the app cookie with Email+Role, update LastSeen, log `login-success` method=google.

Testability note (from Task 3 controller learnings): do NOT call Google in tests. Put the email→user→cookie decision in a public method `Task<IResult> CompleteGoogleSignInAsync(string email, HttpContext ctx)` and unit-test THAT directly with known/unknown/blocked emails; the OAuth handler just extracts the email claim and calls it. Pin the challenge endpoint returns a 302 to Google (accounts.google.com) when ClientId is set.

- [ ] **Step 1: Failing tests** — `GoogleAuthTests`:
  - `CompleteGoogleSignInAsync` with a known Viewer email → issues app cookie (assert `ctx.User`/SignIn called or a 302 to `/`), `login-success` event method=google, LastSeen updated.
  - Unknown email → 302 `/login?denied=1`, `login-denied-unknown` event with the attempted email, NO app cookie.
  - Blocked email → 302 `/login?denied=1`, `login-denied-blocked`, no cookie.
  - `GET /auth/google` with GoogleClientId set → 302 whose Location host is `accounts.google.com`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the challenge endpoint and `CompleteGoogleSignInAsync`; wire the Google handler events to call it. Ensure the external correlation cookie is cleaned up. Lowercase the email from the claim.
- [ ] **Step 4: Run** → green twice.
- [ ] **Step 5: Commit** — `git commit -am "feat(auth): Google sign-in with in-app allow-list mapping"`

---

### Task 6: Change-password page + Settings user management (create/reset/clear password)

**Files:**
- Create: `src/PalPanel/Components/Pages/ChangePassword.razor`
- Modify: `src/PalPanel/Components/Pages/Settings.razor` (Users section: add-user with optional password, reset/clear password), `src/PalPanel/Auth/RoleService.cs` (add password-management methods) or a new `UserAdminService`
- Modify: `src/PalPanel/Auth/AuthEndpoints.cs` (`POST /auth/change-password`)
- Test: `tests/PalPanel.Tests/UserAdminTests.cs`, extend `RoleServiceTests`

**Interfaces:**
- Produces (on `RoleService` or new `IUserAdminService`, guarded by `IAdminGuard`):
```csharp
Task CreateUserAsync(string email, string role, string? initialPassword, string actor);   // lowercased; MustChangePassword=true if pw set; unique-email enforced
Task SetPasswordAsync(string email, string newPassword, string actor);                      // MustChangePassword=true; logs
Task ClearPasswordAsync(string email, string actor);                                        // → Google-only; logs
```
Plus `POST /auth/change-password` (signed-in user: verify current unless MustChangePassword, set new, clear flag).

- [ ] **Step 1: Failing tests** — `UserAdminTests`: CreateUserAsync with password → user exists, role set, PasswordHash present, MustChangePassword true, `user-created` event; without password → PasswordHash null (Google-only). Duplicate email → throws. Non-admin actor → `UnauthorizedAccessException` + `unauthorized-action` (guard). SetPasswordAsync sets a verifiable hash + flag. ClearPasswordAsync nulls the hash. Change-password endpoint: must-change user can set new without current; normal user needs correct current.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the service methods (guard first line), the `ChangePassword.razor` page + endpoint, and the Settings Users UI additions (add-user form gains an optional password field and role; per-row "Set password"/"Clear password" actions). Reuse the inline `.alert` error pattern; surface `UnauthorizedAccessException`/duplicate errors.
- [ ] **Step 4: Run** → green twice.
- [ ] **Step 5: Commit** — `git commit -am "feat(auth): change-password + admin user/password management"`

---

### Task 7: Nav/session UX + docs + packaging

**Files:**
- Modify: `src/PalPanel/Components/Layout/MainLayout.razor` (show signed-in email + Logout; hide nav when unauthenticated)
- Modify: `docs/setup-cloudflare.md` (remove Access-app section; add Google OAuth client setup + ForwardedHeaders note; note the app-login gate)
- Modify: `deploy/README.md` (acceptance checklist: setup → add friends → both login paths → block revokes)
- Modify: `src/PalPanel/appsettings.json` (add empty `GoogleClientId`/`GoogleClientSecret` placeholders + `CookieDaysValid`; remove Access keys)
- Test: extend `SmokeTests`

**Interfaces:** consumes finished auth; produces operator docs + logged-out UX.

- [ ] **Step 1: Failing/ならsmoke test** — `SmokeTests`: with `AuthDisabled=true`, footer shows a Logout control and the dev email; `/login` renders "Sign in with Google" + email/password fields and contains no nav sidebar links to Settings (unauthenticated view). (Adjust assertions to what's actually rendered.)
- [ ] **Step 2: Run** → adjust until RED for the right reason, then implement.
- [ ] **Step 3: Implement** — MainLayout: `<AuthorizeView>` — authenticated shows nav + `email` + Logout (posts `/auth/logout`); not-authenticated shows minimal chrome. Update the three doc/config files: `setup-cloudflare.md` Part C replaced by "Create a Google OAuth client" (consent screen, redirect URI `https://panel.iamfatness.us/signin-google`, Client ID/Secret → `appsettings.Local.json`), and a note that the Cloudflare Access application must be deleted so the app login is the gate; keep the tunnel Parts A/B. `appsettings.json` key changes. `deploy/README.md` checklist updated for the two login paths + block-revocation.
- [ ] **Step 4: Run** — `dotnet build -c Release` + `dotnet test` green twice; syntax-check scripts unchanged.
- [ ] **Step 5: Commit** — `git commit -am "feat(auth): logged-out UX, Google OAuth runbook, packaging updates"`

---

## Self-Review Notes (performed at plan-writing time)

- **Spec coverage:** data §1→T1; auth mechanism §2→T3; password path §3→T2+T4; Google path §3→T5; first-run §4→T4; user management §5→T6; security §6→T2 (hash/lockout) + T3 (cookies/ForwardedHeaders) + T4 (enumeration/antiforgery/audit); deployment §7→T7 (+ live Cloudflare Access deletion done by controller at go-live); testing §8→per-task + T7 acceptance.
- **Type consistency:** `IPasswordService`/`LoginOutcome`/`LoginCheck` defined T2, consumed T4. `CompleteGoogleSignInAsync` defined T5. User-admin methods defined T6. Cookie claim shape (`ClaimTypes.Email`+`ClaimTypes.Role`) constant across T3/T4/T5 and matches existing `HttpContextAuthStateProvider` expectations (verify it reads those claim types; if it read Access-specific claims, update it in T3).
- **Ordering:** T1 only adds (no build break); T3 removes Access + JWT tests in the same task that adds the replacement, so the suite never references deleted types across a task boundary.
- **Known judgment calls:** EnsureCreated (no migration) means dev DB must be deleted for the new columns — documented in T1/T7; production has no prior DB. Dev bypass must satisfy the DB-backed `IAdminGuard` (GetOrCreate dev admin), not just set a claim — encoded in T3.
