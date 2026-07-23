using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace PalPanel.Auth;

// Blazor Server circuits outlive the HTTP request that started them, so we can't
// read HttpContext.User lazily inside GetAuthenticationStateAsync (there may be no
// HttpContext by the time a later render happens). Instead we snapshot the
// ClaimsPrincipal AccessJwtMiddleware attached to the request once, in this
// scoped provider's constructor, which runs at circuit start while the
// originating HttpContext is still available via IHttpContextAccessor.
//
// The provider also subscribes to RoleChangeNotifier so role changes take effect
// on live circuits immediately: when this circuit's user is Blocked, the state
// becomes an unauthenticated principal and NotifyAuthenticationStateChanged makes
// every AuthorizeView re-render at once — no page reload required.
public class HttpContextAuthStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly RoleChangeNotifier _notifier;
    private readonly string? _email;
    private volatile AuthenticationState _state;

    public HttpContextAuthStateProvider(IHttpContextAccessor accessor, RoleChangeNotifier notifier)
    {
        var user = accessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        _state = new AuthenticationState(user);
        _email = user.FindFirst(ClaimTypes.Email)?.Value;
        _notifier = notifier;
        _notifier.RoleChanged += OnRoleChanged;
    }

    private void OnRoleChanged(string email, string newRole)
    {
        if (_email is null || !string.Equals(email, _email, StringComparison.OrdinalIgnoreCase))
            return;

        var user = newRole == "Blocked"
            ? new ClaimsPrincipal(new ClaimsIdentity()) // unauthenticated: Blocked loses all UI
            : new ClaimsPrincipal(new ClaimsIdentity(
              [
                  new Claim(ClaimTypes.Email, _email),
                  new Claim(ClaimTypes.Role, newRole),
              ], authenticationType: "CfAccess"));

        _state = new AuthenticationState(user);
        NotifyAuthenticationStateChanged(Task.FromResult(_state));
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);

    public void Dispose() => _notifier.RoleChanged -= OnRoleChanged;
}
