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
$meta=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualificationTest.cs') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

$m9d=$profile.watchdogM9ProductionIntegration.m9d
if(-not [bool]$profile.lifecycle.watchdogM9DCodeCiPassed){throw 'M9D full harness CODE/CI PASS must remain recorded.'}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed){throw 'M9D EC-contention fix requires formally closed M9C physical PASS.'}
Assert-False ([bool]$m9d.physicalAuthorization.authorized) 'M9D retry authorization must be reblocked after FAIL_CLOSED.'
Assert-False ([bool]$m9d.physicalExecutionAuthorized) 'M9D execution must be reblocked while EC retry fix CI is pending.'
Assert-False ([bool]$m9d.qualificationConstructionAuthorized) 'M9D construction must be reblocked while EC retry fix CI is pending.'
if([string]$m9d.physicalAuthorization.result -cne 'FAIL_CLOSED'){throw 'M9D failed retry result must remain FAIL_CLOSED.'}
if([string]$m9d.ecMutexContentionFix.status -cne 'CODE_PREPARED_CI_PENDING'){throw 'M9D EC contention fix must remain CI-pending.'}
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9D must not promote WatchdogRecoveryValidated.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9D must not promote normal production construction.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9D must keep default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9D must keep automatic/adaptive policy OFF.'
if([string]$m9d.expectedPhysicalBranch -cne 'feature/victus-8c40-m9-canonical-prehardware'){throw 'M9D expected physical branch must stay on the canonical pre-hardware line.'}

$barrier=$harness.IndexOf('# HARD VERSIONED AUTHORIZATION BARRIER.',[StringComparison]::Ordinal)
$admin=$harness.IndexOf('Assert-Administrator',[StringComparison]::Ordinal)
$evidenceCreate=$harness.IndexOf('New-Item -ItemType Directory -Path $markerRoot',[StringComparison]::Ordinal)
$appStart=$harness.IndexOf('$app=Start-M9DTrackedChild',[StringComparison]::Ordinal)
if($barrier-lt 0 -or $admin-lt 0 -or $evidenceCreate-lt 0 -or $appStart-lt 0 -or
   -not ($barrier-lt $admin -and $barrier-lt $evidenceCreate -and $barrier-lt $appStart)){
    throw 'M9D hard authorization barrier must precede Administrator/evidence/GUI active paths.'
}

$normalActivePrefix=$harness.Substring($barrier,$appStart-$barrier)
Assert-NotContains $normalActivePrefix 'Start-Service -Name $serviceName' 'M9D parent must leave Manual/Stopped watchdog bootstrap to the GUI production route.'

foreach($needle in @(
    'm9b.noWritePreflightPassed',
    'm9c.physicalPassed',
    'm9d.physicalAuthorization.authorized',
    'm9d.physicalExecutionAuthorized',
    'm9d.qualificationConstructionAuthorized',
    'expectedPhysicalBranch',
    'Assert-RepositoryProvenance',
    'Assert-ExactTarget',
    'M4 Manual/Stopped/PID0',
    'Wait-StableFF',
    'Assert-OwnedJournal',
    'schema-v2 generation-3 OWNED 30/30',
    '--8c40-m9d-production-lifecycle-test',
    '--8c40-m9d-marker-root',
    'constructionRoute=production-factory-public-backend',
    'serviceBootstrap=gui-ensure-ready',
    'bootstrapStarted=True',
    'transitionMode=m9d-production-modern-standby',
    'Start -> Power -> Sleep',
    'Kernel-Power',
    'M9D FAILSAFE',
    'package-m9d-evidence.ps1'
)){
    Assert-Contains $harness $needle ("M9D harness invariant missing: {0}" -f $needle)
}

Assert-NotContains $harness 'install-watchdog-m4-8c40.ps1' 'M9D must not install/replace the qualified watchdog service.'
Assert-NotContains $harness 'sc.exe config' 'M9D must not mutate watchdog service configuration.'
Assert-NotContains $harness 'shutdown.exe' 'M9D parent must not dispatch the power transition.'
Assert-NotContains $harness 'SetSuspendState' 'M9D parent must not dispatch suspend programmatically.'
Assert-NotContains $harness 'Remove-Item $journalPath' 'M9D must never delete retained durable journal evidence.'
Assert-NotContains $harness 'git clean' 'M9D must never clean evidence to satisfy a gate.'

foreach($needle in @(
    'M9D FAILSAFE ARMED:',
    'WRITE_ARMED',
    'OWNED',
    'RESTORING',
    'M9D FAILSAFE TAKEOVER:',
    'M9D FAILSAFE CONTROLLER-KILL:',
    'M9D FAILSAFE SERVICE-START:',
    'M9D FAILSAFE RECOVERED:'
)){
    Assert-Contains $failsafe $needle ("M9D failsafe invariant missing: {0}" -f $needle)
}
Assert-NotContains $failsafe 'Remove-Item' 'M9D failsafe must preserve retained journal/evidence.'

foreach($needle in @(
    'System.Diagnostics.ProcessStartInfo',
    'UseShellExecute=$false',
    'WaitForExit',
    'ExitCode'
)){
    Assert-Contains $tracked $needle ("M9D tracked-child invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'm9d-package-manifest.json',
    'Get-FileHash',
    'Compress-Archive',
    'ZipSha256',
    'destructiveOperations=$false',
    'm9d-retained-lease-final.json',
    'm9d-app-full.log'
)){
    Assert-Contains $packager $needle ("M9D evidence packaging invariant missing: {0}" -f $needle)
}
Assert-NotContains $packager 'Remove-Item' 'M9D packager must never delete evidence.'

Assert-Contains $meta 'PhysicalExecutionAuthorized = false;' 'M9D compile-time execution gate must be reclosed while EC retry fix is qualified.'
Assert-Contains $gate 'M9DPhysicalQualificationConstructionAuthorized = false;' 'M9D construction gate must be reclosed while EC retry fix is qualified.'
if($harness -match '(?i)\$pid\b'){throw 'M9D harness must not declare or assign $Pid/$PID because PowerShell reserves automatic variable $PID.'}
Assert-Contains $appProgram 'Hp8C40M9DProductionLifecycleQualificationTest.PhysicalExecutionAuthorized' 'M9D app must enforce compile-time physical gate before MainForm.'
Assert-Contains $appProgram '--8c40-m9d-marker-root' 'M9D app must require isolated marker root.'
Assert-Contains $mainForm 'M9D refuses to overwrite existing evidence marker' 'M9D GUI must refuse marker overwrite.'
Assert-Contains $mainForm 'constructionRoute=' 'M9D GUI must publish construction-route evidence.'
Assert-Contains $m4Hardware 'EcMutexReadAttempts = 4' 'M9D EC contention fix must pin four bounded ownership-read attempts.'
Assert-Contains $m4Hardware 'TimeSpan.FromMilliseconds(75)' 'M9D EC contention fix must pin the short retry delay.'
Assert-Contains $m4Hardware 'Global\Access_EC' 'M9D EC contention fix must be restricted to the named EC mutex contention.'
Assert-Contains $m4Hardware 'catch (TimeoutException ex)' 'M9D EC contention fix must retry only TimeoutException-based mutex contention.'

Write-Host 'HP 8C40 M9D parent harness invariant: PASS' -ForegroundColor Green
