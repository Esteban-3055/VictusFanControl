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
$doc=Get-Content -LiteralPath (Join-Path $root 'docs\P15_TARGET_CHECKPOINT.md') -Raw
if([string]$contract.milestone -cne 'P15A'){throw 'P15A milestone mismatch.'}
if([string]$contract.status -notin @('P15A_STARTUP_NO_WRITE_PREPARATION_IMPLEMENTED_AWAITING_CI','P15A_STARTUP_NO_WRITE_PREPARATION_CI_PASS_GATE_CLOSED')){throw 'P15A preparation status mismatch.'}
if([string]$contract.targetProfileId -cne 'HP-8C40-9D0R1LA-F18'){throw 'P15A target mismatch.'}
if([string]$contract.p14Baseline.closureHead -cne '6945b2e34526e5e266189da6553bf3ea010d3893' -or [int]$contract.p14Baseline.closureCiRunNumber -ne 1086 -or [long]$contract.p14Baseline.closureCiRunId -ne 36913062832 -or [string]$contract.p14Baseline.closureCiResult -cne 'SUCCESS'){throw 'P15A P14 closure baseline mismatch.'}
if([string]$contract.p14Baseline.auditedRcSourceHead -cne 'eebcdd5e833256466c1ae023c35f7cef8d40d6ec' -or [string]$contract.p14Baseline.auditedRcArtifactSha256 -cne '588058a8b57c0ca1bb41649288682e0981be845ab747654472d3d10c88d572b5' -or [string]$contract.p14Baseline.auditedRcPayloadZipSha256 -cne '704983caa20abb21c3520ffbd165ad69f9843139b6b2fa47d9e9e2448a32ef68'){throw 'P15A audited RC identity mismatch.'}
Assert-True ([bool]$p14.productization.finalSoftwareRcAuditClosed) 'P15A requires P14.5 formally closed.'
Assert-False ([bool]$contract.startupNoWrite.executionAuthorized) 'P15A physical startup execution must remain CLOSED during preparation.'
Assert-False ([bool]$contract.startupNoWrite.physicalPassed) 'P15A cannot pre-claim physical PASS.'
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
foreach($n in @('P15A AUTHORIZATION BARRIER','executionAuthorized=false','Assert-RepositoryHead','Assert-ExactTarget','verify-p14-artifact-stage.ps1','auditedRcArtifactSha256','auditedRcPayloadZipSha256','P13 UI: startup mode=Firmware; manualGate=False; automaticGate=False.','Recovery completed; telemetry is healthy after 3 complete snapshots.','requiredFirmwareSamplesDuring','P15A-OBSERVED','tray menu','package-p15-startup-evidence.ps1','FAIL_CLOSED')){Assert-Contains $harness $n ("P15A harness invariant missing: {0}" -f $n)}
foreach($n in @('SetFanLevel(','--restore-hp-auto','Start-Service','Stop-Service','Set-Service','New-Service','sc.exe ','NamedPipeFanControlWatchdogLeaseClient','CreateLeaseIfAuthorized','shutdown.exe','SetSuspendState','git clean','--first-fan-write-test')){Assert-NotContains $harness $n ("P15A harness contains forbidden active operation: {0}" -f $n)}
Assert-Contains $packager 'sourceEvidencePreserved=$true' 'P15A evidence packager preservation marker missing.'
Assert-Contains $packager 'gitCleanUsed=$false' 'P15A evidence packager must record no git clean.'
Assert-NotContains $packager 'git clean' 'P15A packager must never invoke git clean.'
Assert-Contains $doc 'P15A — startup / no-write' 'P15A documentation section missing.'
Assert-Contains $doc 'P15B' 'P15B separation documentation missing.'
Write-Host 'HP 8C40 P15A startup/no-write preparation invariant: PASS' -ForegroundColor Green
