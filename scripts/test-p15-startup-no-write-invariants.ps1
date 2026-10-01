$ErrorActionPreference='Stop'
function Assert-True([bool]$v,[string]$m){if(-not $v){throw $m}}
function Assert-False([bool]$v,[string]$m){if($v){throw $m}}
function Assert-Contains([string]$t,[string]$n,[string]$m){if($t.IndexOf($n,[StringComparison]::Ordinal)-lt 0){throw $m}}
function Assert-NotContains([string]$t,[string]$n,[string]$m){if($t.IndexOf($n,[StringComparison]::Ordinal)-ge 0){throw $m}}
$root=Split-Path -Parent $PSScriptRoot
$contract=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json
$p14=Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$profile=Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$watchdogGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$harness=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15-startup-no-write.ps1') -Raw
$packager=Get-Content -LiteralPath (Join-Path $root 'scripts\package-p15-startup-evidence.ps1') -Raw
$payloadResolver=Get-Content -LiteralPath (Join-Path $root 'scripts\expand-p15-rc-payload.ps1') -Raw
$payloadResolverSelfTest=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15-rc-payload-layout.ps1') -Raw
$doc=Get-Content -LiteralPath (Join-Path $root 'docs\P15_TARGET_CHECKPOINT.md') -Raw
if([string]$contract.milestone -cne 'P15A'){throw 'P15A milestone mismatch.'}
if([string]$contract.status -notin @('P15A_STARTUP_NO_WRITE_PREPARATION_CI_PASS_GATE_CLOSED','P15A_STARTUP_NO_WRITE_AUTHORIZED_AWAITING_SAME_HEAD_CI','P15A_STARTUP_NO_WRITE_PAYLOAD_LAYOUT_CORRECTION_CI_PENDING_GATE_CLOSED','P15A_STARTUP_NO_WRITE_PAYLOAD_LAYOUT_CORRECTION_CI_PASS_GATE_CLOSED')){throw 'P15A state mismatch.'}
$executionAuthorized=[bool]$contract.startupNoWrite.executionAuthorized
if($executionAuthorized -and [string]$contract.status -cne 'P15A_STARTUP_NO_WRITE_AUTHORIZED_AWAITING_SAME_HEAD_CI'){throw 'P15A authorized state/status mismatch.'}
if((-not $executionAuthorized) -and [string]$contract.status -notin @('P15A_STARTUP_NO_WRITE_PREPARATION_CI_PASS_GATE_CLOSED','P15A_STARTUP_NO_WRITE_PAYLOAD_LAYOUT_CORRECTION_CI_PENDING_GATE_CLOSED','P15A_STARTUP_NO_WRITE_PAYLOAD_LAYOUT_CORRECTION_CI_PASS_GATE_CLOSED')){throw 'P15A closed state/status mismatch.'}
if([string]$contract.targetProfileId -cne 'HP-8C40-9D0R1LA-F18'){throw 'P15A target mismatch.'}
if([string]$contract.p14Baseline.closureHead -cne '6945b2e34526e5e266189da6553bf3ea010d3893' -or [int]$contract.p14Baseline.closureCiRunNumber -ne 1086 -or [long]$contract.p14Baseline.closureCiRunId -ne 36913062832 -or [string]$contract.p14Baseline.closureCiResult -cne 'SUCCESS'){throw 'P15A P14 closure baseline mismatch.'}
if([string]$contract.p14Baseline.auditedRcSourceHead -cne 'eebcdd5e833256466c1ae023c35f7cef8d40d6ec' -or [string]$contract.p14Baseline.auditedRcArtifactSha256 -cne '588058a8b57c0ca1bb41649288682e0981be845ab747654472d3d10c88d572b5' -or [string]$contract.p14Baseline.auditedRcPayloadZipSha256 -cne '704983caa20abb21c3520ffbd165ad69f9843139b6b2fa47d9e9e2448a32ef68'){throw 'P15A audited RC identity mismatch.'}
if([string]$contract.startupNoWrite.qualifiedInstalledWatchdogExeSha256 -cne 'ec10242ce40c12856cf9222d10e43016854498b3946375c8f73e9f927d9912ed' -or [string]$contract.startupNoWrite.qualifiedInstalledPawnIoModuleSha256 -cne 'c38fd116e7aff4d1fdb0a494e296be0a6708e5a22fc72f14587442fb7f8f7906'){throw 'P15A qualified installed M4 binary/module identity mismatch.'}
Assert-True ([bool]$p14.productization.finalSoftwareRcAuditClosed) 'P15A requires P14.5 formally closed.'
Assert-True ([bool]$contract.startupNoWrite.preparationCiValidated) 'P15A preparation must be CI validated.'
Assert-True ([bool]$contract.startupNoWrite.preparationClosure.closed) 'P15A preparation closure must be recorded.'
if([string]$contract.startupNoWrite.preparationClosure.result -cne 'PASS' -or [string]$contract.startupNoWrite.preparationClosure.implementationHead -cne '8779b60ee994b5fd9b34fb753c90908048974fc8' -or [int]$contract.startupNoWrite.preparationClosure.sourceCiRunNumber -ne 1090 -or [long]$contract.startupNoWrite.preparationClosure.sourceCiRunId -ne 36916449876 -or [string]$contract.startupNoWrite.preparationClosure.sourceCiResult -cne 'SUCCESS'){throw 'P15A preparation closure CI identity mismatch.'}
Assert-False ([bool]$contract.startupNoWrite.preparationClosure.hardwareExecution) 'P15A preparation closure must record no hardware execution.'
if($executionAuthorized){
    if([string]$contract.startupNoWrite.authorization.scope -cne 'P15A startup/no-write only'){throw 'P15A authorization scope mismatch.'}
    if([string]$contract.startupNoWrite.authorization.basisHead -cne '1ea5d081422aced1acc3941e2110ba16289cd791' -or [int]$contract.startupNoWrite.authorization.basisCiRunNumber -ne 1091 -or [long]$contract.startupNoWrite.authorization.basisCiRunId -ne 36916991549 -or [string]$contract.startupNoWrite.authorization.basisCiResult -cne 'SUCCESS'){throw 'P15A authorization basis mismatch.'}
    Assert-True ([bool]$contract.startupNoWrite.authorization.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P15A authorization must require same-HEAD CI success before execution.'
    Assert-False ([bool]$contract.startupNoWrite.authorization.manual30AuthorizationOpened) 'P15A authorization must not open P15B.'
    Assert-False ([bool]$contract.startupNoWrite.authorization.automaticAuthorizationOpened) 'P15A authorization must not open Automatic.'
    Assert-False ([bool]$contract.startupNoWrite.authorization.hardwareExecutionAtAuthorizationCommit) 'P15A authorization commit must record no hardware execution.'
}else{
    Assert-False ([bool]$contract.startupNoWrite.executionAuthorized) 'P15A physical startup execution must remain CLOSED during preparation.'
}
Assert-False ([bool]$contract.startupNoWrite.physicalPassed) 'P15A cannot pre-claim physical PASS.'
if([bool]$contract.startupNoWrite.correction.required){
    Assert-False ([bool]$contract.startupNoWrite.executionAuthorized) 'P15A authorization must remain revoked until correction closure is separately authorized.'
    if([string]$contract.startupNoWrite.correction.failedAttemptSourceHead -cne '8e91c649056ba868ec94aa1864c7669aa90f4550' -or [int]$contract.startupNoWrite.correction.failedAttemptCiRunNumber -ne 1092 -or [long]$contract.startupNoWrite.correction.failedAttemptCiRunId -ne 36920046370 -or [string]$contract.startupNoWrite.correction.failedAttemptResult -cne 'FAIL_CLOSED'){throw 'P15A failed preflight identity mismatch.'}
    Assert-True ([bool]$contract.startupNoWrite.correction.auditedArtifactVerifiedBeforeFailure) 'P15A failed preflight must record audited artifact verification.'
    Assert-False ([bool]$contract.startupNoWrite.correction.guiStarted) 'P15A failed preflight must record GUI not started.'
    Assert-False ([bool]$contract.startupNoWrite.correction.ecSetpointProbeExecuted) 'P15A failed preflight must record no EC setpoint probe.'
    Assert-False ([bool]$contract.startupNoWrite.correction.fanWriteExecuted) 'P15A failed preflight must record no fan write.'
    Assert-False ([bool]$contract.startupNoWrite.correction.firmwareRestoreExecuted) 'P15A failed preflight must record no restore.'
    Assert-False ([bool]$contract.startupNoWrite.correction.watchdogLeaseAcquired) 'P15A failed preflight must record no watchdog lease.'
    Assert-False ([bool]$contract.startupNoWrite.correction.evidencePackageProduced) 'P15A failed preflight did not produce an evidence package.'
    Assert-True ([bool]$contract.startupNoWrite.correction.payloadRootResolverImplemented) 'P15A payload-root resolver correction missing.'
    Assert-True ([bool]$contract.startupNoWrite.correction.payloadRootResolverSelfTestImplemented) 'P15A payload-root resolver self-test missing.'
    if([string]$contract.status -eq 'P15A_STARTUP_NO_WRITE_PAYLOAD_LAYOUT_CORRECTION_CI_PENDING_GATE_CLOSED'){
        Assert-False ([bool]$contract.startupNoWrite.correction.ciValidated) 'Pending P15A correction must not pre-claim CI validation.'
        Assert-False ([bool]$contract.startupNoWrite.correction.closure.closed) 'Pending P15A correction must not pre-close correction.'
    }
    if([string]$contract.status -eq 'P15A_STARTUP_NO_WRITE_PAYLOAD_LAYOUT_CORRECTION_CI_PASS_GATE_CLOSED'){
        Assert-True ([bool]$contract.startupNoWrite.correction.ciValidated) 'Closed P15A correction must be CI validated.'
        Assert-True ([bool]$contract.startupNoWrite.correction.closure.closed) 'Closed P15A correction must record closure.'
        if([string]$contract.startupNoWrite.correction.closure.result -cne 'PASS' -or [string]$contract.startupNoWrite.correction.closure.implementationHead -cne '2a56913030c403637618e98112eaf03accde2b31' -or [int]$contract.startupNoWrite.correction.closure.sourceCiRunNumber -ne 1093 -or [long]$contract.startupNoWrite.correction.closure.sourceCiRunId -ne 36922825446 -or [string]$contract.startupNoWrite.correction.closure.sourceCiResult -cne 'SUCCESS'){throw 'P15A correction closure CI identity mismatch.'}
        Assert-True ([bool]$contract.startupNoWrite.correction.closure.payloadLayoutSelfTestValidated) 'P15A correction closure must record payload-layout self-test.'
        Assert-True ([bool]$contract.startupNoWrite.correction.closure.powerShell51CompatibilityValidated) 'P15A correction closure must record PowerShell 5.1 validation.'
        Assert-True ([bool]$contract.startupNoWrite.correction.closure.warningsAsErrorsBuildValidated) 'P15A correction closure must record warnings-as-errors build.'
        Assert-False ([bool]$contract.startupNoWrite.correction.closure.hardwareExecution) 'P15A correction closure must record no hardware execution.'
    }
}
Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B Manual 30/30 must remain closed.'
Assert-False ([bool]$contract.automatic.executionAuthorized) 'Automatic execution must remain closed.'
Assert-False ([bool]$contract.safetyBoundary.controlEnabledByDefault) 'P15A default control must remain OFF.'
Assert-False ([bool]$contract.safetyBoundary.automaticPolicyEnabled) 'P15A automatic policy must remain OFF.'
Assert-False ([bool]$profile.control.enabledByDefault) 'Profile default control must remain OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Profile automatic policy must remain OFF.'
Assert-Contains $gate 'ManualExecutionAuthorized = false' 'Manual GUI gate must remain CLOSED.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'Automatic GUI gate must remain CLOSED.'
Assert-Contains $watchdogGate 'public static readonly bool ProductionConstructionAuthorized = true;' 'P15A ordinary startup requires already-promoted M9 production construction.'
Assert-Contains $watchdogGate 'M9CPhysicalQualificationConstructionAuthorized = false' 'P15A must not reopen M9C.'
Assert-Contains $watchdogGate 'M9DPhysicalQualificationConstructionAuthorized = false' 'P15A must not reopen M9D.'
foreach($n in @('P15A AUTHORIZATION BARRIER','expand-p15-rc-payload.ps1','executionAuthorized=false','Assert-RepositoryHead','Assert-ExactTarget','verify-p14-artifact-stage.ps1','auditedRcArtifactSha256','auditedRcPayloadZipSha256','Assert-ServiceCommon','Assert-ServiceIntegrity','qualifiedInstalledWatchdogExeSha256','qualifiedInstalledPawnIoModuleSha256','--m4-8c40-lease-service','Get-StrictFirmwareProof','Assert-NoJournal','nonFirmwareSetpointEvidenceDetected','watchdogJournalEvidenceDetected','HP 8C40 production watchdog-backed backend selected through the explicit M9 promotion gate; automatic policy remains OFF.','P13 UI: startup mode=Firmware; manualGate=False; automaticGate=False.','Recovery completed; telemetry is healthy after 3 complete snapshots.','requiredFirmwareSamplesDuring','P15A-OBSERVED','tray menu','package-p15-startup-evidence.ps1','FAIL_CLOSED')){Assert-Contains $harness $n ("P15A harness invariant missing: {0}" -f $n)}
foreach($n in @('SetFanLevel(','--restore-hp-auto','Start-Service','Stop-Service','Set-Service','New-Service','sc.exe ','NamedPipeFanControlWatchdogLeaseClient','CreateLeaseIfAuthorized','shutdown.exe','SetSuspendState','git clean','--first-fan-write-test')){Assert-NotContains $harness $n ("P15A harness contains forbidden active operation: {0}" -f $n)}
Assert-Contains $payloadResolver '[IO.Path]::GetFileNameWithoutExtension' 'P15A resolver must bind package-root name to inner ZIP name.'
Assert-Contains $payloadResolver 'PACKAGE-MANIFEST.json' 'P15A resolver must require package manifest under the resolved root.'
Assert-Contains $payloadResolver 'app\VictusFanControl.App.exe' 'P15A resolver must require GUI under package-root/app.'
Assert-Contains $payloadResolver 'app\modules' 'P15A resolver must return modules under package-root/app.'
foreach($n in @('SetFanLevel(','RestoreFirmwareAuto','Start-Service','Stop-Service','Set-Service','New-Service','sc.exe ','NamedPipeFanControlWatchdogLeaseClient','CreateLeaseIfAuthorized','shutdown.exe','SetSuspendState')){Assert-NotContains $payloadResolver $n ("P15A payload resolver contains forbidden operation: {0}" -f $n)}
Assert-Contains $payloadResolverSelfTest 'VictusFanControl-0.4.0-rc.1-win-x64/app/VictusFanControl.App.exe' 'P15A payload-layout self-test must model the deterministic nested package root.'
Assert-Contains $payloadResolverSelfTest 'P15A RC payload layout self-test: PASS' 'P15A payload-layout self-test PASS marker missing.'
Assert-Contains $packager 'sourceEvidencePreserved=$true' 'P15A evidence packager preservation marker missing.'
Assert-Contains $packager 'gitCleanUsed=$false' 'P15A evidence packager must record no git clean.'
Assert-NotContains $packager 'git clean' 'P15A packager must never invoke git clean.'
Assert-Contains $doc 'P15A — startup / no-write' 'P15A documentation section missing.'
if($executionAuthorized){Assert-Contains $doc 'P15A authorization window' 'P15A authorization documentation missing.'}
Assert-Contains $doc 'P15A payload-layout correction' 'P15A payload-layout correction documentation missing.'
Assert-Contains $doc 'P15B' 'P15B separation documentation missing.'
Write-Host 'HP 8C40 P15A startup/no-write preparation invariant: PASS' -ForegroundColor Green
