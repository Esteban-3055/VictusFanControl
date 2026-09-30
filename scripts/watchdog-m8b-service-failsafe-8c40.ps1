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

function Test-Phase {
    param(
        $Phase,
        [string]$Name,
        [int]$Numeric
    )

    if ($null -eq $Phase) { return $false }

    if ($Phase -is [string]) {
        return ($Phase -ceq $Name -or $Phase -ceq [string]$Numeric)
    }

    try { return ([int]$Phase -eq $Numeric) }
    catch { return $false }
}

function Get-M8BQualifiedLeasePhase {
    param($Journal)

    if ([int]$Journal.SchemaVersion -ne 2 -or
        $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18') {
        return $null
    }

    if (Test-Phase -Phase $Journal.Phase -Name 'WriteArmed' -Numeric 1) {
        if ($null -eq $Journal.PreviousOwned -and
            $null -eq $Journal.Owned -and
            $null -ne $Journal.Pending -and
            [int]$Journal.Pending.Cpu -eq 50 -and
            [int]$Journal.Pending.Gpu -eq 50) {
            return 'WRITE_ARMED'
        }

        return $null
    }

    if (Test-Phase -Phase $Journal.Phase -Name 'Owned' -Numeric 2) {
        if ($null -ne $Journal.Owned -and
            [int]$Journal.Owned.Cpu -eq 50 -and
            [int]$Journal.Owned.Gpu -eq 50) {
            return 'OWNED'
        }

        return $null
    }

    if (Test-Phase -Phase $Journal.Phase -Name 'Restoring' -Numeric 3) {
        $previousMatches =
            $null -ne $Journal.PreviousOwned -and
            [int]$Journal.PreviousOwned.Cpu -eq 50 -and
            [int]$Journal.PreviousOwned.Gpu -eq 50

        $ownedMatches =
            $null -ne $Journal.Owned -and
            [int]$Journal.Owned.Cpu -eq 50 -and
            [int]$Journal.Owned.Gpu -eq 50

        if ($previousMatches -or $ownedMatches) {
            return 'RESTORING'
        }

        return $null
    }

    return $null
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Wait-JournalGone {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path $journalPath)) { return $true }
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
                Log-Line ("M8B FAILSAFE OWNER-REUSED: PID={0} startTicks={1} expected={2}; refusing reused PID." -f
                    $ProcessId,$startTicks,$ProcessStartUtcTicks)
                return $null
            }

            if ($process.HasExited) { return $false }
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
            throw "M8B controller PID $ProcessId creation time changed before failsafe kill."
        }

        if ($process.HasExited) { return }

        Log-Line ("M8B FAILSAFE CONTROLLER-KILL: exact journal owner PID={0} startTicks={1}." -f
            $ProcessId,$ProcessStartUtcTicks)

        $process.Kill()

        if (-not $process.WaitForExit(5000)) {
            throw "Exact M8B controller PID $ProcessId did not terminate within 5 s."
        }
    }
    finally {
        $process.Dispose()
    }
}

try {
    Start-Sleep -Seconds $DelaySeconds

    if (-not (Test-Path $journalPath)) {
        Log-Line 'M8B FAILSAFE NO-OP: durable journal is absent.'
        exit 0
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json

    $qualifiedPhase = Get-M8BQualifiedLeasePhase -Journal $journal

    if ($null -eq $qualifiedPhase) {
        Log-Line 'M8B FAILSAFE REFUSED: retained journal is not an exact-target WRITE_ARMED pending 50/50, OWNED 50/50, or RESTORING 50/50 lease.'
        exit 2
    }

    $ownerPid = [int]$journal.Controller.ProcessId
    $ownerStartTicks = [long]$journal.Controller.ProcessStartUtcTicks

    if ($ownerPid -le 0 -or $ownerStartTicks -le 0) {
        Log-Line 'M8B FAILSAFE REFUSED: journal controller identity is incomplete.'
        exit 3
    }

    Log-Line ("M8B FAILSAFE TAKEOVER: exact-target {0} 50/50 lease remains after delay; controller PID={1} startTicks={2}." -f
        $qualifiedPhase,$ownerPid,$ownerStartTicks)

    $service = Get-ServiceState
    if (-not $service) {
        Log-Line 'M8B FAILSAFE REFUSED: recovery service is not installed.'
        exit 4
    }

    $controllerAlive = Test-ExactControllerAlive -ProcessId $ownerPid -ProcessStartUtcTicks $ownerStartTicks

    if ($null -eq $controllerAlive) {
        Log-Line 'M8B FAILSAFE REFUSED: controller PID was reused; retained journal preserved.'
        exit 5
    }

    if ($controllerAlive) {
        Kill-ExactController -ProcessId $ownerPid -ProcessStartUtcTicks $ownerStartTicks
    }
    else {
        Log-Line 'M8B FAILSAFE OWNER-ABSENT: journal owner process is already gone.'
    }

    $service = Get-ServiceState

    if ($service -and
        $service.State -eq 'Running' -and
        [int]$service.ProcessId -gt 0) {
        Log-Line ("M8B FAILSAFE SERVICE-LIVE: waiting for watchdog PID={0} owner-loss/deadline/restore recovery from phase {1}." -f
            [int]$service.ProcessId,$qualifiedPhase)

        if (Wait-JournalGone -Seconds 15) {
            Log-Line 'M8B FAILSAFE RECOVERED: running watchdog cleared the active M8B journal after exact owner loss/recovery.'
            exit 0
        }

        Log-Line 'M8B FAILSAFE SERVICE-RESTART: active M8B journal persists after owner loss; restarting qualified watchdog.'
        Restart-Service -Name $serviceName -Force
    }
    else {
        Log-Line 'M8B FAILSAFE SERVICE-START: starting qualified LocalSystem watchdog.'
        Start-Service -Name $serviceName
    }

    $deadline = (Get-Date).AddSeconds(20)
    $runningPid = 0

    while ((Get-Date) -lt $deadline) {
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
        Log-Line 'M8B FAILSAFE ERROR: recovery service did not reach Running within 20 s.'
        exit 6
    }

    Log-Line ("M8B FAILSAFE STARTED: recovery service PID={0}; waiting for journal recovery." -f
        $runningPid)

    if (-not (Wait-JournalGone -Seconds 20)) {
        Log-Line 'M8B FAILSAFE ERROR: durable journal remains after recovery-service takeover.'
        exit 7
    }

    Log-Line 'M8B FAILSAFE RECOVERED: durable journal cleared by qualified watchdog recovery.'
    exit 0
}
catch {
    Log-Line ("M8B FAILSAFE ERROR: " + $_.Exception.ToString())
    exit 8
}
