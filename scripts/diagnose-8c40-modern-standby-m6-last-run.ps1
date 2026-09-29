$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$appRoot = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'

$markerPaths = @(
    Join-Path $appRoot 'm6-modern-standby.ready'
    Join-Path $appRoot 'm6-modern-standby.presleep'
    Join-Path $appRoot 'm6-modern-standby.resume-gate'
    Join-Path $appRoot 'm6-modern-standby.reentry'
    Join-Path $appRoot 'm6-modern-standby.result'
)

function Write-Section {
    param([string]$Title)

    Write-Host ''
    Write-Host ("===== {0} =====" -f $Title) -ForegroundColor Cyan
}

function Try-ReadMarkerTimestamp {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return $null
    }

    try {
        $text = Get-Content $Path -Raw
        $parts = $text -split '\|'

        if ($parts.Length -lt 2) {
            return $null
        }

        return [DateTimeOffset]::Parse(
            $parts[1],
            [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return $null
    }
}

function Get-TimestampWindowLines {
    param(
        [string]$Path,
        [DateTimeOffset]$Start,
        [DateTimeOffset]$End
    )

    if (-not (Test-Path $Path)) {
        return @()
    }

    $selected = New-Object System.Collections.Generic.List[string]
    $includeContinuation = $false

    foreach ($line in Get-Content $Path) {
        $match = [regex]::Match(
            $line,
            '^(?<stamp>\d{4}-\d{2}-\d{2}T\S+)\s{2}')

        if ($match.Success) {
            try {
                $stamp = [DateTimeOffset]::Parse(
                    $match.Groups['stamp'].Value,
                    [Globalization.CultureInfo]::InvariantCulture)

                $includeContinuation =
                    $stamp -ge $Start -and
                    $stamp -le $End
            }
            catch {
                $includeContinuation = $false
            }
        }

        if ($includeContinuation) {
            $selected.Add($line)
        }
    }

    return $selected.ToArray()
}

function Normalize-EventMessage {
    param([string]$Message)

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return ''
    }

    return (($Message -replace '\s+', ' ').Trim())
}

Write-Host 'VictusFanControl - HP 8C40 M6 LAST-RUN READ-ONLY DIAGNOSTICS' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This helper performs no fan write, no firmware restore, no watchdog/service mutation and no sleep transition.' -ForegroundColor Yellow

Write-Section 'repository'

$branch = (& git rev-parse --abbrev-ref HEAD 2>&1 | Out-String).Trim()
$head = (& git rev-parse HEAD 2>&1 | Out-String).Trim()
$status = @(& git status --short 2>&1)

Write-Host "Branch: $branch"
Write-Host "HEAD  : $head"

if ($status.Count -eq 0 -or
    ($status.Count -eq 1 -and [string]::IsNullOrWhiteSpace([string]$status[0]))) {
    Write-Host 'Working tree: CLEAN' -ForegroundColor Green
}
else {
    Write-Warning 'Working tree is not clean. Diagnostic collection will continue read-only.'
    $status | ForEach-Object { Write-Host $_ }
}

Write-Section 'M6 markers'

$timestamps = New-Object System.Collections.Generic.List[DateTimeOffset]

foreach ($path in $markerPaths) {
    $name = Split-Path -Leaf $path

    if (Test-Path $path) {
        $text = Get-Content $path -Raw
        Write-Host ("{0}: {1}" -f $name, $text)

        $timestamp = Try-ReadMarkerTimestamp -Path $path
        if ($null -ne $timestamp) {
            $timestamps.Add($timestamp)
        }
    }
    else {
        Write-Host ("{0}: absent" -f $name)
    }
}

if ($timestamps.Count -eq 0) {
    throw 'No timestamped M6 marker is available. Nothing to correlate automatically.'
}

$ordered = @($timestamps | Sort-Object)
$windowStart = $ordered[0].AddSeconds(-45)
$windowEnd = $ordered[$ordered.Count - 1].AddSeconds(45)

Write-Host ''
Write-Host ("Correlation window: {0:O} -> {1:O}" -f $windowStart, $windowEnd)

$appLogPath = Join-Path $appRoot ("logs\events-{0}.log" -f $windowStart.LocalDateTime.ToString('yyyy-MM-dd'))
$serviceLogPath = Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f $windowStart.LocalDateTime.ToString('yyyy-MM-dd'))

Write-Section 'VFC application log in M6 window'

$appLines = @(Get-TimestampWindowLines -Path $appLogPath -Start $windowStart -End $windowEnd)

if ($appLines.Count -eq 0) {
    Write-Host "No application-log lines found in window. Path=$appLogPath"
}
else {
    $appLines | ForEach-Object { Write-Host $_ }
}

Write-Section 'M4 watchdog log in M6 window'

