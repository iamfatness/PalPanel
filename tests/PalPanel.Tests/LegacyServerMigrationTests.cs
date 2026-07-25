using Microsoft.EntityFrameworkCore;
using PalPanel;
using PalPanel.Auth;
using PalPanel.Data;

public class LegacyServerMigrationTests
{
    // Identity protector: keeps the test independent of the Data Protection keyring.
    private class IdentityProtector : ISecretProtector
    {
        public string Protect(string p) => p;
        public string Unprotect(string c) => c;
    }

    private static PanelDb NewDb()
    {
        var opts = new DbContextOptionsBuilder<PanelDb>()
            .UseSqlite($"Data Source={Path.GetTempFileName()}").Options;
        var db = new PanelDb(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private static PanelOptions Legacy() => new()
    {
        ServerExePath = @"C:\PalServer\PalServer.exe",
        ServerArgs = "-publiclobby",
        ServerProcessName = "PalServer",
        SaveDirectory = @"C:\PalServer\Pal\Saved",
        BackupDirectory = @"C:\PalPanel\Backups",
        BackupsToKeep = 15,
        ApiBaseUrl = "http://localhost:8212",
        AdminPassword = "s3cret",
    };

    [Fact]
    public async Task SeedsOneServerFromLegacyAndStampsExistingRows()
    {
        using var db = NewDb();
        db.Samples.Add(new Sample { ServerId = Guid.Empty, Ts = DateTimeOffset.UtcNow, Players = 2 });
        db.Events.Add(new EventLog { ServerId = Guid.Empty, Ts = DateTimeOffset.UtcNow, Type = "start", Detail = "x" });
        db.Sessions.Add(new PlayerSession { ServerId = Guid.Empty, UserId = "u", Name = "n", JoinedAt = DateTimeOffset.UtcNow });
        db.Schedules.Add(new Schedule { ServerId = Guid.Empty, Cron = "0 4 * * *", Action = "restart" });
        await db.SaveChangesAsync();

        var id = await LegacyServerMigration.EnsureSeededAsync(db, Legacy(), new IdentityProtector());

        db.ChangeTracker.Clear();
        var server = Assert.Single(db.Servers);
        Assert.Equal(id, server.Id);
        Assert.Equal(@"C:\PalServer\PalServer.exe", server.ExePath);
        Assert.Equal(15, server.BackupsToKeep);
        Assert.Equal("s3cret", server.AdminPasswordEnc); // identity protector => plaintext preserved

        Assert.Equal(id, db.Samples.Single().ServerId);
        Assert.Equal(id, db.Events.Single().ServerId);
        Assert.Equal(id, db.Sessions.Single().ServerId);
        Assert.Equal(id, db.Schedules.Single().ServerId);
    }

    [Fact]
    public async Task IsIdempotent()
    {
        using var db = NewDb();
        var first = await LegacyServerMigration.EnsureSeededAsync(db, Legacy(), new IdentityProtector());
        var second = await LegacyServerMigration.EnsureSeededAsync(db, Legacy(), new IdentityProtector());
        Assert.Equal(first, second);
        Assert.Single(db.Servers);
    }

    [Fact]
    public async Task DoesNothingWhenAServerAlreadyExists()
    {
        using var db = NewDb();
        var existing = new ServerConfig { Id = Guid.NewGuid(), Name = "Pre-existing" };
        db.Servers.Add(existing);
        await db.SaveChangesAsync();

        var id = await LegacyServerMigration.EnsureSeededAsync(db, Legacy(), new IdentityProtector());
        Assert.Equal(existing.Id, id);
        Assert.Single(db.Servers);
    }
}
