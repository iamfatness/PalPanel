using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

namespace PalPanel.Auth;

// Concurrency note (accepted for v1): GetOrCreateAsync's "is this the first user
// ever" check and SetRoleAsync's "is this the last Admin" check are read-then-write
// (TOCTOU) sequences with no transaction/serialization between the read and the
// write. Two exactly-simultaneous first-ever logins could both become Admin, and two
// simultaneous demotions of different Admins could in principle strand zero Admins.
// PalPanel is a single-operator panel behind Cloudflare Access with a handful of
// users, so the race window isn't worth locking complexity yet; revisit if this
// ever becomes multi-operator.
public class RoleService(IDbContextFactory<PanelDb> factory, IEventSink events, RoleChangeNotifier notifier)
{
    public static readonly string[] ValidRoles = ["Admin", "Viewer", "Blocked"];

    // First user ever seen becomes Admin; every subsequent new user starts as Viewer.
    // Revisits update LastSeen but never change an existing user's role.
    public async Task<PanelPrincipal> GetOrCreateAsync(string email)
    {
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        var now = DateTimeOffset.UtcNow;
        if (user is null)
        {
            var isFirstEver = !await db.Users.AnyAsync();
            user = new PanelUser
            {
                Email = email,
                Role = isFirstEver ? "Admin" : "Viewer",
                FirstSeen = now,
                LastSeen = now,
            };
            db.Users.Add(user);
        }
        else
        {
            user.LastSeen = now;
        }
        await db.SaveChangesAsync();
        return new PanelPrincipal(user.Email, user.Role);
    }

    public async Task SetRoleAsync(string email, string role, string actor)
    {
        if (!ValidRoles.Contains(role))
            throw new ArgumentException($"Invalid role '{role}'. Must be one of: {string.Join(", ", ValidRoles)}", nameof(role));

        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email)
            ?? throw new InvalidOperationException($"No such user '{email}'");

        if (user.Role == "Admin" && role != "Admin")
        {
            var adminCount = await db.Users.CountAsync(u => u.Role == "Admin");
            if (adminCount <= 1)
                throw new InvalidOperationException("Cannot demote the last remaining Admin");
        }

        var oldRole = user.Role;
        user.Role = role;
        await db.SaveChangesAsync();
        await events.LogAsync("role-change", $"{email}: {oldRole} -> {role}", actor);
        notifier.Notify(email, role); // live circuits react immediately (Blocked loses UI now)
    }

    public async Task<IReadOnlyList<PanelUser>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.OrderBy(u => u.Email).ToListAsync();
    }
}
