param([switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    $env:DOTNET_CLI_HOME = Join-Path $workspace '.build\dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $local = Get-ChildItem (Join-Path $workspace '.build\toolchain') -Directory -Filter 'dotnet-10.0.*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name.Substring(7) } -Descending | Select-Object -First 1
    $dotnet = if ($local) { Join-Path $local.FullName 'dotnet.exe' } else { 'dotnet' }
    if (-not $SkipRestore) {
        & $dotnet restore .\PhotoOrganizer.slnx --packages .\.build\packages --locked-mode -p:NuGetAudit=true
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    }
    & $dotnet build .\PhotoOrganizer.slnx -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }
