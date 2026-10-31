param(
    [string]$PackageName = 'PhotoOrganizer.LocalTest',
    [string]$Publisher = 'CN=PhotoOrganizer Development',
    [string]$DisplayName = 'Photo Organizer',
    [string]$PublisherDisplayName = 'Photo Organizer',
    [string]$Version = '1.0.0.0'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if ($PackageName -notmatch '^[A-Za-z0-9.-]+$') { throw 'Invalid package identity.' }
if ($Version -notmatch '^[1-9]\d*\.\d+\.\d+\.0$') { throw 'Use a Store version with a nonzero major version and final component 0.' }
$local = Get-ChildItem (Join-Path $workspace '.build\toolchain') -Directory -Filter 'dotnet-10.0.*' -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name.Substring(7) } -Descending | Select-Object -First 1
$dotnet = if ($local) { Join-Path $local.FullName 'dotnet.exe' } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $workspace '.build\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$output = Join-Path $workspace ('artifacts\package-' + $Version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$payload = Join-Path $output 'app'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
& $dotnet restore (Join-Path $workspace 'packaging\BuildTools.csproj') --packages (Join-Path $workspace '.build\packages') -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Packaging tools restore failed.' }
$cache = Join-Path $workspace '.build\packages'
& $dotnet publish (Join-Path $workspace 'PhotoOrganizer\PhotoOrganizer.csproj') -c Release -r win-x64 --self-contained true --output $payload -p:NuGetAudit=true -p:PublishReadyToRun=false -p:DebugType=None "-p:RestorePackagesPath=$cache"
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
& (Join-Path $PSScriptRoot 'Create-Assets.ps1') -Destination (Join-Path $payload 'Assets')
$name = [Security.SecurityElement]::Escape($PackageName)
$publisherXml = [Security.SecurityElement]::Escape($Publisher)
$displayXml = [Security.SecurityElement]::Escape($DisplayName)
$publisherDisplayXml = [Security.SecurityElement]::Escape($PublisherDisplayName)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap rescap">
 <Identity Name="$name" Publisher="$publisherXml" Version="$Version" ProcessorArchitecture="x64" />
 <Properties>
  <DisplayName>$displayXml</DisplayName>
  <PublisherDisplayName>$publisherDisplayXml</PublisherDisplayName>
  <Logo>Assets\Logo50.png</Logo>
 </Properties>
 <Resources><Resource Language="zh-CN"/><Resource Language="en-US"/></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
 <Applications>
  <Application Id="App" Executable="PhotoOrganizer.exe" EntryPoint="Windows.FullTrustApplication">
   <uap:VisualElements DisplayName="$displayXml" Description="Offline photo organization with preview and undo"
    Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Assets\Logo44.png" BackgroundColor="#006DB1">
   </uap:VisualElements>
  </Application>
 </Applications>
 <Capabilities><rescap:Capability Name="runFullTrust"/></Capabilities>
</Package>
"@
[IO.File]::WriteAllText((Join-Path $payload 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))
$tools = Join-Path $workspace '.build\packages\microsoft.windows.sdk.buildtools\10.0.26100.9457\bin\10.0.26100.0\x64'
$package = Join-Path $output "$PackageName-$Version-x64.msix"
& (Join-Path $tools 'makeappx.exe') pack /d $payload /p $package /o > (Join-Path $output 'makeappx.log') 2>&1
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath (Join-Path $output 'makeappx.log') -Tail 15 | Write-Output
    throw 'MSIX validation or packaging failed.'
}
$upload = Join-Path $output "$PackageName-$Version-x64.msixupload"
$zip = [IO.Compression.ZipFile]::Open($upload, [IO.Compression.ZipArchiveMode]::Create)
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $package, (Split-Path $package -Leaf),
        [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
} finally { $zip.Dispose() }
Get-FileHash -LiteralPath $package -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'package-hash.json') -Encoding utf8
Write-Output "PACKAGE: $package"
