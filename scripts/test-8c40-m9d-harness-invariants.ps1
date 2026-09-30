$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$harness=Get-Content (Join-Path $PSScriptRoot 'test-8c40-production-watchdog-m9d.ps1') -Raw
$failsafe=Get-Content (Join-Path $PSScriptRoot 'watchdog-m9d-service-failsafe-8c40.ps1') -Raw
$packager=Get-Content (Join-Path $PSScriptRoot 'package-m9d-evidence.ps1') -Raw
$app=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$m9d=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualification.cs') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized) 'M9D physical authorization must remain false during harness preparation.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9D physical execution must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9D construction must remain blocked.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9D harness preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9D harness preparation must not promote production construction.'
if([string]$profile.watchdogM9ProductionIntegration.m9d.codeCi.fullHarness.result -cne 'PASS'){throw 'M9D full harness code/CI PASS must remain recorded.'}
if([string]$profile.watchdogM9ProductionIntegration.m9d.codeCi.fullHarness.commit -cne 'da0d366aaddb0f2584b898a45df013d823ed9423'){throw 'M9D full harness evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9d.codeCi.fullHarness.runNumber -ne 852){throw 'M9D full harness CI run number changed.'}

$barrier=$harness.IndexOf('# HARD VERSIONED AUTHORIZATION BARRIER',[StringComparison]::Ordinal)
$admin=$harness.IndexOf('Assert-Administrator',[StringComparison]::Ordinal)
$evidence=$harness.IndexOf('New-Item -ItemType Directory -Force -Path $evidenceRoot',[StringComparison]::Ordinal)
$startService=$harness.IndexOf('Start-Service -Name $serviceName',[StringComparison]::Ordinal)
if($barrier-lt 0 -or $admin-lt 0 -or $evidence-lt 0 -or $startService-lt 0 -or
   -not ($barrier-lt $admin -and $barrier-lt $evidence -and $barrier-lt $startService)){
    throw 'M9D hard profile barrier must precede Administrator/evidence/service active paths.'
}

foreach($needle in @(
    'm9b.noWritePreflightPassed',
    'm9c.physicalPassed',
    'm9d.physicalAuthorization.authorized',
    'm9d.physicalExecutionAuthorized',
    'm9d.qualificationConstructionAuthorized',
    'feature/victus-8c40-m9d-preparation',
    'Assert-RepositoryProvenance',
    'Assert-ExactTarget',
    'Assert-ServiceBaseline',
    'Assert-NoExistingM9DMarkers',
    'Move-Item -LiteralPath $Source -Destination $destination',
    'schema-v2 generation-3 OWNED 30/30',
    'Start-M9DFailsafe',
    'Kill-ExactGui',
    'Parent issues no HP restore',
    'WATCHDOG OWNER LOSS:',
    'disposition=RestoredFirmware',
    'M9D-SLEEP',
    'Windows Start -> Power -> Sleep',
    'Wait-ModernStandbyKernelEvidence',
    '$_.Id -eq 506',
    '$_.Id -eq 507',
    '$_.Id -eq 524',
    'GUID_SESSION_DISPLAY_STATUS/Off',
    'acceptedUserResumes',
    'M9D D2 causal log invalid',
    'package-m9d-evidence.ps1',
    'was NOT deleted'
)){
    Assert-Contains $harness $needle ("M9D harness invariant missing: {0}" -f $needle)
}

Assert-NotContains $harness 'install-watchdog-m4-8c40.ps1' 'M9D must not install/replace the qualified watchdog service.'
Assert-NotContains $harness '--restore-hp-auto' 'M9D parent must never invoke direct HP restore.'
Assert-NotContains $harness 'SetFanLevel(' 'M9D parent must not issue fan writes.'
Assert-NotContains $harness 'shutdown.exe' 'M9D parent must not programmatically request sleep/hibernate.'
Assert-NotContains $harness 'SetSuspendState' 'M9D parent must not programmatically request sleep.'
Assert-NotContains $harness 'Remove-Item' 'M9D parent must never delete journal/marker/evidence to satisfy a gate.'

$d1Failsafe=$harness.IndexOf('$failsafe=Start-M9DFailsafe $failsafeD1Log',[StringComparison]::Ordinal)
$d1App=$harness.IndexOf('$app=Start-M9DApp',$d1Failsafe,[StringComparison]::Ordinal)
$d1Ready=$harness.IndexOf('$ready=Wait-ReadyMarker $app',$d1App,[StringComparison]::Ordinal)
$d1Journal=$harness.IndexOf('Assert-OwnedJournal $journal $d1GuiPid',$d1Ready,[StringComparison]::Ordinal)
$d1Kill=$harness.IndexOf('Kill-ExactGui $app $d1GuiStartTicks',$d1Journal,[StringComparison]::Ordinal)
if($d1Failsafe-lt 0 -or $d1App-lt 0 -or $d1Ready-lt 0 -or $d1Journal-lt 0 -or $d1Kill-lt 0 -or
   -not ($d1Failsafe-lt $d1App -and $d1App-lt $d1Ready -and $d1Ready-lt $d1Journal -and $d1Journal-lt $d1Kill)){
    throw 'M9D D1 order must be failsafe -> GUI -> READY -> journal proof -> exact GUI kill.'
}

$d2Baseline=$harness.IndexOf("Assert-StableFirmwareOwned 'M9D D2 baseline'",[StringComparison]::Ordinal)
$d2Failsafe=$harness.IndexOf('$failsafe=Start-M9DFailsafe $failsafeD2Log',$d2Baseline,[StringComparison]::Ordinal)
$d2App=$harness.IndexOf('$app=Start-M9DApp',$d2Failsafe,[StringComparison]::Ordinal)
$d2Ready=$harness.IndexOf('$ready=Wait-ReadyMarker $app',$d2App,[StringComparison]::Ordinal)
$sleepConfirm=$harness.IndexOf("Read-Host 'Type exactly M9D-SLEEP",$d2Ready,[StringComparison]::Ordinal)
$result=$harness.IndexOf('Wait-ResultMarker $app',$sleepConfirm,[StringComparison]::Ordinal)
if($d2Baseline-lt 0 -or $d2Failsafe-lt 0 -or $d2App-lt 0 -or $d2Ready-lt 0 -or $sleepConfirm-lt 0 -or $result-lt 0 -or
   -not ($d2Baseline-lt $d2Failsafe -and $d2Failsafe-lt $d2App -and $d2App-lt $d2Ready -and $d2Ready-lt $sleepConfirm -and $sleepConfirm-lt $result)){
    throw 'M9D D2 order must be clean baseline -> failsafe -> fresh GUI -> READY -> explicit user sleep confirmation -> result.'
}

foreach($needle in @(
    'M9D FAILSAFE ARMED:',
    'WRITE_ARMED',
    'OWNED',
    'RESTORING',
    'Pending.Cpu -eq 30',
    'Owned.Cpu -eq 30',
    'ProcessStartUtcTicks',
    'M9D FAILSAFE TAKEOVER:',
    'M9D FAILSAFE CONTROLLER-KILL:',
    'Wait-JournalGone'
)){
    Assert-Contains $failsafe $needle ("M9D failsafe invariant missing: {0}" -f $needle)
}
Assert-NotContains $failsafe 'Remove-Item' 'M9D failsafe must never delete retained journal evidence.'

foreach($needle in @(
    'm9d-package-manifest.json',
    'Get-FileHash',
    'Compress-Archive',
    'ZipSha256',
    'destructiveOperations=$false',
    'm9d-retained-lease-final.json',
    'main-form-source',
    'app-program-source'
)){
    Assert-Contains $packager $needle ("M9D packager invariant missing: {0}" -f $needle)
}
Assert-NotContains $packager 'Remove-Item' 'M9D evidence packager must not delete historical evidence.'

Assert-Contains $app 'M9D refused because preserved lifecycle marker evidence already exists' 'M9D App must refuse stale marker evidence instead of deleting it.'
Assert-Contains $program '!Hp8C40M9DProductionLifecycleQualification.PhysicalExecutionAuthorized' 'M9D App execution gate must remain closed.'
Assert-Contains $m9d 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M9D compile-time execution must remain closed.'
Assert-Contains $gate 'public static readonly bool M9DPhysicalQualificationConstructionAuthorized = false;' 'M9D construction gate must remain closed.'

Write-Host 'HP 8C40 M9D parent harness/failsafe/evidence invariant: PASS' -ForegroundColor Green
