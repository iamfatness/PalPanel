using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PalPanel.Tests;

// Stands in for Cloudflare Access's /cdn-cgi/access/certs endpoint: a real Kestrel
// instance (like StubPalServer) so AccessJwtMiddleware's outbound JWKS fetch is a
// genuine HTTP round-trip, not an in-memory fake.
public sealed class StubJwksServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RSA _rsa;
    public string BaseUrl { get; }
    public const string KeyId = "test-key";

    public StubJwksServer()
    {
        _rsa = RSA.Create(2048);

        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _app = b.Build();
        _app.MapGet("/cdn-cgi/access/certs", () =>
        {
            var pub = _rsa.ExportParameters(includePrivateParameters: false);
            var jwk = new
            {
                kty = "RSA",
                use = "sig",
                kid = KeyId,
                alg = "RS256",
                n = Base64UrlEncoder.Encode(pub.Modulus),
                e = Base64UrlEncoder.Encode(pub.Exponent),
            };
            return Results.Json(new { keys = new[] { jwk } });
        });
        _app.Start();
        BaseUrl = _app.Urls.First();
    }

    // Issuer/audience/email/expiry are all caller-controlled so tests can exercise
    // both the happy path and each individual failure mode (wrong aud, expired, etc).
    public string IssueToken(string issuer, string audience, string email, TimeSpan? lifetime = null)
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["email"] = email },
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)),
            SigningCredentials = credentials,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _rsa.Dispose();
    }
}
