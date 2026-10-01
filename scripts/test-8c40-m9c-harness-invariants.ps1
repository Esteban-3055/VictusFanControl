$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$harness=Get-Content (Join-Path $PSScriptRoot 'test-8c40-production-watchdog-m9c.ps1') -Raw
$failsafe=Get-Content (Join-Path $PSScriptRoot 'watchdog-m9c-service-failsafe-8c40.ps1') -Raw
$tracked=Get-Content (Join-Path $PSScriptRoot 'm9c-tracked-child.ps1') -Raw
$packager=Get-Content (Join-Path $PSScriptRoot 'package-m9c-evidence.ps1') -Raw
$controller=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9CProductionPathQualificationTest.cs') -Raw
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

if(-not [bool]$profile.lifecycle.watchdogM9CCodeCiPassed){throw 'M9C full harness CODE/CI PASS must remain recorded.'}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed){throw 'M9C authorization requires formal M9B physical read-only PASS.'}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed){throw 'M9C physical PASS must be formally recorded.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalAuthorization.authorized) 'M9C physical authorization must be re-blocked after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9C controller execution gate must be reclosed after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9C temporary construction gate must be reclosed after PASS.'
if([string]$profile.watchdogM9ProductionIntegration.m9c.physicalEvidence.evidenceZipSha256 -cne '4851434d5cbf59b7ae326857fa9757803e855bd03ac447654eddb8c0a1a698f3'){throw 'M9C parent invariant evidence hash changed.'}
Assert-True ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'Post-M9 promotion must keep the M9C harness closed while watchdog recovery is enabled.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9C harness preparation must not promote production construction.'
if([string]$profile.watchdogM9ProductionIntegration.m9c.expectedPhysicalBranch -cne 'feature/victus-8c40-m9-canonical-prehardware'){throw 'M9C expected physical branch must stay on the canonical pre-hardware line.'}

$barrier=$harness.IndexOf('# HARD VERSIONED AUTHORIZATION BARRIER',[StringComparison]::Ordinal)
$admin=$harness.IndexOf('Assert-Administrator',[StringComparison]::Ordinal)
$startService=$harness.IndexOf('Start-Service -Name $serviceName',[StringComparison]::Ordinal)
$evidenceCreate=$harness.IndexOf('New-Item -ItemType Directory -Force -Path $evidenceRoot',[StringComparison]::Ordinal)
if($barrier-lt 0 -or $admin-lt 0 -or $startService-lt 0 -or $evidenceCreate-lt 0 -or
   -not ($barrier-lt $admin -and $barrier-lt $startService -and $barrier-lt $evidenceCreate)){
    throw 'M9C parent hard profile authorization barrier must precede Administrator/evidence/service active paths.'
}

foreach($needle in @(
    'm9b.noWritePreflightPassed',
    'physicalAuthorization.authorized',
    'physicalExecutionAuthorized',
    'qualificationConstructionAuthorized',
    'feature/victus-8c40-m9-canonical-prehardware',
    'Assert-RepositoryProvenance',
    'Assert-ExactTarget',
    'Assert-ServiceBaseline',
    'M4 Manual/Stopped/PID0',
    'Assert-StableFirmwareOwned',
    'for($read=1;$read -le 6;$read++)',
    '$consecutive -ge 2',
    'M9C-CONTINUE',
    'schema-v2 generation-3 OWNED 30/30',
    'ProcessStartUtcTicks',
    'Start-M9CFailsafe',
    'Start-M9CTrackedChild',
    'Wait-M9CTrackedChildExitCode',
    'HpFanControlBackendFactory.Create',
    'PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE',
    'm9c-retained-lease-before-recovery.json',
    'was NOT deleted',
    'package-m9c-evidence.ps1'
)){
    Assert-Contains $harness $needle ("M9C harness invariant missing: {0}" -f $needle)
}

Assert-NotContains $harness 'install-watchdog-m4-8c40.ps1' 'M9C must not auto-install/replace the previously qualified service.'
Assert-NotContains $harness 'Remove-Item' 'M9C parent must never delete journal/evidence to satisfy closure.'

$failsafeStart=$harness.IndexOf('$failsafe=Start-M9CFailsafe',[StringComparison]::Ordinal)
$controllerStart=$harness.IndexOf('$controller=Start-M9CTrackedChild',[StringComparison]::Ordinal)
$ready=$harness.IndexOf('$ready=Wait-ReadyMarker',[StringComparison]::Ordinal)
$journal=$harness.IndexOf('Assert-OwnedJournal',[Math]::Max(0,$ready),[StringComparison]::Ordinal)
$continue=$harness.IndexOf("'M9C-CONTINUE' | Set-Content",[StringComparison]::Ordinal)
if($failsafeStart-lt 0 -or $controllerStart-lt 0 -or $ready-lt 0 -or $journal-lt 0 -or $continue-lt 0 -or
   -not ($failsafeStart-lt $controllerStart -and $controllerStart-lt $ready -and $ready-lt $journal -and $journal-lt $continue)){
    throw 'M9C parent must arm failsafe -> launch tracked controller -> READY -> journal proof -> CONTINUE.'
}

foreach($needle in @(
    'M9C FAILSAFE ARMED:',
    'WRITE_ARMED',
    'OWNED',
    'RESTORING',
    'Pending.Cpu -eq 30',
    'Owned.Cpu -eq 30',
    'ProcessStartUtcTicks',
    'M9C FAILSAFE TAKEOVER:',
    'M9C FAILSAFE CONTROLLER-KILL:',
    'Wait-JournalGone'
)){
    Assert-Contains $failsafe $needle ("M9C failsafe invariant missing: {0}" -f $needle)
}
Assert-NotContains $failsafe 'Remove-Item' 'M9C failsafe must never delete retained journal evidence.'

foreach($needle in @(
    'System.Diagnostics.ProcessStartInfo',
    'UseShellExecute=$false',
    'WaitForExit',
    'ExitCode'
)){
    Assert-Contains $tracked $needle ("M9C tracked-child invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'm9c-package-manifest.json',
    'Get-FileHash',
    'Compress-Archive',
    'ZipSha256',
    'destructiveOperations=$false',
    'm9c-retained-lease-final.json'
)){
    Assert-Contains $packager $needle ("M9C package invariant missing: {0}" -f $needle)
}
Assert-NotContains $packager 'Remove-Item' 'M9C evidence packager must not delete historical evidence.'

Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M9C controller compile-time physical gate must be reclosed after physical PASS.'
Assert-Contains $gate 'public static readonly bool M9CPhysicalQualificationConstructionAuthorized = false;' 'M9C temporary construction gate must be reclosed after physical PASS.'

Write-Host 'HP 8C40 M9C parent harness/failsafe/evidence invariant: PASS' -ForegroundColor Green
