using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PalPanel.Auth;
using PalPanel.Data;
using PalPanel.Supervisor;

namespace PalPanel.Servers;

// The read surface poller/scheduler/UI use to reach live server runtimes. Implemented by
// ServerManager; abstracted so those consumers can be tested with fake runtimes.
public interface IServerRegistry
{
    IReadOnlyCollection<ServerRuntime> All();
    ServerRuntime? Get(Guid id);
}

// Owns the live ServerRuntime per enabled server and is the single entry point the UI, poller,
// and scheduler use to reach a server. Config lives in the DB (Servers table); this keeps the
// in-memory runtimes in sync with it.
//
// NOTE: two servers must not share a ProcessName — adoption (FindExisting) keys on the process
// name, so identical names would let both runtimes adopt the same process. AddAsync/UpdateAsync
// reject duplicate process names.
public sealed class ServerManager(
    IDbContextFactory<PanelDb> dbf,
    IProcessLauncher launcher,
    IHttpClientFactory httpFactory,
    IAdminGuard guard,
    ISecretProtector protector,
    ILogger<ServerManager>? log = null) : IServerRegistry
{
    private readonly ConcurrentDictionary<Guid, ServerRuntime> _runtimes = new();

    // Build runtimes for all enabled servers and adopt any already-running processes. A single
    // malformed config (e.g. a bad API URL) is skipped loudly rather than aborting startup for
    // every other server — the panel must still come up and manage the healthy servers.
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var configs = await db.Servers.Where(s => s.Enabled).ToListAsync(ct);
        foreach (var cfg in configs)
        {
            try
            {
                var rt = BuildRuntime(cfg);
                _runtimes[cfg.Id] = rt;
                rt.Supervisor.AdoptExistingIfRunning();
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Skipping server {Name} ({Id}): failed to build runtime", cfg.Name, cfg.Id);
            }
        }
    }

    public IReadOnlyCollection<ServerRuntime> All() => _runtimes.Values.ToList();

    public ServerRuntime? Get(Guid id) => _runtimes.TryGetValue(id, out var rt) ? rt : null;

    public async Task<IReadOnlyList<ServerConfig>> AllConfigsAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Servers.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<ServerConfig?> GetConfigAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<Guid> AddAsync(ServerConfig cfg, string plaintextPassword, CancellationToken ct = default)
    {
        await EnsureUniqueProcessNameAsync(cfg, ct);
        cfg.AdminPasswordEnc = protector.Protect(plaintextPassword);
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            db.Servers.Add(cfg);
            await db.SaveChangesAsync(ct);
        }
        if (cfg.Enabled)
        {
            var rt = BuildRuntime(cfg);
            _runtimes[cfg.Id] = rt;
            rt.Supervisor.AdoptExistingIfRunning();
        }
        return cfg.Id;
    }

    // Editing a server's config is only safe while it is not actively running (the supervisor
    // captured the old settings at construction). Callers stop the server first; this refuses
    // loudly otherwise rather than silently mutating a live server's config.
    public async Task UpdateAsync(ServerConfig cfg, string? newPlaintextPassword, CancellationToken ct = default)
    {
        if (Get(cfg.Id) is { State: ServerState.Running or ServerState.Starting or ServerState.Stopping })
            throw new InvalidOperationException("Stop the server before editing its configuration.");
        await EnsureUniqueProcessNameAsync(cfg, ct);

        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var row = await db.Servers.FirstOrDefaultAsync(s => s.Id == cfg.Id, ct)
                      ?? throw new InvalidOperationException($"Server {cfg.Id} not found.");
            cfg.AdminPasswordEnc = string.IsNullOrEmpty(newPlaintextPassword)
                ? row.AdminPasswordEnc                       // keep existing secret when left blank
                : protector.Protect(newPlaintextPassword);
            db.Entry(row).CurrentValues.SetValues(cfg);
            await db.SaveChangesAsync(ct);
        }

        _runtimes.TryRemove(cfg.Id, out _);
        if (cfg.Enabled)
        {
            var rt = BuildRuntime(cfg);
            _runtimes[cfg.Id] = rt;
            rt.Supervisor.AdoptExistingIfRunning();
        }
    }

    // Auto-restart settings only affect the poller's decisions, not the live process, so they can
    // be changed while the server runs: persist, then mutate the live runtime's config in place so
    // the poller reads the new values on its next tick (no rebuild/stop required).
    public async Task UpdateAutoRestartAsync(Guid id, int unreachableMinutes, double memoryGb, CancellationToken ct = default)
    {
        unreachableMinutes = Math.Max(0, unreachableMinutes);
        memoryGb = Math.Max(0, memoryGb);
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var row = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return;
            row.AutoRestartUnreachableMinutes = unreachableMinutes;
            row.AutoRestartMemoryGb = memoryGb;
            await db.SaveChangesAsync(ct);
        }
        if (Get(id) is { } rt)
        {
            rt.Config.AutoRestartUnreachableMinutes = unreachableMinutes;
            rt.Config.AutoRestartMemoryGb = memoryGb;
        }
    }

    // Launch args only affect a future launch, so they're updatable while the server runs: persist,
    // then apply to the live runtime (which feeds the supervisor's next launch).
    public async Task UpdateLaunchArgsAsync(Guid id, string args, CancellationToken ct = default)
    {
        args = (args ?? "").Trim();
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var row = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return;
            row.LaunchArgs = args;
            await db.SaveChangesAsync(ct);
        }
        Get(id)?.SetLaunchArgs(args);
    }

    // Live-update the optional public hostname (used only by the reachability/DNS check).
    public async Task UpdatePublicHostnameAsync(Guid id, string hostname, CancellationToken ct = default)
    {
        hostname = (hostname ?? "").Trim();
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var row = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return;
            row.PublicHostname = hostname;
            await db.SaveChangesAsync(ct);
        }
        if (Get(id) is { } rt) rt.Config.PublicHostname = hostname;
    }

    public async Task RemoveAsync(Guid id, string actor, CancellationToken ct = default)
    {
        if (_runtimes.TryRemove(id, out var rt))
        {
            // Best-effort graceful stop so removing a server doesn't leave an orphan process.
            try { await rt.Orchestrator.StopAsync(actor, ct); } catch { /* removal proceeds regardless */ }
        }
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is not null) { db.Servers.Remove(row); await db.SaveChangesAsync(ct); }
    }

    private ServerRuntime BuildRuntime(ServerConfig cfg) =>
        ServerRuntime.Build(cfg, launcher, httpFactory.CreateClient($"pal-{cfg.Id}"), dbf, guard, protector);

    private async Task EnsureUniqueProcessNameAsync(ServerConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ProcessName)) return;
        await using var db = await dbf.CreateDbContextAsync(ct);
        var clash = await db.Servers.AnyAsync(
            s => s.Id != cfg.Id && s.ProcessName == cfg.ProcessName, ct);
        if (clash) throw new InvalidOperationException(
            $"Another server already uses process name '{cfg.ProcessName}'. Process names must be unique.");
    }
}
