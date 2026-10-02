$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$contract=Get-Content -LiteralPath (Join-Path $repoRoot 'release\p16-target-checkpoint.json') -Raw | ConvertFrom-Json
$p15=Get-Content -LiteralPath (Join-Path $repoRoot 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER. Keep before Administrator checks,
# service/process control, PawnIO/EC probing, GUI launch or recovery work.
if([string]$contract.status -cne 'P16B_NORMAL_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI'){
    throw 'P16 PHYSICAL BLOCKED: contract is not in the dedicated P16B authorized state.'
}
if(-not [bool]$contract.normalManual.executionAuthorized -or
   -not [bool]$contract.normalManual.controllerPhysicalExecutionAuthorized -or
   -not [bool]$contract.normalManual.physicalGatesOpened){
    throw 'P16 PHYSICAL BLOCKED: dedicated P16 parent/source gates are closed.'
}
if([string]$p15.status -cne 'P15D2_VARIABLE_MANUAL_PHYSICAL_PASS_FORMALLY_CLOSED' -or
   -not [bool]$p15.guiManualVariableLevel.physicalPassed -or
   -not [bool]$p15.guiManualVariableLevel.evidenceClosed -or
   [bool]$p15.guiManualVariableLevel.executionAuthorized -or
   [bool]$p15.guiManualVariableLevel.controllerPhysicalExecutionAuthorized){
    throw 'P16 PHYSICAL BLOCKED: P15D2 must remain physically closed and re-blocked.'
}
if([bool]$contract.safetyBoundary.controlEnabledByDefault -or
   [bool]$contract.safetyBoundary.automaticPolicyEnabled -or
   [bool]$contract.safetyBoundary.permanentUserManualExecutionAuthorized -or
   [bool]$contract.safetyBoundary.automaticExecutionAuthorized -or
   [bool]$contract.safetyBoundary.candidateCurvePhysicallyValidated -or
   [bool]$contract.safetyBoundary.candidateCurveAuthorizedForProduction -or
   [bool]$contract.safetyBoundary.m9cQualificationConstructionAuthorized -or
   [bool]$contract.safetyBoundary.m9dQualificationConstructionAuthorized){
    throw 'P16 PHYSICAL BLOCKED: permanent/default/Automatic/Candidate/consumed-gate boundary is open.'
}

$expectedBranch='feature/victus-8c40-p16-normal-manual'
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
$token='8C40-P16-NORMAL-MANUAL-30-40-30'
$packager=Join-Path $PSScriptRoot 'package-p16-evidence.ps1'
# Reuse the already-qualified exact-target 30/40 delayed recovery implementation.
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-p15d2-service-failsafe-8c40.ps1'
. (Join-Path $PSScriptRoot 'p15b-tracked-child.ps1')
. (Join-Path $PSScriptRoot 'p15b-service-baseline.ps1')
. (Join-Path $PSScriptRoot 'p16-hardening-helpers.ps1')

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\p16-normal-manual_{0}" -f $stamp)
$summaryPath=Join-Path $evidenceRoot 'p16-harness-summary.json'
$baselineFfPath=Join-Path $evidenceRoot 'p16-baseline-ff.json'
$setpoint30InitialPath=Join-Path $evidenceRoot 'p16-setpoint-30-initial.json'
$journal30InitialPath=Join-Path $evidenceRoot 'p16-journal-30-initial.json'
$setpoint40Path=Join-Path $evidenceRoot 'p16-setpoint-40.json'
$journal40Path=Join-Path $evidenceRoot 'p16-journal-40.json'
$setpoint30ReturnPath=Join-Path $evidenceRoot 'p16-setpoint-30-return.json'
$journal30ReturnPath=Join-Path $evidenceRoot 'p16-journal-30-return.json'
$firmwareFfPath=Join-Path $evidenceRoot 'p16-firmware-ff.json'
$postExitFfPath=Join-Path $evidenceRoot 'p16-post-exit-ff.json'
$cleanupFfPath=Join-Path $evidenceRoot 'p16-cleanup-ff.json'
$serviceSnapshotsPath=Join-Path $evidenceRoot 'p16-service-snapshots.json'
$serviceSegmentPath=Join-Path $evidenceRoot 'p16-watchdog-log-segment.txt'
$failsafeLog=Join-Path $evidenceRoot 'p16-failsafe.log'
$appLogSegmentPath=Join-Path $evidenceRoot 'p16-app-log-segment.txt'
$appLogPath=Join-Path $env:LOCALAPPDATA ("VictusFanControl\logs\events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$attemptFenceRoot=Join-Path $env:ProgramData 'VictusFanControl\Qualification\P16\attempts'
$attemptFenceEvidencePath=Join-Path $evidenceRoot 'p16-attempt-fence.json'

$head=$null;$gui=$null;$failsafe=$null;$initialBaseline=$null
$initialServiceMode=$null;$initialServicePid=0;$initialServiceStartTicks=0L
$watchdogPid=0;$watchdogStartTicks=0L;$guiPid=0;$guiStartTicks=0L
$ownedSessionId=$null;$serviceLogBoundary=0;$appLogBoundary=0
$causalChainPass=$false;$appInteractionAuditPass=$false;$initialHealthyAuditPass=$false
$strongRestorePass=$false;$finalJournalAbsent=$false;$firmwareFfPass=$false
$postExitFfPass=$false;$cleanupFirmwareProofPass=$false;$finalServiceBaselinePass=$false
$failsafeTakeover=$false;$attemptFenceClaimed=$false;$pass=$false;$failure=$null;$packagePath=$null;$packageSha256=$null
$serviceBefore=$null;$serviceDuring=$null;$serviceAfter=$null

function Assert-Administrator {
 $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
 $principal=New-Object Security.Principal.WindowsPrincipal($identity)
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'P16 must run from an elevated PowerShell.'}
}
function Assert-RepositoryProvenance {
 $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
 if($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch){throw "P16 requires branch '$expectedBranch'; observed '$branch'."}
 $local=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
 $upstream=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
 if($LASTEXITCODE -ne 0 -or $local -notmatch '^[0-9a-f]{40}$' -or $local -cne $upstream){throw "P16 requires local HEAD == upstream HEAD. local=$local upstream=$upstream"}
 $status=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
 if($LASTEXITCODE -ne 0){throw 'P16 could not inspect git status.'}
 $blocking=@($status -split "[\r\n]+" | Where-Object {-not [string]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)})
 if($blocking.Count -gt 0){$blocking|ForEach-Object{Write-Host $_};throw 'P16 requires committed source/config state; only preserved untracked logs/evidence is allowed.'}
 return $local
}
function Assert-ExactTarget {
 $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
 $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
 $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop
 $sku=([string]$system.SystemSKUNumber).Trim();$skuBase=($sku -split '#',2)[0].Trim()
 $biosText=@(([string]$bios.SMBIOSBIOSVersion).Trim(),([string]$bios.Version).Trim()) -join ' | '
 if(([string]$board.Manufacturer).Trim() -cne 'HP' -or ([string]$board.Product).Trim() -cne '8C40' -or
    ([string]$board.Version).Trim() -cne '63.43' -or ([string]$system.Manufacturer).Trim() -cne 'HP' -or
    ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or $skuBase -cne '9D0R1LA' -or
    $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){throw 'P16 exact-target fingerprint mismatch.'}
}
function Get-ServiceState {Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue}
function Get-ProcessStartTicks([int]$ProcessId){$p=[System.Diagnostics.Process]::GetProcessById($ProcessId);try{return [long]$p.StartTime.ToUniversalTime().Ticks}finally{$p.Dispose()}}
function Assert-ServiceCommon($svc,[string]$Context){
 if(-not $svc){throw "$Context requires installed M4 service."}
 if([string]$svc.StartMode -cne 'Manual' -or [string]$svc.StartName -notmatch 'LocalSystem|Local System'){throw "$Context requires Manual/LocalSystem service."}
 foreach($required in @($serviceExe,'--service-name VictusFanControlWatchdogM4','--m4-8c40-lease-service')){
  if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){throw "$Context service configuration mismatch; missing '$required'."}
 }
 $exeHash=(Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
 $moduleHash=(Get-FileHash -LiteralPath $serviceModule -Algorithm SHA256).Hash.ToLowerInvariant()
 if($exeHash -cne $expectedServiceExeSha -or $moduleHash -cne $expectedServiceModuleSha){throw "$Context qualified watchdog executable/module hash mismatch."}
}
function Wait-M4Ready([int]$ExpectedPid){
 $deadline=(Get-Date).AddSeconds(20)
 while((Get-Date) -lt $deadline){
  if(Test-Path -LiteralPath $statusPath){
   try{$s=Get-Content -LiteralPath $statusPath -Raw|ConvertFrom-Json
    if([int]$s.ProcessId -eq $ExpectedPid -and $s.Ready -and -not $s.Blocked -and [int]$s.SessionId -eq 0 -and
       $s.AccountName -match 'SYSTEM$' -and $s.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
       $s.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and $s.RecoveryDisposition -ceq 'Ready'){return $s}
   }catch{}
  }
  Start-Sleep -Milliseconds 100
 }
 throw 'P16 timed out waiting for exact M4 Ready state.'
}
function Get-ValidatedServiceBaseline {
 $svc=Get-ServiceState;Assert-ServiceCommon $svc 'P16 baseline'
 $journalPresent=Test-Path -LiteralPath $journalPath;$readyVerified=$false;$servicePid=[int]$svc.ProcessId;$ticks=0L
 if([string]$svc.State -ceq 'Running' -and $servicePid -gt 0 -and -not $journalPresent){
  $ticks=Get-ProcessStartTicks $servicePid;[void](Wait-M4Ready $servicePid)
  $confirmed=Get-ServiceState;Assert-ServiceCommon $confirmed 'P16 inherited-running baseline confirmation'
  if([string]$confirmed.State -cne 'Running' -or [int]$confirmed.ProcessId -ne $servicePid -or (Get-ProcessStartTicks $servicePid) -ne $ticks){throw 'P16 inherited Running watchdog identity changed during Ready verification.'}
  $readyVerified=$true;$svc=$confirmed
 }
 $mode=Resolve-P15BServiceBaselineMode -State ([string]$svc.State) -StartMode ([string]$svc.StartMode) -StartName ([string]$svc.StartName) -ProcessId ([int]$svc.ProcessId) -JournalPresent $journalPresent -ReadyVerified $readyVerified
 if($mode -ceq 'Stopped'){$servicePid=0;$ticks=0L}
 [pscustomobject]@{Mode=$mode;ProcessId=$servicePid;ProcessStartUtcTicks=$ticks;Service=$svc;ReadyVerified=$readyVerified;JournalPresent=$journalPresent}
}
function Get-PowerSnapshot {
 Add-Type -AssemblyName System.Windows.Forms
 $s=[System.Windows.Forms.SystemInformation]::PowerStatus;$percent=$null
 if($s.BatteryLifePercent -ge 0){$percent=[math]::Round([double]$s.BatteryLifePercent*100,0)}
 [pscustomobject]@{PowerLineStatus=[string]$s.PowerLineStatus;BatteryPercent=$percent}
}
function Assert-PowerSane {
 $p=Get-PowerSnapshot
 if($p.PowerLineStatus -cne 'Online'){throw "P16 requires AC online; observed '$($p.PowerLineStatus)'."}
 if($null -eq $p.BatteryPercent -or [double]$p.BatteryPercent -lt 20){throw "P16 requires readable battery >=20%; observed '$($p.BatteryPercent)'."}
}
function Assert-NoConflictingController {
 $conflicts=@(Get-Process OmenMon,OmenMon-Reborn,VictusFanControl.App -ErrorAction SilentlyContinue)
 if($conflicts.Count -gt 0){throw ("P16 requires no pre-existing OmenMon/OmenMon-Reborn/VictusFanControl.App process. Found: "+(($conflicts|ForEach-Object{"$($_.ProcessName):$($_.Id)"}) -join ', '))}
}
function Read-8C40Setpoint {
 return Invoke-P16BoundedEcContentionRetry -MaximumAttempts 3 -DelayMilliseconds 75 -Operation {
  $raw=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1|Out-String)
  if($LASTEXITCODE -ne 0){throw "P16 setpoint probe failed. Raw: $raw"}
  $line=($raw -split "[\r\n]+"|Where-Object{$_ -match '^setpoint CPU='}|Select-Object -Last 1)
  $m=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
  if(-not $m.Success){throw "P16 could not parse setpoint probe. Raw: $raw"}
  [pscustomobject]@{timestampUtc=(Get-Date).ToUniversalTime().ToString('O');cpu=[int]$m.Groups[1].Value;gpu=[int]$m.Groups[2].Value;raw=[string]$line}
 }
}
function Assert-StableSetpoint([int]$Cpu,[int]$Gpu,[string]$Context,[string]$EvidencePath){
 $samples=@();$consecutive=0
 for($read=1;$read -le 6;$read++){
  $s=Read-8C40Setpoint;$samples+=@($s);Write-Host ("{0} proof {1}/6: {2}" -f $Context,$read,$s.raw)
  if($s.cpu -eq $Cpu -and $s.gpu -eq $Gpu){$consecutive++;if($consecutive -ge 2){
   [ordered]@{context=$Context;passed=$true;expectedCpu=$Cpu;expectedGpu=$Gpu;requiredConsecutive=2;maximumReads=6;samples=$samples}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $EvidencePath -Encoding UTF8;return
  }}else{$consecutive=0}
  if($read -lt 6){Start-Sleep -Milliseconds 100}
 }
 [ordered]@{context=$Context;passed=$false;expectedCpu=$Cpu;expectedGpu=$Gpu;requiredConsecutive=2;maximumReads=6;samples=$samples}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $EvidencePath -Encoding UTF8
 throw "$Context requires two consecutive $Cpu/$Gpu observations within six reads."
}
function Test-OwnedPhase($phase){if($null -eq $phase){return $false};if($phase -is [string]){return ($phase -ceq 'Owned' -or $phase -ceq '2')};try{return ([int]$phase -eq 2)}catch{return $false}}
function Assert-OwnedJournal($journal,[int]$ExpectedPid,[long]$ExpectedTicks,[int]$ExpectedLevel,[long]$ExpectedGeneration,[string]$ExpectedSessionId){
 if([int]$journal.SchemaVersion -ne 2 -or $journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or -not (Test-OwnedPhase $journal.Phase) -or
    [long]$journal.Generation -ne $ExpectedGeneration -or [int]$journal.Controller.ProcessId -ne $ExpectedPid -or
    [long]$journal.Controller.ProcessStartUtcTicks -ne $ExpectedTicks -or [int]$journal.Owned.Cpu -ne $ExpectedLevel -or [int]$journal.Owned.Gpu -ne $ExpectedLevel){
  throw "P16 durable journal is not schema-v2 generation $ExpectedGeneration OWNED $ExpectedLevel/$ExpectedLevel bound to exact GUI identity."
 }
 $session=[string]$journal.SessionId;if([string]::IsNullOrWhiteSpace($session)){throw 'P16 journal SessionId is missing.'}
 if(-not [string]::IsNullOrWhiteSpace($ExpectedSessionId) -and $session -cne $ExpectedSessionId){throw "P16 ownership session changed: expected=$ExpectedSessionId observed=$session"}
 return $session
}
function Wait-JournalGone([int]$Seconds){$deadline=(Get-Date).AddSeconds($Seconds);while((Get-Date)-lt $deadline){if(-not(Test-Path -LiteralPath $journalPath)){return $true};Start-Sleep -Milliseconds 200};return(-not(Test-Path -LiteralPath $journalPath))}
function Get-AppLogSegment {if(-not(Test-Path -LiteralPath $appLogPath -PathType Leaf)){return @()};return @(Get-Content -LiteralPath $appLogPath|Select-Object -Skip $appLogBoundary)}
function Wait-AppLogMatch([string]$Pattern,[int]$Seconds,[string]$Label){
 $deadline=(Get-Date).AddSeconds($Seconds)
 while((Get-Date)-lt $deadline){
  $matches=@(Get-AppLogSegment|Where-Object{([string]$_)-match $Pattern})
  if($matches.Count -gt 0){return [string]$matches[-1]}
  if($gui){$gui.Refresh();if($gui.HasExited){throw "P16 GUI exited before $Label."}}
  Start-Sleep -Milliseconds 100
 }
 throw "P16 timed out waiting for $Label."
}
function Wait-P16InteractionOutcome([string]$SuccessPattern,[string]$FailurePattern,[int]$Seconds,[string]$Label,[int]$StartIndex){
 $deadline=(Get-Date).AddSeconds($Seconds)
 while((Get-Date)-lt $deadline){
  $segment=@(Get-AppLogSegment)
  $window=if($StartIndex -lt $segment.Count){@($segment|Select-Object -Skip $StartIndex)}else{@()}
  if($window.Count -gt 0){
   $outcome=Resolve-P16InteractionOutcome -Lines $window -SuccessPattern $SuccessPattern -FailurePattern $FailurePattern
   if([string]$outcome.Kind -ceq 'Failure'){throw ("P16 observed FAILED CLOSED/blocked outcome during {0}: {1}" -f $Label,$outcome.Line)}
   if([string]$outcome.Kind -ceq 'Success'){return [string]$outcome.Line}
  }
  if($gui){$gui.Refresh();if($gui.HasExited){throw "P16 GUI exited before $Label."}}
  Start-Sleep -Milliseconds 100
 }
 throw "P16 timed out waiting for $Label."
}
function Assert-AppInteractionAudit {
 $segment=@(Get-AppLogSegment);$segment|Set-Content -LiteralPath $appLogSegmentPath -Encoding UTF8
 $manualModes=@($segment|Where-Object{([string]$_)-match 'P13 mode request Manual:'})
 $firmwareModes=@($segment|Where-Object{([string]$_)-match 'P13 mode request Firmware:'})
 $automaticModes=@($segment|Where-Object{([string]$_)-match 'P13 mode request Automatic:'})
 $manualApplies=@($segment|Where-Object{([string]$_)-match 'P13 manual request [0-9]+/[0-9]+:'})
 if($manualModes.Count -ne 1 -or $firmwareModes.Count -ne 1 -or $automaticModes.Count -ne 0 -or $manualApplies.Count -ne 3){
  throw "P16 app interaction counts invalid: ManualMode=$($manualModes.Count) FirmwareMode=$($firmwareModes.Count) AutomaticMode=$($automaticModes.Count) ManualApply=$($manualApplies.Count)."
 }
 $patterns=@(
  'P13 mode request Manual: action=HoldFirmware; authorized=True; authority=Firmware;',
  'P13 manual request 30/30: action=EnterCustomAndApply; authorized=True; authority=Custom;',
  'P13 manual request 40/40: action=ApplyChangedLevel; authorized=True; authority=Custom;',
  'P13 manual request 30/30: action=ApplyChangedLevel; authorized=True; authority=Custom;',
  'P13 mode request Firmware: action=RestoreFirmware; authorized=True; authority=Firmware;'
 )
 $cursor=-1
 foreach($pattern in $patterns){$found=-1;for($i=$cursor+1;$i -lt $segment.Count;$i++){if(([string]$segment[$i]).Contains($pattern,[StringComparison]::Ordinal)){$found=$i;break}};if($found -lt 0){throw "P16 app-log causal interaction missing/out of order: $pattern"};$cursor=$found}
}
function Start-P16Failsafe {
 $p=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$failsafeScript,'-DelaySeconds','120','-LogPath',$failsafeLog) -WindowStyle Hidden -PassThru
 $deadline=(Get-Date).AddSeconds(3)
 while((Get-Date)-lt $deadline){$p.Refresh();if($p.HasExited){throw 'P16 reused independent failsafe exited before ARMED proof.'};if(Test-Path -LiteralPath $failsafeLog){$t=Get-Content -LiteralPath $failsafeLog -Raw -ErrorAction SilentlyContinue;if($t -match 'P15D2 FAILSAFE ARMED:'){return $p}};Start-Sleep -Milliseconds 50}
 throw 'P16 reused independent failsafe did not publish ARMED proof.'
}
function Test-FailsafeTakeover {
 if(-not(Test-Path -LiteralPath $failsafeLog)){return $false};$t=Get-Content -LiteralPath $failsafeLog -Raw
 return($t -match 'P15D2 FAILSAFE TAKEOVER:' -or $t -match 'P15D2 FAILSAFE CONTROLLER-KILL:' -or $t -match 'P15D2 FAILSAFE SERVICE-START:' -or $t -match 'P15D2 FAILSAFE SERVICE-RESTART:' -or $t -match 'P15D2 FAILSAFE STARTED:' -or $t -match 'P15D2 FAILSAFE RECOVERED:')
}
function Assert-CausalServiceLog([int]$ControllerPid){
 if(-not(Test-Path -LiteralPath $serviceLog)){throw 'P16 watchdog service log missing.'}
 $all=@(Get-Content -LiteralPath $serviceLog);$segment=@($all|Select-Object -Skip $serviceLogBoundary);$segment|Set-Content -LiteralPath $serviceSegmentPath -Encoding UTF8
 $tag="controller PID=$ControllerPid";$prepare=@();$intent30=@();$intent40=@();$commit30=@();$commit40=@();$restore=@();$release=@()
 for($i=0;$i -lt $segment.Count;$i++){$line=[string]$segment[$i];if(-not $line.Contains($tag)){continue};if($line -match 'WATCHDOG PREPARE ACK'){$prepare+=@($i)};if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line -match 'target=30/30'){$intent30+=@($i)};if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line -match 'target=40/40'){$intent40+=@($i)};if($line -match 'WATCHDOG COMMIT ACK' -and $line -match 'target=30/30'){$commit30+=@($i)};if($line -match 'WATCHDOG COMMIT ACK' -and $line -match 'target=40/40'){$commit40+=@($i)};if($line -match 'WATCHDOG RESTORE_BEGIN ACK'){$restore+=@($i)};if($line -match 'WATCHDOG RELEASE ACK'){$release+=@($i)}}
 if($prepare.Count -ne 1 -or $intent30.Count -ne 2 -or $intent40.Count -ne 1 -or $commit30.Count -ne 2 -or $commit40.Count -ne 1 -or $restore.Count -ne 1 -or $release.Count -ne 1){throw ("P16 causal counts invalid: PREPARE={0} I30={1} I40={2} C30={3} C40={4} RESTORE={5} RELEASE={6}" -f $prepare.Count,$intent30.Count,$intent40.Count,$commit30.Count,$commit40.Count,$restore.Count,$release.Count)}
 if(-not($prepare[0] -lt $intent30[0] -and $intent30[0] -lt $commit30[0] -and $commit30[0] -lt $intent40[0] -and $intent40[0] -lt $commit40[0] -and $commit40[0] -lt $intent30[1] -and $intent30[1] -lt $commit30[1] -and $commit30[1] -lt $restore[0] -and $restore[0] -lt $release[0])){throw 'P16 causal ordering is not PREPARE < 30 write/commit < 40 write/commit < return-30 write/commit < RESTORE_BEGIN < RELEASE.'}
}
function Snapshot-Service([string]$Phase){$svc=Get-ServiceState;if(-not $svc){return [pscustomobject]@{phase=$Phase;installed=$false}};$ticks=0L;if([int]$svc.ProcessId -gt 0){try{$ticks=Get-ProcessStartTicks ([int]$svc.ProcessId)}catch{}};[pscustomobject]@{phase=$Phase;installed=$true;state=[string]$svc.State;startMode=[string]$svc.StartMode;startName=[string]$svc.StartName;processId=[int]$svc.ProcessId;processStartUtcTicks=[string]$ticks;pathName=[string]$svc.PathName}}
function Write-Summary([string]$Result,[string]$Failure){
 [ordered]@{schemaVersion=1;gate='P16-HARNESS';result=$Result;failure=$Failure;evidenceHead=$head;timestampUtc=(Get-Date).ToUniversalTime().ToString('O');guiPid=$guiPid;guiStartUtcTicks=[string]$guiStartTicks;watchdogPid=$watchdogPid;watchdogStartUtcTicks=[string]$watchdogStartTicks;ownedSessionId=$ownedSessionId;initialServiceMode=$initialServiceMode;initialServicePid=$initialServicePid;initialServiceStartUtcTicks=[string]$initialServiceStartTicks;initialHealthyAuditPass=$initialHealthyAuditPass;causalChainPass=$causalChainPass;appInteractionAuditPass=$appInteractionAuditPass;strongRestorePass=$strongRestorePass;finalJournalAbsent=$finalJournalAbsent;firmwareFfPass=$firmwareFfPass;postExitFfPass=$postExitFfPass;cleanupFirmwareProofPass=$cleanupFirmwareProofPass;finalServiceBaselinePass=$finalServiceBaselinePass;failsafeTakeover=$failsafeTakeover;attemptFenceClaimed=$attemptFenceClaimed;p16ExecutionAuthorized=[bool]$contract.normalManual.executionAuthorized;p16ControllerPhysicalExecutionAuthorized=[bool]$contract.normalManual.controllerPhysicalExecutionAuthorized;permanentUserManualExecutionAuthorized=[bool]$contract.safetyBoundary.permanentUserManualExecutionAuthorized;automaticExecutionAuthorized=[bool]$contract.safetyBoundary.automaticExecutionAuthorized}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator
$head=Assert-RepositoryProvenance
Assert-ExactTarget
Assert-PowerSane
Assert-NoConflictingController
$serviceBefore=Snapshot-Service 'before'
$initialBaseline=Get-ValidatedServiceBaseline
$initialServiceMode=[string]$initialBaseline.Mode;$initialServicePid=[int]$initialBaseline.ProcessId;$initialServiceStartTicks=[long]$initialBaseline.ProcessStartUtcTicks
New-Item -ItemType Directory -Force -Path $evidenceRoot|Out-Null

Write-Host 'VictusFanControl - HP 8C40 P16 NORMAL USER MANUAL QUALIFICATION' -ForegroundColor Cyan
Write-Host 'Normal app only: Firmware -> Manual -> 30 -> 40 -> 30 -> Firmware -> tray Exit. Automatic is forbidden.' -ForegroundColor Yellow

try{
 Write-Host 'Step 1: same-HEAD build and static regressions...' -ForegroundColor Cyan
 dotnet build .\VictusFanControl.sln -c Release -warnaserror
 if($LASTEXITCODE -ne 0){throw "P16 build failed with exit=$LASTEXITCODE."}
 foreach($script in @('test-p16-normal-manual-invariants.ps1','test-p16-evidence-packaging.ps1','test-p15d2-variable-manual-invariants.ps1','test-p15d1-tray-exit-invariants.ps1','test-p15c-gui-manual-invariants.ps1','test-p13-control-surface-invariants.ps1')){& (Join-Path $PSScriptRoot $script);if($LASTEXITCODE -ne 0){throw "P16 regression '$script' failed with exit=$LASTEXITCODE."}}
 dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test;if($LASTEXITCODE -ne 0){throw 'P16 SafetyGate self-test failed.'}
 dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test;if($LASTEXITCODE -ne 0){throw 'P16 coordinator self-test failed.'}
 dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test;if($LASTEXITCODE -ne 0){throw 'P16 HP backend self-test failed.'}

 Write-Host 'Step 2: independent Firmware/service baseline...' -ForegroundColor Cyan
 Assert-StableSetpoint 255 255 'P16 baseline FF/FF' $baselineFfPath
 $preTokenBaseline=Get-ValidatedServiceBaseline
 if([string]$preTokenBaseline.Mode -cne $initialServiceMode){throw "P16 service baseline mode changed before token: initial=$initialServiceMode current=$($preTokenBaseline.Mode)."}
 if($initialServiceMode -ceq 'Running' -and ([int]$preTokenBaseline.ProcessId -ne $initialServicePid -or [long]$preTokenBaseline.ProcessStartUtcTicks -ne $initialServiceStartTicks)){throw 'P16 inherited Running watchdog identity changed before operator token.'}
 Assert-PowerSane

 $confirm=Read-Host "Type exactly $token to authorize one bounded P16 normal-Manual session"
 if($confirm -cne $token){throw 'P16 cancelled before failsafe/GUI active boundary.'}

 $attemptFence=New-P16AuthorizationAttemptFence -Root $attemptFenceRoot -AuthorizationHead $head -EvidencePath $attemptFenceEvidencePath
 $attemptFenceClaimed=[bool]$attemptFence.Claimed
 Write-Host ("P16 one-shot authorization fence claimed for HEAD {0}." -f $head) -ForegroundColor Cyan

 Write-Host 'Step 3: arm reused delayed independent failsafe...' -ForegroundColor Cyan
 $failsafe=Start-P16Failsafe
 if(Test-Path -LiteralPath $serviceLog){$serviceLogBoundary=@(Get-Content -LiteralPath $serviceLog).Count}
 if(Test-Path -LiteralPath $appLogPath -PathType Leaf){
  if((Get-Item -LiteralPath $appLogPath).Length -gt 4MB){throw 'P16 refuses current daily app log above 4 MiB because rotation could invalidate the external interaction audit.'}
  $appLogBoundary=@(Get-Content -LiteralPath $appLogPath).Count
 }

 Write-Host 'Step 4: launch NORMAL app with no P16/P15 qualification arguments...' -ForegroundColor Cyan
 if(-not(Test-Path -LiteralPath $appExe -PathType Leaf)){throw "P16 GUI executable missing after build: $appExe"}
 $gui=Start-P15BTrackedChild -Executable $appExe -Arguments @('--modules-dir',$modulesDir) -WorkingDirectory $repoRoot
 $guiPid=$gui.Id;$guiStartTicks=[long]$gui.StartTime.ToUniversalTime().Ticks
 [void](Wait-AppLogMatch 'P13 UI: startup mode=Firmware; manualGate=True; automaticGate=False\.' 60 'normal GUI startup gate proof')
 [void](Wait-AppLogMatch 'Recovery completed; telemetry is healthy after 3 complete snapshots\.' 60 'initial three-snapshot Healthy recovery')
 $initialHealthyAuditPass=$true

 $runtimeSvc=Get-ServiceState;Assert-ServiceCommon $runtimeSvc 'P16 GUI runtime'
 if([string]$runtimeSvc.State -cne 'Running' -or [int]$runtimeSvc.ProcessId -le 0){throw 'P16 normal GUI did not establish Running M4 watchdog.'}
 $watchdogPid=[int]$runtimeSvc.ProcessId;$watchdogStartTicks=Get-ProcessStartTicks $watchdogPid;[void](Wait-M4Ready $watchdogPid)
 if($initialServiceMode -ceq 'Running' -and ($watchdogPid -ne $initialServicePid -or $watchdogStartTicks -ne $initialServiceStartTicks)){throw 'P16 normal GUI startup changed inherited Running watchdog identity.'}
 $serviceDuring=Snapshot-Service 'normal-gui-healthy'

 $interactionStart=@(Get-AppLogSegment).Count
 Write-Host '';Write-Host 'P16 ACTION #1: open Fan Control and click Manual ONCE. Do not Apply yet.' -ForegroundColor Yellow
 [void](Wait-P16InteractionOutcome 'P13 mode request Manual: action=HoldFirmware; authorized=True; authority=Firmware;' 'P13 mode request Manual FAILED CLOSED:|P13 mode request Manual: BLOCKED|P13 mode request Automatic:|P13 control interaction blocked before production adapter access:' 90 'normal Manual mode request' $interactionStart)

 $interactionStart=@(Get-AppLogSegment).Count
 Write-Host 'P16 ACTION #2: set level 30 and click Apply ONCE.' -ForegroundColor Yellow
 [void](Wait-P16InteractionOutcome 'P13 manual request 30/30: action=EnterCustomAndApply; authorized=True; authority=Custom;' 'P13 manual request 30/30 FAILED CLOSED:|P13 manual request 30/30: BLOCKED|P13 manual request (?!30/30:)[0-9]+/[0-9]+:|P13 control interaction blocked before production adapter access:' 90 'initial Apply 30/30' $interactionStart)
 Assert-StableSetpoint 30 30 'P16 parent initial OWNED 30/30' $setpoint30InitialPath
 if(-not(Test-Path -LiteralPath $journalPath)){throw 'P16 initial 30/30 journal missing.'}
 $j30=Get-Content -LiteralPath $journalPath -Raw|ConvertFrom-Json;$ownedSessionId=Assert-OwnedJournal $j30 $guiPid $guiStartTicks 30 3 $null;Copy-Item -LiteralPath $journalPath -Destination $journal30InitialPath

 $interactionStart=@(Get-AppLogSegment).Count
 Write-Host 'P16 ACTION #3: set level 40 and click Apply ONCE.' -ForegroundColor Yellow
 [void](Wait-P16InteractionOutcome 'P13 manual request 40/40: action=ApplyChangedLevel; authorized=True; authority=Custom;' 'P13 manual request 40/40 FAILED CLOSED:|P13 manual request 40/40: BLOCKED|P13 manual request (?!40/40:)[0-9]+/[0-9]+:|P13 control interaction blocked before production adapter access:' 90 'changed Apply 40/40' $interactionStart)
 Assert-StableSetpoint 40 40 'P16 parent changed OWNED 40/40' $setpoint40Path
 $j40=Get-Content -LiteralPath $journalPath -Raw|ConvertFrom-Json;[void](Assert-OwnedJournal $j40 $guiPid $guiStartTicks 40 5 $ownedSessionId);Copy-Item -LiteralPath $journalPath -Destination $journal40Path

 $interactionStart=@(Get-AppLogSegment).Count
 Write-Host 'P16 ACTION #4: set level back to 30 and click Apply ONCE.' -ForegroundColor Yellow
 [void](Wait-P16InteractionOutcome 'P13 manual request 30/30: action=ApplyChangedLevel; authorized=True; authority=Custom;' 'P13 manual request 30/30 FAILED CLOSED:|P13 manual request 30/30: BLOCKED|P13 manual request (?!30/30:)[0-9]+/[0-9]+:|P13 control interaction blocked before production adapter access:' 90 'return Apply 30/30' $interactionStart)
 Assert-StableSetpoint 30 30 'P16 parent return OWNED 30/30' $setpoint30ReturnPath
 $j30Return=Get-Content -LiteralPath $journalPath -Raw|ConvertFrom-Json;[void](Assert-OwnedJournal $j30Return $guiPid $guiStartTicks 30 7 $ownedSessionId);Copy-Item -LiteralPath $journalPath -Destination $journal30ReturnPath

 $interactionStart=@(Get-AppLogSegment).Count
 Write-Host 'P16 ACTION #5: click Firmware ONCE. Do NOT click Automatic.' -ForegroundColor Yellow
 [void](Wait-P16InteractionOutcome 'P13 mode request Firmware: action=RestoreFirmware; authorized=True; authority=Firmware;' 'P13 mode request Firmware FAILED CLOSED:|P13 mode request Automatic:|P13 control interaction blocked before production adapter access:' 90 'normal Firmware restore request' $interactionStart)
 if(-not(Wait-JournalGone 20)){throw 'P16 Firmware request returned but durable journal remains.'}
 Assert-StableSetpoint 255 255 'P16 FF/FF after real Firmware request' $firmwareFfPath;$firmwareFfPass=$true
 Assert-CausalServiceLog $guiPid;$causalChainPass=$true;$strongRestorePass=$true;$finalJournalAbsent=$true
 Assert-AppInteractionAudit;$appInteractionAuditPass=$true

 Write-Host '';Write-Host 'P16 CONTROL PROOF COMPLETE. Right-click the VictusFanControl tray icon and choose Exit ONCE.' -ForegroundColor Green
 $guiExit=Wait-P15BTrackedChildExitCode -Process $gui -Seconds 90
 if($guiExit -ne 0){throw "P16 normal GUI exited with code $guiExit."}
 [void](Wait-AppLogMatch 'Explicit application shutdown started\.' 5 'normal explicit tray shutdown log')
 if(Test-Path -LiteralPath $journalPath){throw 'P16 clean tray Exit recreated or retained a durable journal.'}
 Assert-StableSetpoint 255 255 'P16 post-exit FF/FF' $postExitFfPath;$postExitFfPass=$true

 $finalSvc=Get-ServiceState;Assert-ServiceCommon $finalSvc 'P16 final post-exit Firmware baseline'
 if([string]$finalSvc.State -cne 'Running' -or [int]$finalSvc.ProcessId -ne $watchdogPid -or (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){throw 'P16 final watchdog service is not the same Running/Ready process.'}
 [void](Wait-M4Ready $watchdogPid);$serviceAfter=Snapshot-Service 'after-clean-tray-exit'
 @($serviceBefore,$serviceDuring,$serviceAfter)|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $serviceSnapshotsPath -Encoding UTF8;$finalServiceBaselinePass=$true
 if(Test-FailsafeTakeover){$failsafeTakeover=$true;throw 'P16 independent failsafe took over; safe but invalid for normal-path qualification.'}
 $pass=$true
 Write-Host 'PASS: P16 normal app Manual 30 -> 40 -> 30 -> Firmware -> tray Exit completed.' -ForegroundColor Green
}catch{
 $failure=$_.Exception.Message
 Write-Host ("P16 FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}finally{
 # Do not terminate an in-flight normal GUI from the parent. If a durable lease
 # remains, the already-armed qualified failsafe owns bounded recovery.
 try{if(Test-Path -LiteralPath $appLogPath -PathType Leaf){@(Get-AppLogSegment)|Set-Content -LiteralPath $appLogSegmentPath -Encoding UTF8}}catch{}
 if($pass){
  try{if(-not $postExitFfPass){Assert-StableSetpoint 255 255 'P16 cleanup FF/FF' $cleanupFfPath;$cleanupFirmwareProofPass=$true}else{$cleanupFirmwareProofPass=$true}}catch{Write-Warning ("P16 cleanup firmware proof failed: {0}" -f $_.Exception.Message)}
 }else{
  Write-Warning 'P16 FAIL_CLOSED evidence preserved. Leave the delayed failsafe armed; if the GUI still owns Custom it will perform qualified recovery after its bounded delay.'
 }
 if(-not(Test-Path -LiteralPath $serviceSnapshotsPath)){try{$serviceAfter=Snapshot-Service 'cleanup-final';@($serviceBefore,$serviceDuring,$serviceAfter)|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $serviceSnapshotsPath -Encoding UTF8}catch{}}
 try{Write-Summary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure}catch{}
 try{$pkg=& $packager -EvidenceRoot $evidenceRoot -RepositoryRoot $repoRoot;$packagePath=[string]$pkg.ZipPath;$packageSha256=[string]$pkg.ZipSha256}catch{Write-Warning ("P16 evidence packaging failed: {0}" -f $_.Exception.Message);if($pass){$pass=$false;$failure='P16 physical sequence passed but evidence packaging failed.'}}
 if($gui){try{$gui.Dispose()}catch{}}
}

if($pass){Write-Host ("P16 NORMAL-MANUAL PASS. Evidence ZIP: {0}" -f $packagePath) -ForegroundColor Green;Write-Host ("SHA-256: {0}" -f $packageSha256) -ForegroundColor Green;exit 0}
Write-Error ("P16 FAIL_CLOSED. Evidence preserved at {0}. {1}" -f $evidenceRoot,$failure)
exit 1
