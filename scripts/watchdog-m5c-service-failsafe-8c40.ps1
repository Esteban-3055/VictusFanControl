param(
    [ValidateRange(90, 300)]
    [int]$DelaySeconds = 120,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogM4'
$journalPath = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'

function Log-Line {
    param([string]$Text)

    $directory = Split-Path -Parent $LogPath
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Add-Content -Path $LogPath -Value ("{0:O}  {1}" -f (Get-Date), $Text)
}

function Test-OwnedPhase {
    param($Phase)

    if ($null -eq $Phase) {
        return $false
    }

    if ($Phase -is [string]) {
        return ($Phase -ceq 'Owned' -or $Phase -ceq '2')
    }

    try {
        return ([int]$Phase -eq 2)
    }
    catch {
        return $false
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Wait-JournalGone {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path $journalPath)) {
            return $true
        }

        Start-Sleep -Milliseconds 200
    }

    return (-not (Test-Path $journalPath))
}

function Test-ExactControllerAlive {
    param(
        [int]$ProcessId,
        [long]$ProcessStartUtcTicks
    )

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)

        try {
            $startTicks = [long]$process.StartTime.ToUniversalTime().Ticks

            if ($startTicks -ne $ProcessStartUtcTicks) {
                Log-Line ("M5C FAILSAFE OWNER-REUSED: PID={0} startTicks={1} expected={2}; refusing to touch reused process." -f
                    $ProcessId,
                    $startTicks,
                    $ProcessStartUtcTicks)

                return $null
            }

            if ($process.HasExited) {
                return $false
            }

            return $true
        }
        finally {
            $process.Dispose()
        }
    }
    catch [System.ArgumentException] {
        return $false
    }
    catch [System.InvalidOperationException] {
        return $false
    }
}

function Kill-ExactController {
    param(
        [int]$ProcessId,
        [long]$ProcessStartUtcTicks
    )

    $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)

    try {
        $startTicks = [long]$process.StartTime.ToUniversalTime().Ticks

        if ($startTicks -ne $ProcessStartUtcTicks) {
            throw "Controller PID $ProcessId creation time changed before failsafe kill."
        }

        if ($process.HasExited) {
            return
        }

        Log-Line ("M5C FAILSAFE CONTROLLER-KILL: exact journal owner PID={0} startTicks={1}." -f
            $ProcessId,
            $ProcessStartUtcTicks)

        $process.Kill()

        if (-not $process.WaitForExit(5000)) {
            throw "Exact controller PID $ProcessId did not terminate within 5 s."
        }
    }
    finally {
        $process.Dispose()
    }
}

try {
    Start-Sleep -Seconds $DelaySeconds

    if (-not (Test-Path $journalPath)) {
        Log-Line 'M5C FAILSAFE NO-OP: durable journal is absent.'
        exit 0
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json

    if ([int]$journal.SchemaVersion -ne 2 -or
        $journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not (Test-OwnedPhase -Phase $journal.Phase) -or
        [int]$journal.Owned.Cpu -ne 30 -or
        [int]$journal.Owned.Gpu -ne 30) {
        Log-Line 'M5C FAILSAFE REFUSED: retained journal is not exact-target durable OWNED 30/30.'
        exit 2
    }

    $ownerPid = [int]$journal.Controller.ProcessId
    $ownerStartTicks = [long]$journal.Controller.ProcessStartUtcTicks

    if ($ownerPid -le 0 -or $ownerStartTicks -le 0) {
        Log-Line 'M5C FAILSAFE REFUSED: journal controller identity is incomplete.'
        exit 3
    }

    Log-Line ("M5C FAILSAFE TAKEOVER: exact-target OWNED 30/30 remains after delay; controller PID={0} startTicks={1}." -f
        $ownerPid,
        $ownerStartTicks)

    $service = Get-ServiceState
    if (-not $service) {
        Log-Line 'M5C FAILSAFE REFUSED: recovery service is not installed.'
        exit 4
    }

    $controllerAlive = Test-ExactControllerAlive -ProcessId $ownerPid -ProcessStartUtcTicks $ownerStartTicks

    if ($null -eq $controllerAlive) {
        Log-Line 'M5C FAILSAFE REFUSED: controller PID was reused; retained journal preserved for manual inspection.'
        exit 5
    }

    if ($controllerAlive) {
        Kill-ExactController -ProcessId $ownerPid -ProcessStartUtcTicks $ownerStartTicks
    }
    else {
        Log-Line 'M5C FAILSAFE OWNER-ABSENT: journal owner process is already gone.'
    }

    # Give a running watchdog a chance to perform its normal owner-loss recovery
    # after the exact controller is gone.
    $service = Get-ServiceState

    if ($service -and
        $service.State -eq 'Running' -and
        [int]$service.ProcessId -gt 0) {
        Log-Line ("M5C FAILSAFE SERVICE-LIVE: waiting for watchdog PID={0} owner-loss recovery." -f
            [int]$service.ProcessId)

        if (Wait-JournalGone -Seconds 15) {
            Log-Line 'M5C FAILSAFE RECOVERED: running watchdog cleared the journal after exact owner loss.'
            exit 0
        }

        # At this point the exact owner is gone and the durable journal remains.
        # Restarting the already-qualified service is safe and forces its normal
        # startup-recovery path; this process never performs an HP fan write.
        Log-Line 'M5C FAILSAFE SERVICE-RESTART: journal persists after owner death; restarting the already-qualified recovery service.'
        Restart-Service -Name $serviceName -Force
    }
    else {
        Log-Line 'M5C FAILSAFE SERVICE-START: service is absent; starting the already-qualified LocalSystem recovery service.'
        Start-Service -Name $serviceName
    }

    $serviceDeadline = (Get-Date).AddSeconds(20)
    $runningPid = 0

    while ((Get-Date) -lt $serviceDeadline) {
        $current = Get-ServiceState

        if ($current -and
            $current.State -eq 'Running' -and
            [int]$current.ProcessId -gt 0) {
            $runningPid = [int]$current.ProcessId
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if ($runningPid -le 0) {
        Log-Line 'M5C FAILSAFE ERROR: recovery service did not reach Running within 20 s.'
        exit 6
    }

    Log-Line ("M5C FAILSAFE STARTED: recovery service PID={0}; waiting for service-owned journal recovery." -f
        $runningPid)

    if (-not (Wait-JournalGone -Seconds 20)) {
        Log-Line 'M5C FAILSAFE ERROR: durable journal remains after recovery-service takeover.'
        exit 7
    }

    Log-Line 'M5C FAILSAFE RECOVERED: durable journal cleared by the already-qualified watchdog recovery path.'
    exit 0
}
catch {
    Log-Line ("M5C FAILSAFE ERROR: " + $_.Exception.ToString())
    exit 8
}
