namespace PalPanel;

public class PanelOptions
{
    public string ServerExePath { get; set; } = @"C:\PalServer\PalServer.exe";
    public string ServerArgs { get; set; } = "-publiclobby";
    public string ServerProcessName { get; set; } = "PalServer";
    public string SaveDirectory { get; set; } = @"C:\PalServer\Pal\Saved";
    public string BackupDirectory { get; set; } = @"C:\PalPanel\Backups";
    public int BackupsToKeep { get; set; } = 20;
    public string ApiBaseUrl { get; set; } = "http://localhost:8212";
    public string AdminPassword { get; set; } = "";
    public int GracefulStopTimeoutSeconds { get; set; } = 60;
    public int CrashWindowMinutes { get; set; } = 10;
    public int MaxCrashesInWindow { get; set; } = 3;
    public int PollIntervalSeconds { get; set; } = 10;
    public string DbPath { get; set; } = "palpanel.db";
    public string AccessTeamDomain { get; set; } = "";   // e.g. https://yourteam.cloudflareaccess.com
    public string AccessAud { get; set; } = "";          // Access application AUD tag
    public bool AuthDisabled { get; set; } = false;      // dev only
}
