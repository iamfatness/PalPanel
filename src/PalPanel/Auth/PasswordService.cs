using Microsoft.AspNetCore.Identity;
using PalPanel.Data;

namespace PalPanel.Auth;

public class PasswordService : IPasswordService
{
    public const int MaxFailedLogins = 5;
    public const int LockoutMinutes = 15;

    // Used only as a throwaway carrier since PasswordHasher<T>'s API takes a user instance
    // but never actually inspects it for the Hash/HashPassword/VerifyHashedPassword operations
    // we use here.
    static readonly PanelUser DummyUser = new();

    readonly PasswordHasher<PanelUser> _hasher = new();

    // A precomputed hash of a fixed dummy password, used to perform a "dummy verify" when the
    // real user has no PasswordHash set, so that the NoPassword code path takes comparable time
    // to a real verification (timing-attack mitigation to avoid leaking account existence).
    static readonly string DummyHash = new PasswordHasher<PanelUser>().HashPassword(DummyUser, "dummy-password-for-timing-Xx9!");

    public string Hash(string password) => _hasher.HashPassword(DummyUser, password);

    // Exactly one Verify against the fixed, precomputed DummyHash -- no per-call HashPassword,
    // so this costs the same single PBKDF2 round as a real user's wrong-password Verify. Always
    // returns false (there is no real account behind it); the return value exists only so the
    // caller can treat this uniformly with a real check.
    public bool VerifyDummy(string password)
    {
        _ = Verify(DummyHash, password);
        return false;
    }

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        if (string.IsNullOrEmpty(password)) return false;

        PasswordVerificationResult result;
        try
        {
            // Scope the catch tightly to the verify call: a malformed stored hash surfaces as
            // FormatException (base64 decode) and must be treated as a failed login, not thrown.
            // We deliberately do NOT swallow other exception types here — those indicate a real
            // caller/programming bug and should surface rather than masquerade as bad credentials.
            result = _hasher.VerifyHashedPassword(DummyUser, hash, password);
        }
        catch (FormatException)
        {
            return false;
        }
        return result == PasswordVerificationResult.Success || result == PasswordVerificationResult.SuccessRehashNeeded;
    }

    public LoginCheck CheckPassword(PanelUser user, string password, DateTimeOffset now)
    {
        if (user.LockedUntil is DateTimeOffset lockedUntil && lockedUntil > now)
            return new LoginCheck(LoginOutcome.Locked, MutatedUser: false);

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            // Dummy verify for timing parity; result is discarded.
            _ = Verify(DummyHash, password);
            return new LoginCheck(LoginOutcome.NoPassword, MutatedUser: false);
        }

        if (Verify(user.PasswordHash, password))
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            return new LoginCheck(LoginOutcome.Success, MutatedUser: true);
        }

        user.FailedLoginCount++;
        if (user.FailedLoginCount >= MaxFailedLogins)
        {
            user.LockedUntil = now.AddMinutes(LockoutMinutes);
            return new LoginCheck(LoginOutcome.Locked, MutatedUser: true);
        }

        return new LoginCheck(LoginOutcome.BadCredentials, MutatedUser: true);
    }
}
