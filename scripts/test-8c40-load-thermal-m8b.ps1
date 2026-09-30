param(
    [ValidateRange(90,300)]
    [int]$FailsafeDelaySeconds = 120
)

$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$stateDir=Join-Path $serviceRoot 'state'
$statusPath=Join-Path $stateDir 'm4-8c40.status.json'
$journalPath=Join-Path $stateDir 'lease.json'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-m8b-service-failsafe-8c40.ps1'
$token='8C40-M8B-LOAD50'

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m8b-watchdog-load_{0}" -f $stamp)
$readyPath=Join-Path $evidenceRoot 'm8b-ready.json'
$resultPath=Join-Path $evidenceRoot 'm8b-result.json'
$summaryPath=Join-Path $evidenceRoot 'm8b-harness-summary.json'
$failsafeLog=Join-Path $evidenceRoot 'm8b-failsafe.log'

$controller=$null
$failsafe=$null
$pass=$false
$failure=$null
$serviceStartedByHarness=$false
$watchdogPid=0
$watchdogStartTicks=0L
$controllerPid=0
$controllerStartTicks=0L
$journalGeneration=$null
$logLineBoundary=0
$failsafeTakeover=$false
$causalChainPass=$false
$finalClosurePass=$false
$finalJournalAbsent=$false
$finalFirmwareProofPass=$false
$finalServiceBaselinePass=$false

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)

    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M8B must be run from an elevated PowerShell.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ProcessStartTicks {
    param([int]$ProcessId)

    $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try {
        return [long]$process.StartTime.ToUniversalTime().Ticks
    }
    finally {
        $process.Dispose()
    }
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){
        throw "M8B read-only setpoint probe failed. Raw output: $output"
    }

    $line=($output -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1)

    if(-not $line){
        throw "M8B could not parse HP 8C40 setpoint probe. Raw output: $output"
    }

    $match=[regex]::Match($line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){
        throw "M8B could not parse setpoints from: $line"
    }

    [pscustomobject]@{
        Cpu=[int]$match.Groups[1].Value
        Gpu=[int]$match.Groups[2].Value
        Raw=$line
    }
}

function Assert-StableFirmwareOwned {
    param([string]$Context)

    $first=Read-8C40Setpoint
    Write-Host ("{0} EC 1/2: {1}" -f $Context,$first.Raw)
    Start-Sleep -Milliseconds 75
    $second=Read-8C40Setpoint
    Write-Host ("{0} EC 2/2: {1}" -f $Context,$second.Raw)

    if($first.Cpu -ne 255 -or $first.Gpu -ne 255 -or
       $second.Cpu -ne 255 -or $second.Gpu -ne 255){
        throw "$Context requires two consecutive independent FF/FF observations."
    }
}

function Assert-M8BServiceInstalledBaseline {
    $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if(-not $svc){
        Write-Host 'M4 watchdog service is absent; installing through the versioned M4 installer before the write boundary.' -ForegroundColor Yellow
        & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')
        if($LASTEXITCODE -ne 0){throw "M4 installer failed with exit=$LASTEXITCODE."}
        $svc=Get-Service -Name $serviceName -ErrorAction Stop
    }

    $cim=Get-ServiceState
    if(-not $cim){throw 'M8B could not read the installed M4 service definition.'}

    if($svc.StartType -ne 'Manual' -or $svc.Status -ne 'Stopped'){
        throw "M8B requires M4 baseline Manual/Stopped; observed StartType=$($svc.StartType) Status=$($svc.Status)."
    }

    if($cim.StartName -notmatch 'LocalSystem|Local System'){
        throw "M8B requires the M4 service to run as LocalSystem; observed '$($cim.StartName)'."
    }

    if([string]$cim.PathName -notmatch 'VictusFanControl\.Watchdog\.exe' -or
       [string]$cim.PathName -notmatch '--m4-8c40-lease-service'){
        throw "M8B M4 service command line is not the isolated 8C40 lease service: $($cim.PathName)"
    }

    Write-Host ("M4 service baseline: StartType={0}, Status={1}, Account={2}" -f
        $svc.StartType,$svc.Status,$cim.StartName)
}

