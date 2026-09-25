param(
    [ValidateRange(5, 120)]
    [int]$MinimumSleepSeconds = 10,

    [ValidateRange(30, 900)]
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($env:OS -ne 'Windows_NT') {
    throw 'Gate G0 physical clock probe requires Windows.'
}

Write-Host 'VictusFanControl - GATE G0 PHYSICAL CLOCK PROBE (READ-ONLY)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test does not open PawnIO, EC, HP WMI fan control, the watchdog service pipe, or the lease journal.'
Write-Host 'It executes the production WindowsMonotonicClock inside the .NET 8 watchdog process and compares it with UTC wall time.'
Write-Host ''

& (Join-Path $PSScriptRoot 'test-watchdog-gate-g0-clock-invariants.ps1')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$assemblyPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\bin\Release\net8.0-windows\VictusFanControl.Watchdog.dll'
if (-not (Test-Path $assemblyPath)) {
    throw "Watchdog assembly was not produced at expected path: $assemblyPath"
}

& dotnet $assemblyPath --gate-g0-clock-probe --minimum-sleep-seconds $MinimumSleepSeconds --timeout-seconds $TimeoutSeconds
exit $LASTEXITCODE
