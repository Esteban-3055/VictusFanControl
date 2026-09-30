param(
    [ValidateRange(90,300)]
    [int]$DelaySeconds=120,

    [Parameter(Mandatory=$true)]
    [string]$LogPath
)

$ErrorActionPreference='Stop'
$serviceName='VictusFanControlWatchdogM4'
$journalPath=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'

function Log-Line([string]$Text){
    $directory=Split-Path -Parent $LogPath
    if($directory){New-Item -ItemType Directory -Force -Path $directory | Out-Null}
    Add-Content -LiteralPath $LogPath -Value ("{0:O}  {1}" -f (Get-Date),$Text)
}

function Test-Phase($Phase,[string]$Name,[int]$Numeric){
    if($null -eq $Phase){return $false}
    if($Phase -is [string]){return ($Phase -ceq $Name -or $Phase -ceq [string]$Numeric)}
    try{return ([int]$Phase -eq $Numeric)}catch{return $false}
}

function Get-M9DQualifiedLeasePhase($Journal){
    if([int]$Journal.SchemaVersion -ne 2 -or
       $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18'){
        return $null
    }

    if(Test-Phase $Journal.Phase 'WriteArmed' 1){
        if($null -eq $Journal.PreviousOwned -and
           $null -eq $Journal.Owned -and
           $null -ne $Journal.Pending -and
           [int]$Journal.Pending.Cpu -eq 30 -and
           [int]$Journal.Pending.Gpu -eq 30){return 'WRITE_ARMED'}
        return $null
    }

    if(Test-Phase $Journal.Phase 'Owned' 2){
        if($null -ne $Journal.Owned -and
           [int]$Journal.Owned.Cpu -eq 30 -and
           [int]$Journal.Owned.Gpu -eq 30){return 'OWNED'}
        return $null
    }

    if(Test-Phase $Journal.Phase 'Restoring' 3){
        $previousMatches=
            $null -ne $Journal.PreviousOwned -and
            [int]$Journal.PreviousOwned.Cpu -eq 30 -and
            [int]$Journal.PreviousOwned.Gpu -eq 30

        $ownedMatches=
            $null -ne $Journal.Owned -and
            [int]$Journal.Owned.Cpu -eq 30 -and
            [int]$Journal.Owned.Gpu -eq 30

        if($previousMatches -or $ownedMatches){return 'RESTORING'}
    }

    return $null
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function Test-ExactControllerAlive([int]$ProcessId,[long]$ProcessStartUtcTicks){
    try {
        $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
        try {
            $startTicks=[long]$process.StartTime.ToUniversalTime().Ticks
            if($startTicks -ne $ProcessStartUtcTicks){
                Log-Line ("M9D FAILSAFE OWNER-REUSED: PID={0} startTicks={1} expected={2}; refusing reused PID." -f
                    $ProcessId,$startTicks,$ProcessStartUtcTicks)
                return $null
            }
            if($process.HasExited){return $false}
            return $true
        }
        finally {$process.Dispose()}
    }
    catch [System.ArgumentException] {return $false}
    catch [System.InvalidOperationException] {return $false}
}

function Kill-ExactController([int]$ProcessId,[long]$ProcessStartUtcTicks){
    $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try {
        $startTicks=[long]$process.StartTime.ToUniversalTime().Ticks
        if($startTicks -ne $ProcessStartUtcTicks){
            throw "M9D controller PID $ProcessId creation time changed before failsafe kill."
        }
        if($process.HasExited){return}

        Log-Line ("M9D FAILSAFE CONTROLLER-KILL: exact journal owner PID={0} startTicks={1}." -f
            $ProcessId,$ProcessStartUtcTicks)

        $process.Kill()
        if(-not $process.WaitForExit(5000)){
            throw "Exact M9D controller PID $ProcessId did not terminate within 5 s."
        }
    }
    finally {$process.Dispose()}
}

try {
    Log-Line ("M9D FAILSAFE ARMED: pid={0} delaySeconds={1}; no takeover before delay expiry." -f
        $PID,$DelaySeconds)

    Start-Sleep -Seconds $DelaySeconds

    if(-not (Test-Path -LiteralPath $journalPath)){
        Log-Line 'M9D FAILSAFE NO-OP: durable journal is absent.'
        exit 0
    }

    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    $phase=Get-M9DQualifiedLeasePhase $journal
    if($null -eq $phase){
        Log-Line 'M9D FAILSAFE REFUSED: retained journal is not an exact-target WRITE_ARMED/OWNED/RESTORING 30/30 lease.'
        exit 2
    }

    $ownerPid=[int]$journal.Controller.ProcessId
    $ownerStartTicks=[long]$journal.Controller.ProcessStartUtcTicks
    if($ownerPid -le 0 -or $ownerStartTicks -le 0){
        Log-Line 'M9D FAILSAFE REFUSED: journal controller identity is incomplete.'
        exit 3
    }

    Log-Line ("M9D FAILSAFE TAKEOVER: exact-target {0} 30/30 lease remains; controller PID={1} startTicks={2}." -f
        $phase,$ownerPid,$ownerStartTicks)

    $service=Get-ServiceState
    if(-not $service){
        Log-Line 'M9D FAILSAFE REFUSED: recovery service is not installed.'
        exit 4
    }

    $alive=Test-ExactControllerAlive $ownerPid $ownerStartTicks
    if($null -eq $alive){
        Log-Line 'M9D FAILSAFE REFUSED: controller PID was reused; journal preserved.'
        exit 5
    }

    if($alive){Kill-ExactController $ownerPid $ownerStartTicks}
    else{Log-Line 'M9D FAILSAFE OWNER-ABSENT: journal owner process is already gone.'}

    $service=Get-ServiceState
    if($service -and $service.State -eq 'Running' -and [int]$service.ProcessId -gt 0){
        Log-Line ("M9D FAILSAFE SERVICE-LIVE: waiting for watchdog PID={0} recovery from {1}." -f
            [int]$service.ProcessId,$phase)

        if(Wait-JournalGone 15){
            Log-Line 'M9D FAILSAFE RECOVERED: running watchdog cleared retained lease.'
            exit 0
        }

        Log-Line 'M9D FAILSAFE SERVICE-RESTART: journal persists; restarting qualified watchdog.'
        Restart-Service -Name $serviceName -Force
    }
    else {
        Log-Line 'M9D FAILSAFE SERVICE-START: starting qualified LocalSystem watchdog.'
        Start-Service -Name $serviceName
    }

    $deadline=(Get-Date).AddSeconds(20)
    $runningPid=0
    while((Get-Date) -lt $deadline){
        $current=Get-ServiceState
        if($current -and $current.State -eq 'Running' -and [int]$current.ProcessId -gt 0){
            $runningPid=[int]$current.ProcessId
            break
        }
        Start-Sleep -Milliseconds 200
    }

    if($runningPid -le 0){
        Log-Line 'M9D FAILSAFE ERROR: recovery service did not reach Running.'
        exit 6
    }

    Log-Line ("M9D FAILSAFE STARTED: recovery service PID={0}; waiting for journal recovery." -f $runningPid)

    if(-not (Wait-JournalGone 20)){
        Log-Line 'M9D FAILSAFE ERROR: durable journal remains after recovery-service takeover.'
        exit 7
    }

    Log-Line 'M9D FAILSAFE RECOVERED: durable journal cleared by qualified watchdog recovery.'
    exit 0
}
catch {
    Log-Line ("M9D FAILSAFE ERROR: " + $_.Exception.ToString())
    exit 8
}
