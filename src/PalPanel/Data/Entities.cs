namespace PalPanel.Data;

public class Sample
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; }
    public int Players { get; set; }
    public double Fps { get; set; }
    public double FrameTimeMs { get; set; }
    public long MemoryBytes { get; set; }
    public int UptimeSeconds { get; set; }
}

public class SampleRollup
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; }
    public string Granularity { get; set; } = "minute";
    public double AvgPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public double AvgFps { get; set; }
    public long AvgMemoryBytes { get; set; }
}

public class PlayerSession
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

public class EventLog
{
    public long Id { get; set; }
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
    public string Cron { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Parameters { get; set; }
    public bool Enabled { get; set; } = true;
}
