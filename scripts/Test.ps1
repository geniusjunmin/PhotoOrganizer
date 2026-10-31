param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
$local = Get-ChildItem (Join-Path $workspace '.build\toolchain') -Directory -Filter 'dotnet-10.0.*' -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name.Substring(7) } -Descending | Select-Object -First 1
$dotnet = if ($local) { Join-Path $local.FullName 'dotnet.exe' } else { 'dotnet' }
$reports = Join-Path $workspace ('artifacts\tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$output = & $dotnet (Join-Path $workspace 'PhotoOrganizer.Tests\bin\Release\net10.0-windows\PhotoOrganizer.Tests.dll') (Join-Path $workspace '.build\verification') 2>&1
$exitCode = $LASTEXITCODE
$output | Tee-Object -LiteralPath (Join-Path $reports 'test-output.txt') | Write-Output
$cases = @($output | ForEach-Object { "$_" } | Where-Object { $_.StartsWith('PASS: ') })
$xml = New-Object Xml.XmlDocument
$suite = $xml.CreateElement('testsuite')
$suite.SetAttribute('name', 'PhotoOrganizer')
$suite.SetAttribute('tests', [string]($cases.Count + [int]($exitCode -ne 0)))
$suite.SetAttribute('failures', [string][int]($exitCode -ne 0))
$xml.AppendChild($suite) | Out-Null
foreach ($case in $cases) {
    $node = $xml.CreateElement('testcase')
    $node.SetAttribute('name', $case.Substring(6))
    $suite.AppendChild($node) | Out-Null
}
if ($exitCode -ne 0) {
    $node = $xml.CreateElement('testcase')
    $node.SetAttribute('name', 'Test process failure')
    $failure = $xml.CreateElement('failure')
    $failure.InnerText = $output -join [Environment]::NewLine
    $node.AppendChild($failure) | Out-Null
    $suite.AppendChild($node) | Out-Null
}
$xml.Save((Join-Path $reports 'test-results.xml'))
Write-Output "REPORTS: $reports"
if ($exitCode -ne 0) { throw "Automated tests failed with exit code $exitCode." }
