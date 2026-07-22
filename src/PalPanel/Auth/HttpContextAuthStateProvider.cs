using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace PalPanel.Auth;

// Blazor Server circuits outlive the HTTP request that started them, so we can't
// read HttpContext.User lazily inside GetAuthenticationStateAsync (there may be no
// HttpContext by the time a later render happens). Instead we snapshot the
// ClaimsPrincipal AccessJwtMiddleware attached to the request once, in this
// scoped provider's constructor, which runs at circuit start while the
// originating HttpContext is still available via IHttpContextAccessor.
public class HttpContextAuthStateProvider : AuthenticationStateProvider
{
    private readonly AuthenticationState _state;

    public HttpContextAuthStateProvider(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        _state = new AuthenticationState(user);
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);
}
