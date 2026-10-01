param(
    [ValidateRange(90,300)]
    [int]$FailsafeDelaySeconds=120
)

$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER. Keep before Administrator checks,
# service mutation, process launch, PawnIO/EC probing or evidence-side recovery.
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed){
    throw 'M9C PHYSICAL BLOCKED: M9B read-only physical PASS is not recorded.'
}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalAuthorization.authorized -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized){
    throw 'M9C PHYSICAL BLOCKED: explicit physical/controller/construction authorization is incomplete.'
}
if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
   [bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized -or
   [bool]$profile.control.enabledByDefault -or
   [bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled){
    throw 'M9C refuses if production watchdog/default/automatic policy has already been promoted.'
}

$expectedBranch='feature/victus-8c40-m9-final-prehardware'
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$stateDir=Join-Path $serviceRoot 'state'
$statusPath=Join-Path $stateDir 'm4-8c40.status.json'
$journalPath=Join-Path $stateDir 'lease.json'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'

$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-m9c-service-failsafe-8c40.ps1'
$packagingScript=Join-Path $PSScriptRoot 'package-m9c-evidence.ps1'
$token='8C40-M9C-PRODUCTION30'
. (Join-Path $PSScriptRoot 'm9c-tracked-child.ps1')

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9c-production-smoke_{0}" -f $stamp)
$readyPath=Join-Path $evidenceRoot 'm9c-ready.json'
$continuePath=Join-Path $evidenceRoot 'm9c-continue.txt'
$resultPath=Join-Path $evidenceRoot 'm9c-result.json'
$summaryPath=Join-Path $evidenceRoot 'm9c-harness-summary.json'
$failsafeLog=Join-Path $evidenceRoot 'm9c-failsafe.log'
$baselineFfPath=Join-Path $evidenceRoot 'm9c-baseline-ff.json'
$finalFfPath=Join-Path $evidenceRoot 'm9c-final-ff.json'
$cleanupFfPath=Join-Path $evidenceRoot 'm9c-cleanup-ff.json'
$serviceSegmentPath=Join-Path $evidenceRoot 'm9c-watchdog-log-segment.txt'
$retainedBeforeRecoveryPath=Join-Path $evidenceRoot 'm9c-retained-lease-before-recovery.json'

$controller=$null
$failsafe=$null
$pass=$false
$failure=$null
$head=$null
$serviceStartedByHarness=$false
$watchdogPid=0
$watchdogStartTicks=0L
$controllerPid=0
$controllerStartTicks=0L
$journalGeneration=$null
$logLineBoundary=0
$failsafeTakeover=$false
$failsafePid=0
$causalChainPass=$false
$finalClosurePass=$false
$finalJournalAbsent=$false
$finalFirmwareProofPass=$false
$cleanupFirmwareProofPass=$false
$finalServiceBaselinePass=$false
$packagePath=$null
$packageSha256=$null

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M9C must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch){
        throw "M9C requires branch '$expectedBranch'; observed '$branch'."
    }

    $localHead=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $localHead -notmatch '^[0-9a-f]{40}$'){
        throw "M9C could not resolve a valid HEAD. Raw='$localHead'"
    }

    $upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)){
        throw 'M9C requires a configured tracked upstream.'
    }

    $upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $localHead -cne $upstreamHead){
        throw "M9C requires local HEAD == upstream HEAD. local=$localHead upstream=$upstreamHead"
    }

    $authorizationSourceHead=[string]$profile.watchdogM9ProductionIntegration.m9c.physicalAuthorization.sourceHead
    $parentHead=(& git rev-parse HEAD^ 2>&1 | Out-String).Trim()
    if($authorizationSourceHead -notmatch '^[0-9a-f]{40}(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'M9C could not inspect repository status.'}
    $blocking=@(
        $statusText -split "[\r\n]+" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
        }
    )
    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'M9C requires committed source/config state; only untracked logs/ evidence is allowed.'
    }

    return $localHead
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop

    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    if(([string]$board.Manufacturer).Trim() -cne 'HP' -or
       ([string]$board.Product).Trim() -cne '8C40' -or
       ([string]$board.Version).Trim() -cne '63.43' -or
       ([string]$system.Manufacturer).Trim() -cne 'HP' -or
       ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M9C exact-target fingerprint mismatch.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ProcessStartTicks([int]$ProcessId){
    $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try{return [long]$process.StartTime.ToUniversalTime().Ticks}
    finally{$process.Dispose()}
}

function Assert-ServiceBaseline {
    $svc=Get-ServiceState
    if(-not $svc){throw 'M9C requires the already-qualified M4 service to be installed; harness will not install it.'}
    if([string]$svc.State -cne 'Stopped' -or
       [string]$svc.StartMode -cne 'Manual' -or
       [int]$svc.ProcessId -ne 0){
        throw "M9C requires M4 Manual/Stopped/PID0; observed $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)."
    }
    if([string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw "M9C requires LocalSystem; observed '$($svc.StartName)'."
    }
    foreach($required in @(
        $serviceExe,
        '--service-name VictusFanControlWatchdogM4',
        '--m4-8c40-lease-service'
    )){
        if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){
            throw "M9C M4 service configuration mismatch; missing '$required' in '$($svc.PathName)'."
        }
    }
    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "M9C service executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "M9C service module missing: $serviceModule"}
    return $svc
}

function Get-PowerSnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $status=[System.Windows.Forms.SystemInformation]::PowerStatus
    $percent=$null
    if($status.BatteryLifePercent -ge 0){$percent=[math]::Round([double]$status.BatteryLifePercent*100,0)}
    return [pscustomobject]@{
        PowerLineStatus=[string]$status.PowerLineStatus
        BatteryPercent=$percent
    }
}

function Assert-PowerSane {
    $power=Get-PowerSnapshot
    if($power.PowerLineStatus -cne 'Online'){throw "M9C requires AC online; observed '$($power.PowerLineStatus)'."}
    if($null -eq $power.BatteryPercent -or [double]$power.BatteryPercent -lt 20){
        throw "M9C requires readable battery >=20%; observed '$($power.BatteryPercent)'."
    }
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9C setpoint probe failed. Raw: $output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $match=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M9C could not parse setpoint probe. Raw: $output"}
    return [pscustomobject]@{
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        cpu=[int]$match.Groups[1].Value
        gpu=[int]$match.Groups[2].Value
        raw=[string]$line
    }
}

