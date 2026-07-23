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
    private RSA _rsa;
    private int _keyGeneration = 1;
    public string BaseUrl { get; }
    public string KeyId => $"test-key-{_keyGeneration}";

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

    // Simulates Cloudflare rotating the Access signing key: the certs endpoint now
    // serves ONLY the new key (with a new kid), and IssueToken signs with the new
    // key from here on. Tokens signed with the old key can no longer be validated.
    public void RotateKey()
    {
        _rsa.Dispose();
        _rsa = RSA.Create(2048);
        _keyGeneration++;
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
