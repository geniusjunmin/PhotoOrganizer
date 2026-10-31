param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [switch]$RunWack
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$payloadSource = (Resolve-Path -LiteralPath $PayloadDirectory).Path
if (-not $payloadSource.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only a generated payload inside this workspace may be tested.'
}
$id = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$name = "PhotoOrganizer.Test.$id"
$reportFolder = Join-Path $workspace "artifacts\installation-test-$id"
$payload = Join-Path $reportFolder 'app'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -Path (Join-Path $payloadSource '*') -Destination $payload -Recurse
$tools = Join-Path $workspace '.build\packages\microsoft.windows.sdk.buildtools\10.0.26100.9457\bin\10.0.26100.0\x64'
$report = [ordered]@{ Name = $name; Install = $false; Launch = $false; Upgrade = $false; DataRetained = $false;
    Uninstall = $false; PhotosRetained = $false; CertificateRemoved = $false;
    WackExit = $null; WackOverall = $null; WackRequiredFailures = $null; WackRequiredWarnings = $null; WackOptionalFailures = $null; Error = $null }
$certificate = $null
$installed = $null
function Assert-Test($condition, $message) { if (-not $condition) { throw "Assertion failed: $message" }; Write-Output "PASS: $message" }
function Build-TestPackage([string]$version) {
    $manifestPath = Join-Path $payload 'AppxManifest.xml'
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    $manifest.Package.Identity.Name = $name
    $manifest.Package.Identity.Publisher = 'CN=PhotoOrganizer Development'
    $manifest.Package.Identity.Version = $version
    $manifest.Save($manifestPath)
    $path = Join-Path $reportFolder "$name-$version.msix"
    & (Join-Path $tools 'makeappx.exe') pack /d $payload /p $path /o > (Join-Path $reportFolder "makeappx-$version.log") 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Test package creation failed.' }
    & (Join-Path $tools 'signtool.exe') sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $path > (Join-Path $reportFolder "sign-$version.log") 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Test package signing failed.' }
    return $path
}
function Read-AppDiagnostics($package, [string]$label) {
    $path = Join-Path $reportFolder "$label.json"
    $arguments = '--diagnostics-file "' + $path + '"'
    $process = Start-Process -FilePath (Join-Path $package.InstallLocation 'PhotoOrganizer.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $path)) { throw 'Installed application failed to produce diagnostics.' }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}
try {
    if (Get-AppxPackage -Name $name) { throw 'Generated test identity already exists.' }
    $certificate = New-SelfSignedCertificate -Type Custom -KeyUsage DigitalSignature -Subject 'CN=PhotoOrganizer Development' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -FriendlyName "Photo Organizer temporary test $id" -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(14)
    $publicCertificate = Join-Path $reportFolder 'temporary-test-certificate.cer'
    Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
    Import-Certificate -FilePath $publicCertificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    $first = Build-TestPackage '1.0.0.0'
    Add-AppxPackage -Path $first
    $installed = Get-AppxPackage -Name $name
    Assert-Test ($null -ne $installed) 'MSIX installed under an isolated test identity'
    $report.Install = $true
    $diagnostics = Read-AppDiagnostics $installed 'installed'
    Assert-Test ($diagnostics.PackageIdentity -eq $installed.PackageFullName -and $diagnostics.Runtime -eq '10.0.14') 'Installed executable has package identity and bundled .NET 10.0.14'
    Assert-Test ($diagnostics.DpiContext -eq 'PerMonitorV2') 'Installed process uses PerMonitorV2 DPI awareness'
    $report.Launch = $true
    $data = Join-Path $env:LOCALAPPDATA ('Packages\' + $installed.PackageFamilyName + '\LocalState\PhotoOrganizer')
    $fixture = Join-Path $reportFolder 'photo-fixture'
    $dotnet = Join-Path $workspace '.build\toolchain\dotnet-10.0.403\dotnet.exe'
    & $dotnet (Join-Path $workspace 'PhotoOrganizer.Tests\bin\Release\net10.0-windows\PhotoOrganizer.Tests.dll') '--seed-package-data' $data $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Package test data creation failed.' }
    $original = Join-Path $fixture 'source\original.png'
    $originalHash = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
    $second = Build-TestPackage '1.0.1.0'
    Add-AppxPackage -Path $second
    $installed = Get-AppxPackage -Name $name
    Assert-Test ($installed.Version.ToString() -eq '1.0.1.0') 'MSIX upgraded from 1.0.0.0 to 1.0.1.0'
    $report.Upgrade = $true
    $diagnostics = Read-AppDiagnostics $installed 'upgraded'
    Assert-Test ($diagnostics.Language -eq 'en' -and $diagnostics.Theme -eq 'Dark' -and $diagnostics.HistoryEntries -eq 1) 'Settings and SQLite history survived package upgrade'
    $report.DataRetained = $true
    if ($RunWack) {
        $appcert = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe'
        if (-not (Test-Path -LiteralPath $appcert)) { throw 'Windows App Certification Kit is not installed.' }
        & $appcert reset > (Join-Path $reportFolder 'wack-reset.log') 2>&1
        $arguments = 'test -packagefullname "' + $installed.PackageFullName + '" -reportoutputpath "' + (Join-Path $reportFolder 'wack-report.xml') + '"'
        $process = Start-Process -FilePath $appcert -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
        $report.WackExit = $process.ExitCode
        Write-Output ('WACK_EXIT: ' + $process.ExitCode)
        [xml]$wack = Get-Content -LiteralPath (Join-Path $reportFolder 'wack-report.xml') -Raw -Encoding UTF8
        $report.WackOverall = $wack.REPORT.OVERALL_RESULT
        $report.WackRequiredFailures = @($wack.SelectNodes('//TEST') | Where-Object { $_.OPTIONAL -ne 'TRUE' -and $_.SelectSingleNode('RESULT').InnerText -eq 'FAIL' }).Count
        $report.WackRequiredWarnings = @($wack.SelectNodes('//TEST') | Where-Object { $_.OPTIONAL -ne 'TRUE' -and $_.SelectSingleNode('RESULT').InnerText -ne 'PASS' -and $_.SelectSingleNode('RESULT').InnerText -ne 'FAIL' }).Count
        $report.WackOptionalFailures = @($wack.SelectNodes('//TEST') | Where-Object { $_.OPTIONAL -eq 'TRUE' -and $_.SelectSingleNode('RESULT').InnerText -ne 'PASS' }).Count
        Assert-Test ($report.WackExit -eq 0 -and $report.WackOverall -eq 'PASS' -and $report.WackRequiredFailures -eq 0 -and $report.WackRequiredWarnings -eq 0) 'All required Windows App Certification Kit checks passed'
    }
} catch {
    $report.Error = $_.Exception.Message
    throw
} finally {
    try {
        $owned = Get-AppxPackage -Name $name
        if ($owned) { Remove-AppxPackage -Package $owned.PackageFullName }
        $report.Uninstall = $null -eq (Get-AppxPackage -Name $name)
        if ($original -and (Test-Path -LiteralPath $original)) {
            $report.PhotosRetained = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -eq $originalHash
        }
        if ($certificate) {
            $trusted = 'Cert:\LocalMachine\TrustedPeople\' + $certificate.Thumbprint
            if (Test-Path -LiteralPath $trusted) { Remove-Item -LiteralPath $trusted }
            $private = 'Cert:\CurrentUser\My\' + $certificate.Thumbprint
            if (Test-Path -LiteralPath $private) { Remove-Item -LiteralPath $private -DeleteKey }
            $report.CertificateRemoved = -not (Test-Path -LiteralPath $trusted) -and -not (Test-Path -LiteralPath $private)
        }
    } finally {
        $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportFolder 'installation-report.json') -Encoding UTF8
        Write-Output "INSTALLATION_REPORT: $reportFolder"
    }
}
Assert-Test $report.Uninstall 'Test package uninstalled'
Assert-Test $report.PhotosRetained 'User photo fixture preserved after uninstall'
Assert-Test $report.CertificateRemoved 'Temporary signing certificate and private key removed'
