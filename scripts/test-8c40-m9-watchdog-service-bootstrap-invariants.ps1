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
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'Service bootstrap preparation must not authorize M9D physical execution.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'Service bootstrap preparation must not authorize M9D construction.'

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
