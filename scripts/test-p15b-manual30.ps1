$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$contractPath=Join-Path $repoRoot 'release\p15-target-checkpoint.json'
$contract=Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER. Keep before Administrator checks,
# service control, process launch, PawnIO/EC probing, WMI or recovery work.
if(-not [bool]$contract.startupNoWrite.physicalPassed -or
   -not [bool]$contract.startupNoWrite.evidenceClosed -or
   [bool]$contract.startupNoWrite.executionAuthorized){
    throw 'P15B PHYSICAL BLOCKED: P15A must be formally closed and re-blocked.'
}
if(-not [bool]$contract.manual30.executionAuthorized -or
   -not [bool]$contract.manual30.controllerPhysicalExecutionAuthorized){
    throw 'P15B PHYSICAL BLOCKED: Manual30 harness/controller authorization is closed.'
}
if([bool]$contract.automatic.executionAuthorized -or
   [bool]$contract.safetyBoundary.manualExecutionAuthorized -or
   [bool]$contract.safetyBoundary.automaticExecutionAuthorized -or
   [bool]$contract.safetyBoundary.controlEnabledByDefault -or
   [bool]$contract.safetyBoundary.automaticPolicyEnabled){
    throw 'P15B PHYSICAL BLOCKED: later/user-facing/default/automatic control gate is open.'
}

$expectedBranch='feature/victus-8c40-p15-hardware-checkpoint'
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'
$expectedServiceExeSha='ec10242ce40c12856cf9222d10e43016854498b3946375c8f73e9f927d9912ed'
$expectedServiceModuleSha='c38fd116e7aff4d1fdb0a494e296be0a6708e5a22fc72f14587442fb7f8f7906'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$token='8C40-P15B-MANUAL30'
$packager=Join-Path $PSScriptRoot 'package-p15b-evidence.ps1'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-p15b-service-failsafe-8c40.ps1'
. (Join-Path $PSScriptRoot 'p15b-tracked-child.ps1')
. (Join-Path $PSScriptRoot 'p15b-service-baseline.ps1')

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\p15b-manual30_{0}" -f $stamp)
$readyPath=Join-Path $evidenceRoot 'p15b-ready.json'
$continuePath=Join-Path $evidenceRoot 'p15b-continue.txt'
$resultPath=Join-Path $evidenceRoot 'p15b-controller-result.json'
$summaryPath=Join-Path $evidenceRoot 'p15b-harness-summary.json'
$baselineFfPath=Join-Path $evidenceRoot 'p15b-baseline-ff.json'
$ownedSetpointPath=Join-Path $evidenceRoot 'p15b-owned-setpoint.json'
$ownedJournalPath=Join-Path $evidenceRoot 'p15b-owned-journal.json'
$finalFfPath=Join-Path $evidenceRoot 'p15b-final-ff.json'
$cleanupFfPath=Join-Path $evidenceRoot 'p15b-cleanup-ff.json'
$serviceSnapshotsPath=Join-Path $evidenceRoot 'p15b-service-snapshots.json'
$serviceSegmentPath=Join-Path $evidenceRoot 'p15b-watchdog-log-segment.txt'
$failsafeLog=Join-Path $evidenceRoot 'p15b-failsafe.log'

