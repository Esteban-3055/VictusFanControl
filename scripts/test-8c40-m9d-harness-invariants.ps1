$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$harness=Get-Content (Join-Path $PSScriptRoot 'test-8c40-production-watchdog-m9d.ps1') -Raw
$failsafe=Get-Content (Join-Path $PSScriptRoot 'watchdog-m9d-service-failsafe-8c40.ps1') -Raw
$tracked=Get-Content (Join-Path $PSScriptRoot 'm9d-tracked-child.ps1') -Raw
$packager=Get-Content (Join-Path $PSScriptRoot 'package-m9d-evidence.ps1') -Raw
$appProgram=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$contract=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualification.cs') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

if(-not [bool]$profile.lifecycle.watchdogM9DCodeCiPassed){throw 'M9D full harness CODE/CI PASS must remain recorded.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized) 'M9D physical authorization must remain false during code preparation.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9D execution must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9D construction must remain blocked.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9D harness preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9D harness preparation must not promote production construction.'

$barrier=$harness.IndexOf('# HARD VERSIONED AUTHORIZATION BARRIER',[StringComparison]::Ordinal)
$admin=$harness.IndexOf('Assert-Administrator',[StringComparison]::Ordinal)
$evidence=$harness.IndexOf('New-Item -ItemType Directory -Path $markerRoot',[StringComparison]::Ordinal)
$startService=$harness.IndexOf('Start-Service -Name $serviceName',[StringComparison]::Ordinal)
$appStart=$harness.IndexOf('$app=Start-M9DTrackedChild',[StringComparison]::Ordinal)
if($barrier-lt 0 -or $admin-lt 0 -or $evidence-lt 0 -or $startService-lt 0 -or $appStart-lt 0 -or
   -not ($barrier-lt $admin -and $barrier-lt $evidence -and $barrier-lt $startService -and $barrier-lt $appStart)){
    throw 'M9D hard profile barrier must precede Administrator/evidence/service/App active boundaries.'
}

foreach($needle in @(
    'm9b.noWritePreflightPassed',
    'm9c.physicalPassed',
    'm9d.physicalAuthorization.authorized',
    'm9d.physicalExecutionAuthorized',
    'm9d.qualificationConstructionAuthorized',
    'feature/victus-8c40-m9d-production-lifecycle',
    'Assert-RepositoryProvenance',
    'Assert-ExactTarget',
    'Assert-ServiceBaseline',
    'M4 Manual/Stopped/PID0',
    'Read-StableFirmwareOwned',
    'Start-M9DFailsafe',
    'M9D FAILSAFE ARMED:',
    '--8c40-m9d-production-lifecycle-test',
    '--8c40-m9d-marker-root',
    'Read-OwnedJournal $appPid $appStartTicks',
    'M9D-SLEEP',
    'Sleep is manual only',
    'presleep.marker',
    'resume-gate.marker',
    'reentry.marker',
    'result.marker',
    'Read-ModernStandbyEvidence',
    'm9d-retained-lease-before-recovery.json',
    'evidence copied, not deleted',
    'package-m9d-evidence.ps1'
)){
    Assert-Contains $harness $needle ("M9D harness invariant missing: {0}" -f $needle)
}

Assert-NotContains $harness 'install-watchdog-m4-8c40.ps1' 'M9D must never reinstall/replace the qualified M4 service.'
Assert-NotContains $harness 'Remove-Item' 'M9D parent must never delete journal/evidence.'
Assert-NotContains $harness 'shutdown.exe' 'M9D parent must not dispatch a power transition.'
Assert-NotContains $harness 'SetSuspendState' 'M9D parent must not dispatch suspend.'

foreach($needle in @('System.Diagnostics.ProcessStartInfo','UseShellExecute=$false','WaitForExit','ExitCode')){
    Assert-Contains $tracked $needle ("M9D tracked-child invariant missing: {0}" -f $needle)
}

foreach($needle in @('M9D FAILSAFE ARMED:','WRITE_ARMED','OWNED','RESTORING','ProcessStartUtcTicks','M9D FAILSAFE TAKEOVER:','Wait-JournalGone')){
    Assert-Contains $failsafe $needle ("M9D failsafe invariant missing: {0}" -f $needle)
}
Assert-NotContains $failsafe 'Remove-Item' 'M9D failsafe must never delete a retained journal.'

foreach($needle in @('m9d-package-manifest.json','Get-FileHash','Compress-Archive','ZipSha256','destructiveOperations=$false','m9d-retained-lease-final.json')){
    Assert-Contains $packager $needle ("M9D packaging invariant missing: {0}" -f $needle)
}
Assert-NotContains $packager 'Remove-Item' 'M9D packager must not delete source evidence.'

Assert-Contains $contract 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M9D compile-time App gate must remain closed.'
Assert-Contains $gate 'public static readonly bool M9DPhysicalQualificationConstructionAuthorized = false;' 'M9D construction gate must remain closed.'
Assert-Contains $appProgram '// HARD M9D BARRIER' 'M9D App hard barrier is missing.'
Assert-Contains $mainForm 'M9D refuses to overwrite existing marker evidence' 'M9D App must preserve existing marker evidence.'

Write-Host 'HP 8C40 M9D parent harness/failsafe/evidence invariant: PASS' -ForegroundColor Green
