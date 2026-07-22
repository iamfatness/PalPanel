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
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public AccessJwtMiddleware(RequestDelegate next, IOptions<PanelOptions> options)
    {
        _next = next;
        _options = options.Value;
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
        catch
        {
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
        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = _options.AccessTeamDomain,
            ValidAudience = _options.AccessAud,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
        };

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, validationParameters);
        if (!result.IsValid)
            throw result.Exception ?? new SecurityTokenException("token validation failed");

        var email = result.ClaimsIdentity.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email))
            throw new SecurityTokenException("token is missing the email claim");
        return email;
    }

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
