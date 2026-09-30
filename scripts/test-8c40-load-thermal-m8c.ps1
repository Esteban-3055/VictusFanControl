param(
    [ValidateRange(90,300)]
    [int]$FailsafeDelaySeconds = 120
)

$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json

# HARD AUTHORIZATION BARRIER. This must remain before Administrator checks,
# build, SCM/service operations, process launch, PawnIO probes or fan control.
if(-not [bool]$profile.loadThermalM8Qualification.m8b.physicalPassed -or
   -not [bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized){
    throw 'M8C PHYSICAL BLOCKED: requires recorded M8B physical PASS plus explicit M8C physicalExecutionAuthorized=true.'
}

$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$stateDir=Join-Path $serviceRoot 'state'
$statusPath=Join-Path $stateDir 'm4-8c40.status.json'
$journalPath=Join-Path $stateDir 'lease.json'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-m8c-service-failsafe-8c40.ps1'
$packageScript=Join-Path $PSScriptRoot 'package-latest-m8c-evidence.ps1'
$token='8C40-M8C-THERMAL50'
. (Join-Path $PSScriptRoot 'm8c-tracked-child.ps1')

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m8c-thermal-preemption_{0}" -f $stamp)
$summaryPath=Join-Path $evidenceRoot 'm8c-harness-summary.json'

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)

    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M8C must be run from an elevated PowerShell.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ProcessStartTicks {
    param([int]$ProcessId)

    $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try { return [long]$process.StartTime.ToUniversalTime().Ticks }
    finally { $process.Dispose() }
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){
        throw "M8C read-only setpoint probe failed. Raw output: $output"
    }

    $line=($output -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1)

    if(-not $line){throw "M8C could not parse HP 8C40 setpoint probe. Raw output: $output"}

    $match=[regex]::Match($line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M8C could not parse setpoints from: $line"}

    [pscustomobject]@{
        Cpu=[int]$match.Groups[1].Value
        Gpu=[int]$match.Groups[2].Value
        Raw=$line
    }
}

function Assert-StableFirmwareOwned {
    param([string]$Context)

    $first=Read-8C40Setpoint
    Start-Sleep -Milliseconds 75
    $second=Read-8C40Setpoint

    Write-Host ("{0} EC 1/2: {1}" -f $Context,$first.Raw)
    Write-Host ("{0} EC 2/2: {1}" -f $Context,$second.Raw)

    if($first.Cpu -ne 255 -or $first.Gpu -ne 255 -or
       $second.Cpu -ne 255 -or $second.Gpu -ne 255){
        throw "$Context requires two consecutive independent FF/FF observations."
    }
}

function Assert-ServiceBaseline {
    $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if(-not $svc){
        & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')
        if($LASTEXITCODE -ne 0){throw "M4 installer failed with exit=$LASTEXITCODE."}
        $svc=Get-Service -Name $serviceName -ErrorAction Stop
    }

    $cim=Get-ServiceState
    if(-not $cim){throw 'M8C could not read the installed M4 service definition.'}

    if($svc.StartType -ne 'Manual' -or $svc.Status -ne 'Stopped'){
        throw "M8C requires M4 baseline Manual/Stopped; observed StartType=$($svc.StartType) Status=$($svc.Status)."
    }

    if($cim.StartName -notmatch 'LocalSystem|Local System'){
        throw "M8C requires LocalSystem watchdog; observed '$($cim.StartName)'."
    }

    if([string]$cim.PathName -notmatch 'VictusFanControl\.Watchdog\.exe' -or
       [string]$cim.PathName -notmatch '--m4-8c40-lease-service'){
        throw "M8C M4 service command line is not the isolated 8C40 lease service: $($cim.PathName)"
    }
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
            catch {}
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Timed out waiting for exact-target LocalSystem M4 Ready state.'
}

function Test-OwnedPhase {
    param($Phase)

    if($null -eq $Phase){return $false}
    if($Phase -is [string]){return ($Phase -ceq 'Owned' -or $Phase -ceq '2')}

    try { return ([int]$Phase -eq 2) }
    catch { return $false }
}

