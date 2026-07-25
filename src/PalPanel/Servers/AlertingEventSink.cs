using PalPanel.Control;
using PalPanel.Data;

namespace PalPanel.Servers;

// Decorates a server's event sink: every event is persisted as normal, then notable lifecycle
// events are translated into crash/health alerts. Centralizing it here means the poller, supervisor
// and orchestrator need no alerting code — they already emit these events. Mapping (email fires on
// Warning+; Info is in-panel only):
//   crash                                          -> down (Warning)
//   held                                           -> down (Critical, escalates the crash alert)
//   launch-failed/restart-start-failed/…-failed    -> down (Critical)
//   api-unreachable                                -> reachable (Warning)
//   api-recovered                                  -> resolve reachable (+ down) with an all-clear
//   restart-launched/start/adopt                   -> resolve down (server is back up)
//   auto-restart                                   -> notification (Info)
//   backup-failed                                  -> notification (Warning)
public sealed class AlertingEventSink(IEventSink inner, AlertService alerts, Guid serverId, string serverName)
    : IEventSink
{
    public async Task LogAsync(string type, string detail, string? actorEmail = null)
    {
        await inner.LogAsync(type, detail, actorEmail);   // persist the raw event first, always
        try { await MapAsync(type, detail); }
        catch { /* alerting must never break the event pipeline */ }
    }

    private async Task MapAsync(string type, string detail)
    {
        switch (type)
        {
            case "crash":
                await alerts.RaiseAsync(serverId, serverName, "server-down", AlertSeverity.Warning,
                    $"{serverName}: server crashed", detail);
                break;
            case "held":
                await alerts.RaiseAsync(serverId, serverName, "server-down", AlertSeverity.Critical,
                    $"{serverName}: crash loop — auto-restart held", detail);
                break;
            case "launch-failed":
            case "restart-start-failed":
            case "auto-restart-failed":
                await alerts.RaiseAsync(serverId, serverName, "server-down", AlertSeverity.Critical,
                    $"{serverName}: restart failed", detail);
                break;
            case "api-unreachable":
                await alerts.RaiseAsync(serverId, serverName, "reachable", AlertSeverity.Warning,
                    $"{serverName}: REST API not answering", detail);
                break;
            case "api-recovered":
                await alerts.ResolveAsync(serverId, "reachable", $"{serverName}: REST API answering again");
                await alerts.ResolveAsync(serverId, "server-down", null);   // a recovered API means it's up
                break;
            case "restart-launched":
            case "start":
            case "adopt":
                await alerts.ResolveAsync(serverId, "server-down", null);
                break;
            case "auto-restart":
                await alerts.NotifyAsync(serverId, serverName, "auto-restart", AlertSeverity.Info,
                    $"{serverName}: auto-restarted", detail);
                break;
            case "backup-failed":
                await alerts.NotifyAsync(serverId, serverName, "backup", AlertSeverity.Warning,
                    $"{serverName}: backup failed", detail);
                break;
        }
    }
}
