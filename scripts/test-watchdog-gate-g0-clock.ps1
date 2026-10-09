param(
    [ValidateRange(15, 120)]
    [int]$WakeAfterSeconds = 20,

    [ValidateRange(5, 60)]
    [int]$MinimumExcludedSeconds = 10
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($env:OS -ne 'Windows_NT') {
    throw 'Gate G0 automatic S3 clock probe requires Windows.'
}

if ($WakeAfterSeconds -lt ($MinimumExcludedSeconds + 5)) {
    throw 'WakeAfterSeconds must be at least 5 seconds greater than MinimumExcludedSeconds.'
}

Write-Host 'VictusFanControl - GATE G0 AUTOMATIC S3 CLOCK PROBE' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This is FAN-HARDWARE READ-ONLY but it WILL suspend Windows.' -ForegroundColor Yellow
Write-Host 'It does not open PawnIO, EC, HP WMI fan control, the watchdog service pipe, or the lease journal.'
Write-Host ('It will arm an automatic wake request for about {0} seconds after dispatch.' -f $WakeAfterSeconds)
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
$overallExitCode = 1
$eventWindowStart = Get-Date

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

    & dotnet $assemblyPath --gate-g0-clock-probe --wake-after-seconds $WakeAfterSeconds --minimum-excluded-seconds $MinimumExcludedSeconds --gate-g0-auto-s3-token '88F8-G0-AUTO-S3'
    $probeExitCode = $LASTEXITCODE

    # Event Log delivery can lag the return from resume slightly. Give the
    # System log a small bounded window, then require causal suspend+resume
    # evidence instead of trusting only the final timing result.
    $eventDeadline = (Get-Date).AddSeconds(10)
    $kernelEvents = @()
    $sleepEvent = $null
    $resumeEvent = $null

    do {
        $kernelEvents = @(
            Get-WinEvent -FilterHashtable @{
                LogName = 'System'
                Id = 42, 107
                StartTime = $eventWindowStart.AddSeconds(-2)
            } -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ProviderName -eq 'Microsoft-Windows-Kernel-Power'
            } |
            Sort-Object TimeCreated
        )

        $sleepEvent = @(
            $kernelEvents |
            Where-Object Id -eq 42
        ) | Select-Object -First 1

        $resumeEvent = @(
            $kernelEvents |
            Where-Object Id -eq 107
        ) | Select-Object -Last 1

        if ($null -ne $sleepEvent -and
            $null -ne $resumeEvent -and
            $resumeEvent.TimeCreated -ge $sleepEvent.TimeCreated) {
            break
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $eventDeadline)

    Write-Host ''
    Write-Host 'Kernel-Power S3 evidence:'
    if ($kernelEvents.Count -gt 0) {
        $kernelEvents |
            Select-Object TimeCreated, Id, ProviderName, Message |
            Format-List |
            Out-String |
            Write-Host
    }
    else {
        Write-Host '  No Kernel-Power 42/107 events found in the test window.' -ForegroundColor Red
    }

    if ($probeExitCode -ne 0) {
        Write-Host ('Gate G0 automatic S3 probe: FAIL (probe exit {0})' -f $probeExitCode) -ForegroundColor Red
        $overallExitCode = $probeExitCode
    }
    elseif ($null -eq $sleepEvent -or
            $null -eq $resumeEvent -or
            $resumeEvent.TimeCreated -lt $sleepEvent.TimeCreated) {
        Write-Host 'Gate G0 automatic S3 probe: FAIL - missing ordered Kernel-Power 42 -> 107 evidence.' -ForegroundColor Red
        $overallExitCode = 1
    }
    else {
        $eventDuration = $resumeEvent.TimeCreated - $sleepEvent.TimeCreated
        Write-Host ('Kernel-Power causal interval: {0:N1} seconds' -f $eventDuration.TotalSeconds)
        Write-Host 'Gate G0 automatic S3 clock semantics: PASS' -ForegroundColor Green
        $overallExitCode = 0
    }
}
finally {
    if (Test-Path $probeBuildRoot) {
        Remove-Item $probeBuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $overallExitCode
