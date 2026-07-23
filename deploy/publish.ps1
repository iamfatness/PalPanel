<#
.SYNOPSIS
    Publishes PalPanel as a self-contained win-x64 build ready to copy to the
    target machine (192.168.1.50) and install as a Windows service.

.PARAMETER OutDir
    Output directory for the published build. Defaults to ..\publish relative
    to this script (i.e. <repo root>\publish), which is already gitignored.

.EXAMPLE
    .\deploy\publish.ps1
    .\deploy\publish.ps1 -OutDir C:\temp\palpanel-build
#>
param(
    [string]$OutDir = "$PSScriptRoot\..\publish"
)

$ErrorActionPreference = "Stop"

$project = "$PSScriptRoot\..\src\PalPanel"
if (-not (Test-Path $project)) {
    throw "Could not find project at $project. Run this script from a checkout of the palpanel repo."
}

Write-Host "Publishing PalPanel (Release, win-x64, self-contained) to $OutDir ..."
dotnet publish $project -c Release -r win-x64 --self-contained -o $OutDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Published to $OutDir."
Write-Host "Next steps:"
Write-Host "  1. Copy the contents of $OutDir to the target machine, e.g. C:\PalPanel\app"
Write-Host "  2. Create appsettings.Local.json next to PalPanel.exe with the Panel:AdminPassword,"
Write-Host "     ServerExePath, SaveDirectory, BackupDirectory, and (if using Google sign-in)"
Write-Host "     GoogleClientId/GoogleClientSecret values (see docs/setup-cloudflare.md). Never commit this file."
Write-Host "  3. Run deploy\install-service.ps1 as Administrator on the target machine."
