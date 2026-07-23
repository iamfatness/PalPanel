<#
.SYNOPSIS
    Installs PalPanel as a Windows service (must be run as Administrator).

.DESCRIPTION
    Creates the "PalPanel" service pointing at PalPanel.exe in the given
    install directory, configures failure recovery (auto-restart with
    backoff), and starts it. Safe to run while the Palworld server
    (PalServer.exe) is already running -- PalPanel adopts an already-running
    server on startup instead of double-launching it.

.PARAMETER InstallDir
    Directory containing the published PalPanel.exe. Defaults to
    C:\PalPanel\app. Copy the output of deploy\publish.ps1 here first.

.EXAMPLE
    # As Administrator:
    .\deploy\install-service.ps1
    .\deploy\install-service.ps1 -InstallDir "D:\Apps\PalPanel"
#>
param(
    [string]$InstallDir = "C:\PalPanel\app"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator (right-click PowerShell -> Run as administrator)."
}

$exe = Join-Path $InstallDir "PalPanel.exe"
if (-not (Test-Path $exe)) {
    throw "Could not find $exe. Run deploy\publish.ps1 and copy the output to $InstallDir first."
}

$existing = sc.exe query PalPanel 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "PalPanel service already exists. Stopping and removing it before reinstalling..."
    sc.exe stop PalPanel | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete PalPanel | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Creating PalPanel service (binPath: $exe) ..."
sc.exe create PalPanel binPath= "`"$exe`"" start= auto DisplayName= "PalPanel (Palworld control panel)"
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe create failed with exit code $LASTEXITCODE"
}

Write-Host "Configuring failure recovery (restart after 5s, 15s, 60s; reset counter after 24h) ..."
sc.exe failure PalPanel reset= 86400 actions= restart/5000/restart/15000/restart/60000
sc.exe failureflag PalPanel 1 | Out-Null

Write-Host "Starting PalPanel service ..."
sc.exe start PalPanel

Write-Host ""
Write-Host "Service installed and started. Check http://localhost:5080 on this machine."
Write-Host "If the Palworld server (PalServer.exe) was already running, PalPanel will have adopted it"
Write-Host "rather than starting a second copy -- no need to stop it first."