$head=$null
$controller=$null
$failsafe=$null
$serviceStartedByHarness=$false
$serviceRestartedForCleanup=$false
$initialServiceMode=$null
$initialServicePid=0
$initialServiceStartTicks=0L
$initialServiceStatePreserved=$false
$watchdogPid=0
$watchdogStartTicks=0L
$controllerPid=0
$controllerStartTicks=0L
$logLineBoundary=0
$causalChainPass=$false
$strongRestorePass=$false
$finalJournalAbsent=$false
$finalFirmwareProofPass=$false
$finalServiceBaselinePass=$false
$cleanupFirmwareProofPass=$false
$failsafeTakeover=$false
$pass=$false
$failure=$null
$packagePath=$null
$packageSha256=$null
$serviceBefore=$null
$serviceDuring=$null
$serviceAfter=$null

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'P15B must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch){
        throw "P15B requires branch '$expectedBranch'; observed '$branch'."
    }
    $local=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $local -notmatch '^[0-9a-f]{40}$' -or $local -cne $upstream){
        throw "P15B requires local HEAD == upstream HEAD. local=$local upstream=$upstream"
    }
    $status=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'P15B could not inspect git status.'}
    $blocking=@($status -split "[\r\n]+" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
    })
    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'P15B requires committed source/config state; only untracked logs/ evidence is allowed.'
    }
    return $local
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop
    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(([string]$bios.SMBIOSBIOSVersion).Trim(),([string]$bios.Version).Trim()) -join ' | '
    if(([string]$board.Manufacturer).Trim() -cne 'HP' -or
       ([string]$board.Product).Trim() -cne '8C40' -or
       ([string]$board.Version).Trim() -cne '63.43' -or
       ([string]$system.Manufacturer).Trim() -cne 'HP' -or
       ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'P15B exact-target fingerprint mismatch.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ProcessStartTicks([int]$ProcessId){
    $p=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try{return [long]$p.StartTime.ToUniversalTime().Ticks}finally{$p.Dispose()}
}

function Assert-ServiceCommon($svc,[string]$Context){
    if(-not $svc){throw "$Context requires installed M4 service."}
    if([string]$svc.StartMode -cne 'Manual' -or
       [string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw "$Context requires Manual/LocalSystem service."
    }
    foreach($required in @($serviceExe,'--service-name VictusFanControlWatchdogM4','--m4-8c40-lease-service')){
        if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){
            throw "$Context service configuration mismatch; missing '$required'."
        }
    }
    $exeHash=(Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $moduleHash=(Get-FileHash -LiteralPath $serviceModule -Algorithm SHA256).Hash.ToLowerInvariant()
    if($exeHash -cne $expectedServiceExeSha -or $moduleHash -cne $expectedServiceModuleSha){
        throw "$Context qualified watchdog executable/module hash mismatch."
    }
}

function Get-ValidatedServiceBaseline {
    $svc=Get-ServiceState
    Assert-ServiceCommon $svc 'P15B baseline'

    $journalPresent=Test-Path -LiteralPath $journalPath
    $readyVerified=$false
    $pid=[int]$svc.ProcessId
    $ticks=0L

    if([string]$svc.State -ceq 'Running' -and $pid -gt 0 -and -not $journalPresent){
        $ticks=Get-ProcessStartTicks $pid
        [void](Wait-M4Ready $pid)

        $confirmed=Get-ServiceState
        Assert-ServiceCommon $confirmed 'P15B inherited-running baseline confirmation'
        if([string]$confirmed.State -cne 'Running' -or
           [int]$confirmed.ProcessId -ne $pid -or
           (Get-ProcessStartTicks $pid) -ne $ticks){
            throw 'P15B inherited Running watchdog identity changed during Ready verification.'
        }

        $readyVerified=$true
        $svc=$confirmed
    }

    $mode=Resolve-P15BServiceBaselineMode -State ([string]$svc.State) -StartMode ([string]$svc.StartMode) -StartName ([string]$svc.StartName) -ProcessId ([int]$svc.ProcessId) -JournalPresent $journalPresent -ReadyVerified $readyVerified

    if($mode -ceq 'Stopped'){
        $pid=0
        $ticks=0L
    }

    [pscustomobject]@{
        Mode=$mode
        ProcessId=$pid
        ProcessStartUtcTicks=$ticks
        Service=$svc
        ReadyVerified=$readyVerified
        JournalPresent=$journalPresent
    }
}

function Get-PowerSnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $s=[System.Windows.Forms.SystemInformation]::PowerStatus
    $percent=$null
    if($s.BatteryLifePercent -ge 0){$percent=[math]::Round([double]$s.BatteryLifePercent*100,0)}
    [pscustomobject]@{PowerLineStatus=[string]$s.PowerLineStatus;BatteryPercent=$percent}
}

