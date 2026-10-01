$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}

function Assert-Contains([string]$Text, [string]$Needle, [string]$Message) {
    Assert-True ($Text.Contains($Needle, [System.StringComparison]::Ordinal)) $Message
}

$root = Split-Path -Parent $PSScriptRoot
$profilePath = Join-Path $root 'profiles\HP-8C40.json'
$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$target = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40TargetProfile.cs') -Raw
$gate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw
$readmeEs = Get-Content -LiteralPath (Join-Path $root 'README.es.md') -Raw
$adaptive = Get-Content -LiteralPath (Join-Path $root 'docs\ADAPTIVE_POLICY_PREPARATION.md') -Raw
$roadmap = Get-Content -LiteralPath (Join-Path $root 'docs\POST_M9_SOFTWARE_ROADMAP.md') -Raw

Assert-True ($profile.lifecycle.watchdogRecoveryValidated -eq $true) 'P10 requires promoted watchdogRecoveryValidated=true.'
Assert-True ($profile.lifecycle.watchdogM9PhysicalPassed -eq $true) 'P10 requires formally passed M9 physical lifecycle evidence.'
Assert-True ($profile.watchdogM9ProductionIntegration.m9d.physicalPassed -eq $true) 'P10 requires M9D physicalPassed=true.'
Assert-True ($profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized -eq $false) 'M9D authorization must remain consumed/reblocked.'
Assert-True ($profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized -eq $false) 'M9D execution must remain reblocked.'
Assert-True ($profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized -eq $false) 'M9D construction must remain reblocked.'
Assert-True ($profile.watchdogM9ProductionIntegration.promotion.authorized -eq $true) 'M9 production promotion must remain authorized.'
Assert-True ($profile.control.enabledByDefault -eq $false) 'Default fan control must remain OFF.'
Assert-True ($profile.loadThermalM8Qualification.automaticPolicyEnabled -eq $false) 'Automatic policy must remain OFF.'

Assert-Contains $target 'WatchdogRecoveryValidated: true' 'Target profile must preserve M9 watchdog recovery promotion.'
Assert-Contains $gate 'ProductionConstructionAuthorized = true' 'Production watchdog construction must remain promoted.'
Assert-Contains $gate 'M9CPhysicalQualificationConstructionAuthorized = false' 'M9C qualification construction must remain closed.'
Assert-Contains $gate 'M9DPhysicalQualificationConstructionAuthorized = false' 'M9D qualification construction must remain closed.'

Assert-Contains $readme 'HP 8C40 / 9D0R1LA / BIOS F.18' 'README must describe the current exact target.'
Assert-Contains $readme 'control.enabledByDefault=false' 'README must preserve default-control OFF.'
Assert-Contains $readmeEs 'automaticPolicyEnabled=false' 'Spanish README must preserve automatic-policy OFF.'
Assert-True (-not $readme.Contains('Current development status: v0.4 backend integration.', [System.StringComparison]::Ordinal)) 'README must not advertise the obsolete v0.4/88F8 current state.'
Assert-Contains $adaptive 'WatchdogRecoveryValidated=true' 'Adaptive policy document must reflect post-M9 watchdog promotion.'
Assert-Contains $roadmap 'P15' 'Post-M9 roadmap must preserve the separate target-side checkpoint.'

Write-Host 'HP 8C40 P10 post-M9 software invariant: PASS'
