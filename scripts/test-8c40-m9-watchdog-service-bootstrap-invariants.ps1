$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$bootstrap=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Hp8C40WatchdogServiceBootstrap.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$manifest=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\app.manifest') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'Service bootstrap preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'Service bootstrap preparation must not open production construction.'
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed){throw 'Service bootstrap M9D EC fix requires formally closed M9C physical PASS.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'Service bootstrap invariant must observe M9D execution reblocked during EC fix CI.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'Service bootstrap invariant must observe M9D construction reblocked during EC fix CI.'
if(-not [bool]$profile.lifecycle.watchdogM9DServiceBootstrapCodeCiPassed){throw 'GUI service-bootstrap CODE/CI PASS must remain recorded.'}
if([string]$profile.watchdogM9ProductionIntegration.m9d.serviceBootstrap.codeCi.result -cne 'PASS'){throw 'GUI service-bootstrap code/CI result changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9d.serviceBootstrap.codeCi.commit -cne 'f322e195523b8001b11449f8d18cb7f2facf5d74'){throw 'GUI service-bootstrap code/CI commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9d.serviceBootstrap.codeCi.runNumber -ne 901){throw 'GUI service-bootstrap CI run changed.'}

foreach($needle in @(
    'ServiceName = "VictusFanControlWatchdogM4"',
    'ServiceStartMode.Manual',
    'ServiceControllerStatus.Stopped',
    'service.Start()',
    'M6WatchdogStateReader.RequireReady(snapshot)',
    'snapshot.JournalPresent',
    'EnsureProductionReady',
    'EnsureM9DQualificationReady',
    'RequireProductionConstructionAuthorized'
)){
    Assert-Contains $bootstrap $needle ("M9 service-bootstrap invariant missing: {0}" -f $needle)
}

foreach($forbidden in @(
    'SetFanLevel',
    'RestoreFirmwareAuto',
    'sc.exe',
    'DeleteService',
    'ChangeServiceConfig',
    'File.Delete',
    'Remove-Item'
)){
    Assert-NotContains $bootstrap $forbidden ("M9 service bootstrap contains forbidden operation: {0}" -f $forbidden)
}

Assert-Contains $mainForm 'Hp8C40WatchdogServiceBootstrap' 'MainForm does not consume GUI-side service bootstrap.'
Assert-Contains $mainForm 'EnsureProductionReady' 'Normal production path does not bootstrap watchdog service.'
Assert-Contains $mainForm 'EnsureM9DQualificationReady' 'M9D route does not exercise the same GUI-side service bootstrap.'
Assert-Contains $mainForm 'serviceBootstrap=' 'M9D READY evidence does not identify GUI-side service bootstrap.'
Assert-Contains $manifest 'requestedExecutionLevel level="requireAdministrator"' 'GUI service bootstrap requires the existing elevated application manifest.'

Write-Host 'HP 8C40 M9 GUI watchdog service-bootstrap invariant: PASS' -ForegroundColor Green
