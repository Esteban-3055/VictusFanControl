$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$preflightPath = Join-Path $PSScriptRoot 'test-watchdog-m5c-preflight-8c40.ps1'
$failsafePath = Join-Path $PSScriptRoot 'watchdog-m5c-service-failsafe-8c40.ps1'
$preflight = Get-Content $preflightPath -Raw
$failsafe = Get-Content $failsafePath -Raw

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    if (-not $Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw $Message
    }
}

Assert-Contains $preflight 'HP-8C40-9D0R1LA-F18' 'M5C preflight must pin the exact target profile.'
Assert-Contains $preflight 'VictusFanControl.Watchdog.M4.8C40.v2' 'M5C preflight must pin the isolated target-bound M4 pipe.'
Assert-Contains $preflight 'SessionId -eq 0' 'M5C preflight must require Session 0.'
Assert-Contains $preflight 'AccountName -match ''SYSTEM$''' 'M5C preflight must require LocalSystem.'
Assert-Contains $preflight '$baseline.Cpu -ne 255' 'M5C preflight must require firmware-owned CPU FF.'
Assert-Contains $preflight '$baseline.Gpu -ne 255' 'M5C preflight must require firmware-owned GPU FF.'
Assert-Contains $preflight 'M5C preflight refuses to overwrite or delete retained durable ownership evidence.' 'M5C preflight must fail closed on an existing journal.'
Assert-Contains $preflight 'sc.exe failure $serviceName' 'M5C preflight must configure the temporary SCM restart policy.'
Assert-Contains $preflight 'restart/$RestartDelayMs/restart/5000/restart/10000' 'M5C preflight must validate the ordered restart policy.'
Assert-Contains $preflight 'sc.exe failureflag $serviceName 1' 'M5C preflight must enable SCM failure actions.'
Assert-Contains $preflight 'Restore-M4Baseline' 'M5C preflight must restore the ordinary M4 service baseline.'
Assert-Contains $preflight '$svc.StartType -ne ''Manual''' 'M5C preflight must verify Manual service startup after cleanup.'
Assert-Contains $preflight '$svc.Status -ne ''Stopped''' 'M5C preflight must verify the service is stopped after cleanup.'
Assert-Contains $preflight 'No fan-level write has been issued.' 'M5C preflight must explicitly remain no-write.'
Assert-Contains $preflight 'finally {' 'M5C preflight must restore service baseline from a finally block.'
Assert-Contains $preflight 'a durable journal appeared during a no-write preflight. It will NOT be deleted or overwritten.' 'M5C cleanup must preserve unexpected durable evidence.'
Assert-Contains $preflight '$baselineRestored = $true' 'M5C preflight must prove the ordinary M4 service baseline was restored.'
Assert-Contains $preflight 'M5C preflight could not restore the ordinary M4 service baseline without risking retained durable evidence.' 'M5C preflight must fail closed if safe cleanup is not possible.'

foreach ($forbidden in @(
    'SetFanLevel(',
    '--restore-hp-auto',
    '--8c40-m4-lease10',
    '--8c40-m4-lease30',
    '--8c40-m4-lease50',
    'Stop-Process',
    '.Kill(',
    'Remove-Item $journalPath'
)) {
    if ($preflight.Contains($forbidden, [StringComparison]::Ordinal)) {
        throw "M5C no-write preflight contains forbidden active/fault-injection token: $forbidden"
    }
}

Assert-Contains $failsafe 'HP-8C40-9D0R1LA-F18' 'M5C delayed failsafe must pin the exact target id.'
Assert-Contains $failsafe 'Test-OwnedPhase' 'M5C delayed failsafe must require durable OWNED phase.'
Assert-Contains $failsafe '[int]$journal.Owned.Cpu -ne 30' 'M5C delayed failsafe must require OWNED CPU 30.'
Assert-Contains $failsafe '[int]$journal.Owned.Gpu -ne 30' 'M5C delayed failsafe must require OWNED GPU 30.'
Assert-Contains $failsafe 'Start-Service -Name $serviceName' 'M5C delayed failsafe may only recover by starting the already-qualified service.'
Assert-Contains $failsafe 'failsafe will not delete it' 'M5C delayed failsafe must leave journal authority to the service.'

foreach ($forbidden in @(
    'SetFanLevel(',
    '--restore-hp-auto',
    'Hp8C40BiosFanControl',
    'Remove-Item $journalPath'
)) {
    if ($failsafe.Contains($forbidden, [StringComparison]::Ordinal)) {
        throw "M5C delayed service failsafe contains forbidden direct recovery authority: $forbidden"
    }
}

Write-Host 'HP 8C40 M5C no-write preflight/service-failsafe invariant self-test: PASS' -ForegroundColor Green