function Assert-PowerSane {
    $p=Get-PowerSnapshot
    if($p.PowerLineStatus -cne 'Online'){throw "P15B requires AC online; observed '$($p.PowerLineStatus)'."}
    if($null -eq $p.BatteryPercent -or [double]$p.BatteryPercent -lt 20){
        throw "P15B requires readable battery >=20%; observed '$($p.BatteryPercent)'."
    }
}

function Read-8C40Setpoint {
    $raw=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "P15B setpoint probe failed. Raw: $raw"}
    $line=($raw -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $m=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $m.Success){throw "P15B could not parse setpoint probe. Raw: $raw"}
    [pscustomobject]@{timestampUtc=(Get-Date).ToUniversalTime().ToString('O');cpu=[int]$m.Groups[1].Value;gpu=[int]$m.Groups[2].Value;raw=[string]$line}
}

function Assert-StableSetpoint([int]$Cpu,[int]$Gpu,[string]$Context,[string]$EvidencePath){
    $samples=@();$consecutive=0
    for($read=1;$read -le 6;$read++){
        $s=Read-8C40Setpoint
        $samples+=@($s)
        Write-Host ("{0} proof {1}/6: {2}" -f $Context,$read,$s.raw)
        if($s.cpu -eq $Cpu -and $s.gpu -eq $Gpu){
            $consecutive++
            if($consecutive -ge 2){
                [ordered]@{context=$Context;passed=$true;expectedCpu=$Cpu;expectedGpu=$Gpu;requiredConsecutive=2;maximumReads=6;samples=$samples} |
                    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
                return
            }
        }else{$consecutive=0}
        if($read -lt 6){Start-Sleep -Milliseconds 100}
    }
    [ordered]@{context=$Context;passed=$false;expectedCpu=$Cpu;expectedGpu=$Gpu;requiredConsecutive=2;maximumReads=6;samples=$samples} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
    throw "$Context requires two consecutive $Cpu/$Gpu observations within six reads."
}

function Wait-M4Ready([int]$ExpectedPid){
    $deadline=(Get-Date).AddSeconds(20)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try{
                $s=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if([int]$s.ProcessId -eq $ExpectedPid -and $s.Ready -and -not $s.Blocked -and
                   [int]$s.SessionId -eq 0 -and $s.AccountName -match 'SYSTEM$' -and
                   $s.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $s.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   $s.RecoveryDisposition -ceq 'Ready'){return $s}
            }catch{}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'P15B timed out waiting for exact M4 Ready state.'
}

function Test-OwnedPhase($phase){
    if($null -eq $phase){return $false}
    if($phase -is [string]){return ($phase -ceq 'Owned' -or $phase -ceq '2')}
    try{return ([int]$phase -eq 2)}catch{return $false}
}

function Assert-OwnedJournal($journal,[int]$ExpectedPid,[long]$ExpectedTicks){
    if([int]$journal.SchemaVersion -ne 2 -or
       $journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Test-OwnedPhase $journal.Phase) -or
       [int]$journal.Generation -ne 3 -or
       [int]$journal.Controller.ProcessId -ne $ExpectedPid -or
       [long]$journal.Controller.ProcessStartUtcTicks -ne $ExpectedTicks -or
       [int]$journal.Owned.Cpu -ne 30 -or
       [int]$journal.Owned.Gpu -ne 30){
        throw 'P15B durable journal is not schema-v2 generation-3 OWNED 30/30 bound to the exact controller identity.'
    }
}

