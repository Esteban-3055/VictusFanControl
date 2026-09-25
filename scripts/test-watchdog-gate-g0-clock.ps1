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

# Use an isolated output directory. An older PowerShell session may still have a
# previous watchdog DLL loaded from the original reflection-based probe and
# Windows keeps that file locked until the shell exits. Gate G0 must not depend
# on closing the user's shell or overwrite the production/default build output.
$probeBuildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('VictusFanControl-GateG0-' + [Guid]::NewGuid().ToString('N'))
$probeExitCode = 1

try {
    New-Item -ItemType Directory -Path $probeBuildRoot -Force | Out-Null

    dotnet build .\src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj -c Release -warnaserror -o $probeBuildRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Gate G0 isolated watchdog build failed with exit code $LASTEXITCODE."
    }

    $assemblyPath = Join-Path $probeBuildRoot 'VictusFanControl.Watchdog.dll'
    if (-not (Test-Path $assemblyPath)) {
        throw "Watchdog assembly was not produced at expected isolated path: $assemblyPath"
    }

    & dotnet $assemblyPath --gate-g0-clock-probe --minimum-sleep-seconds $MinimumSleepSeconds --timeout-seconds $TimeoutSeconds
    $probeExitCode = $LASTEXITCODE
}
finally {
    if (Test-Path $probeBuildRoot) {
        Remove-Item $probeBuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $probeExitCode
