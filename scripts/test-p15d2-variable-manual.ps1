$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$contractPath=Join-Path $repoRoot 'release\p15-target-checkpoint.json'
$contract=Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER. Keep before Administrator checks,
# service/process control, PawnIO/EC probing, GUI launch or recovery work.
if([string]$contract.status -notin @(
    'P15D2_VARIABLE_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI',
    'P15D2_VARIABLE_MANUAL_REAUTHORIZED_AFTER_EC_TRANSIENT_AWAITING_SAME_HEAD_CI'
)){
    throw 'P15D2 PHYSICAL BLOCKED: contract is not in a dedicated authorized state.'
}
if(-not [bool]$contract.startupNoWrite.physicalPassed -or
   -not [bool]$contract.startupNoWrite.evidenceClosed -or
   [bool]$contract.startupNoWrite.executionAuthorized -or
   -not [bool]$contract.manual30.physicalPassed -or
   -not [bool]$contract.manual30.evidenceClosed -or
   [bool]$contract.manual30.executionAuthorized -or
   [bool]$contract.manual30.controllerPhysicalExecutionAuthorized -or
   -not [bool]$contract.guiManual.physicalPassed -or
   -not [bool]$contract.guiManual.evidenceClosed -or
   [bool]$contract.guiManual.executionAuthorized -or
   [bool]$contract.guiManual.controllerPhysicalExecutionAuthorized -or
   -not [bool]$contract.guiLifecycleTrayExit.physicalPassed -or
   -not [bool]$contract.guiLifecycleTrayExit.evidenceClosed -or
   [bool]$contract.guiLifecycleTrayExit.executionAuthorized -or
   [bool]$contract.guiLifecycleTrayExit.controllerPhysicalExecutionAuthorized){
    throw 'P15D2 PHYSICAL BLOCKED: P15A/P15B/P15C/P15D1 must be physically closed and re-blocked.'
}
if(-not [bool]$contract.guiManualVariableLevel.executionAuthorized -or
   -not [bool]$contract.guiManualVariableLevel.controllerPhysicalExecutionAuthorized){
    throw 'P15D2 PHYSICAL BLOCKED: dedicated parent/GUI qualification authorization is closed.'
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
    throw 'P15D2 PHYSICAL BLOCKED: user/default/automatic/candidate/consumed qualification boundary is open.'
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
$token='8C40-P15D2-MANUAL30-40-40-30'
$packager=Join-Path $PSScriptRoot 'package-p15d2-evidence.ps1'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-p15d2-service-failsafe-8c40.ps1'
. (Join-Path $PSScriptRoot 'p15b-tracked-child.ps1')
. (Join-Path $PSScriptRoot 'p15b-service-baseline.ps1')

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\p15d2-variable-manual_{0}" -f $stamp)
$readyPath=Join-Path $evidenceRoot 'p15d2-gui-ready.json'
$step1Path=Join-Path $evidenceRoot 'p15d2-step1-apply30.json'
$parent30Path=Join-Path $evidenceRoot 'p15d2-parent-30-verified.txt'
$step2Path=Join-Path $evidenceRoot 'p15d2-step2-apply40.json'
$parent40Path=Join-Path $evidenceRoot 'p15d2-parent-40-verified.txt'
$step3Path=Join-Path $evidenceRoot 'p15d2-step3-hold40.json'
$parentHold40Path=Join-Path $evidenceRoot 'p15d2-parent-hold40-verified.txt'
$step4Path=Join-Path $evidenceRoot 'p15d2-step4-return30.json'
$parentReturn30Path=Join-Path $evidenceRoot 'p15d2-parent-return30-verified.txt'
$firmwareRestoredPath=Join-Path $evidenceRoot 'p15d2-firmware-restored.json'
$eventsPath=Join-Path $evidenceRoot 'p15d2-gui-events.jsonl'
$summaryPath=Join-Path $evidenceRoot 'p15d2-harness-summary.json'
$baselineFfPath=Join-Path $evidenceRoot 'p15d2-baseline-ff.json'
$setpoint30InitialPath=Join-Path $evidenceRoot 'p15d2-setpoint-30-initial.json'
$journal30InitialPath=Join-Path $evidenceRoot 'p15d2-journal-30-initial.json'
$setpoint40Path=Join-Path $evidenceRoot 'p15d2-setpoint-40.json'
$journal40Path=Join-Path $evidenceRoot 'p15d2-journal-40.json'
$setpoint40HoldPath=Join-Path $evidenceRoot 'p15d2-setpoint-40-hold.json'
$journal40HoldPath=Join-Path $evidenceRoot 'p15d2-journal-40-hold.json'
$setpoint30ReturnPath=Join-Path $evidenceRoot 'p15d2-setpoint-30-return.json'
$journal30ReturnPath=Join-Path $evidenceRoot 'p15d2-journal-30-return.json'
$finalFfPath=Join-Path $evidenceRoot 'p15d2-final-ff.json'
$cleanupFfPath=Join-Path $evidenceRoot 'p15d2-cleanup-ff.json'
$serviceSnapshotsPath=Join-Path $evidenceRoot 'p15d2-service-snapshots.json'
$serviceSegmentPath=Join-Path $evidenceRoot 'p15d2-watchdog-log-segment.txt'
$failsafeLog=Join-Path $evidenceRoot 'p15d2-failsafe.log'
$appLogEvidencePath=Join-Path $evidenceRoot 'p15d2-app.log'

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
$ownedSessionId=$null
$logLineBoundary=0
$holdLogBoundary=0
$causalChainPass=$false
$duplicateNoRetransmitPass=$false
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
        throw 'P15D2 must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch){
        throw "P15D2 requires branch '$expectedBranch'; observed '$branch'."
    }
    $local=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $local -notmatch '^[0-9a-f]{40}$' -or $local -cne $upstream){
        throw "P15D2 requires local HEAD == upstream HEAD. local=$local upstream=$upstream"
    }
    $status=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'P15D2 could not inspect git status.'}
    $blocking=@($status -split "[\r\n]+" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
    })
    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'P15D2 requires committed source/config state; only preserved untracked logs/evidence is allowed.'
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
        throw 'P15D2 exact-target fingerprint mismatch.'
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
    throw 'P15D2 timed out waiting for exact M4 Ready state.'
}