function Wait-M4Ready {
    param([int]$ExpectedPid)

    $deadline=(Get-Date).AddSeconds(20)

    while((Get-Date) -lt $deadline){
        if(Test-Path $statusPath){
            try {
                $status=Get-Content $statusPath -Raw | ConvertFrom-Json

                if([int]$status.ProcessId -eq $ExpectedPid -and
                   $status.Ready -and
                   -not $status.Blocked -and
                   [int]$status.SessionId -eq 0 -and
                   $status.AccountName -match 'SYSTEM$' -and
                   $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   $status.RecoveryDisposition -ceq 'Ready'){
                    return $status
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Timed out waiting for exact-target LocalSystem M4 Ready state.'
}

function Test-OwnedPhase {
    param($Phase)

    if($null -eq $Phase){return $false}

    if($Phase -is [string]){
        return ($Phase -ceq 'Owned' -or $Phase -ceq '2')
    }

    try { return ([int]$Phase -eq 2) }
    catch { return $false }
}

function Assert-M8BOwnedJournal {
    param(
        $Journal,
        [int]$ExpectedControllerPid,
        [long]$ExpectedControllerStartTicks
    )

    if([int]$Journal.SchemaVersion -ne 2 -or
       $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Test-OwnedPhase -Phase $Journal.Phase) -or
       [int]$Journal.Controller.ProcessId -ne $ExpectedControllerPid -or
       [long]$Journal.Controller.ProcessStartUtcTicks -ne $ExpectedControllerStartTicks -or
       [int]$Journal.Owned.Cpu -ne 50 -or
       [int]$Journal.Owned.Gpu -ne 50){
        throw 'M8B durable journal is not exact-target OWNED 50/50 bound to the exact controller PID + creation time.'
    }
}

function Start-M8BFailsafe {
    if(-not (Test-Path $failsafeScript)){
        throw "M8B failsafe script is missing: $failsafeScript"
    }

    $process=Start-Process powershell.exe -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy','Bypass',
        '-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,
        '-LogPath',$failsafeLog
    ) -WindowStyle Hidden -PassThru

    Start-Sleep -Milliseconds 250
    $process.Refresh()

    if($process.HasExited){
        throw 'M8B independent failsafe exited before the controller launch.'
    }

    return $process
}

function Test-FailsafeTakeover {
    if(-not (Test-Path $failsafeLog)){return $false}

    $text=Get-Content $failsafeLog -Raw

    return ($text -match 'M8B FAILSAFE TAKEOVER:' -or
            $text -match 'M8B FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M8B FAILSAFE SERVICE-START:' -or
            $text -match 'M8B FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M8B FAILSAFE STARTED:' -or
            $text -match 'M8B FAILSAFE RECOVERED:')
}

function Wait-ReadyMarker {
    param([int]$ExpectedPid)

    $deadline=(Get-Date).AddSeconds(35)

    while((Get-Date) -lt $deadline){
        if(Test-Path $readyPath){
            $ready=Get-Content $readyPath -Raw | ConvertFrom-Json

            if([int]$ready.ProcessId -ne $ExpectedPid){
                throw "M8B READY PID mismatch: marker=$($ready.ProcessId) expected=$ExpectedPid."
            }

            return $ready
        }

        if($controller -and $controller.HasExited){
            try {[void]$controller.WaitForExit(5000)}catch{}
            $controller.Refresh()

            $exitCode='unavailable'
            try {
                if($null -ne $controller.ExitCode){
                    $exitCode=[string]$controller.ExitCode
                    if([string]::IsNullOrWhiteSpace($exitCode)){$exitCode='unavailable'}
                }
            }
            catch {}

            $resultDetail='result evidence unavailable'
            if(Test-Path $resultPath){
                try {
                    $earlyResult=Get-Content $resultPath -Raw | ConvertFrom-Json
                    $resultDetail=("result={0}; reason={1}" -f
                        [string]$earlyResult.Result,
                        [string]$earlyResult.FailureReason)
                }
                catch {
                    $resultDetail=("result evidence unreadable: {0}" -f $_.Exception.Message)
                }
            }

            throw ("M8B controller exited before READY. ExitCode={0}; {1}" -f
                $exitCode,$resultDetail)
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Timed out waiting for M8B OWNED 50/50 READY marker.'
}

function Wait-ControllerExit {
    param([int]$Seconds)

    if(-not $controller.WaitForExit($Seconds*1000)){
        throw "M8B controller did not exit within $Seconds s after READY."
    }

    $controller.Refresh()
    $exitCode=$controller.ExitCode

    if($null -eq $exitCode){
        throw 'M8B controller terminated but ExitCode was unavailable.'
    }

    return [int]$exitCode
}

function Wait-JournalGone {
    param([int]$Seconds)

    $deadline=(Get-Date).AddSeconds($Seconds)

    while((Get-Date) -lt $deadline){
        if(-not (Test-Path $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }

    return (-not (Test-Path $journalPath))
}

function Assert-CausalServiceLog {
    param([int]$ControllerPid)

    if(-not (Test-Path $serviceLog)){
        throw "M8B service log is missing: $serviceLog"
    }

    $all=@(Get-Content $serviceLog)
    $new=@($all | Select-Object -Skip $logLineBoundary)

    $tag="controller PID=$ControllerPid"

    $prepare=@()
    $intent=@()
    $commit=@()
    $restore=@()
    $release=@()

    for($i=0;$i -lt $new.Count;$i++){
        $line=[string]$new[$i]

        if($line -match 'WATCHDOG PREPARE ACK' -and $line.Contains($tag)){$prepare+=@($i)}
        if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line.Contains($tag) -and $line -match 'target=50/50'){$intent+=@($i)}
        if($line -match 'WATCHDOG COMMIT ACK' -and $line.Contains($tag) -and $line -match 'target=50/50'){$commit+=@($i)}
        if($line -match 'WATCHDOG RESTORE_BEGIN ACK' -and $line.Contains($tag)){$restore+=@($i)}
        if($line -match 'WATCHDOG RELEASE ACK' -and $line.Contains($tag)){$release+=@($i)}
    }

    if($prepare.Count -ne 1 -or
       $intent.Count -ne 1 -or
       $commit.Count -ne 1 -or
       $restore.Count -ne 1 -or
       $release.Count -ne 1){
        Write-Host 'M8B new watchdog log segment:' -ForegroundColor Cyan
        $new | Select-Object -Last 160
        throw ("M8B causal log counts invalid: PREPARE={0} WRITE_INTENT={1} COMMIT={2} RESTORE_BEGIN={3} RELEASE={4}" -f
            $prepare.Count,$intent.Count,$commit.Count,$restore.Count,$release.Count)
    }

    if(-not ($prepare[0] -lt $intent[0] -and
             $intent[0] -lt $commit[0] -and
             $commit[0] -lt $restore[0] -and
             $restore[0] -lt $release[0])){
        throw 'M8B watchdog causal ordering is not PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE.'
    }

    Write-Host 'M8B watchdog causal chain: PREPARE -> WRITE_INTENT(50/50) -> COMMIT(50/50) -> RESTORE_BEGIN -> RELEASE.' -ForegroundColor Green
}

function Write-HarnessSummary {
    param(
        [string]$Result,
        [string]$Failure
    )

    $summary=[ordered]@{
        schemaVersion=1
        gate='M8B-HARNESS'
        result=$Result
        failure=$Failure
        evidenceHead=((& git rev-parse HEAD 2>$null) | Select-Object -First 1)
        timestamp=(Get-Date).ToUniversalTime().ToString('O')
        watchdogPid=$watchdogPid
        watchdogStartUtcTicks=$watchdogStartTicks
        controllerPid=$controllerPid
        controllerStartUtcTicks=$controllerStartTicks
        journalGeneration=$journalGeneration
        failsafeDelaySeconds=$FailsafeDelaySeconds
        failsafeTakeover=$failsafeTakeover
        causalChainPass=$causalChainPass
        finalClosurePass=$finalClosurePass
        finalJournalAbsent=$finalJournalAbsent
        finalFirmwareProofPass=$finalFirmwareProofPass
        finalServiceBaselinePass=$finalServiceBaselinePass
        readyPath=$readyPath
        resultPath=$resultPath
        failsafeLog=$failsafeLog
    }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8
}

Assert-Administrator
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 M8B WATCHDOG-BACKED REPRESENTATIVE LOAD 50/50' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This is a WRITE-CAPABLE qualification gate.' -ForegroundColor Yellow
Write-Host 'It may issue exactly one real equal-only 50/50 command after representative load is established.' -ForegroundColor Yellow
Write-Host 'An independent delayed failsafe is armed before the write-capable controller launches.' -ForegroundColor Yellow
Write-Host ''

try {
    foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
        if(Get-Process -Name $name -ErrorAction SilentlyContinue){
            throw "M8B refused while '$name' is running."
        }
    }

    Write-Host 'Step 1: exact-current-HEAD M8 no-write preflight...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8-preflight.ps1')
    if($LASTEXITCODE -ne 0){throw "M8 preflight failed with exit=$LASTEXITCODE."}

    Write-Host ''
    Write-Host 'Step 2: M8B static harness invariant...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8b-invariants.ps1')
    if($LASTEXITCODE -ne 0){throw "M8B invariant failed with exit=$LASTEXITCODE."}

    $profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
    if(-not [bool]$profile.loadThermalM8Qualification.m8a.physicalPassed){
        throw 'M8B refuses because M8A physicalPassed is not true in the versioned profile.'
    }

    if(-not [bool]$profile.loadThermalM8Qualification.m8a.m8bAuthorized){
        throw 'M8B refuses because M8A has not authorized M8B code/spec progression.'
    }

    if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
       [bool]$profile.control.enabledByDefault){
        throw 'M8B refuses if production watchdog recovery or automatic policy is already enabled.'
    }

    Write-Host ''
    Write-Host 'Step 3: clean durable/service/firmware baseline...' -ForegroundColor Cyan

    if(Test-Path $journalPath){
        Get-Content $journalPath
        throw 'M8B refuses while durable lease evidence exists. The journal was not deleted.'
    }

    Assert-M8BServiceInstalledBaseline
    Assert-StableFirmwareOwned -Context 'M8B baseline'

    Write-Host ''
    Write-Host 'M8B fixed physical criteria:' -ForegroundColor Cyan
    Write-Host '  Pre-write: 3 consecutive representative samples within at most 10.'
    Write-Host '  One and only one ApplyAsync(50/50).'
    Write-Host '  Supervision: 30 samples at 1 second; >=22/30 representative and >=10 consecutive.'
    Write-Host '  CPU 95..98.x C: 5 unique consecutive readings required for thermal handoff.'
    Write-Host '  CPU >=99 C: immediate hard handoff. GPU >=82 C: immediate qualification abort.'
    Write-Host '  EC must remain 50/50 with guards 00/00 and dual tach feedback during supervision.'
    Write-Host ''

    Write-Host 'Start a normal game/3D workload and reach active gameplay/rendering now.' -ForegroundColor Yellow
    Write-Host 'Do not start a synthetic stress test.' -ForegroundColor Yellow
    $confirm=Read-Host "When the workload is active, type exactly $token"

    if($confirm -cne $token){
        throw 'M8B cancelled before service start/failsafe/controller write boundary.'
    }

    Write-Host ''
    Write-Host 'Return to active gameplay/rendering NOW. There are 5 seconds to refocus the game before watchdog/controller startup.' -ForegroundColor Yellow
    Write-Host 'Do not leave the game minimized or paused; the representative GPU power gate remains unchanged.' -ForegroundColor Yellow
    Start-Sleep -Seconds 5

    Write-Host ''
    Write-Host 'Step 4: start and bind the exact LocalSystem M4 watchdog...' -ForegroundColor Cyan

    Start-Service -Name $serviceName
    $serviceStartedByHarness=$true

    $cim=Get-ServiceState
    if(-not $cim -or $cim.State -ne 'Running' -or [int]$cim.ProcessId -le 0){
        throw 'M8B watchdog service did not reach Running.'
    }

    $watchdogPid=[int]$cim.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks -ProcessId $watchdogPid
    $status=Wait-M4Ready -ExpectedPid $watchdogPid

    Write-Host ("Watchdog: PID={0} startTicks={1} account={2} target={3} pipe={4}" -f
        $watchdogPid,$watchdogStartTicks,$status.AccountName,$status.TargetProfileId,$status.PipeName)

    if(Test-Path $serviceLog){
        $logLineBoundary=@(Get-Content $serviceLog).Count
    }

    Write-Host ''
    Write-Host 'Step 5: ARM INDEPENDENT FAILSAFE BEFORE WRITE-CAPABLE CONTROLLER...' -ForegroundColor Yellow

    $failsafe=Start-M8BFailsafe
    Write-Host ("Failsafe PID={0} delay={1}s log={2}" -f
        $failsafe.Id,$FailsafeDelaySeconds,$failsafeLog)

    Write-Host ''
    Write-Host 'Step 6: launch M8B controller; it must prove load before its single 50/50 write...' -ForegroundColor Cyan

    $controller=Start-Process -FilePath 'dotnet' -ArgumentList @(
        $cli,
        '--8c40-m8b-watchdog-load',
        '--8c40-m8b-token',$token,
        '--8c40-m8b-ready-path',$readyPath,
        '--8c40-m8b-result-path',$resultPath,
        '--modules-dir',$modulesDir
    ) -PassThru -NoNewWindow

    $controllerPid=$controller.Id
    $controllerStartTicks=[long]$controller.StartTime.ToUniversalTime().Ticks

    $ready=Wait-ReadyMarker -ExpectedPid $controllerPid

    if([int]$ready.SchemaVersion -ne 1 -or
       $ready.Gate -cne 'M8B' -or
       $ready.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       [long]$ready.ProcessStartUtcTicks -ne $controllerStartTicks -or
       $ready.Authority -cne 'Custom' -or
       [int]$ready.CpuSetpoint -ne 50 -or
       [int]$ready.GpuSetpoint -ne 50 -or
       [int]$ready.CpuRpm -le 0 -or
       [int]$ready.GpuRpm -le 0 -or
       [int]$ready.MaxFan -ne 0 -or
       [int]$ready.FanSwitch -ne 0 -or
       [int]$ready.ApplyCalls -ne 1 -or
       $ready.Ack -cne 'backend-ec+tachs+watchdog-owned'){
        throw 'M8B READY marker does not prove exact healthy OWNED 50/50 with one ApplyAsync.'
    }

    if(-not (Test-Path $journalPath)){
        throw 'M8B READY exists but durable OWNED journal is missing.'
    }

    $journal=Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-M8BOwnedJournal -Journal $journal -ExpectedControllerPid $controllerPid -ExpectedControllerStartTicks $controllerStartTicks
    $journalGeneration=[int]$journal.Generation

    if((Get-ServiceState).ProcessId -ne $watchdogPid){
        throw 'M8B watchdog PID changed after controller reached OWNED.'
    }

    if((Get-ProcessStartTicks -ProcessId $watchdogPid) -ne $watchdogStartTicks){
        throw 'M8B watchdog process creation time changed after controller reached OWNED.'
    }

    $failsafe.Refresh()
    if($failsafe.HasExited){
        throw 'M8B failsafe exited before OWNED supervision completed.'
    }

    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        throw 'M8B independent failsafe fired before the normal completion boundary.'
    }

    Write-Host ("OWNED proof: controller PID={0} startTicks={1} journalGeneration={2} EC=50/50 RPM={3}/{4}" -f
        $controllerPid,$controllerStartTicks,$journalGeneration,$ready.CpuRpm,$ready.GpuRpm) -ForegroundColor Green

    Write-Host ''
    Write-Host 'Step 7: wait for bounded 30-s controller supervision + normal restore/release...' -ForegroundColor Cyan

    $controllerExit=Wait-ControllerExit -Seconds 50

    if(-not (Test-Path $resultPath)){
        throw 'M8B controller exited without durable result evidence.'
    }

    $result=Get-Content $resultPath -Raw | ConvertFrom-Json

    if($controllerExit -ne 0 -or [string]$result.Result -cne 'PASS'){
        throw "M8B controller failed. exit=$controllerExit result=$($result.Result) reason=$($result.FailureReason)"
    }

    if([int]$result.ApplyCalls -ne 1 -or
       -not [bool]$result.NormalRestoreCompleted -or
       -not [bool]$result.FinalFirmwareOwned -or
       [int]$result.Window.RepresentativeSamples -lt 22 -or
       [int]$result.Window.MaximumConsecutiveRepresentative -lt 10){
        throw 'M8B durable result does not prove one write, representative soak and verified normal restore.'
    }

    if(Test-Path $journalPath){
        Get-Content $journalPath
        throw 'M8B normal controller completion left durable journal evidence behind.'
    }

    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        throw 'M8B run is safe but invalid because the independent failsafe took over.'
    }

    Assert-CausalServiceLog -ControllerPid $controllerPid
    $causalChainPass=$true

    Write-Host ''
    Write-Host 'Step 8: independent final firmware/service closure...' -ForegroundColor Cyan

    if(Test-Path $journalPath){
        throw 'M8B final closure found a retained journal after normal Release.'
    }
    $finalJournalAbsent=$true

    Assert-StableFirmwareOwned -Context 'M8B final'
    $finalFirmwareProofPass=$true

    Stop-Service -Name $serviceName -Force
    $serviceStartedByHarness=$false

    $finalService=Get-Service -Name $serviceName -ErrorAction Stop
    if($finalService.StartType -ne 'Manual' -or $finalService.Status -ne 'Stopped'){
        throw "M8B final service baseline invalid: StartType=$($finalService.StartType) Status=$($finalService.Status)."
    }
    $finalServiceBaselinePass=$true
    Write-Host ("M8B final service baseline: StartType={0}, Status={1}" -f
        $finalService.StartType,$finalService.Status) -ForegroundColor Green

    $finalClosurePass=
        $finalJournalAbsent -and
        $finalFirmwareProofPass -and
        $finalServiceBaselinePass
    $pass=$true

    Write-Host ''
    Write-Host 'PASS: HP 8C40 M8B watchdog-backed representative-load 50/50 completed.' -ForegroundColor Green
    Write-Host 'Proven: load-before-write -> PREPARE -> one WRITE_INTENT/50/50/COMMIT -> OWNED journal + dual tachs -> 30-s load supervision without retransmit -> RESTORE_BEGIN -> local FF/FF -> RELEASE -> journal absent -> independent FF/FF -> M4 Manual/stopped.' -ForegroundColor Green
    Write-Host ("Evidence root: {0}" -f $evidenceRoot) -ForegroundColor Green
}
catch {
    $failure=$_.Exception.Message
    Write-Host ''
    Write-Host ("M8B FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally {
    if($failsafe){
        try {
            $failsafe.Refresh()
            if(-not $failsafe.HasExited){
                Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
                try {[void]$failsafe.WaitForExit(3000)}catch{}
            }
        }
        catch {}
    }

    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
    }

    if($controller){
        try {
            $controller.Refresh()
            if(-not $controller.HasExited){
                Write-Warning 'M8B cleanup is terminating the exact qualification controller; watchdog recovery must own any retained lease.'
                Stop-Process -Id $controller.Id -Force -ErrorAction SilentlyContinue
                try {[void]$controller.WaitForExit(5000)}catch{}
            }
        }
        catch {}
    }

    if(Test-Path $journalPath){
        $svc=Get-ServiceState

        if(-not $svc -or $svc.State -ne 'Running'){
            Write-Warning 'M8B cleanup: journal remains and watchdog is not running; starting qualified LocalSystem recovery service.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            $serviceStartedByHarness=$true
        }

        [void](Wait-JournalGone -Seconds 25)
    }

    $finalJournalAbsent=(-not (Test-Path $journalPath))

    if($finalJournalAbsent){
        try {
            Assert-StableFirmwareOwned -Context 'M8B cleanup'
            $finalFirmwareProofPass=$true
        }
        catch {
            $finalFirmwareProofPass=$false
            Write-Warning "M8B cleanup FF/FF proof failed: $($_.Exception.Message)"
        }

        try {
            $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if($svc -and $svc.Status -ne 'Stopped'){
                Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            }

            $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if($svc -and $svc.StartType -eq 'Manual' -and $svc.Status -eq 'Stopped'){
                $finalServiceBaselinePass=$true
                Write-Host ("M8B cleanup service baseline: StartType={0}, Status={1}" -f
                    $svc.StartType,$svc.Status) -ForegroundColor Green
            }
            else {
                $finalServiceBaselinePass=$false
                Write-Warning "M8B cleanup service baseline is not verified Manual/Stopped."
            }
        }
        catch {
            $finalServiceBaselinePass=$false
            Write-Warning "M8B cleanup service stop failed: $($_.Exception.Message)"
        }

        $finalClosurePass=
            $finalJournalAbsent -and
            $finalFirmwareProofPass -and
            $finalServiceBaselinePass
    }
    else {
        $finalClosurePass=$false
        Write-Host ''
        Write-Host 'CRITICAL: durable M8B ownership evidence remains and was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Do not run another fan-write gate until watchdog recovery/inspection closes this state.' -ForegroundColor Red
    }

    try {
        Write-HarnessSummary -Result $(if($pass){'PASS'}else{'FAIL_CLOSED'}) -Failure $failure
        Write-Host ("M8B harness summary: {0}" -f $summaryPath)
    }
    catch {
        Write-Warning "Could not write M8B harness summary: $($_.Exception.Message)"
    }

    if($controller){$controller.Dispose()}
    if($failsafe){$failsafe.Dispose()}
}

if(-not $pass){
    throw "M8B FAILED: $failure"
}