function Assert-M8COwnedJournal {
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
        throw 'M8C durable journal is not exact-target OWNED 50/50 bound to the exact controller identity.'
    }
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

function Start-M8CFailsafe {
    param([string]$LogPath)

    $process=Start-Process powershell.exe -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy','Bypass',
        '-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,
        '-LogPath',$LogPath
    ) -WindowStyle Hidden -PassThru

    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if($process.HasExited){throw 'M8C independent failsafe exited before controller launch.'}

    $armedDeadline=(Get-Date).AddSeconds(3)
    $armed=$false

    while((Get-Date) -lt $armedDeadline){
        if(Test-Path $LogPath){
            $armedText=Get-Content $LogPath -Raw -ErrorAction SilentlyContinue
            if($armedText -match 'M8C FAILSAFE ARMED:'){
                $armed=$true
                break
            }
        }

        $process.Refresh()
        if($process.HasExited){
            throw 'M8C independent failsafe exited before publishing its ARMED evidence.'
        }

        Start-Sleep -Milliseconds 50
    }

    if(-not $armed){
        throw 'M8C independent failsafe did not publish ARMED evidence before controller launch.'
    }

    return $process
}

function Test-FailsafeTakeover {
    param([string]$LogPath)

    if(-not (Test-Path $LogPath)){return $false}
    $text=Get-Content $LogPath -Raw

    return ($text -match 'M8C FAILSAFE TAKEOVER:' -or
            $text -match 'M8C FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M8C FAILSAFE SERVICE-START:' -or
            $text -match 'M8C FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M8C FAILSAFE STARTED:' -or
            $text -match 'M8C FAILSAFE RECOVERED:')
}