function Assert-StableFirmwareOwned([string]$Context,[string]$EvidencePath){
    $samples=@()
    $consecutive=0

    for($read=1;$read -le 6;$read++){
        $sample=Read-8C40Setpoint
        $samples+=@($sample)
        Write-Host ("{0} FF proof {1}/6: {2}" -f $Context,$read,$sample.raw)

        if($sample.cpu -eq 255 -and $sample.gpu -eq 255){
            $consecutive++
            if($consecutive -ge 2){
                [ordered]@{
                    context=$Context
                    passed=$true
                    requiredConsecutive=2
                    maximumReads=6
                    samples=$samples
                } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
                return
            }
        }
        else {$consecutive=0}

        if($read -lt 6){Start-Sleep -Milliseconds 75}
    }

    [ordered]@{
        context=$Context
        passed=$false
        requiredConsecutive=2
        maximumReads=6
        samples=$samples
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8

    throw "$Context requires two consecutive independent FF/FF observations within six reads."
}

function Wait-M4Ready([int]$ExpectedPid){
    $deadline=(Get-Date).AddSeconds(20)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try {
                $status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if([int]$status.ProcessId -eq $ExpectedPid -and
                   $status.Ready -and -not $status.Blocked -and
                   [int]$status.SessionId -eq 0 -and
                   $status.AccountName -match 'SYSTEM$' -and
                   $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   $status.RecoveryDisposition -ceq 'Ready'){
                    return $status
                }
            } catch {}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9C timed out waiting for exact M4 Ready state.'
}

function Test-OwnedPhase($Phase){
    if($null -eq $Phase){return $false}
    if($Phase -is [string]){return ($Phase -ceq 'Owned' -or $Phase -ceq '2')}
    try{return ([int]$Phase -eq 2)}catch{return $false}
}

function Assert-OwnedJournal($Journal,[int]$ExpectedPid,[long]$ExpectedStartTicks){
    if([int]$Journal.SchemaVersion -ne 2 -or
       $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Test-OwnedPhase $Journal.Phase) -or
       [int]$Journal.Generation -ne 3 -or
       [int]$Journal.Controller.ProcessId -ne $ExpectedPid -or
       [long]$Journal.Controller.ProcessStartUtcTicks -ne $ExpectedStartTicks -or
       [int]$Journal.Owned.Cpu -ne 30 -or
       [int]$Journal.Owned.Gpu -ne 30){
        throw 'M9C durable journal is not schema-v2 generation-3 OWNED 30/30 bound to exact controller PID+creation ticks.'
    }
}

function Start-M9CFailsafe {
    if(-not (Test-Path -LiteralPath $failsafeScript -PathType Leaf)){throw "M9C failsafe missing: $failsafeScript"}

    $process=Start-Process powershell.exe -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass',
        '-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,
        '-LogPath',$failsafeLog
    ) -WindowStyle Hidden -PassThru

    $deadline=(Get-Date).AddSeconds(3)
    while((Get-Date) -lt $deadline){
        $process.Refresh()
        if($process.HasExited){throw 'M9C independent failsafe exited before ARMED proof.'}

        if(Test-Path -LiteralPath $failsafeLog){
            $text=Get-Content -LiteralPath $failsafeLog -Raw -ErrorAction SilentlyContinue
            if($text -match 'M9C FAILSAFE ARMED:'){return $process}
        }
        Start-Sleep -Milliseconds 50
    }

    throw 'M9C independent failsafe did not publish ARMED proof before controller launch.'
}

function Test-FailsafeTakeover {
    if(-not (Test-Path -LiteralPath $failsafeLog)){return $false}
    $text=Get-Content -LiteralPath $failsafeLog -Raw
    return ($text -match 'M9C FAILSAFE TAKEOVER:' -or
            $text -match 'M9C FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M9C FAILSAFE SERVICE-START:' -or
            $text -match 'M9C FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M9C FAILSAFE STARTED:' -or
            $text -match 'M9C FAILSAFE RECOVERED:')
}

function Wait-ReadyMarker([int]$ExpectedPid){
    $deadline=(Get-Date).AddSeconds(45)

    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $readyPath){
            $ready=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
            if([int]$ready.processId -ne $ExpectedPid){
                throw "M9C READY PID mismatch: marker=$($ready.processId), expected=$ExpectedPid."
            }
            return $ready
        }

        if($controller -and $controller.HasExited){
            $controller.Refresh()
            $exit='unavailable'
            try{$exit=[string]$controller.ExitCode}catch{}
            $detail='result unavailable'
            if(Test-Path -LiteralPath $resultPath){
                try {
                    $early=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                    $detail=("result={0}; reason={1}" -f $early.result,$early.failureReason)
                } catch {}
            }
            throw "M9C controller exited before READY. exit=$exit; $detail"
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'M9C timed out waiting for OWNED 30/30 READY marker.'
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function Assert-CausalServiceLog([int]$ControllerPid){
    if(-not (Test-Path -LiteralPath $serviceLog)){throw "M9C service log missing: $serviceLog"}

    $all=@(Get-Content -LiteralPath $serviceLog)
    $segment=@($all | Select-Object -Skip $logLineBoundary)
    $segment | Set-Content -LiteralPath $serviceSegmentPath -Encoding UTF8

    $tag="controller PID=$ControllerPid"
    $prepare=@();$intent=@();$commit=@();$restore=@();$release=@()

    for($i=0;$i -lt $segment.Count;$i++){
        $line=[string]$segment[$i]
        if($line -match 'WATCHDOG PREPARE ACK' -and $line.Contains($tag)){$prepare+=@($i)}
        if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line.Contains($tag) -and $line -match 'target=30/30'){$intent+=@($i)}
        if($line -match 'WATCHDOG COMMIT ACK' -and $line.Contains($tag) -and $line -match 'target=30/30'){$commit+=@($i)}
        if($line -match 'WATCHDOG RESTORE_BEGIN ACK' -and $line.Contains($tag)){$restore+=@($i)}
        if($line -match 'WATCHDOG RELEASE ACK' -and $line.Contains($tag)){$release+=@($i)}
    }

    if($prepare.Count -ne 1 -or $intent.Count -ne 1 -or $commit.Count -ne 1 -or
       $restore.Count -ne 1 -or $release.Count -ne 1){
        throw ("M9C causal log counts invalid: PREPARE={0} INTENT={1} COMMIT={2} RESTORE={3} RELEASE={4}" -f
            $prepare.Count,$intent.Count,$commit.Count,$restore.Count,$release.Count)
    }

    if(-not ($prepare[0] -lt $intent[0] -and $intent[0] -lt $commit[0] -and
             $commit[0] -lt $restore[0] -and $restore[0] -lt $release[0])){
        throw 'M9C causal ordering is not PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE.'
    }
}

