param([string]$Channel = '10.0')
$ErrorActionPreference = 'Stop'
if ($Channel -ne '10.0') { throw 'This project targets the .NET 10 stable channel.' }
$workspace = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $workspace '.build\toolchain'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
$metadata = Invoke-RestMethod -Uri "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$Channel/releases.json"
$version = $metadata.'latest-sdk'
if ($version -notmatch '^10\.0\.\d+$') { throw 'Unexpected SDK version from release metadata.' }
$release = $metadata.releases | Where-Object { $_.sdk.version -eq $version } | Select-Object -First 1
$file = $release.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') } | Select-Object -First 1
if (-not $file) { throw 'The official release has no Windows x64 SDK archive.' }
$archive = Join-Path $tools "dotnet-sdk-$version.zip"
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $file.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK SHA512 verification failed.' }
$install = Join-Path $tools "dotnet-$version"
if (-not (Test-Path -LiteralPath (Join-Path $install 'dotnet.exe'))) {
    Expand-Archive -LiteralPath $archive -DestinationPath $install
}
Set-Content -LiteralPath (Join-Path $tools 'selected-sdk.txt') -Value $version -Encoding utf8
& (Join-Path $install 'dotnet.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'The SDK could not be started.' }
