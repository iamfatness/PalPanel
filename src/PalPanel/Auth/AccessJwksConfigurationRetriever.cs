using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace PalPanel.Auth;

// Cloudflare Access's /cdn-cgi/access/certs endpoint returns a bare JWKS document
// ({"keys": [...] }), not a full OpenID discovery document. This retriever adapts
// that JWKS response into an OpenIdConnectConfiguration so we can still use
// ConfigurationManager<T>'s built-in caching + automatic-refresh-on-signing-key-miss
// behavior instead of hand-rolling our own cache.
public class AccessJwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        var keySet = new JsonWebKeySet(json);
        var config = new OpenIdConnectConfiguration();
        foreach (var key in keySet.GetSigningKeys())
            config.SigningKeys.Add(key);
        return config;
    }
}
