namespace PalPanel.Auth;

// Broadcasts role changes so live Blazor circuits can react immediately (a Blocked
// user must lose the UI right away, not on their next full page load). Singleton;
// HttpContextAuthStateProvider instances (one per circuit) subscribe/unsubscribe.
public class RoleChangeNotifier
{
    public event Action<string, string>? RoleChanged; // (email, newRole)

    public void Notify(string email, string newRole) => RoleChanged?.Invoke(email, newRole);
}