function Write-HarnessSummary([string]$Result,[string]$Failure){
    [ordered]@{
        schemaVersion=1
        gate='M9C-HARNESS'
        result=$Result
        failure=$Failure
        evidenceHead=$head
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        watchdogPid=$watchdogPid
        watchdogStartUtcTicks=$watchdogStartTicks
        controllerPid=$controllerPid
        controllerStartUtcTicks=$controllerStartTicks
        journalGeneration=$journalGeneration
        failsafeDelaySeconds=$FailsafeDelaySeconds
        failsafePid=$failsafePid
        failsafeTakeover=$failsafeTakeover
        causalChainPass=$causalChainPass
        finalClosurePass=$finalClosurePass
        finalJournalAbsent=$finalJournalAbsent
        finalFirmwareProofPass=$finalFirmwareProofPass
        cleanupFirmwareProofPass=$cleanupFirmwareProofPass
        finalServiceBaselinePass=$finalServiceBaselinePass
        productionConstructionAuthorized=$false
        watchdogRecoveryValidated=$false
        automaticPolicyEnabled=$false
        controlEnabledByDefault=$false
        readyPath=$readyPath
        resultPath=$resultPath
        packagePath=$packagePath
        packageSha256=$packageSha256
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9C refused while '$name' is running."}
}

$head=Assert-RepositoryProvenance
Assert-ExactTarget
Assert-PowerSane
Assert-ServiceBaseline | Out-Null

if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9C refuses retained watchdog journal evidence; it was not deleted.'
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 M9C PRODUCTION-PATH WATCHDOG SMOKE' -ForegroundColor Cyan
Write-Host 'WRITE-CAPABLE ONLY AFTER THE VERSIONED M9C AUTHORIZATION BARRIER.' -ForegroundColor Yellow
Write-Host 'Exactly one equal-only 30/30 ApplyAsync is permitted.' -ForegroundColor Yellow
Write-Host ''

try {
    Write-Host 'Step 1: same-HEAD build/static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "M9C build failed with exit=$LASTEXITCODE."}

    foreach($script in @(
        'test-8c40-m9-production-watchdog-invariants.ps1',
        'test-8c40-m9b-readonly-preflight-invariants.ps1',
        'test-8c40-m9c-production-smoke-invariants.ps1',
        'test-8c40-m9c-harness-invariants.ps1',
        'test-8c40-m9c-tracked-child-selftest.ps1',
        'test-8c40-m9c-evidence-packaging.ps1'
    )){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE -ne 0){throw "M9C regression '$script' failed with exit=$LASTEXITCODE."}
    }

    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'M9C SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'M9C coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'M9C HP backend/gate self-test failed.'}

    Write-Host 'Step 2: stable firmware/service baseline...' -ForegroundColor Cyan
    Assert-StableFirmwareOwned 'M9C baseline' $baselineFfPath
    Assert-ServiceBaseline | Out-Null
    Assert-PowerSane

    Write-Host ''
    $confirm=Read-Host "Type exactly $token to authorize this one versioned 30/30 physical smoke"
    if($confirm -cne $token){throw 'M9C cancelled before service/failsafe/controller active boundary.'}

    Write-Host 'Step 3: start exact LocalSystem M4 watchdog...' -ForegroundColor Cyan
    Start-Service -Name $serviceName
    $serviceStartedByHarness=$true

    $svc=Get-ServiceState
    if(-not $svc -or $svc.State -ne 'Running' -or [int]$svc.ProcessId -le 0){
        throw 'M9C watchdog service did not reach Running.'
    }

    $watchdogPid=[int]$svc.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
    $status=Wait-M4Ready $watchdogPid

    if(Test-Path -LiteralPath $serviceLog){$logLineBoundary=@(Get-Content -LiteralPath $serviceLog).Count}

    Write-Host ("Watchdog PID={0} startTicks={1} target={2}" -f
        $watchdogPid,$watchdogStartTicks,$status.TargetProfileId)

    Write-Host 'Step 4: arm independent delayed failsafe BEFORE controller...' -ForegroundColor Cyan
    $failsafe=Start-M9CFailsafe
    $failsafePid=$failsafe.Id

    Write-Host 'Step 5: launch native tracked M9C controller...' -ForegroundColor Cyan
    $dotnetExecutable=(Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $controller=Start-M9CTrackedChild -Executable $dotnetExecutable -Arguments @(
        $cli,
        '--8c40-m9c-production-smoke',
        '--8c40-m9c-token',$token,
        '--8c40-m9c-ready-path',$readyPath,
        '--8c40-m9c-continue-path',$continuePath,
        '--8c40-m9c-result-path',$resultPath,
        '--modules-dir',$modulesDir
    ) -WorkingDirectory $repoRoot

    $controllerPid=$controller.Id
    $controllerStartTicks=[long]$controller.StartTime.ToUniversalTime().Ticks
    $ready=Wait-ReadyMarker $controllerPid

    if([int]$ready.schemaVersion -ne 1 -or
       $ready.gate -cne 'M9C' -or
       $ready.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       [long]$ready.processStartUtcTicks -ne $controllerStartTicks -or
       $ready.authority -cne 'Custom' -or
       [int]$ready.cpuSetpoint -ne 30 -or [int]$ready.gpuSetpoint -ne 30 -or
       [int]$ready.cpuRpm -le 0 -or [int]$ready.gpuRpm -le 0 -or
       [int]$ready.applyCalls -ne 1 -or
       -not [bool]$ready.constructionScopeClosedBeforeCustom -or
       ([string]$ready.constructionRoute).IndexOf('HpFanControlBackendFactory.Create',[StringComparison]::Ordinal) -lt 0){
        throw 'M9C READY marker does not prove exact normal factory/public-backend OWNED 30/30 with one ApplyAsync.'
    }

    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9C READY exists but durable OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $controllerPid $controllerStartTicks
    $journalGeneration=[int]$journal.Generation

    $svc=Get-ServiceState
    if([int]$svc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'M9C watchdog process identity changed after controller reached OWNED.'
    }

    $failsafe.Refresh()
    if($failsafe.HasExited){throw 'M9C failsafe exited before parent OWNED proof.'}
    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        throw 'M9C independent failsafe fired before parent continuation.'
    }

    Write-Host ("M9C OWNED proof: controller PID={0} ticks={1}; journal generation={2}; RPM={3}/{4}" -f
        $controllerPid,$controllerStartTicks,$journalGeneration,$ready.cpuRpm,$ready.gpuRpm) -ForegroundColor Green

    'M9C-CONTINUE' | Set-Content -LiteralPath $continuePath -Encoding ASCII

    Write-Host 'Step 6: five-frame supervision + normal restore/release...' -ForegroundColor Cyan
    $controllerExit=Wait-M9CTrackedChildExitCode -Process $controller -Seconds 30

    if(-not (Test-Path -LiteralPath $resultPath)){throw 'M9C controller exited without durable result evidence.'}
    $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json

    if($controllerExit -ne 0 -or [string]$result.result -cne 'PASS_CONTROLLER_LOCAL_CLOSURE'){
        throw "M9C controller failed. exit=$controllerExit result=$($result.result) reason=$($result.failureReason)"
    }

    if([int]$result.applyCalls -ne 1 -or
       -not [bool]$result.readyPublished -or
       -not [bool]$result.continueObserved -or
       -not [bool]$result.constructionScopeClosedBeforeCustom -or
       -not [bool]$result.normalRestoreCompleted -or
       -not [bool]$result.finalFirmwareOwned -or
       -not [bool]$result.physicalExecutionAuthorized -or
       -not [bool]$result.qualificationConstructionAuthorized -or
       [bool]$result.productionConstructionAuthorized -or
       [bool]$result.watchdogRecoveryValidated -or
       @($result.supervisionSamples).Count -ne 5){
        throw 'M9C result does not prove the bounded one-write production-path transaction and closed production promotion.'
    }

    if(Test-Path -LiteralPath $journalPath){
        Copy-Item -LiteralPath $journalPath -Destination $retainedBeforeRecoveryPath
        throw 'M9C normal completion left durable journal evidence.'
    }

    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        throw 'M9C is safe but invalid because the independent failsafe took over.'
    }

    Assert-CausalServiceLog $controllerPid
    $causalChainPass=$true

    Write-Host 'Step 7: independent final firmware/service closure...' -ForegroundColor Cyan
    $finalJournalAbsent=$true
    Assert-StableFirmwareOwned 'M9C final' $finalFfPath
    $finalFirmwareProofPass=$true

    Stop-Service -Name $serviceName -Force
    $serviceStartedByHarness=$false

    $finalSvc=Get-ServiceState
    if(-not $finalSvc -or
       $finalSvc.State -ne 'Stopped' -or
       $finalSvc.StartMode -ne 'Manual' -or
       [int]$finalSvc.ProcessId -ne 0 -or
       [string]$finalSvc.StartName -notmatch 'LocalSystem|Local System'){
        throw 'M9C final M4 service baseline is not Manual/Stopped/PID0/LocalSystem.'
    }

    $finalServiceBaselinePass=$true
    $finalClosurePass=$true
    $pass=$true

    Write-Host 'PASS: M9C controller and parent causal closure completed.' -ForegroundColor Green
}
catch {
    $failure=$_.Exception.Message
    Write-Host ("M9C FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally {
    if($controller){
        try {
            $controller.Refresh()
            if(-not $controller.HasExited){
                Write-Warning 'M9C cleanup is terminating the exact tracked controller; watchdog recovery owns any retained lease.'
                $controller.Kill()
                try{[void]$controller.WaitForExit(5000)}catch{}
            }
        } catch {}
    }

    if(Test-Path -LiteralPath $journalPath){
        try {
            if(-not (Test-Path -LiteralPath $retainedBeforeRecoveryPath)){
                Copy-Item -LiteralPath $journalPath -Destination $retainedBeforeRecoveryPath
            }
        } catch {}

        $svc=Get-ServiceState
        if(-not $svc -or $svc.State -ne 'Running'){
            Write-Warning 'M9C cleanup: retained journal exists; starting qualified recovery service without deleting evidence.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            $serviceStartedByHarness=$true
        }

        [void](Wait-JournalGone 25)
    }

    $finalJournalAbsent=(-not (Test-Path -LiteralPath $journalPath))

    if($finalJournalAbsent){
        try {
            Assert-StableFirmwareOwned 'M9C cleanup' $cleanupFfPath
            $cleanupFirmwareProofPass=$true
            if(-not $finalFirmwareProofPass){$finalFirmwareProofPass=$true}
        }
        catch {
            $cleanupFirmwareProofPass=$false
            Write-Warning "M9C cleanup FF/FF proof failed: $($_.Exception.Message)"
        }

        try {
            $svc=Get-ServiceState
            if($svc -and $svc.State -ne 'Stopped'){
                Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            }

            $svc=Get-ServiceState
            if($svc -and $svc.State -eq 'Stopped' -and
               $svc.StartMode -eq 'Manual' -and [int]$svc.ProcessId -eq 0 -and
               [string]$svc.StartName -match 'LocalSystem|Local System'){
                $finalServiceBaselinePass=$true
            }
            else {$finalServiceBaselinePass=$false}
        }
        catch {
            $finalServiceBaselinePass=$false
            Write-Warning "M9C cleanup service baseline failed: $($_.Exception.Message)"
        }

        if($failsafe){
            try {
                $failsafe.Refresh()
                if(-not $failsafe.HasExited){
                    Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
                    try{[void]$failsafe.WaitForExit(3000)}catch{}
                }
            } catch {}
        }
    }
    else {
        $pass=$false
        $finalClosurePass=$false
        Write-Host 'CRITICAL: M9C durable ownership evidence remains and was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Qualified recovery service/failsafe is left available; do not run another write gate.' -ForegroundColor Red
    }

    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        $pass=$false
    }

    $finalClosurePass=
        $finalJournalAbsent -and
        $finalFirmwareProofPass -and
        $cleanupFirmwareProofPass -and
        $finalServiceBaselinePass -and
        -not $failsafeTakeover

    if(-not $finalClosurePass){$pass=$false}

    try {
        Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure
    }
    catch {
        $pass=$false
        $failure="M9C summary write failed: $($_.Exception.Message)"
    }

    try {
        $package=& $packagingScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot -WatchdogLogPath $serviceLog -WatchdogStatusPath $statusPath -WatchdogJournalPath $journalPath
        $packagePath=[string]$package.ZipPath
        $packageSha256=[string]$package.ZipSha256
        Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure
        Write-Host ("M9C evidence ZIP: {0}" -f $packagePath)
        Write-Host ("M9C ZIP SHA256 : {0}" -f $packageSha256)
    }
    catch {
        $pass=$false
        $packFailure="M9C evidence packaging failed: $($_.Exception.Message)"
        $failure=$(if([string]::IsNullOrWhiteSpace($failure)){$packFailure}else{"$failure | $packFailure"})
        try{Write-HarnessSummary 'FAIL_CLOSED' $failure}catch{}
    }

    if($controller){$controller.Dispose()}

    if($failsafe){
        if($finalJournalAbsent){
            try{$failsafe.Dispose()}catch{}
        }
        else {
            # Do not kill a live recovery failsafe while retained ownership exists.
            try{$failsafe.Dispose()}catch{}
        }
    }
}

