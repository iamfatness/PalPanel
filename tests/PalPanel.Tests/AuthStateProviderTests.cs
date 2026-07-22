using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PalPanel.Auth;

namespace PalPanel.Tests;

public class AuthStateProviderTests
{
    private sealed class FakeAccessor(HttpContext? ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }

    private static (HttpContextAuthStateProvider Provider, RoleChangeNotifier Notifier) Make(string email, string role)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Role, role),
            ], authenticationType: "CfAccess")),
        };
        var notifier = new RoleChangeNotifier();
        return (new HttpContextAuthStateProvider(new FakeAccessor(ctx), notifier), notifier);
    }

    [Fact]
    public async Task BlockedNotification_ForSameEmail_MakesStateUnauthenticated_AndRaisesChange()
    {
        var (provider, notifier) = Make("viewer@x.com", "Viewer");
        var changed = false;
        provider.AuthenticationStateChanged += _ => changed = true;

        notifier.Notify("viewer@x.com", "Blocked");

        var state = await provider.GetAuthenticationStateAsync();
        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
        Assert.Null(state.User.FindFirst(ClaimTypes.Role));
        Assert.True(changed, "NotifyAuthenticationStateChanged must fire so AuthorizeView re-renders");
    }

    [Fact]
    public async Task Notification_ForDifferentEmail_IsNoOp()
    {
        var (provider, notifier) = Make("viewer@x.com", "Viewer");
        var changed = false;
        provider.AuthenticationStateChanged += _ => changed = true;

        notifier.Notify("someone-else@x.com", "Blocked");

        var state = await provider.GetAuthenticationStateAsync();
        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal("Viewer", state.User.FindFirst(ClaimTypes.Role)?.Value);
        Assert.False(changed);
    }

    [Fact]
    public async Task PromotionNotification_ForSameEmail_UpdatesRoleClaim()
    {
        var (provider, notifier) = Make("viewer@x.com", "Viewer");

        notifier.Notify("viewer@x.com", "Admin");

        var state = await provider.GetAuthenticationStateAsync();
        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal("Admin", state.User.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Equal("viewer@x.com", state.User.FindFirst(ClaimTypes.Email)?.Value);
    }

    [Fact]
    public async Task Dispose_Unsubscribes_SoLaterNotificationsAreIgnored()
    {
        var (provider, notifier) = Make("viewer@x.com", "Viewer");
        provider.Dispose();

        notifier.Notify("viewer@x.com", "Blocked");

        var state = await provider.GetAuthenticationStateAsync();
        Assert.True(state.User.Identity?.IsAuthenticated); // unchanged after Dispose
    }

    [Fact]
    public async Task AnonymousCircuit_IgnoresNotifications()
    {
        // No HttpContext at circuit start (e.g. prerendering edge case) -> empty principal;
        // role-change notifications for any email must be a no-op, not a crash.
        var notifier = new RoleChangeNotifier();
        var provider = new HttpContextAuthStateProvider(new FakeAccessor(null), notifier);

        notifier.Notify("anyone@x.com", "Blocked");

        var state = await provider.GetAuthenticationStateAsync();
        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
    }
}