function Get-ValidatedServiceBaseline {
    $svc=Get-ServiceState
    Assert-ServiceCommon $svc 'P15D2 baseline'

    $journalPresent=Test-Path -LiteralPath $journalPath
    $readyVerified=$false
    $servicePid=[int]$svc.ProcessId
    $ticks=0L

    if([string]$svc.State -ceq 'Running' -and $servicePid -gt 0 -and -not $journalPresent){
        $ticks=Get-ProcessStartTicks $servicePid
        [void](Wait-M4Ready $servicePid)

        $confirmed=Get-ServiceState
        Assert-ServiceCommon $confirmed 'P15D2 inherited-running baseline confirmation'
        if([string]$confirmed.State -cne 'Running' -or
           [int]$confirmed.ProcessId -ne $servicePid -or
           (Get-ProcessStartTicks $servicePid) -ne $ticks){
            throw 'P15D2 inherited Running watchdog identity changed during Ready verification.'
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
    if($p.PowerLineStatus -cne 'Online'){throw "P15D2 requires AC online; observed '$($p.PowerLineStatus)'."}
    if($null -eq $p.BatteryPercent -or [double]$p.BatteryPercent -lt 20){
        throw "P15D2 requires readable battery >=20%; observed '$($p.BatteryPercent)'."
    }
}

function Assert-NoConflictingController {
    $conflicts=@(Get-Process OmenMon,OmenMon-Reborn,VictusFanControl.App -ErrorAction SilentlyContinue)
    if($conflicts.Count -gt 0){
        throw ("P15D2 requires no pre-existing OmenMon/OmenMon-Reborn/VictusFanControl.App process. Found: " +
            (($conflicts | ForEach-Object {"$($_.ProcessName):$($_.Id)"}) -join ', '))
    }
}

function Read-8C40Setpoint {
    $raw=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "P15D2 setpoint probe failed. Raw: $raw"}
    $line=($raw -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $m=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $m.Success){throw "P15D2 could not parse setpoint probe. Raw: $raw"}
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
            if($gui.HasExited){throw "P15D2 GUI exited before $Label."}
        }
        Start-Sleep -Milliseconds 100
    }
    throw "P15D2 timed out waiting for $Label."
}

