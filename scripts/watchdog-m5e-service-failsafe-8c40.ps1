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

function Test-WriteArmedPhase {
    param($Phase)

    if ($null -eq $Phase) {
        return $false
    }

    if ($Phase -is [string]) {
        return ($Phase -ceq 'WriteArmed' -or $Phase -ceq '1')
    }

    try {
        return ([int]$Phase -eq 1)
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

function Get-ExactControllerState {
    param(
        [int]$ProcessId,
        [long]$ProcessStartUtcTicks
    )

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)

        try {
            $startTicks = [long]$process.StartTime.ToUniversalTime().Ticks

            if ($startTicks -ne $ProcessStartUtcTicks) {
                Log-Line ("M5E FAILSAFE OWNER-REUSED: PID={0} startTicks={1} expected={2}; refusing reused process." -f
                    $ProcessId,
                    $startTicks,
                    $ProcessStartUtcTicks)

                return 'Reused'
            }

            if ($process.HasExited) {
                return 'Absent'
            }

            return 'Alive'
        }
        finally {
            $process.Dispose()
        }
    }
    catch [System.ArgumentException] {
        return 'Absent'
    }
    catch [System.InvalidOperationException] {
        return 'Absent'
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
            throw "Controller PID $ProcessId creation time changed before M5E failsafe kill."
        }

        if ($process.HasExited) {
            return
        }

        Log-Line ("M5E FAILSAFE CONTROLLER-KILL: exact WRITE_ARMED owner PID={0} startTicks={1}." -f
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
        Log-Line 'M5E FAILSAFE NO-OP: durable journal is absent.'
        exit 0
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json

    if ([int]$journal.SchemaVersion -ne 2 -or
        $journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not (Test-WriteArmedPhase -Phase $journal.Phase) -or
        [long]$journal.Generation -ne 2 -or
        $null -ne $journal.PreviousOwned -or
        [int]$journal.Pending.Cpu -ne 30 -or
        [int]$journal.Pending.Gpu -ne 30 -or
        $null -ne $journal.Owned) {
        Log-Line 'M5E FAILSAFE REFUSED: retained journal is not exact-target generation-2 WRITE_ARMED pending 30/30 with no previous/owned target.'
        exit 2
    }

    $ownerPid = [int]$journal.Controller.ProcessId
    $ownerStartTicks = [long]$journal.Controller.ProcessStartUtcTicks

    if ($ownerPid -le 0 -or $ownerStartTicks -le 0) {
        Log-Line 'M5E FAILSAFE REFUSED: journal controller identity is incomplete.'
        exit 3
    }

    Log-Line ("M5E FAILSAFE TAKEOVER: exact WRITE_ARMED pending 30/30 remains after delay; controller PID={0} startTicks={1}." -f
        $ownerPid,
        $ownerStartTicks)

    $service = Get-ServiceState
    if (-not $service) {
        Log-Line 'M5E FAILSAFE REFUSED: recovery service is not installed.'
        exit 4
    }

    $ownerState = Get-ExactControllerState -ProcessId $ownerPid -ProcessStartUtcTicks $ownerStartTicks

    if ($ownerState -ceq 'Reused') {
        Log-Line 'M5E FAILSAFE REFUSED: controller PID was reused; retained journal preserved for manual inspection.'
        exit 5
    }

    if ($ownerState -ceq 'Alive') {
        Kill-ExactController -ProcessId $ownerPid -ProcessStartUtcTicks $ownerStartTicks
    }
    else {
        Log-Line 'M5E FAILSAFE OWNER-ABSENT: exact journal owner is already gone.'
    }

    $service = Get-ServiceState

    if ($service -and
        $service.State -eq 'Running' -and
        [int]$service.ProcessId -gt 0) {
        Log-Line ("M5E FAILSAFE SERVICE-LIVE: waiting for watchdog PID={0} WRITE_ARMED owner-loss/deadline recovery." -f
            [int]$service.ProcessId)

        if (Wait-JournalGone -Seconds 15) {
            Log-Line 'M5E FAILSAFE RECOVERED: running watchdog cleared the WRITE_ARMED journal.'
            exit 0
        }

        Log-Line 'M5E FAILSAFE SERVICE-RESTART: exact owner is gone but WRITE_ARMED journal persists; restarting already-qualified recovery service.'
        Restart-Service -Name $serviceName -Force
    }
    else {
        Log-Line 'M5E FAILSAFE SERVICE-START: service is absent; starting already-qualified LocalSystem recovery service.'
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
        Log-Line 'M5E FAILSAFE ERROR: recovery service did not reach Running within 20 s.'
        exit 6
    }

    Log-Line ("M5E FAILSAFE STARTED: recovery service PID={0}; waiting for watchdog-owned journal recovery." -f
        $runningPid)

    if (-not (Wait-JournalGone -Seconds 20)) {
        Log-Line 'M5E FAILSAFE ERROR: durable WRITE_ARMED journal remains after recovery-service takeover.'
        exit 7
    }

    Log-Line 'M5E FAILSAFE RECOVERED: durable WRITE_ARMED journal cleared by qualified watchdog recovery.'
    exit 0
}
catch {
    Log-Line ("M5E FAILSAFE ERROR: " + $_.Exception.ToString())
    exit 8
}
