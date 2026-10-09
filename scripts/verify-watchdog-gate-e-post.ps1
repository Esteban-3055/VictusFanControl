$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdog'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogService'
$statusPath = Join-Path $serviceRoot 'state\gate-d.status.json'
$journalPath = Join-Path $serviceRoot 'state\lease.json'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'

function Read-EcState {
    if (-not (Test-Path $cli)) {
        throw "VictusFanControl CLI was not found at '$cli'. Build Release first."
    }

    $output = (& dotnet $cli --probe-88f8-ec-state 2>&1 | Out-String)
    $line = ($output -split "[\r\n]+" |
        Where-Object { $_ -match '^level CPU=' } |
        Select-Object -Last 1)

    if (-not $line) {
        throw "Could not parse EC state. Raw output: $output"
    }

    $match = [regex]::Match(
        $line,
        'level CPU=(\d+) GPU=(\d+)')

    if (-not $match.Success) {
        throw "Could not parse EC state line: $line"
    }

    [pscustomobject]@{
        Cpu = [int]$match.Groups[1].Value
        Gpu = [int]$match.Groups[2].Value
        Raw = $line
    }
}

Write-Host 'VictusFanControl - GATE E POSTCHECK (READ-ONLY)' -ForegroundColor Cyan
Write-Host ''

$svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
$servicePid = [int]$svc.ProcessId

Write-Host "Service state       : $($svc.State)"
Write-Host "Service start mode  : $($svc.StartMode)"
Write-Host "Service account     : $($svc.StartName)"
Write-Host "Service PID         : $servicePid"

if ($svc.State -cne 'Running') {
    throw "Watchdog service is not Running; state=$($svc.State)."
}

if ($svc.StartMode -notin @('Auto', 'Automatic')) {
    throw "Watchdog service is not configured for automatic start; StartMode=$($svc.StartMode)."
}

if ($svc.StartName -notmatch 'LocalSystem|SYSTEM') {
    throw "Watchdog service account is not LocalSystem; StartName=$($svc.StartName)."
}

if ($servicePid -le 0) {
    throw 'Running watchdog service has no valid PID.'
}

if (-not (Test-Path $statusPath)) {
    throw "Watchdog status marker is missing: $statusPath"
}

$status = Get-Content $statusPath -Raw | ConvertFrom-Json

Write-Host "Status Ready        : $($status.Ready)"
Write-Host "Status Blocked      : $($status.Blocked)"
Write-Host "Status Session      : $($status.SessionId)"
Write-Host "Status PID          : $($status.ProcessId)"
Write-Host "Status recovery     : $($status.RecoveryDisposition)"
Write-Host "Status detail       : $($status.Detail)"

if (-not $status.Ready -or $status.Blocked) {
    throw 'Watchdog status is not Ready/unblocked.'
}

if ([int]$status.SessionId -ne 0) {
    throw "Watchdog status is not Session 0; SessionId=$($status.SessionId)."
}

if ([int]$status.ProcessId -ne $servicePid) {
    throw "Watchdog status PID does not match the running service PID ($($status.ProcessId) != $servicePid)."
}

$journalExists = Test-Path $journalPath
Write-Host "Durable journal     : $(if ($journalExists) { 'PRESENT' } else { 'absent' })"

if ($journalExists) {
    throw 'Gate E postcheck requires no active durable lease journal.'
}

$ec = Read-EcState
Write-Host "Independent EC      : $($ec.Raw)"

if ($ec.Cpu -ne 255 -or $ec.Gpu -ne 255) {
    throw "Gate E postcheck requires firmware-owned FF/FF; read $($ec.Cpu)/$($ec.Gpu)."
}

Write-Host ''
Write-Host 'SCM recovery configuration (read-only):' -ForegroundColor Cyan
& sc.exe qfailure $serviceName | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe qfailure failed with exit code $LASTEXITCODE."
}

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$post = Read-Host 'Type SAME if the CPU undervolt is unchanged, or CHANGED if it changed'

if ($post -cne 'SAME') {
    if ($post -ceq 'CHANGED') {
        Write-Warning 'Undervolt preservation FAILED/CHANGED.'
        exit 2
    }

    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 3
}

Write-Host ''
Write-Host 'PASS: Gate E postcheck confirms production watchdog Running/Automatic/LocalSystem, Ready in Session 0, no lease journal, EC FF/FF, and unchanged OMEN undervolt.' -ForegroundColor Green
Write-Host 'No fan-control write was issued by this postcheck.' -ForegroundColor Green
exit 0
