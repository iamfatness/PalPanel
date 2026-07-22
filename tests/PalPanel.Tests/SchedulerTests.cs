using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Control;
using PalPanel.Data;

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
    }

    private class ThrowingOrchestrator : IServerOrchestrator
    {
        public Task StartAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task RestartAsync(string actor, IReadOnlyList<int>? warningMinutes, CancellationToken ct)
            => throw new InvalidOperationException("boom");
        public Task SaveAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task AnnounceAsync(string actor, string message, CancellationToken ct) => Task.CompletedTask;
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

    private static IDbContextFactory<PanelDb> NewDb()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return dbf;
    }

    // initialLastCheck seeds the scheduler's in-memory "last checked" cursor so tests can
    // control the fire window without waiting on real wall-clock time (production leaves
    // it defaulted to real startup time).
    private static SchedulerService MakeScheduler(
        IDbContextFactory<PanelDb> dbf, IServerOrchestrator orch, IBackupService backups, IEventSink events,
        DateTimeOffset initialLastCheck) =>
        new(dbf, orch, backups, events, initialLastCheck: initialLastCheck);

    [Fact]
    public async Task DueSchedule_FiresOnceThenNotAgainSameMinute()
    {
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { Cron = "0 4 * * *", Action = "restart", Enabled = true });
            db.SaveChanges();
        }
        var orch = new FakeOrchestrator();
        var sched = MakeScheduler(dbf, orch, new FakeBackup(), new RecordingEvents(),
            DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

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
    public async Task DisabledSchedule_NeverFires()
    {
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { Cron = "0 4 * * *", Action = "restart", Enabled = false });
            db.SaveChanges();
        }
        var orch = new FakeOrchestrator();
        var sched = MakeScheduler(dbf, orch, new FakeBackup(), new RecordingEvents(),
            DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        Assert.Equal(0, orch.RestartCalls);
    }

    [Fact]
    public async Task BackupSchedule_FiresCreateBackupWithScheduledReason()
    {
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { Cron = "0 4 * * *", Action = "backup", Enabled = true });
            db.SaveChanges();
        }
        var backup = new FakeBackup();
        var sched = MakeScheduler(dbf, new FakeOrchestrator(), backup, new RecordingEvents(),
            DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        Assert.Equal(["scheduled"], backup.Reasons);
    }

    [Fact]
    public async Task InvalidCron_LogsScheduleErrorOnceAndDoesNotThrow()
    {
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { Cron = "not a cron", Action = "restart", Enabled = true });
            db.SaveChanges();
        }
        var events = new RecordingEvents();
        var sched = MakeScheduler(dbf, new FakeOrchestrator(), new FakeBackup(), events,
            DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);
        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:05:10Z"), default);

        Assert.Single(events.Events, e => e.Type == "schedule-error");
    }

    [Fact]
    public async Task ScheduleThrows_LogsScheduleErrorLoudlyAndOtherSchedulesStillRun()
    {
        var dbf = NewDb();
        using (var db = dbf.CreateDbContext())
        {
            db.Schedules.Add(new Schedule { Cron = "0 4 * * *", Action = "restart", Enabled = true });
            db.Schedules.Add(new Schedule { Cron = "0 4 * * *", Action = "backup", Enabled = true });
            db.SaveChanges();
        }
        var events = new RecordingEvents();
        var backup = new FakeBackup();
        var sched = MakeScheduler(dbf, new ThrowingOrchestrator(), backup, events,
            DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await sched.CheckDueAsync(DateTimeOffset.Parse("2026-01-10T04:00:10Z"), default);

        Assert.Contains(events.Events, e => e.Type == "schedule-error");
        Assert.Equal(["scheduled"], backup.Reasons); // loop survives the restart schedule's failure
    }
}
