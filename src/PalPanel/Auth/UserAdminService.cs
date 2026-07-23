using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

namespace PalPanel.Auth;

// Admin-only user/password-management surface for Settings.razor. Kept separate from
// RoleService (which owns GetOrCreateAsync/SetRoleAsync/ListAsync) so that service stays
// focused on the login-time role lifecycle; this one owns account provisioning and password
// administration. Every mutating method here calls IAdminGuard.EnsureAdminAsync FIRST, exactly
// like RoleService.SetRoleAsync and every IServerOrchestrator method -- see IAdminGuard.cs for
// why AuthorizeView alone is never sufficient.
public interface IUserAdminService
{
    Task CreateUserAsync(string email, string role, string? initialPassword, string actor, CancellationToken ct = default);
    Task SetPasswordAsync(string email, string newPassword, string actor, CancellationToken ct = default);
    Task ClearPasswordAsync(string email, string actor, CancellationToken ct = default);
    Task DeleteUserAsync(string email, string actor, CancellationToken ct = default);
}

public class UserAdminService(IAdminGuard guard, IPasswordService passwords, IDbContextFactory<PanelDb> factory, IEventSink events) : IUserAdminService
{
    public async Task CreateUserAsync(string email, string role, string? initialPassword, string actor, CancellationToken ct = default)
    {
        await guard.EnsureAdminAsync(actor, "user-create", ct);

        var normalizedEmail = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            throw new InvalidOperationException("Email is required.");

        if (!RoleService.ValidRoles.Contains(role))
            throw new ArgumentException($"Invalid role '{role}'. Must be one of: {string.Join(", ", RoleService.ValidRoles)}", nameof(role));

        var hasPassword = !string.IsNullOrEmpty(initialPassword);
        if (hasPassword && initialPassword!.Length < AuthEndpoints.MinPasswordLength)
            throw new InvalidOperationException($"Password must be at least {AuthEndpoints.MinPasswordLength} characters.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct))
            throw new InvalidOperationException($"A user with email '{normalizedEmail}' already exists.");

        var now = DateTimeOffset.UtcNow;
        var user = new PanelUser
        {
            Email = normalizedEmail,
            Role = role,
            FirstSeen = now,
            LastSeen = now,
            PasswordHash = hasPassword ? passwords.Hash(initialPassword!) : null,
            MustChangePassword = hasPassword, // blank password => Google-only account, nothing to force-change
        };

        try
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Defense in depth against a concurrent double-submit racing past the AnyAsync()
            // check above -- the unique index on Email surfaces here instead of a duplicate row.
            throw new InvalidOperationException($"A user with email '{normalizedEmail}' already exists.");
        }

        await events.LogAsync("user-created", $"{normalizedEmail}: role={role} password={(hasPassword ? "set" : "none")}", actor);
    }

    public async Task SetPasswordAsync(string email, string newPassword, string actor, CancellationToken ct = default)
    {
        await guard.EnsureAdminAsync(actor, "password-reset", ct);

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < AuthEndpoints.MinPasswordLength)
            throw new InvalidOperationException($"Password must be at least {AuthEndpoints.MinPasswordLength} characters.");

        var normalizedEmail = (email ?? "").Trim().ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, ct)
            ?? throw new InvalidOperationException($"No such user '{normalizedEmail}'");

        user.PasswordHash = passwords.Hash(newPassword);
        user.MustChangePassword = true; // forces them to pick their own password on next sign-in
        await db.SaveChangesAsync(ct);
        await events.LogAsync("password-reset", normalizedEmail, actor);
    }

    public async Task ClearPasswordAsync(string email, string actor, CancellationToken ct = default)
    {
        await guard.EnsureAdminAsync(actor, "password-clear", ct);

        var normalizedEmail = (email ?? "").Trim().ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, ct)
            ?? throw new InvalidOperationException($"No such user '{normalizedEmail}'");

        user.PasswordHash = null; // reverts to Google-only sign-in
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);
        await events.LogAsync("password-cleared", normalizedEmail, actor);
    }

    public async Task DeleteUserAsync(string email, string actor, CancellationToken ct = default)
    {
        await guard.EnsureAdminAsync(actor, "user-delete", ct);

        var normalizedEmail = (email ?? "").Trim().ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, ct)
            ?? throw new InvalidOperationException($"No such user '{normalizedEmail}'");

        if (user.Role == "Admin")
        {
            var adminCount = await db.Users.CountAsync(u => u.Role == "Admin", ct);
            if (adminCount <= 1)
                throw new InvalidOperationException("Cannot delete the last remaining Admin");
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await events.LogAsync("user-deleted", normalizedEmail, actor);
    }
}
