using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PalPanel.Auth;
using PalPanel.Control;
using PalPanel.Data;
using PalPanel.Monitoring;
using PalPanel.PalApi;
using PalPanel.Supervisor;

namespace PalPanel.Servers;

// Everything one managed Palworld server needs, bundled and owned by ServerManager. Per-server
// config is mapped onto a PanelOptions instance so the existing (single-server-shaped) services
// — ProcessSupervisor, BackupService, ServerOrchestrator — are reused unchanged, one instance
// per server. Each runtime owns its own SnapshotService and a ServerId-stamping event sink.
public sealed class ServerRuntime
{
    public Guid Id => Config.Id;
    public ServerConfig Config { get; }
    public ProcessSupervisor Supervisor { get; }
    public IPalApi Api { get; }
    public IServerOrchestrator Orchestrator { get; }
    public IBackupService Backups { get; }
    public SnapshotService Snapshot { get; }
    public IEventSink Events { get; }

    public ServerState State => Supervisor.State;

    // The PanelOptions the supervisor reads its launch args from (same instance), so we can update
    // launch args live without rebuilding the runtime. Null for FromParts-built test runtimes.
    private readonly PanelOptions? _panelOptions;

    private ServerRuntime(ServerConfig cfg, ProcessSupervisor sup, IPalApi api,
        IServerOrchestrator orch, IBackupService backups, SnapshotService snap, IEventSink events,
        PanelOptions? panelOptions = null)
    {
        Config = cfg; Supervisor = sup; Api = api; Orchestrator = orch;
        Backups = backups; Snapshot = snap; Events = events; _panelOptions = panelOptions;
    }

    // Launch args only affect a FUTURE launch, not the running process, so they can change live.
    // Mutating the supervisor's PanelOptions (same instance) means its next Launch uses the new args.
    public void SetLaunchArgs(string args)
    {
        Config.LaunchArgs = args;
        if (_panelOptions is not null) _panelOptions.ServerArgs = args;
    }

    // Compose a runtime from already-built parts. Used by Build and by tests that need to
    // inject stub/fault-injecting collaborators (a throwing API or event sink).
    public static ServerRuntime FromParts(ServerConfig cfg, ProcessSupervisor sup, IPalApi api,
        IServerOrchestrator orch, IBackupService backups, SnapshotService snap, IEventSink events)
        => new(cfg, sup, api, orch, backups, snap, events);

    public static ServerRuntime Build(
        ServerConfig cfg,
        IProcessLauncher launcher,
        HttpClient http,
        IDbContextFactory<PanelDb> dbf,
        IAdminGuard guard,
        ISecretProtector protector)
    {
        var opts = Options.Create(ToPanelOptions(cfg, protector.Unprotect(cfg.AdminPasswordEnc)));
        var sup = new ProcessSupervisor(launcher, opts);
        var api = new PalApiClient(http, new PalApiSettings(cfg.ApiBaseUrl, protector.Unprotect(cfg.AdminPasswordEnc)));
        var sink = new ServerEventSink(dbf, cfg.Id);
        var backups = new BackupService(opts, sup, sink);
        var orch = new ServerOrchestrator(sup, api, backups, sink, guard);
        var snap = new SnapshotService();

        // A failing event sink must be loud but never propagate into supervisor internals.
        sup.OnEvent = async (t, d) => { try { await sink.LogAsync(t, d); } catch { /* sink already loud via ILogger elsewhere */ } };

        return new ServerRuntime(cfg, sup, api, orch, backups, snap, sink, opts.Value);
    }

    // Map the DB-backed per-server config onto the (still single-server-shaped) PanelOptions the
    // reused services read. Panel-global fields (DbPath, auth, cookies) are irrelevant here.
    private static PanelOptions ToPanelOptions(ServerConfig cfg, string adminPassword) => new()
    {
        ServerExePath = cfg.ExePath,
        ServerArgs = cfg.LaunchArgs,
        ServerProcessName = cfg.ProcessName,
        SaveDirectory = cfg.SaveDirectory,
        BackupDirectory = cfg.BackupDirectory,
        BackupsToKeep = cfg.BackupsToKeep,
        ApiBaseUrl = cfg.ApiBaseUrl,
        AdminPassword = adminPassword,
        GracefulStopTimeoutSeconds = cfg.GracefulStopTimeoutSeconds,
        CrashWindowMinutes = cfg.CrashWindowMinutes,
        MaxCrashesInWindow = cfg.MaxCrashesInWindow,
        PollIntervalSeconds = cfg.PollIntervalSeconds,
    };
}
