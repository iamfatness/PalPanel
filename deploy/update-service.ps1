<#
.SYNOPSIS
    In-place update of an already-installed PalPanel Windows service on this host.

    Stops the PalPanel service, snapshots the current app directory, copies a fresh
    publish output over it while PRESERVING live data (palpanel.db, the dp-keys\
    Data Protection keyring, and appsettings.Local.json secrets), restarts the
    service, and health-checks http://localhost:5080.

    Run deploy\publish.ps1 first so the source build is current. This script must
    run elevated (stopping/starting a Windows service requires Administrator); if
    it isn't, it relaunches itself with a UAC prompt.

.NOTES
    Restarting the PalPanel *service* does NOT restart the Palworld game server —
    PalServer.exe keeps running and the panel re-adopts it on startup. The only
    downtime is a few seconds of the web dashboard; no players are dropped.

.EXAMPLE
    .\deploy\update-service.ps1
#>
param(
    [string]$Source      = "$PSScriptRoot\..\publish",
    [string]$AppDir      = "C:\PalPanel\app",
    [string]$ServiceName = "PalPanel",
    [int]   $HealthPort  = 5080
)

$ErrorActionPreference = "Stop"

# --- self-elevate: service control needs Administrator ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Not elevated - relaunching as Administrator (approve the UAC prompt)..." -ForegroundColor Yellow
    $q = '"'
    $relaunch = @('-NoExit','-NoProfile','-ExecutionPolicy','Bypass',
                  '-File',    ($q + $PSCommandPath + $q),
                  '-Source',  ($q + $Source + $q),
                  '-AppDir',  ($q + $AppDir + $q),
                  '-ServiceName', $ServiceName,
                  '-HealthPort',  $HealthPort)
    Start-Process powershell -Verb RunAs -ArgumentList $relaunch
    return
}

# --- resolve + sanity-check paths ---
$Source = (Resolve-Path $Source).Path
if (-not (Test-Path (Join-Path $Source 'PalPanel.dll'))) {
    throw "No PalPanel.dll under $Source. Run deploy\publish.ps1 first."
}
if (-not (Test-Path $AppDir)) {
    throw "$AppDir not found. This script updates an existing install; use install-service.ps1 for a first install."
}
$svc = Get-Service -Name $ServiceName -ErrorAction Stop

# --- live data that must NEVER be overwritten or deleted ---
$excludeFiles = @('appsettings.Local.json','palpanel.db','palpanel.db-wal','palpanel.db-shm')
$excludeDirs  = @((Join-Path $AppDir 'dp-keys'))

Write-Host "Stopping $ServiceName ..."
if ($svc.Status -ne 'Stopped') {
    Stop-Service $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus('Stopped', '00:00:30')
}

# --- snapshot the current install (full, including data) for rollback ---
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "C:\PalPanel\app.bak-$stamp"
Write-Host "Backing up $AppDir -> $backup ..."
robocopy $AppDir $backup /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Backup robocopy failed (code $LASTEXITCODE); aborting before touching the live install." }

# --- copy the new build over the app dir, WITHOUT /MIR (which would delete data),
#     and excluding the live secrets/db/keyring so they survive untouched ---
Write-Host "Deploying new build into $AppDir (preserving DB, keyring, local settings) ..."
robocopy $Source $AppDir /E /R:1 /W:1 /XF $excludeFiles /XD $excludeDirs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    Write-Warning "Deploy copy failed (code $LASTEXITCODE). Roll back: robocopy $backup $AppDir /MIR"
    throw "Deploy aborted."
}

Write-Host "Starting $ServiceName ..."
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')

# --- health check ---
Write-Host "Health check http://localhost:$HealthPort ..."
$up = $false
for ($i = 0; $i -lt 20; $i++) {
    try { Invoke-WebRequest "http://localhost:$HealthPort" -UseBasicParsing -TimeoutSec 3 | Out-Null; $up = $true; break }
    catch { Start-Sleep -Seconds 2 }
}

if ($up) {
    Write-Host "`nPalPanel updated and responding. Backup: $backup" -ForegroundColor Green
} else {
    Write-Warning "Service started but http://localhost:$HealthPort did not respond in ~40s."
    Write-Warning "Check: Get-EventLog -LogName Application -Source PalPanel -Newest 20"
    Write-Warning "Roll back:  Stop-Service $ServiceName; robocopy $backup $AppDir /MIR; Start-Service $ServiceName"
}