if(-not $pass){
    throw "M9C FAILED: $failure"
}

Write-Host ''
Write-Host 'PASS: HP 8C40 M9C production-path watchdog smoke fully closed.' -ForegroundColor Green
Write-Host 'Proven: normal factory/public backend -> PREPARE -> one 30/30 -> COMMIT -> 5-frame supervision -> RESTORE_BEGIN -> FF/FF -> RELEASE -> journal absent -> cleanup FF/FF -> M4 Manual/Stopped.' -ForegroundColor Green
 -or
       $LASTEXITCODE -ne 0 -or
       $parentHead -cne $authorizationSourceHead){
        throw "M9C authorization must be a direct child of its same-CI source HEAD. source=$authorizationSourceHead parent=$parentHead current=$localHead"
    }

    $statusText=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'M9C could not inspect repository status.'}
    $blocking=@(
        $statusText -split "[\r\n]+" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
        }
    )
    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'M9C requires committed source/config state; only untracked logs/ evidence is allowed.'
    }

    return $localHead
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop

    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    if(([string]$board.Manufacturer).Trim() -cne 'HP' -or
       ([string]$board.Product).Trim() -cne '8C40' -or
       ([string]$board.Version).Trim() -cne '63.43' -or
       ([string]$system.Manufacturer).Trim() -cne 'HP' -or
       ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M9C exact-target fingerprint mismatch.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ProcessStartTicks([int]$ProcessId){
    $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try{return [long]$process.StartTime.ToUniversalTime().Ticks}
    finally{$process.Dispose()}
}

function Assert-ServiceBaseline {
    $svc=Get-ServiceState
    if(-not $svc){throw 'M9C requires the already-qualified M4 service to be installed; harness will not install it.'}
    if([string]$svc.State -cne 'Stopped' -or
       [string]$svc.StartMode -cne 'Manual' -or
       [int]$svc.ProcessId -ne 0){
        throw "M9C requires M4 Manual/Stopped/PID0; observed $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)."
    }
    if([string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw "M9C requires LocalSystem; observed '$($svc.StartName)'."
    }
    foreach($required in @(
        $serviceExe,
        '--service-name VictusFanControlWatchdogM4',
        '--m4-8c40-lease-service'
    )){
        if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){
            throw "M9C M4 service configuration mismatch; missing '$required' in '$($svc.PathName)'."
        }
    }
    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "M9C service executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "M9C service module missing: $serviceModule"}
    return $svc
}

function Get-PowerSnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $status=[System.Windows.Forms.SystemInformation]::PowerStatus
    $percent=$null
    if($status.BatteryLifePercent -ge 0){$percent=[math]::Round([double]$status.BatteryLifePercent*100,0)}
    return [pscustomobject]@{
        PowerLineStatus=[string]$status.PowerLineStatus
        BatteryPercent=$percent
    }
}

