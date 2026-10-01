param(
    [ValidateRange(180,600)]
    [int]$DelaySeconds=300,

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
       $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18'){return $null}

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
        $previousMatches=$null -ne $Journal.PreviousOwned -and [int]$Journal.PreviousOwned.Cpu -eq 30 -and [int]$Journal.PreviousOwned.Gpu -eq 30
        $ownedMatches=$null -ne $Journal.Owned -and [int]$Journal.Owned.Cpu -eq 30 -and [int]$Journal.Owned.Gpu -eq 30
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

function Get-ExactControllerState([int]$ProcessId,[long]$StartTicks){
    try {
        $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
        try {
            if([long]$process.StartTime.ToUniversalTime().Ticks -ne $StartTicks){return 'REUSED'}
            if($process.HasExited){return 'ABSENT'}
            return 'ALIVE'
        } finally {$process.Dispose()}
    }
    catch [System.ArgumentException] {return 'ABSENT'}
    catch [System.InvalidOperationException] {return 'ABSENT'}
}

try {
    Log-Line ("M9D FAILSAFE ARMED: pid={0} delaySeconds={1}; no takeover before delay expiry." -f $PID,$DelaySeconds)
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
    $ownerTicks=[long]$journal.Controller.ProcessStartUtcTicks
    if($ownerPid -le 0 -or $ownerTicks -le 0){
        Log-Line 'M9D FAILSAFE REFUSED: journal controller identity is incomplete.'
        exit 3
    }

    Log-Line ("M9D FAILSAFE TAKEOVER: exact-target {0} lease remains; owner PID={1} startTicks={2}." -f $phase,$ownerPid,$ownerTicks)

    $state=Get-ExactControllerState $ownerPid $ownerTicks
    if($state -ceq 'REUSED'){
        Log-Line 'M9D FAILSAFE REFUSED: controller PID was reused; journal preserved.'
        exit 5
    }

    if($state -ceq 'ALIVE'){
        $process=[System.Diagnostics.Process]::GetProcessById($ownerPid)
        try {
            Log-Line ("M9D FAILSAFE CONTROLLER-KILL: exact journal owner PID={0} startTicks={1}." -f $ownerPid,$ownerTicks)
            $process.Kill()
            if(-not $process.WaitForExit(5000)){throw 'M9D exact controller did not terminate within 5 s.'}
        } finally {$process.Dispose()}
    }

    $service=Get-ServiceState
    if(-not $service){
        Log-Line 'M9D FAILSAFE REFUSED: recovery service is not installed.'
        exit 4
    }

    if($service.State -ne 'Running' -or [int]$service.ProcessId -le 0){
        Log-Line 'M9D FAILSAFE SERVICE-START: starting qualified LocalSystem watchdog.'
        Start-Service -Name $serviceName
    }

    if(-not (Wait-JournalGone 25)){
        Log-Line 'M9D FAILSAFE SERVICE-RESTART: journal persists; restarting qualified watchdog.'
        Restart-Service -Name $serviceName -Force
        if(-not (Wait-JournalGone 25)){
            Log-Line 'M9D FAILSAFE ERROR: journal remains after watchdog recovery.'
            exit 7
        }
    }

    Log-Line 'M9D FAILSAFE RECOVERED: durable journal cleared by qualified watchdog.'
    exit 0
}
catch {
    Log-Line ("M9D FAILSAFE ERROR: " + $_.Exception.ToString())
    exit 8
}
