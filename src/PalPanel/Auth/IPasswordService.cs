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
    /// Runs exactly one password verification against a fixed, precomputed dummy hash and always
    /// returns false. Used by the login path for a non-existent email so the request costs the
    /// SAME single PBKDF2 round as a real user's wrong-password attempt — never hash a throwaway
    /// value per request (that would be two rounds, making unknown emails measurably slower and
    /// re-introducing an account-enumeration timing signal in the opposite direction).
    /// </summary>
    bool VerifyDummy(string password);

    /// <summary>
    /// Checks a login attempt against a PanelUser's stored hash and lockout state, mutating the
    /// user's counters as a side effect (caller is responsible for persisting the user afterwards).
    /// </summary>
    LoginCheck CheckPassword(PanelUser user, string password, DateTimeOffset now);
}