function Assert-PowerSane {
    $power=Get-PowerSnapshot
    if($power.PowerLineStatus -cne 'Online'){throw "M9C requires AC online; observed '$($power.PowerLineStatus)'."}
    if($null -eq $power.BatteryPercent -or [double]$power.BatteryPercent -lt 20){
        throw "M9C requires readable battery >=20%; observed '$($power.BatteryPercent)'."
    }
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9C setpoint probe failed. Raw: $output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $match=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M9C could not parse setpoint probe. Raw: $output"}
    return [pscustomobject]@{
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        cpu=[int]$match.Groups[1].Value
        gpu=[int]$match.Groups[2].Value
        raw=[string]$line
    }
}

function Assert-StableFirmwareOwned([string]$Context,[string]$EvidencePath){
    $samples=@()
    $consecutive=0

    for($read=1;$read -le 6;$read++){
        $sample=Read-8C40Setpoint
        $samples+=@($sample)
        Write-Host ("{0} FF proof {1}/6: {2}" -f $Context,$read,$sample.raw)

        if($sample.cpu -eq 255 -and $sample.gpu -eq 255){
            $consecutive++
            if($consecutive -ge 2){
                [ordered]@{
                    context=$Context
                    passed=$true
                    requiredConsecutive=2
                    maximumReads=6
                    samples=$samples
                } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
                return
            }
        }
        else {$consecutive=0}

        if($read -lt 6){Start-Sleep -Milliseconds 75}
    }

    [ordered]@{
        context=$Context
        passed=$false
        requiredConsecutive=2
        maximumReads=6
        samples=$samples
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8

    throw "$Context requires two consecutive independent FF/FF observations within six reads."
}

function Wait-M4Ready([int]$ExpectedPid){
    $deadline=(Get-Date).AddSeconds(20)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try {
                $status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if([int]$status.ProcessId -eq $ExpectedPid -and
                   $status.Ready -and -not $status.Blocked -and
                   [int]$status.SessionId -eq 0 -and
                   $status.AccountName -match 'SYSTEM$' -and
                   $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   $status.RecoveryDisposition -ceq 'Ready'){
                    return $status
                }
            } catch {}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9C timed out waiting for exact M4 Ready state.'
}

function Test-OwnedPhase($Phase){
    if($null -eq $Phase){return $false}
    if($Phase -is [string]){return ($Phase -ceq 'Owned' -or $Phase -ceq '2')}
    try{return ([int]$Phase -eq 2)}catch{return $false}
}

function Assert-OwnedJournal($Journal,[int]$ExpectedPid,[long]$ExpectedStartTicks){
    if([int]$Journal.SchemaVersion -ne 2 -or
       $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Test-OwnedPhase $Journal.Phase) -or
       [int]$Journal.Generation -ne 3 -or
       [int]$Journal.Controller.ProcessId -ne $ExpectedPid -or
       [long]$Journal.Controller.ProcessStartUtcTicks -ne $ExpectedStartTicks -or
       [int]$Journal.Owned.Cpu -ne 30 -or
       [int]$Journal.Owned.Gpu -ne 30){
        throw 'M9C durable journal is not schema-v2 generation-3 OWNED 30/30 bound to exact controller PID+creation ticks.'
    }
}

