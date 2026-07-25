namespace PalPanel.Data;

// One managed Palworld server instance. Per-server config previously lived (single-server)
// in PanelOptions; it now lives here, UI-managed and DB-backed. The admin password is stored
// DPAPI-encrypted (AdminPasswordEnc), exactly like the panel's auth secrets.
public class ServerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public string LaunchArgs { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public int BackupsToKeep { get; set; } = 20;
    public string ApiBaseUrl { get; set; } = "";
    public string AdminPasswordEnc { get; set; } = "";
    public int GracefulStopTimeoutSeconds { get; set; } = 60;
    public int CrashWindowMinutes { get; set; } = 10;
    public int MaxCrashesInWindow { get; set; } = 3;
    public int PollIntervalSeconds { get; set; } = 10;
    public bool AutoRestart { get; set; } = true;
    public bool Enabled { get; set; } = true;
    // Health-based auto-restart (0 = off), evaluated by the poller.
    public int AutoRestartUnreachableMinutes { get; set; }   // restart if the API is unreachable this long
    public double AutoRestartMemoryGb { get; set; }          // restart if process memory exceeds this many GB
    public long SteamAppId { get; set; } = 2394010;          // Palworld Dedicated Server; used for SteamCMD updates
    public string PublicHostname { get; set; } = "";         // optional: domain players connect to, for the reachability/DNS check
}

public class Sample
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public int Players { get; set; }
    public double Fps { get; set; }
    public double FrameTimeMs { get; set; }
    public long MemoryBytes { get; set; }
    public double Cpu { get; set; }        // server-process CPU %, 0-100 across all cores
    public int UptimeSeconds { get; set; }
}

public class SampleRollup
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public string Granularity { get; set; } = "minute";
    public double AvgPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public double AvgFps { get; set; }
    public long AvgMemoryBytes { get; set; }
    public double AvgCpu { get; set; }
}

public class PlayerSession
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

// A ban the panel issued: the authoritative current list lives in the server's banlist.txt
// (SteamIDs only), so we keep this to carry the name + reason + who/when for panel-issued bans.
public class BannedPlayer
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? BannedBy { get; set; }
    public DateTimeOffset BannedAt { get; set; }
}

public class EventLog
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? ActorEmail { get; set; }
}

public class PanelUser
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Role { get; set; } = "Viewer";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string? PasswordHash { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

public class Schedule
{
    public int Id { get; set; }
    public Guid ServerId { get; set; }
    public string Cron { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Parameters { get; set; }
    public bool Enabled { get; set; } = true;
}