function Test-OwnedPhase($phase){
    if($null -eq $phase){return $false}
    if($phase -is [string]){return ($phase -ceq 'Owned' -or $phase -ceq '2')}
    try{return ([int]$phase -eq 2)}catch{return $false}
}


function Assert-OwnedJournal($journal,[int]$ExpectedPid,[long]$ExpectedTicks,[int]$ExpectedLevel,[long]$ExpectedGeneration,[string]$ExpectedSessionId){
    if([int]$journal.SchemaVersion -ne 2 -or
       $journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Test-OwnedPhase $journal.Phase) -or
       [long]$journal.Generation -ne $ExpectedGeneration -or
       [int]$journal.Controller.ProcessId -ne $ExpectedPid -or
       [long]$journal.Controller.ProcessStartUtcTicks -ne $ExpectedTicks -or
       [int]$journal.Owned.Cpu -ne $ExpectedLevel -or
       [int]$journal.Owned.Gpu -ne $ExpectedLevel){
        throw "P15D2 durable journal is not schema-v2 generation $ExpectedGeneration OWNED $ExpectedLevel/$ExpectedLevel bound to the exact GUI identity."
    }
    $session=[string]$journal.SessionId
    if([string]::IsNullOrWhiteSpace($session)){throw 'P15D2 durable journal SessionId is missing.'}
    if(-not [string]::IsNullOrWhiteSpace($ExpectedSessionId) -and $session -cne $ExpectedSessionId){
        throw "P15D2 ownership session changed: expected=$ExpectedSessionId observed=$session"
    }
    return $session
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function Start-P15D2Failsafe {
    $p=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$failsafeScript,'-DelaySeconds','300','-LogPath',$failsafeLog) -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(3)
    while((Get-Date) -lt $deadline){
        $p.Refresh()
        if($p.HasExited){throw 'P15D2 independent failsafe exited before ARMED proof.'}
        if(Test-Path -LiteralPath $failsafeLog){
            $t=Get-Content -LiteralPath $failsafeLog -Raw -ErrorAction SilentlyContinue
            if($t -match 'P15D2 FAILSAFE ARMED:'){return $p}
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'P15D2 independent failsafe did not publish ARMED proof.'
}

function Test-FailsafeTakeover {
    if(-not (Test-Path -LiteralPath $failsafeLog)){return $false}
    $t=Get-Content -LiteralPath $failsafeLog -Raw
    return ($t -match 'P15D2 FAILSAFE TAKEOVER:' -or $t -match 'P15D2 FAILSAFE CONTROLLER-KILL:' -or
            $t -match 'P15D2 FAILSAFE SERVICE-START:' -or $t -match 'P15D2 FAILSAFE SERVICE-RESTART:' -or
            $t -match 'P15D2 FAILSAFE STARTED:' -or $t -match 'P15D2 FAILSAFE RECOVERED:')
}


function Assert-NoRetransmitAfterBoundary([int]$ControllerPid,[int]$Boundary){
    if(-not (Test-Path -LiteralPath $serviceLog)){throw 'P15D2 watchdog service log missing for duplicate-target proof.'}
    $all=@(Get-Content -LiteralPath $serviceLog)
    $segment=@($all | Select-Object -Skip $Boundary)
    $tag="controller PID=$ControllerPid"
    $writes=@($segment | Where-Object {
        ([string]$_).Contains($tag) -and
        (([string]$_ -match 'WATCHDOG WRITE_INTENT ACK') -or ([string]$_ -match 'WATCHDOG COMMIT ACK'))
    })
    if($writes.Count -ne 0){
        $writes | ForEach-Object {Write-Host $_}
        throw 'P15D2 duplicate 40/40 caused a watchdog WRITE_INTENT/COMMIT retransmission.'
    }
}

function Assert-CausalServiceLog([int]$ControllerPid){
    if(-not (Test-Path -LiteralPath $serviceLog)){throw 'P15D2 watchdog service log missing.'}
    $all=@(Get-Content -LiteralPath $serviceLog)
    $segment=@($all | Select-Object -Skip $logLineBoundary)
    $segment | Set-Content -LiteralPath $serviceSegmentPath -Encoding UTF8
    $tag="controller PID=$ControllerPid"
    $prepare=@();$intent30=@();$intent40=@();$commit30=@();$commit40=@();$restore=@();$release=@()
    for($i=0;$i -lt $segment.Count;$i++){
        $line=[string]$segment[$i]
        if(-not $line.Contains($tag)){continue}
        if($line -match 'WATCHDOG PREPARE ACK'){$prepare+=@($i)}
        if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line -match 'target=30/30'){$intent30+=@($i)}
        if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line -match 'target=40/40'){$intent40+=@($i)}
        if($line -match 'WATCHDOG COMMIT ACK' -and $line -match 'target=30/30'){$commit30+=@($i)}
        if($line -match 'WATCHDOG COMMIT ACK' -and $line -match 'target=40/40'){$commit40+=@($i)}
        if($line -match 'WATCHDOG RESTORE_BEGIN ACK'){$restore+=@($i)}
        if($line -match 'WATCHDOG RELEASE ACK'){$release+=@($i)}
    }
    if($prepare.Count -ne 1 -or $intent30.Count -ne 2 -or $intent40.Count -ne 1 -or
       $commit30.Count -ne 2 -or $commit40.Count -ne 1 -or $restore.Count -ne 1 -or $release.Count -ne 1){
        throw ("P15D2 causal counts invalid: PREPARE={0} I30={1} I40={2} C30={3} C40={4} RESTORE={5} RELEASE={6}" -f
            $prepare.Count,$intent30.Count,$intent40.Count,$commit30.Count,$commit40.Count,$restore.Count,$release.Count)
    }
    if(-not ($prepare[0] -lt $intent30[0] -and $intent30[0] -lt $commit30[0] -and
             $commit30[0] -lt $intent40[0] -and $intent40[0] -lt $commit40[0] -and
             $commit40[0] -lt $intent30[1] -and $intent30[1] -lt $commit30[1] -and
             $commit30[1] -lt $restore[0] -and $restore[0] -lt $release[0])){
        throw 'P15D2 causal ordering is not PREPARE < 30 write/commit < 40 write/commit < return-30 write/commit < RESTORE_BEGIN < RELEASE.'
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
        schemaVersion=1;gate='P15D2-HARNESS';result=$Result;failure=$Failure;evidenceHead=$head;
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O');guiPid=$guiPid;guiStartUtcTicks=[string]$guiStartTicks;
        watchdogPid=$watchdogPid;watchdogStartUtcTicks=[string]$watchdogStartTicks;ownedSessionId=$ownedSessionId;
        initialServiceMode=$initialServiceMode;initialServicePid=$initialServicePid;initialServiceStartUtcTicks=[string]$initialServiceStartTicks;
        causalChainPass=$causalChainPass;duplicateNoRetransmitPass=$duplicateNoRetransmitPass;
        strongRestorePass=$strongRestorePass;finalJournalAbsent=$finalJournalAbsent;
        finalFirmwareProofPass=$finalFirmwareProofPass;cleanupFirmwareProofPass=$cleanupFirmwareProofPass;
        finalServiceBaselinePass=$finalServiceBaselinePass;failsafeTakeover=$failsafeTakeover;
        p15d2ExecutionAuthorized=[bool]$contract.guiManualVariableLevel.executionAuthorized;
        p15d2ControllerPhysicalExecutionAuthorized=[bool]$contract.guiManualVariableLevel.controllerPhysicalExecutionAuthorized;
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

Write-Host 'VictusFanControl - HP 8C40 P15D2 REAL GUI VARIABLE MANUAL' -ForegroundColor Cyan
Write-Host 'P15D2 sequence: Manual -> 30/30 -> 40/40 -> duplicate 40/40 NO-RETRANSMIT -> 30/30 -> Firmware. Automatic is forbidden.' -ForegroundColor Yellow

try{
    Write-Host 'Step 1: same-HEAD build and P15D2 static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "P15D2 build failed with exit=$LASTEXITCODE."}
    foreach($script in @('test-p15d2-variable-manual-invariants.ps1','test-p15d2-evidence-packaging.ps1','test-p15d1-tray-exit-invariants.ps1','test-p15c-gui-manual-invariants.ps1','test-p15b-manual30-invariants.ps1','test-p13-control-surface-invariants.ps1')){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE -ne 0){throw "P15D2 regression '$script' failed with exit=$LASTEXITCODE."}
    }
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'P15D2 SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'P15D2 coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --adaptive-policy-self-test
    if($LASTEXITCODE -ne 0){throw 'P15D2 adaptive production/policy self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'P15D2 HP backend self-test failed.'}

    Write-Host 'Step 2: independent firmware/service baseline...' -ForegroundColor Cyan
    Assert-StableSetpoint 255 255 'P15D2 baseline FF/FF' $baselineFfPath
    $preTokenBaseline=Get-ValidatedServiceBaseline
    if([string]$preTokenBaseline.Mode -cne $initialServiceMode){
        throw "P15D2 service baseline mode changed before token: initial=$initialServiceMode current=$($preTokenBaseline.Mode)."
    }
    if($initialServiceMode -ceq 'Running' -and
       ([int]$preTokenBaseline.ProcessId -ne $initialServicePid -or
        [long]$preTokenBaseline.ProcessStartUtcTicks -ne $initialServiceStartTicks)){
        throw 'P15D2 inherited Running watchdog identity changed before the operator token.'
    }
    Assert-PowerSane

    $confirm=Read-Host "Type exactly $token to authorize one bounded P15D2 variable-Manual session"
    if($confirm -cne $token){throw 'P15D2 cancelled before failsafe/GUI active boundary.'}

    Write-Host 'Step 3: arm delayed independent failsafe before GUI control can be used...' -ForegroundColor Cyan
    $failsafe=Start-P15D2Failsafe
    if(Test-Path -LiteralPath $serviceLog){$logLineBoundary=@(Get-Content -LiteralPath $serviceLog).Count}

    Write-Host 'Step 4: launch the real GUI in dedicated P15D2 qualification mode...' -ForegroundColor Cyan
    if(-not (Test-Path -LiteralPath $appExe -PathType Leaf)){throw "P15D2 GUI executable missing after build: $appExe"}
    $gui=Start-P15BTrackedChild -Executable $appExe -Arguments @(
        '--8c40-p15d2-variable-manual-test',
        '--8c40-p15d2-test-token',$token,
        '--8c40-p15d2-marker-root',$evidenceRoot,
        '--modules-dir',$modulesDir
    ) -WorkingDirectory $repoRoot
    $guiPid=$gui.Id
    $guiStartTicks=[long]$gui.StartTime.ToUniversalTime().Ticks

    $ready=Wait-JsonFile $readyPath 60 'GUI READY'
    if([int]$ready.processId -ne $guiPid -or
       [long]$ready.processStartUtcTicks -ne $guiStartTicks -or
       [string]$ready.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       [string]$ready.mode -cne 'Firmware' -or [string]$ready.authority -cne 'Firmware' -or
       -not [bool]$ready.manualQualificationAuthorized -or [bool]$ready.userFacingManualAuthorized -or
       [bool]$ready.automaticAuthorized -or -not [bool]$ready.safetyPermitted -or [int]$ready.healthySafetySamples -ne 3){
        throw 'P15D2 GUI READY does not prove the intended isolated Manual qualification boundary.'
    }
    $appLogPath=[string]$ready.appLogPath

    $runtimeSvc=Get-ServiceState
    Assert-ServiceCommon $runtimeSvc 'P15D2 GUI runtime'
    if([string]$runtimeSvc.State -cne 'Running' -or [int]$runtimeSvc.ProcessId -le 0){throw 'P15D2 GUI did not establish Running M4 watchdog.'}
    $watchdogPid=[int]$runtimeSvc.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
    [void](Wait-M4Ready $watchdogPid)
    if($initialServiceMode -ceq 'Running' -and ($watchdogPid -ne $initialServicePid -or $watchdogStartTicks -ne $initialServiceStartTicks)){
        throw 'P15D2 normal GUI startup changed the inherited Running watchdog identity.'
    }
    $serviceDuring=Snapshot-Service 'gui-ready'

    Write-Host ''
    Write-Host 'P15D2 OPERATOR ACTION #1:' -ForegroundColor Yellow
    Write-Host '  Open Fan Control, click Manual ONCE, set equal level 30, click Apply ONCE.'
    Write-Host '  Do not click anything else until the console advances.'
    $step1=Wait-JsonFile $step1Path 90 'P15D2 real GUI Apply 30/30'
    if([int]$step1.processId -ne $guiPid -or [long]$step1.processStartUtcTicks -ne $guiStartTicks -or
       [int]$step1.equalFanLevel -ne 30 -or [string]$step1.action -cne 'EnterCustomAndApply' -or
       [string]$step1.authority -cne 'Custom' -or [int]$step1.manualModeRequests -ne 1 -or [int]$step1.manualApplyRequests -ne 1){
        throw 'P15D2 step1 marker mismatch.'
    }
    Assert-StableSetpoint 30 30 'P15D2 parent initial OWNED 30/30' $setpoint30InitialPath
    if(-not (Test-Path -LiteralPath $journalPath)){throw 'P15D2 initial 30/30 journal missing.'}
    $j30=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    $ownedSessionId=Assert-OwnedJournal $j30 $guiPid $guiStartTicks 30 3 $null
    Copy-Item -LiteralPath $journalPath -Destination $journal30InitialPath
    ("P15D2-PARENT-30-VERIFIED|session={0}|{1:O}|guiPid={2}|guiStartTicks={3}" -f $ownedSessionId,(Get-Date),$guiPid,$guiStartTicks) |
        Set-Content -LiteralPath $parent30Path -Encoding ASCII

    Write-Host 'P15D2 OPERATOR ACTION #2: set equal level 40 and click Apply ONCE.' -ForegroundColor Yellow
    $step2=Wait-JsonFile $step2Path 90 'P15D2 real GUI changed Apply 40/40'
    if([int]$step2.equalFanLevel -ne 40 -or [string]$step2.action -cne 'ApplyChangedLevel' -or [int]$step2.manualApplyRequests -ne 2){
        throw 'P15D2 step2 marker mismatch.'
    }
    Assert-StableSetpoint 40 40 'P15D2 parent changed OWNED 40/40' $setpoint40Path
    $j40=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    [void](Assert-OwnedJournal $j40 $guiPid $guiStartTicks 40 5 $ownedSessionId)
    Copy-Item -LiteralPath $journalPath -Destination $journal40Path
    ("P15D2-PARENT-40-VERIFIED|session={0}|{1:O}|guiPid={2}|guiStartTicks={3}" -f $ownedSessionId,(Get-Date),$guiPid,$guiStartTicks) |
        Set-Content -LiteralPath $parent40Path -Encoding ASCII

    if(Test-Path -LiteralPath $serviceLog){$holdLogBoundary=@(Get-Content -LiteralPath $serviceLog).Count}else{throw 'P15D2 service log missing before duplicate-target proof.'}
    Write-Host 'P15D2 OPERATOR ACTION #3: leave level at 40 and click Apply ONCE again. This MUST be no-retransmit.' -ForegroundColor Yellow
    $step3=Wait-JsonFile $step3Path 90 'P15D2 duplicate 40/40 HoldCustom'
    if([int]$step3.equalFanLevel -ne 40 -or [string]$step3.action -cne 'HoldCustom' -or
       [string]$step3.result -cne 'HOLD_NO_RETRANSMIT' -or [int]$step3.manualApplyRequests -ne 3){
        throw 'P15D2 duplicate-40 marker mismatch.'
    }
    Assert-NoRetransmitAfterBoundary $guiPid $holdLogBoundary
    Assert-StableSetpoint 40 40 'P15D2 parent duplicate 40/40 hold' $setpoint40HoldPath
    $j40Hold=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    [void](Assert-OwnedJournal $j40Hold $guiPid $guiStartTicks 40 5 $ownedSessionId)
    Copy-Item -LiteralPath $journalPath -Destination $journal40HoldPath
    $duplicateNoRetransmitPass=$true
    ("P15D2-PARENT-HOLD40-VERIFIED|session={0}|{1:O}|guiPid={2}|guiStartTicks={3}" -f $ownedSessionId,(Get-Date),$guiPid,$guiStartTicks) |
        Set-Content -LiteralPath $parentHold40Path -Encoding ASCII

    Write-Host 'P15D2 OPERATOR ACTION #4: set equal level back to 30 and click Apply ONCE.' -ForegroundColor Yellow
    $step4=Wait-JsonFile $step4Path 90 'P15D2 real GUI return Apply 30/30'
    if([int]$step4.equalFanLevel -ne 30 -or [string]$step4.action -cne 'ApplyChangedLevel' -or [int]$step4.manualApplyRequests -ne 4){
        throw 'P15D2 step4 marker mismatch.'
    }
    Assert-StableSetpoint 30 30 'P15D2 parent return OWNED 30/30' $setpoint30ReturnPath
    $j30Return=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    [void](Assert-OwnedJournal $j30Return $guiPid $guiStartTicks 30 7 $ownedSessionId)
    Copy-Item -LiteralPath $journalPath -Destination $journal30ReturnPath
    ("P15D2-PARENT-RETURN30-VERIFIED|session={0}|{1:O}|guiPid={2}|guiStartTicks={3}" -f $ownedSessionId,(Get-Date),$guiPid,$guiStartTicks) |
        Set-Content -LiteralPath $parentReturn30Path -Encoding ASCII

    Write-Host 'P15D2 OPERATOR ACTION #5: click Firmware ONCE. Do NOT click Automatic.' -ForegroundColor Yellow
    $restored=Wait-JsonFile $firmwareRestoredPath 90 'P15D2 real GUI Firmware strong restore'
    if([int]$restored.processId -ne $guiPid -or [long]$restored.processStartUtcTicks -ne $guiStartTicks -or
       [string]$restored.result -cne 'FIRMWARE_RESTORED' -or [int]$restored.manualModeRequests -ne 1 -or
       [int]$restored.manualApplyRequests -ne 4 -or [int]$restored.firmwareModeRequests -ne 1 -or
       [int]$restored.automaticModeRequests -ne 0 -or [string]$restored.action -cne 'RestoreFirmware' -or
       [string]$restored.finalMode -cne 'Firmware' -or [string]$restored.finalAuthority -cne 'Firmware' -or
       -not [bool]$restored.localFirmwareAckVerified -or -not [bool]$restored.watchdogLeaseRequired -or
       -not [bool]$restored.watchdogReleaseVerified -or [bool]$restored.journalPresentAfterRestore){
        throw 'P15D2 Firmware marker does not prove the required strong restore.'
    }

    if(Test-Path -LiteralPath $journalPath){throw 'P15D2 strong restore returned but durable journal remains.'}

    Write-Host ''
    Write-Host 'P15D2 FAN-CONTROL RESTORE PROOF COMPLETE. Right-click the VictusFanControl tray icon and choose Exit ONCE for a clean GUI exit.' -ForegroundColor Green
    $guiExit=Wait-P15BTrackedChildExitCode -Process $gui -Seconds 90
    if($guiExit -ne 0){throw "P15D2 GUI exited with code $guiExit after qualified Firmware restore."}

    if(Test-Path -LiteralPath $journalPath){throw 'P15D2 clean GUI exit recreated or retained a durable journal after Firmware restore.'}
    $finalJournalAbsent=$true
    Assert-CausalServiceLog $guiPid
    $causalChainPass=$true
    Assert-StableSetpoint 255 255 'P15D2 independent final FF/FF after clean GUI exit' $finalFfPath
    $finalFirmwareProofPass=$true

    $finalSvc=Get-ServiceState
    Assert-ServiceCommon $finalSvc 'P15D2 final post-exit Firmware baseline'
    if([string]$finalSvc.State -cne 'Running' -or [int]$finalSvc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'P15D2 final watchdog service is not the same Running/Ready process.'
    }
    [void](Wait-M4Ready $watchdogPid)
    $serviceAfter=Snapshot-Service 'after-clean-gui-exit'
    @($serviceBefore,$serviceDuring,$serviceAfter) | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $serviceSnapshotsPath -Encoding UTF8
    $finalServiceBaselinePass=$true
    if(Test-FailsafeTakeover){$failsafeTakeover=$true;throw 'P15D2 independent failsafe took over; safe but invalid.'}
    $strongRestorePass=$true

    if($appLogPath -and (Test-Path -LiteralPath $appLogPath -PathType Leaf)){
        Copy-Item -LiteralPath $appLogPath -Destination $appLogEvidencePath
    }

    $pass=$true
    Write-Host 'PASS: P15D2 real GUI 30 -> 40 -> duplicate 40 no-retransmit -> 30 -> Firmware strong restore completed.' -ForegroundColor Green
}
catch{
    $failure=$_.Exception.Message
    Write-Host ("P15D2 FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally{
    if($gui){
        try{
            $gui.Refresh()
            if(-not $gui.HasExited){
                Write-Warning 'P15D2 cleanup is terminating only the exact tracked GUI; the qualified watchdog owns any retained durable lease recovery.'
                $gui.Kill()
                try{[void]$gui.WaitForExit(5000)}catch{}
            }
        }catch{}
    }

    $cleanupSafe=$false
    try{
        if(Test-Path -LiteralPath $journalPath){
            if(-not (Wait-JournalGone 25)){
                throw 'P15D2 cleanup timed out waiting for watchdog recovery; delayed failsafe remains armed and evidence is preserved.'
            }
        }
        Assert-StableSetpoint 255 255 'P15D2 cleanup FF/FF' $cleanupFfPath
        $cleanupFirmwareProofPass=$true
        $cleanupSafe=$true
    }catch{
        Write-Warning ("P15D2 cleanup firmware proof failed: {0}" -f $_.Exception.Message)
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
        Write-Warning ("P15D2 evidence packaging failed: {0}" -f $_.Exception.Message)
        if($pass){$pass=$false;$failure='P15D2 physical sequence passed but evidence packaging failed.'}
    }

    if($gui){try{$gui.Dispose()}catch{}}
}

if($pass){
    Write-Host ("P15D2 VARIABLE-MANUAL/STRONG-RESTORE PASS. Evidence ZIP: {0}" -f $packagePath) -ForegroundColor Green
    Write-Host ("SHA-256: {0}" -f $packageSha256) -ForegroundColor Green
    exit 0
}

Write-Error ("P15D2 FAIL_CLOSED. Evidence preserved at {0}. {1}" -f $evidenceRoot,$failure)
exit 1
