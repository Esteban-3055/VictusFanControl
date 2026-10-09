$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$contractPath=Join-Path $repoRoot 'release\p15-target-checkpoint.json'
$contract=Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER. Keep before Administrator checks,
# service/process control, PawnIO/EC probing, GUI launch or recovery work.
if(-not [bool]$contract.startupNoWrite.physicalPassed -or
   -not [bool]$contract.startupNoWrite.evidenceClosed -or
   [bool]$contract.startupNoWrite.executionAuthorized -or
   -not [bool]$contract.manual30.physicalPassed -or
   -not [bool]$contract.manual30.evidenceClosed -or
   [bool]$contract.manual30.executionAuthorized -or
   [bool]$contract.manual30.controllerPhysicalExecutionAuthorized){
    throw 'P15C PHYSICAL BLOCKED: P15A/P15B must be formally closed and re-blocked.'
}
if(-not [bool]$contract.guiManual.executionAuthorized -or
   -not [bool]$contract.guiManual.controllerPhysicalExecutionAuthorized){
    throw 'P15C PHYSICAL BLOCKED: GUI Manual qualification authorization is closed.'
}
if([bool]$contract.automatic.executionAuthorized -or
   [bool]$contract.safetyBoundary.manualExecutionAuthorized -or
   [bool]$contract.safetyBoundary.automaticExecutionAuthorized -or
   [bool]$contract.safetyBoundary.controlEnabledByDefault -or
   [bool]$contract.safetyBoundary.automaticPolicyEnabled -or
   [bool]$contract.safetyBoundary.candidateCurvePhysicallyValidated -or
   [bool]$contract.safetyBoundary.candidateCurveAuthorizedForProduction -or
   [bool]$contract.safetyBoundary.m9cQualificationConstructionAuthorized -or
   [bool]$contract.safetyBoundary.m9dQualificationConstructionAuthorized){
    throw 'P15C PHYSICAL BLOCKED: user/default/automatic/candidate/consumed qualification boundary is open.'
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
$appExe=Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$modulesDir=Join-Path $repoRoot 'modules'
$token='8C40-P15C-GUI-MANUAL30'
$packager=Join-Path $PSScriptRoot 'package-p15c-evidence.ps1'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-p15c-service-failsafe-8c40.ps1'
. (Join-Path $PSScriptRoot 'p15b-tracked-child.ps1')
. (Join-Path $PSScriptRoot 'p15b-service-baseline.ps1')

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\p15c-gui-manual_{0}" -f $stamp)
$readyPath=Join-Path $evidenceRoot 'p15c-gui-ready.json'
$manualAppliedPath=Join-Path $evidenceRoot 'p15c-gui-manual-applied.json'
$parentVerifiedPath=Join-Path $evidenceRoot 'p15c-parent-owned-verified.txt'
$resultPath=Join-Path $evidenceRoot 'p15c-gui-result.json'
$eventsPath=Join-Path $evidenceRoot 'p15c-gui-events.jsonl'
$summaryPath=Join-Path $evidenceRoot 'p15c-harness-summary.json'
$baselineFfPath=Join-Path $evidenceRoot 'p15c-baseline-ff.json'
$ownedSetpointPath=Join-Path $evidenceRoot 'p15c-owned-setpoint.json'
$ownedJournalPath=Join-Path $evidenceRoot 'p15c-owned-journal.json'
$finalFfPath=Join-Path $evidenceRoot 'p15c-final-ff.json'
$cleanupFfPath=Join-Path $evidenceRoot 'p15c-cleanup-ff.json'
$serviceSnapshotsPath=Join-Path $evidenceRoot 'p15c-service-snapshots.json'
$serviceSegmentPath=Join-Path $evidenceRoot 'p15c-watchdog-log-segment.txt'
$failsafeLog=Join-Path $evidenceRoot 'p15c-failsafe.log'
$appLogEvidencePath=Join-Path $evidenceRoot 'p15c-app.log'

$head=$null
$gui=$null
$failsafe=$null
$initialBaseline=$null
$initialServiceMode=$null
$initialServicePid=0
$initialServiceStartTicks=0L
$watchdogPid=0
$watchdogStartTicks=0L
$guiPid=0
$guiStartTicks=0L
$logLineBoundary=0
$causalChainPass=$false
$strongRestorePass=$false
$finalJournalAbsent=$false
$finalFirmwareProofPass=$false
$cleanupFirmwareProofPass=$false
$finalServiceBaselinePass=$false
$failsafeTakeover=$false
$pass=$false
$failure=$null
$packagePath=$null
$packageSha256=$null
$serviceBefore=$null
$serviceDuring=$null
$serviceAfter=$null
$appLogPath=$null

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'P15C must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch){
        throw "P15C requires branch '$expectedBranch'; observed '$branch'."
    }
    $local=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $local -notmatch '^[0-9a-f]{40}$' -or $local -cne $upstream){
        throw "P15C requires local HEAD == upstream HEAD. local=$local upstream=$upstream"
    }
    $status=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'P15C could not inspect git status.'}
    $blocking=@($status -split "[\r\n]+" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
    })
    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'P15C requires committed source/config state; only preserved untracked logs/evidence is allowed.'
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
        throw 'P15C exact-target fingerprint mismatch.'
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
    throw 'P15C timed out waiting for exact M4 Ready state.'
}

