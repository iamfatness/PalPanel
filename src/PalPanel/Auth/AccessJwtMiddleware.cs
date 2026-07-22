using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace PalPanel.Auth;

// Validates the Cf-Access-Jwt-Assertion header Cloudflare Access injects on every
// request once a client has passed the Access login flow. AuthDisabled short-circuits
// everything for local dev / early smoke tests so the app stays usable without a
// Cloudflare tunnel in front of it.
public class AccessJwtMiddleware
{
    private const string HeaderName = "Cf-Access-Jwt-Assertion";

    private readonly RequestDelegate _next;
    private readonly PanelOptions _options;
    private readonly ILogger<AccessJwtMiddleware> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public AccessJwtMiddleware(RequestDelegate next, IOptions<PanelOptions> options, ILogger<AccessJwtMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
        if (!_options.AuthDisabled && !string.IsNullOrWhiteSpace(_options.AccessTeamDomain))
        {
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{_options.AccessTeamDomain.TrimEnd('/')}/cdn-cgi/access/certs",
                new AccessJwksConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = false });
        }
    }

    public async Task InvokeAsync(HttpContext context, RoleService roles)
    {
        if (_options.AuthDisabled)
        {
            SetPrincipal(context, new PanelPrincipal("dev@localhost", "Admin"));
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var headerValues) ||
            string.IsNullOrWhiteSpace(headerValues.ToString()))
        {
            await Unauthorized(context);
            return;
        }

        string email;
        try
        {
            email = await ValidateAsync(headerValues.ToString(), context.RequestAborted);
        }
        catch (SecurityTokenException)
        {
            // Expected failure class: bad signature, wrong iss/aud, expired, missing
            // email claim. Routine under attack or after Access session expiry — 401,
            // no log spam.
            await Unauthorized(context);
            return;
        }
        catch (Exception ex)
        {
            // Unexpected failure (JWKS endpoint unreachable, misconfiguration, bugs).
            // Still fail closed with 401, but log it loudly so it's diagnosable.
            _logger.LogError(ex, "Unexpected error validating Cloudflare Access JWT");
            await Unauthorized(context);
            return;
        }

        var principal = await roles.GetOrCreateAsync(email);
        if (principal.Role == "Blocked")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Blocked");
            return;
        }

        SetPrincipal(context, principal);
        await _next(context);
    }

    private async Task<string> ValidateAsync(string token, CancellationToken ct)
    {
        if (_configManager is null)
            throw new InvalidOperationException("Panel:AccessTeamDomain is not configured");

        var config = await _configManager.GetConfigurationAsync(ct);
        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, BuildValidationParameters(config));

        if (!result.IsValid && IsSigningKeyFailure(result.Exception))
        {
            // Key-rotation path: Cloudflare rotates the Access signing key periodically,
            // and a token signed with the new key won't match our cached JWKS. Request a
            // refresh and retry validation exactly once.
            //
            // ConfigurationManager (IdentityModel 8.x) refreshes in the BACKGROUND:
            // after RequestRefresh(), the next GetConfigurationAsync() returns the stale
            // config and kicks off the fetch; the fresh config appears on a later call
            // (empirically ~50ms with a local JWKS host). So we poll briefly — bounded
            // at ~2s — for a new configuration instance, then retry once with whatever
            // we have and fail closed on 401 if it still doesn't validate.
            //
            // DoS note: RequestRefresh() is rate-limited internally by
            // ConfigurationManager (RefreshInterval floor, default 5 minutes), so
            // attackers spamming garbage signatures cannot make us hammer the JWKS
            // endpoint — we rely on that built-in floor rather than adding our own
            // throttle. The bounded poll only costs the attacker's own request latency.
            _configManager.RequestRefresh();
            var stale = config;
            config = await _configManager.GetConfigurationAsync(ct); // stale; starts background fetch
            for (var i = 0; i < 40 && ReferenceEquals(config, stale); i++)
            {
                await Task.Delay(50, ct);
                config = await _configManager.GetConfigurationAsync(ct);
            }
            result = await handler.ValidateTokenAsync(token, BuildValidationParameters(config));
        }

        if (!result.IsValid)
            throw result.Exception as SecurityTokenException
                ?? new SecurityTokenException("token validation failed", result.Exception);

        var email = result.ClaimsIdentity.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email))
            throw new SecurityTokenException("token is missing the email claim");
        return email;
    }

    private TokenValidationParameters BuildValidationParameters(OpenIdConnectConfiguration config) => new()
    {
        ValidIssuer = _options.AccessTeamDomain,
        ValidAudience = _options.AccessAud,
        IssuerSigningKeys = config.SigningKeys,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256], // pin RS256: reject alg-confusion tokens
        ValidateLifetime = true,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
    };

    // Only signature/key-resolution failures should trigger a JWKS refresh; wrong
    // audience, wrong issuer, or expired tokens fail for reasons fresh keys can't fix.
    private static bool IsSigningKeyFailure(Exception? ex) =>
        ex is SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException;

    private static Task Unauthorized(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync("Unauthorized");
    }

    private static void SetPrincipal(HttpContext context, PanelPrincipal principal)
    {
        context.Items["PanelPrincipal"] = principal;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, principal.Email),
            new Claim(ClaimTypes.Role, principal.Role),
        ], authenticationType: "CfAccess");
        context.User = new ClaimsPrincipal(identity);
    }
}
