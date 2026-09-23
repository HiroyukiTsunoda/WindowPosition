param(
    [switch]$SkipBuild,
    [switch]$CheckApplication,
    [string]$Configuration = 'Release',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'tests\WindowPosition.Smoke\WindowPosition.Smoke.csproj'
$executablePath = Join-Path $repositoryRoot "tests\WindowPosition.Smoke\bin\$Configuration\net10.0-windows\WindowPosition.Smoke.exe"
if (-not $SkipBuild) {
    & dotnet build $projectPath --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Smoke project build failed: $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $executablePath)) { throw "Smoke executable not found: $executablePath" }
if (-not $OutputPath) {
    $reportName = if ($CheckApplication) { 'application-desktop.json' } else { 'desktop-smoke.json' }
    $OutputPath = Join-Path $repositoryRoot "artifacts\$reportName"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$arguments = @('--output', ('"' + $OutputPath + '"'))
if ($CheckApplication) { $arguments += @('--check-app', '"Window Position"') }

# Run this script on the interactive user's Winsta0\Default desktop.
# The test validates the desktop before creating any temporary test window.
$driverProcess = Start-Process -FilePath $executablePath -ArgumentList $arguments -WorkingDirectory $repositoryRoot -PassThru -Wait
if (-not (Test-Path -LiteralPath $OutputPath)) { throw "Smoke report was not created: $OutputPath" }
$report = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
$report.Checks | Format-Table Name, Passed, Details -AutoSize
if ($driverProcess.ExitCode -ne 0 -or -not $report.Success) {
    throw "Desktop verification failed. $($report.Failure) Report: $OutputPath"
}
Write-Output "Desktop verification passed. Report: $OutputPath"