function Wait-ReadyMarker {
    param(
        [string]$Path,
        [int]$ExpectedPid,
        [string]$ExpectedCase,
        [System.Diagnostics.Process]$Controller,
        [string]$ResultPath
    )

    $deadline=(Get-Date).AddSeconds(35)

    while((Get-Date) -lt $deadline){
        if(Test-Path $Path){
            $ready=Get-Content $Path -Raw | ConvertFrom-Json

            if([int]$ready.ProcessId -ne $ExpectedPid){
                throw "M8C READY PID mismatch: marker=$($ready.ProcessId) expected=$ExpectedPid."
            }

            if([string]$ready.CaseName -cne $ExpectedCase){
                throw "M8C READY case mismatch: marker=$($ready.CaseName) expected=$ExpectedCase."
            }

            return $ready
        }

        $Controller.Refresh()
        if($Controller.HasExited){
            $exitCode='unavailable'
            try {
                $exitCode=[string](Wait-M8CTrackedChildExitCode -Process $Controller -Seconds 1)
            }
            catch {
                $exitCode=("unavailable ({0})" -f $_.Exception.Message)
            }

            $resultDetail='result evidence unavailable'
            if(Test-Path $ResultPath){
                try {
                    $early=Get-Content $ResultPath -Raw | ConvertFrom-Json
                    $resultDetail=("result={0}; reason={1}" -f
                        [string]$early.Result,
                        [string]$early.FailureReason)
                }
                catch {
                    $resultDetail=("result evidence unreadable: {0}" -f $_.Exception.Message)
                }
            }

            throw ("M8C controller exited before READY. ExitCode={0}; {1}" -f
                $exitCode,$resultDetail)
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Timed out waiting for M8C real OWNED 50/50 pre-injection READY marker.'
}

function Wait-ControllerExit {
    param(
        [System.Diagnostics.Process]$Controller,
        [int]$Seconds
    )

    return Wait-M8CTrackedChildExitCode -Process $Controller -Seconds $Seconds
}

function Assert-CausalServiceLog {
    param(
        [int]$ControllerPid,
        [int]$LineBoundary
    )

    if(-not (Test-Path $serviceLog)){throw "M8C service log is missing: $serviceLog"}

    $all=@(Get-Content $serviceLog)
    $new=@($all | Select-Object -Skip $LineBoundary)
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

    if($prepare.Count -ne 1 -or $intent.Count -ne 1 -or $commit.Count -ne 1 -or
       $restore.Count -ne 1 -or $release.Count -ne 1){
        throw ("M8C causal log counts invalid: PREPARE={0} WRITE_INTENT={1} COMMIT={2} RESTORE_BEGIN={3} RELEASE={4}" -f
            $prepare.Count,$intent.Count,$commit.Count,$restore.Count,$release.Count)
    }

    if(-not ($prepare[0] -lt $intent[0] -and
             $intent[0] -lt $commit[0] -and
             $commit[0] -lt $restore[0] -and
             $restore[0] -lt $release[0])){
        throw 'M8C watchdog ordering is not PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE.'
    }
}

function Run-M8CSubcycle {
    param(
        [ValidateSet('cpu','gpu')]
        [string]$Case
    )

    $caseName=if($Case -ceq 'cpu'){'CpuConfirmed95'}else{'GpuImmediate87'}
    $caseRoot=Join-Path $evidenceRoot $Case
    $readyPath=Join-Path $caseRoot 'm8c-ready.json'
    $continuePath=Join-Path $caseRoot 'm8c-continue.txt'
    $resultPath=Join-Path $caseRoot 'm8c-result.json'
    $failsafeLog=Join-Path $caseRoot 'm8c-failsafe.log'

    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null

    $controller=$null
    $failsafe=$null
    $serviceStarted=$false
    $controllerPid=0
    $controllerStartTicks=0L
    $watchdogPid=0
    $watchdogStartTicks=0L
    $failsafeTakeover=$false
    $passed=$false
    $failure=$null

    try {
        if(Test-Path $journalPath){
            throw "M8C $Case refuses while a durable lease journal exists."
        }

        Assert-ServiceBaseline
        Assert-StableFirmwareOwned -Context "M8C $Case baseline"

        Start-Service -Name $serviceName
        $serviceStarted=$true

        $cim=Get-ServiceState
        if(-not $cim -or $cim.State -ne 'Running' -or [int]$cim.ProcessId -le 0){
            throw "M8C $Case watchdog service did not reach Running."
        }

        $watchdogPid=[int]$cim.ProcessId
        $watchdogStartTicks=Get-ProcessStartTicks -ProcessId $watchdogPid
        [void](Wait-M4Ready -ExpectedPid $watchdogPid)

        $logBoundary=if(Test-Path $serviceLog){@(Get-Content $serviceLog).Count}else{0}

        $failsafe=Start-M8CFailsafe -LogPath $failsafeLog

        $dotnetExecutable=(Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source

        $controller=Start-M8CTrackedChild -Executable $dotnetExecutable -Arguments @(
            $cli,
            '--8c40-m8c-physical-thermal',
            '--8c40-m8c-physical-token',$token,
            '--8c40-m8c-physical-case',$Case,
            '--8c40-m8c-physical-ready-path',$readyPath,
            '--8c40-m8c-physical-continue-path',$continuePath,
            '--8c40-m8c-physical-result-path',$resultPath,
            '--modules-dir',$modulesDir
        ) -WorkingDirectory $repoRoot

        $controllerPid=$controller.Id
        $controllerStartTicks=[long]$controller.StartTime.ToUniversalTime().Ticks

        $ready=Wait-ReadyMarker -Path $readyPath -ExpectedPid $controllerPid -ExpectedCase $caseName -Controller $controller -ResultPath $resultPath

        if([int]$ready.SchemaVersion -ne 1 -or
           $ready.Gate -cne 'M8C' -or
           $ready.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
           [long]$ready.ProcessStartUtcTicks -ne $controllerStartTicks -or
           $ready.Authority -cne 'Custom' -or
           [int]$ready.CpuSetpoint -ne 50 -or
           [int]$ready.GpuSetpoint -ne 50 -or
           [int]$ready.CpuRpm -le 0 -or
           [int]$ready.GpuRpm -le 0 -or
           [int]$ready.ApplyCalls -ne 1 -or
           $ready.Ack -cne 'real-backend-ec+tachs+watchdog-owned;synthetic-not-yet-injected'){
            throw "M8C $Case READY evidence is incomplete."
        }

        if(-not (Test-Path $journalPath)){throw "M8C $Case READY exists but OWNED journal is missing."}

        $journal=Get-Content $journalPath -Raw | ConvertFrom-Json
        Assert-M8COwnedJournal -Journal $journal -ExpectedControllerPid $controllerPid -ExpectedControllerStartTicks $controllerStartTicks

        if((Get-ServiceState).ProcessId -ne $watchdogPid){
            throw "M8C $Case watchdog PID changed before injection."
        }

        if((Get-ProcessStartTicks -ProcessId $watchdogPid) -ne $watchdogStartTicks){
            throw "M8C $Case watchdog creation time changed before injection."
        }

        $failsafe.Refresh()
        if($failsafe.HasExited){throw "M8C $Case failsafe exited before injection."}

        if(Test-FailsafeTakeover -LogPath $failsafeLog){
            $failsafeTakeover=$true
            throw "M8C $Case failsafe took over before parent continue."
        }

        'M8C-CONTINUE' | Set-Content -Path $continuePath -Encoding ASCII

        $exit=Wait-ControllerExit -Controller $controller -Seconds 45

        if(-not (Test-Path $resultPath)){throw "M8C $Case controller produced no durable result."}

        $result=Get-Content $resultPath -Raw | ConvertFrom-Json

        if($exit -ne 0 -or [string]$result.Result -cne 'PASS_CONTROLLER_LOCAL_CLOSURE'){
            throw "M8C $Case controller failed. exit=$exit result=$($result.Result) reason=$($result.FailureReason)"
        }

        if([int]$result.ApplyCalls -ne 1 -or
           -not [bool]$result.ReadyPublished -or
           -not [bool]$result.ContinueObserved -or
           -not [bool]$result.NormalThermalHandoffCompleted -or
           -not [bool]$result.FinalFirmwareOwned -or
           $result.SyntheticEvidenceMarker -cne 'M8C_SYNTHETIC_QUALIFICATION_ONLY'){
            throw "M8C $Case durable result does not prove the required real/synthetic/local-restore chain."
        }

        if(Test-Path $journalPath){
            Get-Content $journalPath
            throw "M8C $Case local completion left a durable journal."
        }

        if(Test-FailsafeTakeover -LogPath $failsafeLog){
            $failsafeTakeover=$true
            throw "M8C $Case is safety-recovered but invalid because the delayed failsafe took over."
        }

        Assert-CausalServiceLog -ControllerPid $controllerPid -LineBoundary $logBoundary
        Assert-StableFirmwareOwned -Context "M8C $Case final"

        Stop-Service -Name $serviceName -Force
        $serviceStarted=$false

        $svc=Get-Service -Name $serviceName -ErrorAction Stop
        if($svc.StartType -ne 'Manual' -or $svc.Status -ne 'Stopped'){
            throw "M8C $Case final service baseline is not Manual/Stopped."
        }

        $passed=$true

        return [pscustomobject]@{
            case=$Case
            result='PASS'
            controllerPid=$controllerPid
            controllerStartUtcTicks=$controllerStartTicks
            watchdogPid=$watchdogPid
            watchdogStartUtcTicks=$watchdogStartTicks
            failsafePid=$(if($failsafe){$failsafe.Id}else{0})
            failsafeLogPresent=(Test-Path $failsafeLog)
            failsafeTakeover=$failsafeTakeover
            readyPath=$readyPath
            resultPath=$resultPath
            failsafeLog=$failsafeLog
        }
    }
    catch {
        $failure=$_.Exception.Message
        throw
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

        if($controller){
            try {
                $controller.Refresh()
                if(-not $controller.HasExited){
                    Stop-Process -Id $controller.Id -Force -ErrorAction SilentlyContinue
                    try {[void]$controller.WaitForExit(5000)}catch{}
                }
            }
            catch {}
        }

        if(Test-Path $journalPath){
            $svcState=Get-ServiceState
            if(-not $svcState -or $svcState.State -ne 'Running'){
                Start-Service -Name $serviceName -ErrorAction SilentlyContinue
                $serviceStarted=$true
            }

            [void](Wait-JournalGone -Seconds 25)
        }

        if(-not (Test-Path $journalPath)){
            try { Assert-StableFirmwareOwned -Context "M8C $Case cleanup" }
            catch { Write-Warning "M8C $Case cleanup FF/FF proof failed: $($_.Exception.Message)" }

            try {
                $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if($svc -and $svc.Status -ne 'Stopped'){
                    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
                }
            }
            catch {}
        }
        else {
            Write-Host "CRITICAL: M8C $Case durable journal remains: $journalPath" -ForegroundColor Red
        }

        if($controller){$controller.Dispose()}
        if($failsafe){$failsafe.Dispose()}

        if(-not $passed -and $failure){
            Write-Warning "M8C $Case subcycle failed closed: $failure"
        }
    }
}

Assert-Administrator
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 M8C PHYSICAL THERMAL PREEMPTION' -ForegroundColor Cyan
Write-Host 'This gate is explicitly authorized only for the bounded M8C qualification path recorded in the profile.' -ForegroundColor Yellow
Write-Host 'It performs two independent subcycles: CPU synthetic 95 C x5 and GPU synthetic 87 C x1.' -ForegroundColor Yellow
Write-Host 'Real silicon is not intentionally heated to either production threshold.' -ForegroundColor Yellow

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){
        throw "M8C refused while '$name' is running."
    }
}

& (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8-preflight.ps1')
if($LASTEXITCODE -ne 0){throw "M8 preflight failed with exit=$LASTEXITCODE."}

& (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8c-invariants.ps1')
if($LASTEXITCODE -ne 0){throw "M8C synthetic invariant failed with exit=$LASTEXITCODE."}

& (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8c-physical-preparation-invariants.ps1')
if($LASTEXITCODE -ne 0){throw "M8C physical preparation invariant failed with exit=$LASTEXITCODE."}

Assert-ServiceBaseline
Assert-StableFirmwareOwned -Context 'M8C initial baseline'

if(Test-Path $journalPath){throw 'M8C refuses while durable lease evidence exists.'}

$confirm=Read-Host "With a normal representative game/3D workload active, type exactly $token"
if($confirm -cne $token){throw 'M8C cancelled before any service or controller write boundary.'}

$subcycles=@()
$failure=$null
$pass=$false

try {
    $subcycles+=@(Run-M8CSubcycle -Case 'cpu')
    $subcycles+=@(Run-M8CSubcycle -Case 'gpu')

    $pass=$true
}
catch {
    $failure=$_.Exception.Message
}
finally {
    $summary=[ordered]@{
        schemaVersion=1
        gate='M8C-HARNESS'
        result=$(if($pass){'PASS'}else{'FAIL_CLOSED'})
        failure=$failure
        evidenceHead=((& git rev-parse HEAD 2>$null) | Select-Object -First 1)
        timestamp=(Get-Date).ToUniversalTime().ToString('O')
        physicalExecutionAuthorized=[bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized
        m8bPhysicalPassed=[bool]$profile.loadThermalM8Qualification.m8b.physicalPassed
        failsafeDelaySeconds=$FailsafeDelaySeconds
        subcycles=$subcycles
        finalJournalAbsent=(-not (Test-Path $journalPath))
    }

    $summary | ConvertTo-Json -Depth 10 | Set-Content -Path $summaryPath -Encoding UTF8

    try {
        $packOutput=@(& $packageScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot)
        $zipPath=($packOutput | Select-Object -Last 1)
        Write-Host ("M8C automatic evidence package: {0}" -f $zipPath) -ForegroundColor Green
    }
    catch {
        Write-Warning ("M8C evidence auto-packaging failed without masking the qualification result: {0}" -f $_.Exception.Message)
    }
}

if(-not $pass){throw "M8C FAILED: $failure"}

Write-Host 'PASS: M8C CPU and GPU physical preemption subcycles completed.' -ForegroundColor Green
Write-Host ("Evidence root: {0}" -f $evidenceRoot) -ForegroundColor Green