function Wait-Ready([int]$ExpectedPid){
    $deadline=(Get-Date).AddSeconds(45)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $readyPath){
            $r=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
            if([int]$r.processId -ne $ExpectedPid){throw 'P15B READY PID mismatch.'}
            return $r
        }
        if($controller){
            $controller.Refresh()
            if($controller.HasExited){
                $exit='unavailable';try{$exit=[string]$controller.ExitCode}catch{}
                $detail='result unavailable'
                if(Test-Path -LiteralPath $resultPath){
                    try{$early=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json;$detail="result=$($early.result); reason=$($early.failureReason)"}catch{}
                }
                throw "P15B controller exited before READY. exit=$exit; $detail"
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'P15B timed out waiting for OWNED 30/30 READY marker.'
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function Start-P15BFailsafe {
    $p=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$failsafeScript,'-DelaySeconds','90','-LogPath',$failsafeLog) -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(3)
    while((Get-Date) -lt $deadline){
        $p.Refresh()
        if($p.HasExited){throw 'P15B independent failsafe exited before ARMED proof.'}
        if(Test-Path -LiteralPath $failsafeLog){
            $t=Get-Content -LiteralPath $failsafeLog -Raw -ErrorAction SilentlyContinue
            if($t -match 'P15B FAILSAFE ARMED:'){return $p}
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'P15B independent failsafe did not publish ARMED proof.'
}

function Test-FailsafeTakeover {
    if(-not (Test-Path -LiteralPath $failsafeLog)){return $false}
    $t=Get-Content -LiteralPath $failsafeLog -Raw
    return ($t -match 'P15B FAILSAFE TAKEOVER:' -or $t -match 'P15B FAILSAFE CONTROLLER-KILL:' -or
            $t -match 'P15B FAILSAFE SERVICE-START:' -or $t -match 'P15B FAILSAFE SERVICE-RESTART:' -or
            $t -match 'P15B FAILSAFE STARTED:' -or $t -match 'P15B FAILSAFE RECOVERED:')
}

function Assert-CausalServiceLog([int]$ControllerPid){
    if(-not (Test-Path -LiteralPath $serviceLog)){throw 'P15B watchdog service log missing.'}
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
    if($prepare.Count -ne 1 -or $intent.Count -ne 1 -or $commit.Count -ne 1 -or $restore.Count -ne 1 -or $release.Count -ne 1){
        throw ("P15B causal log counts invalid: PREPARE={0} INTENT={1} COMMIT={2} RESTORE={3} RELEASE={4}" -f $prepare.Count,$intent.Count,$commit.Count,$restore.Count,$release.Count)
    }
    if(-not ($prepare[0] -lt $intent[0] -and $intent[0] -lt $commit[0] -and $commit[0] -lt $restore[0] -and $restore[0] -lt $release[0])){
        throw 'P15B causal ordering is not PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE.'
    }
}

function Snapshot-Service([string]$Phase){
    $svc=Get-ServiceState
    if(-not $svc){return [pscustomobject]@{phase=$Phase;installed=$false}}
    $ticks=0L
    if([int]$svc.ProcessId -gt 0){try{$ticks=Get-ProcessStartTicks ([int]$svc.ProcessId)}catch{}}
    [pscustomobject]@{
        phase=$Phase;installed=$true;state=[string]$svc.State;startMode=[string]$svc.StartMode;
        startName=[string]$svc.StartName;processId=[int]$svc.ProcessId;processStartUtcTicks=$ticks;pathName=[string]$svc.PathName
    }
}

function Write-Summary([string]$Result,[string]$Failure){
    [ordered]@{
        schemaVersion=1;gate='P15B-HARNESS';result=$Result;failure=$Failure;evidenceHead=$head;
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O');watchdogPid=$watchdogPid;watchdogStartUtcTicks=$watchdogStartTicks;
        controllerPid=$controllerPid;controllerStartUtcTicks=$controllerStartTicks;causalChainPass=$causalChainPass;
        strongRestorePass=$strongRestorePass;finalJournalAbsent=$finalJournalAbsent;finalFirmwareProofPass=$finalFirmwareProofPass;
        cleanupFirmwareProofPass=$cleanupFirmwareProofPass;finalServiceBaselinePass=$finalServiceBaselinePass;
        initialServiceMode=$initialServiceMode;initialServicePid=$initialServicePid;initialServiceStartUtcTicks=$initialServiceStartTicks;
        serviceStartedByHarness=$serviceStartedByHarness;serviceRestartedForCleanup=$serviceRestartedForCleanup;
        initialServiceStatePreserved=$initialServiceStatePreserved;
        failsafeTakeover=$failsafeTakeover;manual30ExecutionAuthorized=[bool]$contract.manual30.executionAuthorized;
        controllerPhysicalExecutionAuthorized=[bool]$contract.manual30.controllerPhysicalExecutionAuthorized;
        automaticExecutionAuthorized=[bool]$contract.automatic.executionAuthorized
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator
$head=Assert-RepositoryProvenance
Assert-ExactTarget
Assert-PowerSane
$serviceBefore=Snapshot-Service 'before'
$initialBaseline=Get-ValidatedServiceBaseline
$initialServiceMode=[string]$initialBaseline.Mode
$initialServicePid=[int]$initialBaseline.ProcessId
$initialServiceStartTicks=[long]$initialBaseline.ProcessStartUtcTicks
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 P15B ONE-SHOT MANUAL 30/30' -ForegroundColor Cyan
Write-Host 'P15A is formally closed. P15B allows exactly one production Manual 30/30 transaction, then strong restore.' -ForegroundColor Yellow

try{
    Write-Host 'Step 1: same-HEAD build and P15B static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "P15B build failed with exit=$LASTEXITCODE."}
    foreach($script in @('test-p15b-manual30-invariants.ps1','test-p15b-service-baseline-selftest.ps1','test-p15b-tracked-child-selftest.ps1','test-p15b-evidence-packaging.ps1')){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE -ne 0){throw "P15B regression '$script' failed with exit=$LASTEXITCODE."}
    }
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'P15B SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'P15B coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --adaptive-policy-self-test
    if($LASTEXITCODE -ne 0){throw 'P15B adaptive production/policy self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'P15B HP backend self-test failed.'}

    Write-Host 'Step 2: independent firmware/service baseline...' -ForegroundColor Cyan
    Assert-StableSetpoint 255 255 'P15B baseline FF/FF' $baselineFfPath
    $preTokenBaseline=Get-ValidatedServiceBaseline
    if([string]$preTokenBaseline.Mode -cne $initialServiceMode){
        throw "P15B service baseline mode changed before token: initial=$initialServiceMode current=$($preTokenBaseline.Mode)."
    }
    if($initialServiceMode -ceq 'Running' -and
       ([int]$preTokenBaseline.ProcessId -ne $initialServicePid -or
        [long]$preTokenBaseline.ProcessStartUtcTicks -ne $initialServiceStartTicks)){
        throw 'P15B inherited Running watchdog identity changed before the operator token.'
    }
    Assert-PowerSane

    $confirm=Read-Host "Type exactly $token to authorize one physical Manual 30/30 transaction"
    if($confirm -cne $token){throw 'P15B cancelled before service/failsafe/controller active boundary.'}

    Write-Host 'Step 3: establish exact qualified M4 watchdog runtime...' -ForegroundColor Cyan
    if($initialServiceMode -ceq 'Stopped'){
        Start-Service -Name $serviceName
        $serviceStartedByHarness=$true
        $svc=Get-ServiceState
        Assert-ServiceCommon $svc 'P15B runtime started by harness'
        if($svc.State -ne 'Running' -or [int]$svc.ProcessId -le 0){throw 'P15B watchdog did not reach Running.'}
        $watchdogPid=[int]$svc.ProcessId
        $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
        [void](Wait-M4Ready $watchdogPid)
    }elseif($initialServiceMode -ceq 'Running'){
        $svc=Get-ServiceState
        Assert-ServiceCommon $svc 'P15B inherited runtime'
        if($svc.State -ne 'Running' -or [int]$svc.ProcessId -ne $initialServicePid -or
           (Get-ProcessStartTicks $initialServicePid) -ne $initialServiceStartTicks){
            throw 'P15B inherited Running watchdog identity changed after the operator token.'
        }
        $watchdogPid=$initialServicePid
        $watchdogStartTicks=$initialServiceStartTicks
        [void](Wait-M4Ready $watchdogPid)
    }else{
        throw "P15B internal service-baseline mode is unsupported: $initialServiceMode"
    }
    $serviceDuring=Snapshot-Service 'running-before-controller'
    if(Test-Path -LiteralPath $serviceLog){$logLineBoundary=@(Get-Content -LiteralPath $serviceLog).Count}

    Write-Host 'Step 4: arm delayed independent failsafe...' -ForegroundColor Cyan
    $failsafe=Start-P15BFailsafe

    Write-Host 'Step 5: launch native tracked P15B Manual controller...' -ForegroundColor Cyan
    $dotnet=(Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $controller=Start-P15BTrackedChild -Executable $dotnet -Arguments @(
        $cli,'--8c40-p15b-manual30','--8c40-p15b-token',$token,
        '--8c40-p15b-ready-path',$readyPath,'--8c40-p15b-continue-path',$continuePath,
        '--8c40-p15b-result-path',$resultPath,'--modules-dir',$modulesDir
    ) -WorkingDirectory $repoRoot
    $controllerPid=$controller.Id
    $controllerStartTicks=[long]$controller.StartTime.ToUniversalTime().Ticks
    $ready=Wait-Ready $controllerPid

    if([int]$ready.schemaVersion -ne 1 -or [string]$ready.gate -cne 'P15B' -or
       [string]$ready.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       [long]$ready.processStartUtcTicks -ne $controllerStartTicks -or
       [string]$ready.mode -cne 'Manual' -or [string]$ready.authority -cne 'Custom' -or
       [int]$ready.cpuSetpoint -ne 30 -or [int]$ready.gpuSetpoint -ne 30 -or
       [int]$ready.cpuRpm -le 0 -or [int]$ready.gpuRpm -le 0 -or
       [int]$ready.applyManualCalls -ne 1 -or -not [bool]$ready.manualExecutionAuthorized -or
       [bool]$ready.automaticExecutionAuthorized -or
       ([string]$ready.constructionRoute).IndexOf('Hp8C40ProductionWatchdogGate.CreateLeaseIfAuthorized',[StringComparison]::Ordinal) -lt 0 -or
       ([string]$ready.constructionRoute).IndexOf('AdaptiveFanProductionController',[StringComparison]::Ordinal) -lt 0){
        throw 'P15B READY does not prove exactly one intended production Manual 30/30 transaction.'
    }

    Assert-StableSetpoint 30 30 'P15B parent OWNED 30/30' $ownedSetpointPath

    if(-not (Test-Path -LiteralPath $journalPath)){throw 'P15B READY exists but durable OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $controllerPid $controllerStartTicks
    Copy-Item -LiteralPath $journalPath -Destination $ownedJournalPath

    $svc=Get-ServiceState
    if([int]$svc.ProcessId -ne $watchdogPid -or (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'P15B watchdog process identity changed while controller is OWNED.'
    }
    if(Test-FailsafeTakeover){$failsafeTakeover=$true;throw 'P15B independent failsafe fired before continuation.'}

    'P15B-CONTINUE' | Set-Content -LiteralPath $continuePath -Encoding ASCII

    Write-Host 'Step 6: bounded supervision + production strong restore...' -ForegroundColor Cyan
    $controllerExit=Wait-P15BTrackedChildExitCode -Process $controller -Seconds 40
    if(-not (Test-Path -LiteralPath $resultPath)){throw 'P15B controller exited without durable result.'}
    $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if($controllerExit -ne 0 -or [string]$result.result -cne 'PASS_CONTROLLER_STRONG_RESTORE'){
        throw "P15B controller failed. exit=$controllerExit result=$($result.result) reason=$($result.failureReason)"
    }
    if([int]$result.applyManualCalls -ne 1 -or -not [bool]$result.readyPublished -or
       -not [bool]$result.continueObserved -or -not [bool]$result.manualModeSelected -or
       -not [bool]$result.productionRouteConstructed -or -not [bool]$result.productionRestoreCompleted -or
       -not [bool]$result.finalFirmwareOwned -or -not [bool]$result.physicalExecutionAuthorized -or
       -not [bool]$result.productionConstructionAuthorized -or
       [bool]$result.m9cQualificationConstructionAuthorized -or [bool]$result.m9dQualificationConstructionAuthorized -or
       [bool]$result.automaticExecutionAuthorized -or @($result.supervisionSamples).Count -ne 3){
        throw 'P15B controller result does not prove the bounded one-shot production Manual transaction.'
    }
    if($null -eq $result.restoreEvidence -or
       -not [bool]$result.restoreEvidence.localFirmwareAckVerified -or
       -not [bool]$result.restoreEvidence.watchdogLeaseRequired -or
       -not [bool]$result.restoreEvidence.watchdogReleaseVerified){
        throw 'P15B controller result lacks strong local restore + watchdog RELEASE evidence.'
    }
    if(@($result.finalFirmwareSamples).Count -lt 2){
        throw 'P15B controller did not retain independent final FF/FF samples.'
    }

    if(Test-Path -LiteralPath $journalPath){throw 'P15B production RELEASE returned but durable journal remains.'}
    $finalJournalAbsent=$true

    Assert-CausalServiceLog $controllerPid
    $causalChainPass=$true

    Assert-StableSetpoint 255 255 'P15B independent final FF/FF' $finalFfPath
    $finalFirmwareProofPass=$true

    $svc=Get-ServiceState
    Assert-ServiceCommon $svc 'P15B post-restore'
    if($svc.State -ne 'Running' -or [int]$svc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'P15B watchdog identity did not remain stable through strong restore.'
    }

    if(Test-FailsafeTakeover){$failsafeTakeover=$true;throw 'P15B independent failsafe took over; safe but invalid.'}
    $strongRestorePass=$true

    Write-Host 'Step 7: restore/preserve initial service baseline after strong restore proof...' -ForegroundColor Cyan
    if($initialServiceMode -ceq 'Stopped'){
        Stop-Service -Name $serviceName
        $serviceStartedByHarness=$false
        $finalSvc=Get-ServiceState
        if(-not $finalSvc -or $finalSvc.State -ne 'Stopped' -or $finalSvc.StartMode -ne 'Manual' -or
           [int]$finalSvc.ProcessId -ne 0 -or [string]$finalSvc.StartName -notmatch 'LocalSystem|Local System'){
            throw 'P15B final service baseline did not return to Manual/Stopped/PID0/LocalSystem.'
        }
    }else{
        $finalSvc=Get-ServiceState
        Assert-ServiceCommon $finalSvc 'P15B final inherited-running baseline'
        if($finalSvc.State -ne 'Running' -or [int]$finalSvc.ProcessId -ne $initialServicePid -or
           (Get-ProcessStartTicks $initialServicePid) -ne $initialServiceStartTicks){
            throw 'P15B did not preserve the inherited Running watchdog PID/start identity.'
        }
        [void](Wait-M4Ready $initialServicePid)
    }
    $initialServiceStatePreserved=$true
    $serviceAfter=Snapshot-Service 'after'
    $finalServiceBaselinePass=$true
    @($serviceBefore,$serviceDuring,$serviceAfter) | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $serviceSnapshotsPath -Encoding UTF8
    $pass=$true
    Write-Host 'PASS: P15B one-shot Manual 30/30 + strong restore completed.' -ForegroundColor Green
}
catch{
    $failure=$_.Exception.Message
    Write-Host ("P15B FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally{
    if($controller){
        try{
            $controller.Refresh()
            if(-not $controller.HasExited){
                Write-Warning 'P15B cleanup is terminating the exact tracked controller; watchdog recovery owns any retained lease.'
                $controller.Kill()
                try{[void]$controller.WaitForExit(5000)}catch{}
            }
        }catch{}
    }

    $cleanupSafe=$false
    try{
        if(Test-Path -LiteralPath $journalPath){
            $svc=Get-ServiceState
            if(-not $svc){throw 'P15B cleanup cannot find qualified watchdog service.'}
            if($svc.State -ne 'Running'){
                Start-Service -Name $serviceName
                if($initialServiceMode -ceq 'Stopped'){$serviceStartedByHarness=$true}else{$serviceRestartedForCleanup=$true}
            }
            if(-not (Wait-JournalGone 25)){throw 'P15B cleanup timed out waiting for watchdog journal recovery.'}
        }

        Assert-StableSetpoint 255 255 'P15B cleanup FF/FF' $cleanupFfPath
        $cleanupFirmwareProofPass=$true
        $cleanupSafe=$true
    }catch{
        Write-Warning ("P15B cleanup firmware proof failed: {0}" -f $_.Exception.Message)
    }

    if($failsafe){
        try{
            $failsafe.Refresh()
            if(-not $failsafe.HasExited -and ($pass -or $cleanupSafe)){
                Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
                try{[void]$failsafe.WaitForExit(3000)}catch{}
            }
        }catch{}
    }

    if($cleanupSafe -and -not (Test-Path -LiteralPath $journalPath)){
        try{
            $cleanupSvc=Get-ServiceState
            if($initialServiceMode -ceq 'Stopped'){
                if($cleanupSvc -and $cleanupSvc.State -eq 'Running'){
                    Stop-Service -Name $serviceName -ErrorAction Stop
                }
                $serviceStartedByHarness=$false
                $cleanupSvc=Get-ServiceState
                if($cleanupSvc -and $cleanupSvc.State -eq 'Stopped' -and [int]$cleanupSvc.ProcessId -eq 0){
                    $initialServiceStatePreserved=$true
                }
            }elseif($initialServiceMode -ceq 'Running'){
                if(-not $cleanupSvc -or $cleanupSvc.State -ne 'Running'){
                    Start-Service -Name $serviceName -ErrorAction Stop
                    $serviceRestartedForCleanup=$true
                    $cleanupSvc=Get-ServiceState
                }
                Assert-ServiceCommon $cleanupSvc 'P15B cleanup inherited-running baseline'
                if($cleanupSvc.State -eq 'Running' -and [int]$cleanupSvc.ProcessId -gt 0){
                    [void](Wait-M4Ready ([int]$cleanupSvc.ProcessId))
                    $initialServiceStatePreserved=$true
                }
            }
        }catch{
            Write-Warning ("P15B cleanup could not restore initial watchdog service state: {0}" -f $_.Exception.Message)
        }
    }

    if(-not (Test-Path -LiteralPath $serviceSnapshotsPath)){
        try{
            $serviceAfter=Snapshot-Service 'cleanup-final'
            @($serviceBefore,$serviceDuring,$serviceAfter) | ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath $serviceSnapshotsPath -Encoding UTF8
        }catch{}
    }

    try{Write-Summary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure}catch{}

    try{
        $pkg=& $packager -EvidenceRoot $evidenceRoot -RepositoryRoot $repoRoot
        $packagePath=[string]$pkg.ZipPath
        $packageSha256=[string]$pkg.ZipSha256
    }catch{
        Write-Warning ("P15B evidence packaging failed: {0}" -f $_.Exception.Message)
        if($pass){$pass=$false;$failure='P15B physical sequence passed but evidence packaging failed.'}
    }

    if($controller){try{$controller.Dispose()}catch{}}
}

if($pass){
    Write-Host ("P15B MANUAL30/STRONG-RESTORE PASS. Evidence ZIP: {0}" -f $packagePath) -ForegroundColor Green
    Write-Host ("SHA-256: {0}" -f $packageSha256) -ForegroundColor Green
    exit 0
}

Write-Error ("P15B FAIL_CLOSED. Evidence preserved at {0}. {1}" -f $evidenceRoot,$failure)
exit 1
