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
Write-Host 'It loads only the production WindowsMonotonicClock implementation and compares it with UTC wall time.'
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

$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
$clockType = $assembly.GetType(
    'VictusFanControl.Watchdog.WindowsMonotonicClock',
    $true
)

$clock = [Activator]::CreateInstance($clockType, $true)
if ($null -eq $clock) {
    throw 'Could not instantiate production WindowsMonotonicClock.'
}

$millisecondsProperty = $clockType.GetProperty(
    'Milliseconds',
    [System.Reflection.BindingFlags]'Instance,Public,NonPublic'
)

if ($null -eq $millisecondsProperty) {
    throw 'Could not resolve WindowsMonotonicClock.Milliseconds.'
}

function Read-ProductionMonotonicMilliseconds {
    [uint64]($millisecondsProperty.GetValue($clock))
}

$first = Read-ProductionMonotonicMilliseconds
Start-Sleep -Milliseconds 250
$second = Read-ProductionMonotonicMilliseconds

if ($second -lt $first) {
    throw "Production monotonic clock moved backwards before sleep test: $first -> $second"
}

Write-Host ''
Write-Host ('Preflight monotonic sample: {0} -> {1} ms' -f $first, $second)
Write-Host ''
Write-Host ('After arming, put Windows to Sleep and keep it asleep for at least {0} seconds.' -f $MinimumSleepSeconds) -ForegroundColor Yellow
Write-Host ('The detector will wait up to {0} seconds. Do not close this PowerShell window.' -f $TimeoutSeconds)
Write-Host 'After resume, the same process will compare wall-clock progress with the production unbiased clock.'
Write-Host ''
Read-Host 'Press Enter to arm the read-only detector' | Out-Null

$minimumExcludedMs = [double]$MinimumSleepSeconds * 1000.0
$pollMilliseconds = 250
$deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

$previousWall = [DateTimeOffset]::UtcNow
$previousMonotonic = Read-ProductionMonotonicMilliseconds

while ([DateTimeOffset]::UtcNow -lt $deadlineUtc) {
    Start-Sleep -Milliseconds $pollMilliseconds

    $wallNow = [DateTimeOffset]::UtcNow
    $monotonicNow = Read-ProductionMonotonicMilliseconds

    if ($monotonicNow -lt $previousMonotonic) {
        throw "Production monotonic clock moved backwards: $previousMonotonic -> $monotonicNow"
    }

    $wallStepMs = ($wallNow - $previousWall).TotalMilliseconds
    $monotonicStepMs = [double]($monotonicNow - $previousMonotonic)
    $excludedStepMs = $wallStepMs - $monotonicStepMs

    if ($excludedStepMs -ge $minimumExcludedMs) {
        Write-Host ''
        Write-Host ('Detected suspend/resume interval: wall +{0:N0} ms; unbiased +{1:N0} ms; excluded ~{2:N0} ms.' -f $wallStepMs, $monotonicStepMs, $excludedStepMs)
        Write-Host 'Gate G0 physical clock semantics: PASS' -ForegroundColor Green
        Write-Host 'Wall time advanced across sleep while the production QueryUnbiasedInterruptTime-based clock did not count the sleep interval.'
        exit 0
    }

    $previousWall = $wallNow
    $previousMonotonic = $monotonicNow
}

throw ('No qualifying sleep interval was detected within {0} seconds. Re-run and keep the machine asleep for at least {1} seconds.' -f $TimeoutSeconds, $MinimumSleepSeconds)