function Get-ValidatedServiceBaseline {
    $svc=Get-ServiceState
    Assert-ServiceCommon $svc 'P15C baseline'

    $journalPresent=Test-Path -LiteralPath $journalPath
    $readyVerified=$false
    $servicePid=[int]$svc.ProcessId
    $ticks=0L

    if([string]$svc.State -ceq 'Running' -and $servicePid -gt 0 -and -not $journalPresent){
        $ticks=Get-ProcessStartTicks $servicePid
        [void](Wait-M4Ready $servicePid)

        $confirmed=Get-ServiceState
        Assert-ServiceCommon $confirmed 'P15C inherited-running baseline confirmation'
        if([string]$confirmed.State -cne 'Running' -or
           [int]$confirmed.ProcessId -ne $servicePid -or
           (Get-ProcessStartTicks $servicePid) -ne $ticks){
            throw 'P15C inherited Running watchdog identity changed during Ready verification.'
        }

        $readyVerified=$true
        $svc=$confirmed
    }

    $mode=Resolve-P15BServiceBaselineMode -State ([string]$svc.State) -StartMode ([string]$svc.StartMode) -StartName ([string]$svc.StartName) -ProcessId ([int]$svc.ProcessId) -JournalPresent $journalPresent -ReadyVerified $readyVerified
    if($mode -ceq 'Stopped'){$servicePid=0;$ticks=0L}

    [pscustomobject]@{
        Mode=$mode
        ProcessId=$servicePid
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
    if($p.PowerLineStatus -cne 'Online'){throw "P15C requires AC online; observed '$($p.PowerLineStatus)'."}
    if($null -eq $p.BatteryPercent -or [double]$p.BatteryPercent -lt 20){
        throw "P15C requires readable battery >=20%; observed '$($p.BatteryPercent)'."
    }
}

function Assert-NoConflictingController {
    $conflicts=@(Get-Process OmenMon,OmenMon-Reborn,VictusFanControl.App -ErrorAction SilentlyContinue)
    if($conflicts.Count -gt 0){
        throw ("P15C requires no pre-existing OmenMon/OmenMon-Reborn/VictusFanControl.App process. Found: " +
            (($conflicts | ForEach-Object {"$($_.ProcessName):$($_.Id)"}) -join ', '))
    }
}

function Read-8C40Setpoint {
    $raw=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "P15C setpoint probe failed. Raw: $raw"}
    $line=($raw -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $m=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $m.Success){throw "P15C could not parse setpoint probe. Raw: $raw"}
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

function Wait-JsonFile([string]$Path,[int]$Seconds,[string]$Label){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $Path){
            return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
        }
        if($gui){
            $gui.Refresh()
            if($gui.HasExited){throw "P15C GUI exited before $Label."}
        }
        Start-Sleep -Milliseconds 100
    }
    throw "P15C timed out waiting for $Label."
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
        throw 'P15C durable journal is not schema-v2 generation-3 OWNED 30/30 bound to the exact GUI identity.'
    }
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function Start-P15CFailsafe {
    $p=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$failsafeScript,'-DelaySeconds','300','-LogPath',$failsafeLog) -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(3)
    while((Get-Date) -lt $deadline){
        $p.Refresh()
        if($p.HasExited){throw 'P15C independent failsafe exited before ARMED proof.'}
        if(Test-Path -LiteralPath $failsafeLog){
            $t=Get-Content -LiteralPath $failsafeLog -Raw -ErrorAction SilentlyContinue
            if($t -match 'P15C FAILSAFE ARMED:'){return $p}
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'P15C independent failsafe did not publish ARMED proof.'
}

function Test-FailsafeTakeover {
    if(-not (Test-Path -LiteralPath $failsafeLog)){return $false}
    $t=Get-Content -LiteralPath $failsafeLog -Raw
    return ($t -match 'P15C FAILSAFE TAKEOVER:' -or $t -match 'P15C FAILSAFE CONTROLLER-KILL:' -or
            $t -match 'P15C FAILSAFE SERVICE-START:' -or $t -match 'P15C FAILSAFE SERVICE-RESTART:' -or
            $t -match 'P15C FAILSAFE STARTED:' -or $t -match 'P15C FAILSAFE RECOVERED:')
}

function Assert-CausalServiceLog([int]$ControllerPid){
    if(-not (Test-Path -LiteralPath $serviceLog)){throw 'P15C watchdog service log missing.'}
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
        throw ("P15C causal log counts invalid: PREPARE={0} INTENT={1} COMMIT={2} RESTORE={3} RELEASE={4}" -f $prepare.Count,$intent.Count,$commit.Count,$restore.Count,$release.Count)
    }
    if(-not ($prepare[0] -lt $intent[0] -and $intent[0] -lt $commit[0] -and $commit[0] -lt $restore[0] -and $restore[0] -lt $release[0])){
        throw 'P15C causal ordering is not PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE.'
    }
}