function Start-M9CFailsafe {
    if(-not (Test-Path -LiteralPath $failsafeScript -PathType Leaf)){throw "M9C failsafe missing: $failsafeScript"}

    $process=Start-Process powershell.exe -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass',
        '-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,
        '-LogPath',$failsafeLog
    ) -WindowStyle Hidden -PassThru

    $deadline=(Get-Date).AddSeconds(3)
    while((Get-Date) -lt $deadline){
        $process.Refresh()
        if($process.HasExited){throw 'M9C independent failsafe exited before ARMED proof.'}

        if(Test-Path -LiteralPath $failsafeLog){
            $text=Get-Content -LiteralPath $failsafeLog -Raw -ErrorAction SilentlyContinue
            if($text -match 'M9C FAILSAFE ARMED:'){return $process}
        }
        Start-Sleep -Milliseconds 50
    }

    throw 'M9C independent failsafe did not publish ARMED proof before controller launch.'
}

function Test-FailsafeTakeover {
    if(-not (Test-Path -LiteralPath $failsafeLog)){return $false}
    $text=Get-Content -LiteralPath $failsafeLog -Raw
    return ($text -match 'M9C FAILSAFE TAKEOVER:' -or
            $text -match 'M9C FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M9C FAILSAFE SERVICE-START:' -or
            $text -match 'M9C FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M9C FAILSAFE STARTED:' -or
            $text -match 'M9C FAILSAFE RECOVERED:')
}

function Wait-ReadyMarker([int]$ExpectedPid){
    $deadline=(Get-Date).AddSeconds(45)

    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $readyPath){
            $ready=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
            if([int]$ready.processId -ne $ExpectedPid){
                throw "M9C READY PID mismatch: marker=$($ready.processId), expected=$ExpectedPid."
            }
            return $ready
        }

        if($controller -and $controller.HasExited){
            $controller.Refresh()
            $exit='unavailable'
            try{$exit=[string]$controller.ExitCode}catch{}
            $detail='result unavailable'
            if(Test-Path -LiteralPath $resultPath){
                try {
                    $early=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                    $detail=("result={0}; reason={1}" -f $early.result,$early.failureReason)
                } catch {}
            }
            throw "M9C controller exited before READY. exit=$exit; $detail"
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'M9C timed out waiting for OWNED 30/30 READY marker.'
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function Assert-CausalServiceLog([int]$ControllerPid){
    if(-not (Test-Path -LiteralPath $serviceLog)){throw "M9C service log missing: $serviceLog"}

    $all=@(Get-Content -LiteralPath $serviceLog)
    $segment=@($all | Select-Object -Skip $logLineBoundary)
    $segment | Set-Content -LiteralPath $serviceSegmentPath -Encoding UTF8

    $tag="controller PID=$ControllerPid"
    $prepare=@();$intent=@();$commit=@();$restore=@();$release=@()

    for($i=0;$i -lt $segment.Count;$i++){
        $line=[string]$segment[$i]
        if($line -match 'WATCHDOG PREPARE ACK' -and $line.Contains($tag)){$prepare+=@($i)}
        if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line.Contains($tag) -and $line -match 'target=30/30'){$intent+=@($i)}
        if($line -match 'WATCHDOG COMMIT ACK' -and $line.Contains($tag) -and $line -match 'target=30/30'){$commit+=@($i)}
        if($line -match 'WATCHDOG RESTORE_BEGIN ACK' -and $line.Contains($tag)){$restore+=@($i)}
        if($line -match 'WATCHDOG RELEASE ACK' -and $line.Contains($tag)){$release+=@($i)}
    }

    if($prepare.Count -ne 1 -or $intent.Count -ne 1 -or $commit.Count -ne 1 -or
       $restore.Count -ne 1 -or $release.Count -ne 1){
        throw ("M9C causal log counts invalid: PREPARE={0} INTENT={1} COMMIT={2} RESTORE={3} RELEASE={4}" -f
            $prepare.Count,$intent.Count,$commit.Count,$restore.Count,$release.Count)
    }

    if(-not ($prepare[0] -lt $intent[0] -and $intent[0] -lt $commit[0] -and
             $commit[0] -lt $restore[0] -and $restore[0] -lt $release[0])){
        throw 'M9C causal ordering is not PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE.'
    }
}

