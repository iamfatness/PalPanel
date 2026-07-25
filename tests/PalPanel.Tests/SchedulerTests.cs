using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Control;
using PalPanel.Data;
using PalPanel.Monitoring;
using PalPanel.PalApi;
using PalPanel.Servers;
using PalPanel.Supervisor;

public class SchedulerTests
{
    private class FakeOrchestrator : IServerOrchestrator
    {
        public int RestartCalls;
        public List<(string Actor, IReadOnlyList<int>? Warnings)> RestartArgs { get; } = [];
        public Task StartAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task RestartAsync(string actor, IReadOnlyList<int>? warningMinutes, CancellationToken ct)
        { RestartCalls++; RestartArgs.Add((actor, warningMinutes)); return Task.CompletedTask; }
        public Task SaveAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task AnnounceAsync(string actor, string message, CancellationToken ct) => Task.CompletedTask;
        public Task KickAsync(string actor, string userId, string name, CancellationToken ct) => Task.CompletedTask;
        public Task BanAsync(string actor, string userId, string name, CancellationToken ct) => Task.CompletedTask;
        public Task UnbanAsync(string actor, string userId, string name, CancellationToken ct) => Task.CompletedTask;
    }

    private class ThrowingOrchestrator : IServerOrchestrator
    {
        public Task StartAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task RestartAsync(string actor, IReadOnlyList<int>? warningMinutes, CancellationToken ct)
            => throw new InvalidOperationException("boom");
        public Task SaveAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task AnnounceAsync(string actor, string message, CancellationToken ct) => Task.CompletedTask;
        public Task KickAsync(string actor, string userId, string name, CancellationToken ct) => Task.CompletedTask;
        public Task BanAsync(string actor, string userId, string name, CancellationToken ct) => Task.CompletedTask;
        public Task UnbanAsync(string actor, string userId, string name, CancellationToken ct) => Task.CompletedTask;
    }

    private class FakeBackup : IBackupService
    {
        public List<string> Reasons { get; } = [];
        public Task<string> CreateBackupAsync(string reason, CancellationToken ct)
        { Reasons.Add(reason); return Task.FromResult("b.zip"); }
        public IReadOnlyList<BackupInfo> List() => [];
        public Task RestoreAsync(string fileName, CancellationToken ct) => Task.CompletedTask;
    }

    private class RecordingEvents : IEventSink
    {
        public List<(string Type, string Detail)> Events { get; } = [];
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { Events.Add((type, detail)); return Task.CompletedTask; }
    }

    private class FakeRegistry : IServerRegistry
    {
        private readonly Dictionary<Guid, ServerRuntime> _rt = new();
        public void Add(ServerRuntime rt) => _rt[rt.Id] = rt;
        public IReadOnlyCollection<ServerRuntime> All() => _rt.Values.ToList();
        public ServerRuntime? Get(Guid id) => _rt.TryGetValue(id, out var v) ? v : null;
    }

    // Build a runtime bound to a given id with injected orchestrator/backup/events; the
    // supervisor/api/snapshot are inert stand-ins (the scheduler only touches orch/backups/events).
    private static ServerRuntime Runtime(Guid id, IServerOrchestrator orch, IBackupService backups, IEventSink events)
    {
        var sup = new ProcessSupervisor(new FakeLauncher(), Options.Create(new PanelOptions()));
        return ServerRuntime.FromParts(new ServerConfig { Id = id, Name = "t" }, sup, new StubApi(), orch, backups, new SnapshotService(), events);
    }

    private sealed class StubApi : IPalApi
    {
        public Task<ServerInfo?> GetInfoAsync(CancellationToken ct) => Task.FromResult<ServerInfo?>(null);
        public Task<IReadOnlyList<PlayerInfo>> GetPlayersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayerInfo>>([]);
        public Task<ServerMetrics?> GetMetricsAsync(CancellationToken ct) => Task.FromResult<ServerMetrics?>(null);
        public Task AnnounceAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task KickAsync(string userId, string message, CancellationToken ct) => Task.CompletedTask;
        public Task BanAsync(string userId, string message, CancellationToken ct) => Task.CompletedTask;
        public Task UnbanAsync(string userId, CancellationToken ct) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAsync(int waitSeconds, string message, CancellationToken ct) => Task.CompletedTask;
    }

