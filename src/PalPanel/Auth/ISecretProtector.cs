using Microsoft.AspNetCore.DataProtection;

namespace PalPanel.Auth;

// Encrypts per-server secrets (server admin passwords) at rest in the Servers table.
// Backed by ASP.NET Core Data Protection so it works under the Windows service account
// without a Windows-only ProtectedData/DPAPI dependency; the keyring is persisted to a
// fixed directory in Program.cs so keys survive restarts and service-account changes.
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

public class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _p;
    public DataProtectionSecretProtector(IDataProtectionProvider provider)
        => _p = provider.CreateProtector("PalPanel.ServerSecrets.v1");

    public string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? "" : _p.Protect(plaintext);

    // A stored blank stays blank; a genuinely undecryptable blob is surfaced loudly by the
    // caller (never silently treated as an empty password) — see ServerRuntime.Build.
    public string Unprotect(string ciphertext) =>
        string.IsNullOrEmpty(ciphertext) ? "" : _p.Unprotect(ciphertext);
}