function Write-HarnessSummary([string]$Result,[string]$Failure){
    [ordered]@{
        schemaVersion=1
        gate='M9C-HARNESS'
        result=$Result
        failure=$Failure
        evidenceHead=$head
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        watchdogPid=$watchdogPid
        watchdogStartUtcTicks=$watchdogStartTicks
        controllerPid=$controllerPid
        controllerStartUtcTicks=$controllerStartTicks
        journalGeneration=$journalGeneration
        failsafeDelaySeconds=$FailsafeDelaySeconds
        failsafePid=$failsafePid
        failsafeTakeover=$failsafeTakeover
        causalChainPass=$causalChainPass
        finalClosurePass=$finalClosurePass
        finalJournalAbsent=$finalJournalAbsent
        finalFirmwareProofPass=$finalFirmwareProofPass
        cleanupFirmwareProofPass=$cleanupFirmwareProofPass
        finalServiceBaselinePass=$finalServiceBaselinePass
        productionConstructionAuthorized=$false
        watchdogRecoveryValidated=$false
        automaticPolicyEnabled=$false
        controlEnabledByDefault=$false
        readyPath=$readyPath
        resultPath=$resultPath
        packagePath=$packagePath
        packageSha256=$packageSha256
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9C refused while '$name' is running."}
}

$head=Assert-RepositoryProvenance
Assert-ExactTarget
Assert-PowerSane
Assert-ServiceBaseline | Out-Null

if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9C refuses retained watchdog journal evidence; it was not deleted.'
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 M9C PRODUCTION-PATH WATCHDOG SMOKE' -ForegroundColor Cyan
Write-Host 'WRITE-CAPABLE ONLY AFTER THE VERSIONED M9C AUTHORIZATION BARRIER.' -ForegroundColor Yellow
Write-Host 'Exactly one equal-only 30/30 ApplyAsync is permitted.' -ForegroundColor Yellow
Write-Host ''

try {
    Write-Host 'Step 1: same-HEAD build/static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "M9C build failed with exit=$LASTEXITCODE."}

    foreach($script in @(
        'test-8c40-m9-production-watchdog-invariants.ps1',
        'test-8c40-m9b-readonly-preflight-invariants.ps1',
        'test-8c40-m9c-production-smoke-invariants.ps1',
        'test-8c40-m9c-harness-invariants.ps1',
        'test-8c40-m9c-tracked-child-selftest.ps1',
        'test-8c40-m9c-evidence-packaging.ps1'
    )){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE -ne 0){throw "M9C regression '$script' failed with exit=$LASTEXITCODE."}
    }

    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'M9C SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'M9C coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'M9C HP backend/gate self-test failed.'}

    Write-Host 'Step 2: stable firmware/service baseline...' -ForegroundColor Cyan
    Assert-StableFirmwareOwned 'M9C baseline' $baselineFfPath
    Assert-ServiceBaseline | Out-Null
    Assert-PowerSane

    Write-Host ''
    $confirm=Read-Host "Type exactly $token to authorize this one versioned 30/30 physical smoke"
    if($confirm -cne $token){throw 'M9C cancelled before service/failsafe/controller active boundary.'}

    Write-Host 'Step 3: start exact LocalSystem M4 watchdog...' -ForegroundColor Cyan
    Start-Service -Name $serviceName
    $serviceStartedByHarness=$true

    $svc=Get-ServiceState
    if(-not $svc -or $svc.State -ne 'Running' -or [int]$svc.ProcessId -le 0){
        throw 'M9C watchdog service did not reach Running.'
    }

    $watchdogPid=[int]$svc.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
    $status=Wait-M4Ready $watchdogPid

    if(Test-Path -LiteralPath $serviceLog){$logLineBoundary=@(Get-Content -LiteralPath $serviceLog).Count}

    Write-Host ("Watchdog PID={0} startTicks={1} target={2}" -f
        $watchdogPid,$watchdogStartTicks,$status.TargetProfileId)

    Write-Host 'Step 4: arm independent delayed failsafe BEFORE controller...' -ForegroundColor Cyan
    $failsafe=Start-M9CFailsafe
    $failsafePid=$failsafe.Id

    Write-Host 'Step 5: launch native tracked M9C controller...' -ForegroundColor Cyan
    $dotnetExecutable=(Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $controller=Start-M9CTrackedChild -Executable $dotnetExecutable -Arguments @(
        $cli,
        '--8c40-m9c-production-smoke',
        '--8c40-m9c-token',$token,
        '--8c40-m9c-ready-path',$readyPath,
        '--8c40-m9c-continue-path',$continuePath,
        '--8c40-m9c-result-path',$resultPath,
        '--modules-dir',$modulesDir
    ) -WorkingDirectory $repoRoot

    $controllerPid=$controller.Id
    $controllerStartTicks=[long]$controller.StartTime.ToUniversalTime().Ticks
    $ready=Wait-ReadyMarker $controllerPid

    if([int]$ready.schemaVersion -ne 1 -or
       $ready.gate -cne 'M9C' -or
       $ready.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       [long]$ready.processStartUtcTicks -ne $controllerStartTicks -or
       $ready.authority -cne 'Custom' -or
       [int]$ready.cpuSetpoint -ne 30 -or [int]$ready.gpuSetpoint -ne 30 -or
       [int]$ready.cpuRpm -le 0 -or [int]$ready.gpuRpm -le 0 -or
       [int]$ready.applyCalls -ne 1 -or
       -not [bool]$ready.constructionScopeClosedBeforeCustom -or
       ([string]$ready.constructionRoute).IndexOf('HpFanControlBackendFactory.Create',[StringComparison]::Ordinal) -lt 0){
        throw 'M9C READY marker does not prove exact normal factory/public-backend OWNED 30/30 with one ApplyAsync.'
    }

    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9C READY exists but durable OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $controllerPid $controllerStartTicks
    $journalGeneration=[int]$journal.Generation

    $svc=Get-ServiceState
    if([int]$svc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'M9C watchdog process identity changed after controller reached OWNED.'
    }

    $failsafe.Refresh()
    if($failsafe.HasExited){throw 'M9C failsafe exited before parent OWNED proof.'}
    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        throw 'M9C independent failsafe fired before parent continuation.'
    }

    Write-Host ("M9C OWNED proof: controller PID={0} ticks={1}; journal generation={2}; RPM={3}/{4}" -f
        $controllerPid,$controllerStartTicks,$journalGeneration,$ready.cpuRpm,$ready.gpuRpm) -ForegroundColor Green

    'M9C-CONTINUE' | Set-Content -LiteralPath $continuePath -Encoding ASCII

    Write-Host 'Step 6: five-frame supervision + normal restore/release...' -ForegroundColor Cyan
    $controllerExit=Wait-M9CTrackedChildExitCode -Process $controller -Seconds 30

    if(-not (Test-Path -LiteralPath $resultPath)){throw 'M9C controller exited without durable result evidence.'}
    $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json

    if($controllerExit -ne 0 -or [string]$result.result -cne 'PASS_CONTROLLER_LOCAL_CLOSURE'){
        throw "M9C controller failed. exit=$controllerExit result=$($result.result) reason=$($result.failureReason)"
    }

    if([int]$result.applyCalls -ne 1 -or
       -not [bool]$result.readyPublished -or
       -not [bool]$result.continueObserved -or
       -not [bool]$result.constructionScopeClosedBeforeCustom -or
       -not [bool]$result.normalRestoreCompleted -or
       -not [bool]$result.finalFirmwareOwned -or
       -not [bool]$result.physicalExecutionAuthorized -or
       -not [bool]$result.qualificationConstructionAuthorized -or
       [bool]$result.productionConstructionAuthorized -or
       [bool]$result.watchdogRecoveryValidated -or
       @($result.supervisionSamples).Count -ne 5){
        throw 'M9C result does not prove the bounded one-write production-path transaction and closed production promotion.'
    }

    if(Test-Path -LiteralPath $journalPath){
        Copy-Item -LiteralPath $journalPath -Destination $retainedBeforeRecoveryPath
        throw 'M9C normal completion left durable journal evidence.'
    }

    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        throw 'M9C is safe but invalid because the independent failsafe took over.'
    }

    Assert-CausalServiceLog $controllerPid
    $causalChainPass=$true

    Write-Host 'Step 7: independent final firmware/service closure...' -ForegroundColor Cyan
    $finalJournalAbsent=$true
    Assert-StableFirmwareOwned 'M9C final' $finalFfPath
    $finalFirmwareProofPass=$true

    Stop-Service -Name $serviceName -Force
    $serviceStartedByHarness=$false

    $finalSvc=Get-ServiceState
    if(-not $finalSvc -or
       $finalSvc.State -ne 'Stopped' -or
       $finalSvc.StartMode -ne 'Manual' -or
       [int]$finalSvc.ProcessId -ne 0 -or
       [string]$finalSvc.StartName -notmatch 'LocalSystem|Local System'){
        throw 'M9C final M4 service baseline is not Manual/Stopped/PID0/LocalSystem.'
    }

    $finalServiceBaselinePass=$true
    $finalClosurePass=$true
    $pass=$true

    Write-Host 'PASS: M9C controller and parent causal closure completed.' -ForegroundColor Green
}
catch {
    $failure=$_.Exception.Message
    Write-Host ("M9C FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally {
    if($controller){
        try {
            $controller.Refresh()
            if(-not $controller.HasExited){
                Write-Warning 'M9C cleanup is terminating the exact tracked controller; watchdog recovery owns any retained lease.'
                $controller.Kill()
                try{[void]$controller.WaitForExit(5000)}catch{}
            }
        } catch {}
    }

    if(Test-Path -LiteralPath $journalPath){
        try {
            if(-not (Test-Path -LiteralPath $retainedBeforeRecoveryPath)){
                Copy-Item -LiteralPath $journalPath -Destination $retainedBeforeRecoveryPath
            }
        } catch {}

        $svc=Get-ServiceState
        if(-not $svc -or $svc.State -ne 'Running'){
            Write-Warning 'M9C cleanup: retained journal exists; starting qualified recovery service without deleting evidence.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            $serviceStartedByHarness=$true
        }

        [void](Wait-JournalGone 25)
    }

    $finalJournalAbsent=(-not (Test-Path -LiteralPath $journalPath))

    if($finalJournalAbsent){
        try {
            Assert-StableFirmwareOwned 'M9C cleanup' $cleanupFfPath
            $cleanupFirmwareProofPass=$true
            if(-not $finalFirmwareProofPass){$finalFirmwareProofPass=$true}
        }
        catch {
            $cleanupFirmwareProofPass=$false
            Write-Warning "M9C cleanup FF/FF proof failed: $($_.Exception.Message)"
        }

        try {
            $svc=Get-ServiceState
            if($svc -and $svc.State -ne 'Stopped'){
                Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            }

            $svc=Get-ServiceState
            if($svc -and $svc.State -eq 'Stopped' -and
               $svc.StartMode -eq 'Manual' -and [int]$svc.ProcessId -eq 0 -and
               [string]$svc.StartName -match 'LocalSystem|Local System'){
                $finalServiceBaselinePass=$true
            }
            else {$finalServiceBaselinePass=$false}
        }
        catch {
            $finalServiceBaselinePass=$false
            Write-Warning "M9C cleanup service baseline failed: $($_.Exception.Message)"
        }

        if($failsafe){
            try {
                $failsafe.Refresh()
                if(-not $failsafe.HasExited){
                    Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
                    try{[void]$failsafe.WaitForExit(3000)}catch{}
                }
            } catch {}
        }
    }
    else {
        $pass=$false
        $finalClosurePass=$false
        Write-Host 'CRITICAL: M9C durable ownership evidence remains and was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Qualified recovery service/failsafe is left available; do not run another write gate.' -ForegroundColor Red
    }

    if(Test-FailsafeTakeover){
        $failsafeTakeover=$true
        $pass=$false
    }

    $finalClosurePass=
        $finalJournalAbsent -and
        $finalFirmwareProofPass -and
        $cleanupFirmwareProofPass -and
        $finalServiceBaselinePass -and
        -not $failsafeTakeover

    if(-not $finalClosurePass){$pass=$false}

    try {
        Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure
    }
    catch {
        $pass=$false
        $failure="M9C summary write failed: $($_.Exception.Message)"
    }

    try {
        $package=& $packagingScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot -WatchdogLogPath $serviceLog -WatchdogStatusPath $statusPath -WatchdogJournalPath $journalPath
        $packagePath=[string]$package.ZipPath
        $packageSha256=[string]$package.ZipSha256
        Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure
        Write-Host ("M9C evidence ZIP: {0}" -f $packagePath)
        Write-Host ("M9C ZIP SHA256 : {0}" -f $packageSha256)
    }
    catch {
        $pass=$false
        $packFailure="M9C evidence packaging failed: $($_.Exception.Message)"
        $failure=$(if([string]::IsNullOrWhiteSpace($failure)){$packFailure}else{"$failure | $packFailure"})
        try{Write-HarnessSummary 'FAIL_CLOSED' $failure}catch{}
    }

    if($controller){$controller.Dispose()}

    if($failsafe){
        if($finalJournalAbsent){
            try{$failsafe.Dispose()}catch{}
        }
        else {
            # Do not kill a live recovery failsafe while retained ownership exists.
            try{$failsafe.Dispose()}catch{}
        }
    }
}

if(-not $pass){
    throw "M9C FAILED: $failure"
}

Write-Host ''
Write-Host 'PASS: HP 8C40 M9C production-path watchdog smoke fully closed.' -ForegroundColor Green
Write-Host 'Proven: normal factory/public backend -> PREPARE -> one 30/30 -> COMMIT -> 5-frame supervision -> RESTORE_BEGIN -> FF/FF -> RELEASE -> journal absent -> cleanup FF/FF -> M4 Manual/Stopped.' -ForegroundColor Green
