$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$audit=Get-Content (Join-Path $repoRoot 'scripts\audit-8c40-m8b-attempt.ps1') -Raw
$m8=Get-Content (Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}

foreach($needle in @(
    'm8b-ready.json',
    'm8b-result.json',
    'm8b-harness-summary.json',
    'm8b-failsafe.log',
    'watchdog-m4-8c40-2026-09-30.log',
    'WATCHDOG PREPARE ACK',
    'WATCHDOG WRITE_INTENT ACK',
    'WATCHDOG COMMIT ACK',
    'WATCHDOG RESTORE_BEGIN ACK',
    'WATCHDOG RELEASE ACK',
    'failsafeTakeover',
    'finalJournalAbsent',
    'finalFirmwareProofPass',
    'finalServiceBaselinePass',
    'RETROSPECTIVE AUDIT PASS'
)){
    Assert-Contains $audit $needle ("M8B retrospective audit invariant missing: {0}" -f $needle)
}

foreach($forbidden in @(
    'Start-Service',
    'Stop-Service',
    'Restart-Service',
    'Set-Service',
    'Remove-Item',
    'SetFanLevel',
    'dotnet ',
    'sc.exe '
)){
    Assert-NotContains $audit $forbidden ("M8B retrospective audit must remain read-only: {0}" -f $forbidden)
}

Assert-Contains $m8 'retrospective' 'M8 documentation must continue to require retrospective evidence review.'
Assert-Contains $m8 'M8B physical execution remains blocked' 'M8B hardware execution block must remain documented.'

Write-Host 'M8B retrospective audit isolation/invariant self-test: PASS' -ForegroundColor Green
