using PalPanel.Data;

namespace PalPanel.Auth;

public enum LoginOutcome { Success, BadCredentials, Locked, NoPassword }

public record LoginCheck(LoginOutcome Outcome, bool MutatedUser);

public interface IPasswordService
{
    /// <summary>Hashes a password. Never returns the plaintext.</summary>
    string Hash(string password);

    /// <summary>Verifies a password against a hash. Constant-ish time; returns false on null/empty/malformed hash.</summary>
    bool Verify(string hash, string password);

    /// <summary>
    /// Checks a login attempt against a PanelUser's stored hash and lockout state, mutating the
    /// user's counters as a side effect (caller is responsible for persisting the user afterwards).
    /// </summary>
    LoginCheck CheckPassword(PanelUser user, string password, DateTimeOffset now);
}