$serviceLines = @(Get-TimestampWindowLines -Path $serviceLogPath -Start $windowStart -End $windowEnd)

if ($serviceLines.Count -eq 0) {
    Write-Host "No watchdog-log lines found in window. Path=$serviceLogPath"
}
else {
    $serviceLines | ForEach-Object { Write-Host $_ }
}

Write-Section 'Kernel-Power evidence in M6 window'

$kernelEvents = @(
    Get-WinEvent -FilterHashtable @{
        LogName='System'
        ProviderName='Microsoft-Windows-Kernel-Power'
        StartTime=$windowStart.LocalDateTime
        EndTime=$windowEnd.LocalDateTime
    } -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Id -eq 42 -or
        $_.Id -eq 506 -or
        $_.Id -eq 507 -or
        $_.Id -eq 524 -or
        $_.Id -eq 566
    } |
    Sort-Object TimeCreated
)

if ($kernelEvents.Count -eq 0) {
    Write-Host 'No matching Kernel-Power events found.'
}
else {
    foreach ($event in $kernelEvents) {
        Write-Host ("{0:O} | ID={1} | {2}" -f
            $event.TimeCreated,
            $event.Id,
            (Normalize-EventMessage -Message $event.Message))
    }
}

Write-Section 'Power-Troubleshooter evidence in M6 window'

$troubleshooterEvents = @(
    Get-WinEvent -FilterHashtable @{
        LogName='System'
        ProviderName='Microsoft-Windows-Power-Troubleshooter'
        StartTime=$windowStart.LocalDateTime
        EndTime=$windowEnd.LocalDateTime
    } -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -eq 1 } |
    Sort-Object TimeCreated
)

if ($troubleshooterEvents.Count -eq 0) {
    Write-Host 'No matching Power-Troubleshooter events found.'
}
else {
    foreach ($event in $troubleshooterEvents) {
        Write-Host ("{0:O} | ID={1} | {2}" -f
            $event.TimeCreated,
            $event.Id,
            (Normalize-EventMessage -Message $event.Message))
    }
}

Write-Section 'current battery snapshot'

$battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue

if ($battery) {
    $battery |
        Select-Object Name,Status,BatteryStatus,EstimatedChargeRemaining,EstimatedRunTime,DesignVoltage |
        Format-List |
        Out-Host
}
else {
    Write-Host 'Win32_Battery returned no instance.'
}

Write-Section 'automatic classification'

$preSleepPath = Join-Path $appRoot 'm6-modern-standby.presleep'
$resultPath = Join-Path $appRoot 'm6-modern-standby.result'
$preSleepText = if (Test-Path $preSleepPath) { Get-Content $preSleepPath -Raw } else { '' }
$resultText = if (Test-Path $resultPath) { Get-Content $resultPath -Raw } else { '' }

$authorityTransitions = @(
    $appLines |
    Where-Object {
        $_ -match 'Fan authority @' -or
        $_ -match 'Safety supervisor' -or
        $_ -match 'runtime state changed' -or
        $_ -match 'M6: lifecycle boundary'
    }
)

if ($preSleepText -match 'wasCustom=False') {
    Write-Warning 'PRE-SLEEP captured wasCustom=False. The display-Off boundary did not prove active Custom ownership.'
}

if ($authorityTransitions.Count -gt 0) {
    Write-Host 'Relevant authority/safety lines:'
    $authorityTransitions | ForEach-Object { Write-Host ("  " + $_) }
}
else {
    Write-Host 'No explicit authority/safety transition line was found in the selected application-log window.'
}

$criticalBattery = $kernelEvents | Where-Object { $_.Id -eq 524 } | Select-Object -First 1
$batterySleep = $kernelEvents | Where-Object {
    $_.Id -eq 42 -and
    $_.Message -match '(?i)(battery|bater[ií]a)'
} | Select-Object -First 1
$hibernateResume = $kernelEvents | Where-Object {
    $_.Id -eq 507 -and
    $_.Message -match '(?i)hibern'
} | Select-Object -First 1

if ($criticalBattery) {
    Write-Warning 'Kernel-Power 524 critical-battery evidence exists in the M6 window.'
}

if ($batterySleep) {
    Write-Warning 'Kernel-Power 42 reports a battery-triggered sleep in the M6 window.'
}

if ($hibernateResume) {
    Write-Warning 'Kernel-Power 507 reports resume from hibernate in the M6 window.'
}

if ($resultText -match '^PASS\|') {
    Write-Host 'Stored M6 result marker says PASS. Full causal validation is still required.' -ForegroundColor Yellow
}
elseif ($resultText -match '^FAIL\|') {
    Write-Host 'Stored M6 result marker says FAIL / fail-closed.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'DIAGNOSTIC COMPLETE: read-only collection finished. Paste the complete output for causal review.' -ForegroundColor Green