    private static IDbContextFactory<PanelDb> NewDb()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return dbf;
    }

    private static SchedulerService MakeScheduler(
        IDbContextFactory<PanelDb> dbf, IServerRegistry registry, IEventSink events,
        DateTimeOffset initialLastCheck) =>
        new(dbf, registry, events, initialLastCheck: initialLastCheck);

    [Fact]
    public async Task DueSchedule_FiresOnceThenNotAgainSameMinute()
    {
        var sid = Guid.NewGuid();
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { ServerId = sid, Cron = "0 4 * * *", Action = "restart", Enabled = true });
            db.SaveChanges();
        }
        var orch = new FakeOrchestrator();
        var reg = new FakeRegistry(); reg.Add(Runtime(sid, orch, new FakeBackup(), new RecordingEvents()));
        var sched = MakeScheduler(dbf, reg, new RecordingEvents(), DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:40Z"), default);
        Assert.Equal(1, orch.RestartCalls);

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-11T04:00:05Z"), default);
        Assert.Equal(2, orch.RestartCalls);

        Assert.All(orch.RestartArgs, a =>
        {
            Assert.Equal("scheduler", a.Actor);
            Assert.Equal(new[] { 10, 5, 1 }, a.Warnings);
        });
    }

    [Fact]
    public async Task Schedule_FiresAgainstItsOwnServerOnly()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { ServerId = a, Cron = "0 4 * * *", Action = "restart", Enabled = true });
            db.SaveChanges();
        }
        var orchA = new FakeOrchestrator();
        var orchB = new FakeOrchestrator();
        var reg = new FakeRegistry();
        reg.Add(Runtime(a, orchA, new FakeBackup(), new RecordingEvents()));
        reg.Add(Runtime(b, orchB, new FakeBackup(), new RecordingEvents()));
        var sched = MakeScheduler(dbf, reg, new RecordingEvents(), DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        Assert.Equal(1, orchA.RestartCalls);
        Assert.Equal(0, orchB.RestartCalls);
    }

    [Fact]
    public async Task DisabledSchedule_NeverFires()
    {
        var sid = Guid.NewGuid();
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { ServerId = sid, Cron = "0 4 * * *", Action = "restart", Enabled = false });
            db.SaveChanges();
        }
        var orch = new FakeOrchestrator();
        var reg = new FakeRegistry(); reg.Add(Runtime(sid, orch, new FakeBackup(), new RecordingEvents()));
        var sched = MakeScheduler(dbf, reg, new RecordingEvents(), DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        Assert.Equal(0, orch.RestartCalls);
    }

    [Fact]
    public async Task BackupSchedule_FiresCreateBackupWithScheduledReason()
    {
        var sid = Guid.NewGuid();
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { ServerId = sid, Cron = "0 4 * * *", Action = "backup", Enabled = true });
            db.SaveChanges();
        }
        var backup = new FakeBackup();
        var reg = new FakeRegistry(); reg.Add(Runtime(sid, new FakeOrchestrator(), backup, new RecordingEvents()));
        var sched = MakeScheduler(dbf, reg, new RecordingEvents(), DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        Assert.Equal(["scheduled"], backup.Reasons);
    }

    [Fact]
    public async Task InvalidCron_LogsScheduleErrorOnceAndDoesNotThrow()
    {
        var sid = Guid.NewGuid();
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { ServerId = sid, Cron = "not a cron", Action = "restart", Enabled = true });
            db.SaveChanges();
        }
        var events = new RecordingEvents();
        var reg = new FakeRegistry(); reg.Add(Runtime(sid, new FakeOrchestrator(), new FakeBackup(), new RecordingEvents()));
        var sched = MakeScheduler(dbf, reg, events, DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:05:10Z"), default);

        Assert.Single(events.Events, e => e.Type == "schedule-error");
    }

    [Fact]
    public async Task ScheduleForUnknownServer_LogsErrorOnce()
    {
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { ServerId = Guid.NewGuid(), Cron = "0 4 * * *", Action = "restart", Enabled = true });
            db.SaveChanges();
        }
        var events = new RecordingEvents();
        var sched = MakeScheduler(dbf, new FakeRegistry(), events, DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-11T04:00:10Z"), default);
        Assert.Single(events.Events, e => e.Type == "schedule-error");
    }

    [Fact]
    public async Task ScheduleThrows_LogsScheduleErrorLoudlyAndOtherSchedulesStillRun()
    {
        var sid = Guid.NewGuid();
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { ServerId = sid, Cron = "0 4 * * *", Action = "restart", Enabled = true });
            db.Schedules.Add(new Schedule { ServerId = sid, Cron = "0 4 * * *", Action = "backup", Enabled = true });
            db.SaveChanges();
        }
        var events = new RecordingEvents();
        var backup = new FakeBackup();
        var reg = new FakeRegistry(); reg.Add(Runtime(sid, new ThrowingOrchestrator(), backup, new RecordingEvents()));
        var sched = MakeScheduler(dbf, reg, events, DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);

        Assert.Contains(events.Events, e => e.Type == "schedule-error");
        Assert.Equal(["scheduled"], backup.Reasons); // loop survives the restart schedule's failure
    }
}
