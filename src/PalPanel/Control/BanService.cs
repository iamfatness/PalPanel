using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

namespace PalPanel.Control;

// Panel-tracked bans: names + reasons + who/when for bans issued through the panel. The
// authoritative current list is the server's banlist.txt (SteamIDs only); this enriches it.
// One instance is shared; every call opens its own short-lived DbContext.
public class BanService(IDbContextFactory<PanelDb> dbf)
{
    public async Task<IReadOnlyList<BannedPlayer>> ListAsync(Guid serverId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await db.BannedPlayers.Where(b => b.ServerId == serverId).ToListAsync();
        return rows.OrderByDescending(b => b.BannedAt).ToList();
    }

    public async Task<BannedPlayer?> GetAsync(Guid serverId, string userId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.BannedPlayers.FirstOrDefaultAsync(b => b.ServerId == serverId && b.UserId == userId);
    }

    public async Task<bool> IsBannedAsync(Guid serverId, string userId) =>
        await GetAsync(serverId, userId) is not null;

    // Idempotent: a repeat ban updates the existing record rather than duplicating it.
    public async Task RecordAsync(Guid serverId, string userId, string name, string reason, string actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.BannedPlayers.FirstOrDefaultAsync(b => b.ServerId == serverId && b.UserId == userId);
        if (existing is null)
        {
            db.BannedPlayers.Add(new BannedPlayer
            {
                ServerId = serverId, UserId = userId, Name = name,
                Reason = reason, BannedBy = actor, BannedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Name = name; existing.Reason = reason;
            existing.BannedBy = actor; existing.BannedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(Guid serverId, string userId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await db.BannedPlayers.Where(b => b.ServerId == serverId && b.UserId == userId).ToListAsync();
        if (rows.Count > 0) { db.BannedPlayers.RemoveRange(rows); await db.SaveChangesAsync(); }
    }
}