function Snapshot-Service([string]$Phase){
    $svc=Get-ServiceState
    if(-not $svc){return [pscustomobject]@{phase=$Phase;installed=$false}}
    $ticks=0L
    if([int]$svc.ProcessId -gt 0){try{$ticks=Get-ProcessStartTicks ([int]$svc.ProcessId)}catch{}}
    [pscustomobject]@{
        phase=$Phase;installed=$true;state=[string]$svc.State;startMode=[string]$svc.StartMode;
        startName=[string]$svc.StartName;processId=[int]$svc.ProcessId;processStartUtcTicks=[string]$ticks;pathName=[string]$svc.PathName
    }
}

function Write-Summary([string]$Result,[string]$Failure){
    [ordered]@{
        schemaVersion=1;gate='P15C-HARNESS';result=$Result;failure=$Failure;evidenceHead=$head;
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O');guiPid=$guiPid;guiStartUtcTicks=[string]$guiStartTicks;
        watchdogPid=$watchdogPid;watchdogStartUtcTicks=[string]$watchdogStartTicks;
        initialServiceMode=$initialServiceMode;initialServicePid=$initialServicePid;initialServiceStartUtcTicks=[string]$initialServiceStartTicks;
        causalChainPass=$causalChainPass;strongRestorePass=$strongRestorePass;finalJournalAbsent=$finalJournalAbsent;
        finalFirmwareProofPass=$finalFirmwareProofPass;cleanupFirmwareProofPass=$cleanupFirmwareProofPass;
        finalServiceBaselinePass=$finalServiceBaselinePass;failsafeTakeover=$failsafeTakeover;
        guiManualExecutionAuthorized=[bool]$contract.guiManual.executionAuthorized;
        guiQualificationPhysicalExecutionAuthorized=[bool]$contract.guiManual.controllerPhysicalExecutionAuthorized;
        userFacingManualExecutionAuthorized=[bool]$contract.safetyBoundary.manualExecutionAuthorized;
        automaticExecutionAuthorized=[bool]$contract.automatic.executionAuthorized
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator
$head=Assert-RepositoryProvenance
Assert-ExactTarget
Assert-PowerSane
Assert-NoConflictingController
$serviceBefore=Snapshot-Service 'before'
$initialBaseline=Get-ValidatedServiceBaseline
$initialServiceMode=[string]$initialBaseline.Mode
$initialServicePid=[int]$initialBaseline.ProcessId
$initialServiceStartTicks=[long]$initialBaseline.ProcessStartUtcTicks
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 P15C REAL GUI MANUAL 30/30' -ForegroundColor Cyan
Write-Host 'P15C uses the real P13 Manual/Firmware buttons. Automatic and the normal user Manual gate remain closed.' -ForegroundColor Yellow

try{
    Write-Host 'Step 1: same-HEAD build and P15C static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "P15C build failed with exit=$LASTEXITCODE."}
    foreach($script in @('test-p15c-gui-manual-invariants.ps1','test-p15c-evidence-packaging.ps1','test-p15b-manual30-invariants.ps1','test-p13-control-surface-invariants.ps1')){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE -ne 0){throw "P15C regression '$script' failed with exit=$LASTEXITCODE."}
    }
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'P15C SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'P15C coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --adaptive-policy-self-test
    if($LASTEXITCODE -ne 0){throw 'P15C adaptive production/policy self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'P15C HP backend self-test failed.'}

    Write-Host 'Step 2: independent firmware/service baseline...' -ForegroundColor Cyan
    Assert-StableSetpoint 255 255 'P15C baseline FF/FF' $baselineFfPath
    $preTokenBaseline=Get-ValidatedServiceBaseline
    if([string]$preTokenBaseline.Mode -cne $initialServiceMode){
        throw "P15C service baseline mode changed before token: initial=$initialServiceMode current=$($preTokenBaseline.Mode)."
    }
    if($initialServiceMode -ceq 'Running' -and
       ([int]$preTokenBaseline.ProcessId -ne $initialServicePid -or
        [long]$preTokenBaseline.ProcessStartUtcTicks -ne $initialServiceStartTicks)){
        throw 'P15C inherited Running watchdog identity changed before the operator token.'
    }
    Assert-PowerSane

    $confirm=Read-Host "Type exactly $token to authorize one real-GUI Manual 30/30 transaction"
    if($confirm -cne $token){throw 'P15C cancelled before failsafe/GUI active boundary.'}

    Write-Host 'Step 3: arm delayed independent failsafe before GUI control can be used...' -ForegroundColor Cyan
    $failsafe=Start-P15CFailsafe

    if(Test-Path -LiteralPath $serviceLog){$logLineBoundary=@(Get-Content -LiteralPath $serviceLog).Count}

    Write-Host 'Step 4: launch the real GUI in dedicated P15C qualification mode...' -ForegroundColor Cyan
    if(-not (Test-Path -LiteralPath $appExe -PathType Leaf)){throw "P15C GUI executable missing after build: $appExe"}
    $gui=Start-P15BTrackedChild -Executable $appExe -Arguments @(
        '--8c40-p15c-gui-manual-test',
        '--8c40-p15c-test-token',$token,
        '--8c40-p15c-marker-root',$evidenceRoot,
        '--modules-dir',$modulesDir
    ) -WorkingDirectory $repoRoot
    $guiPid=$gui.Id
    $guiStartTicks=[long]$gui.StartTime.ToUniversalTime().Ticks

    $ready=Wait-JsonFile $readyPath 60 'GUI READY'
    if([int]$ready.processId -ne $guiPid -or
       [long]$ready.processStartUtcTicks -ne $guiStartTicks -or
       [string]$ready.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       [string]$ready.mode -cne 'Firmware' -or
       [string]$ready.authority -cne 'Firmware' -or
       -not [bool]$ready.manualQualificationAuthorized -or
       [bool]$ready.userFacingManualAuthorized -or
       [bool]$ready.automaticAuthorized -or
       -not [bool]$ready.safetyPermitted -or
       [int]$ready.healthySafetySamples -ne 3){
        throw 'P15C GUI READY does not prove the intended isolated Manual qualification boundary.'
    }
    $appLogPath=[string]$ready.appLogPath

    $runtimeSvc=Get-ServiceState
    Assert-ServiceCommon $runtimeSvc 'P15C GUI runtime'
    if([string]$runtimeSvc.State -cne 'Running' -or [int]$runtimeSvc.ProcessId -le 0){throw 'P15C GUI did not establish Running M4 watchdog.'}
    $watchdogPid=[int]$runtimeSvc.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
    [void](Wait-M4Ready $watchdogPid)
    if($initialServiceMode -ceq 'Running' -and
       ($watchdogPid -ne $initialServicePid -or $watchdogStartTicks -ne $initialServiceStartTicks)){
        throw 'P15C normal GUI startup changed the inherited Running watchdog identity.'
    }
    $serviceDuring=Snapshot-Service 'gui-ready'

    Write-Host ''
    Write-Host 'P15C OPERATOR ACTION:' -ForegroundColor Yellow
    Write-Host '  1. In the visible VictusFanControl GUI, open Fan Control.'
    Write-Host '  2. Click Manual.'
    Write-Host '  3. Keep the equal level at exactly 30.'
    Write-Host '  4. Click Apply equal CPU/GPU level ONCE.'
    Write-Host '  5. Do NOT click Firmware yet; wait for this console.'
    Write-Host ''

    $applied=Wait-JsonFile $manualAppliedPath 90 'real GUI Manual 30/30 application'
    if([int]$applied.processId -ne $guiPid -or
       [long]$applied.processStartUtcTicks -ne $guiStartTicks -or
       [int]$applied.equalFanLevel -ne 30 -or
       [string]$applied.action -cne 'EnterCustomAndApply' -or
       [string]$applied.authority -cne 'Custom' -or
       [int]$applied.manualModeRequests -ne 1 -or
       [int]$applied.manualApplyRequests -ne 1){
        throw 'P15C GUI marker does not prove exactly one real Manual-mode + Apply 30/30 path.'
    }

    Assert-StableSetpoint 30 30 'P15C parent OWNED 30/30' $ownedSetpointPath
    if(-not (Test-Path -LiteralPath $journalPath)){throw 'P15C GUI reports Manual apply but durable OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $guiPid $guiStartTicks
    Copy-Item -LiteralPath $journalPath -Destination $ownedJournalPath

    $svc=Get-ServiceState
    if([int]$svc.ProcessId -ne $watchdogPid -or (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'P15C watchdog process identity changed while real GUI is OWNED.'
    }
    if(Test-FailsafeTakeover){$failsafeTakeover=$true;throw 'P15C independent failsafe fired before parent ownership proof.'}

    ("P15C-PARENT-OWNED-VERIFIED|{0:O}|guiPid={1}|guiStartTicks={2}" -f (Get-Date),$guiPid,$guiStartTicks) |
        Set-Content -LiteralPath $parentVerifiedPath -Encoding ASCII

    Write-Host ''
    Write-Host 'P15C OWNERSHIP VERIFIED.' -ForegroundColor Green
    Write-Host 'Now click Firmware ONCE in the same GUI. Do not close the GUI manually.' -ForegroundColor Yellow
    Write-Host ''

    $guiResult=Wait-JsonFile $resultPath 90 'GUI Firmware restore result'
    if([string]$guiResult.result -cne 'PASS_GUI_MANUAL30_STRONG_RESTORE' -or
       [int]$guiResult.processId -ne $guiPid -or
       [long]$guiResult.processStartUtcTicks -ne $guiStartTicks -or
       [int]$guiResult.manualModeRequests -ne 1 -or
       [int]$guiResult.manualApplyRequests -ne 1 -or
       [int]$guiResult.firmwareModeRequests -ne 1 -or
       [int]$guiResult.automaticModeRequests -ne 0 -or
       -not [bool]$guiResult.localFirmwareAckVerified -or
       -not [bool]$guiResult.watchdogLeaseRequired -or
       -not [bool]$guiResult.watchdogReleaseVerified -or
       [bool]$guiResult.journalPresentAfterRestore -or
       [string]$guiResult.finalAuthority -cne 'Firmware' -or
       [string]$guiResult.finalMode -cne 'Firmware'){
        throw 'P15C GUI result does not prove the exact one-shot Manual->Firmware strong-restore path.'
    }

    $guiExit=Wait-P15BTrackedChildExitCode -Process $gui -Seconds 30
    if($guiExit -ne 0){throw "P15C GUI exited with code $guiExit after PASS marker."}

    if(Test-Path -LiteralPath $journalPath){throw 'P15C GUI strong restore returned but durable journal remains.'}
    $finalJournalAbsent=$true

    Assert-CausalServiceLog $guiPid
    $causalChainPass=$true

    Assert-StableSetpoint 255 255 'P15C independent final FF/FF' $finalFfPath
    $finalFirmwareProofPass=$true

    $finalSvc=Get-ServiceState
    Assert-ServiceCommon $finalSvc 'P15C final normal-GUI baseline'
    if([string]$finalSvc.State -cne 'Running' -or [int]$finalSvc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'P15C final watchdog service is not the same Running/Ready process used during the GUI transaction.'
    }
    [void](Wait-M4Ready $watchdogPid)
    $serviceAfter=Snapshot-Service 'after-gui-exit'
    @($serviceBefore,$serviceDuring,$serviceAfter) | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $serviceSnapshotsPath -Encoding UTF8
    $finalServiceBaselinePass=$true

    if(Test-FailsafeTakeover){$failsafeTakeover=$true;throw 'P15C independent failsafe took over; safe but invalid.'}
    $strongRestorePass=$true

    if($appLogPath -and (Test-Path -LiteralPath $appLogPath -PathType Leaf)){
        Copy-Item -LiteralPath $appLogPath -Destination $appLogEvidencePath
    }

    $pass=$true
    Write-Host 'PASS: P15C real GUI Manual 30/30 -> Firmware strong restore completed.' -ForegroundColor Green
}
catch{
    $failure=$_.Exception.Message
    Write-Host ("P15C FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally{
    if($gui){
        try{
            $gui.Refresh()
            if(-not $gui.HasExited){
                Write-Warning 'P15C cleanup is terminating only the exact tracked GUI; the qualified watchdog owns any retained durable lease recovery.'
                $gui.Kill()
                try{[void]$gui.WaitForExit(5000)}catch{}
            }
        }catch{}
    }

    $cleanupSafe=$false
    try{
        if(Test-Path -LiteralPath $journalPath){
            if(-not (Wait-JournalGone 25)){
                throw 'P15C cleanup timed out waiting for watchdog recovery; delayed failsafe remains armed and evidence is preserved.'
            }
        }
        Assert-StableSetpoint 255 255 'P15C cleanup FF/FF' $cleanupFfPath
        $cleanupFirmwareProofPass=$true
        $cleanupSafe=$true
    }catch{
        Write-Warning ("P15C cleanup firmware proof failed: {0}" -f $_.Exception.Message)
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

    if(-not (Test-Path -LiteralPath $serviceSnapshotsPath)){
        try{
            $serviceAfter=Snapshot-Service 'cleanup-final'
            @($serviceBefore,$serviceDuring,$serviceAfter) | ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath $serviceSnapshotsPath -Encoding UTF8
        }catch{}
    }

    if($appLogPath -and (Test-Path -LiteralPath $appLogPath -PathType Leaf) -and
       -not (Test-Path -LiteralPath $appLogEvidencePath)){
        try{Copy-Item -LiteralPath $appLogPath -Destination $appLogEvidencePath}catch{}
    }

    try{Write-Summary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure}catch{}

    try{
        $pkg=& $packager -EvidenceRoot $evidenceRoot -RepositoryRoot $repoRoot
        $packagePath=[string]$pkg.ZipPath
        $packageSha256=[string]$pkg.ZipSha256
    }catch{
        Write-Warning ("P15C evidence packaging failed: {0}" -f $_.Exception.Message)
        if($pass){$pass=$false;$failure='P15C physical sequence passed but evidence packaging failed.'}
    }

    if($gui){try{$gui.Dispose()}catch{}}
}

if($pass){
    Write-Host ("P15C GUI-MANUAL/STRONG-RESTORE PASS. Evidence ZIP: {0}" -f $packagePath) -ForegroundColor Green
    Write-Host ("SHA-256: {0}" -f $packageSha256) -ForegroundColor Green
    exit 0
}

Write-Error ("P15C FAIL_CLOSED. Evidence preserved at {0}. {1}" -f $evidenceRoot,$failure)
exit 1
