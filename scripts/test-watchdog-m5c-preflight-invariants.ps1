$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$preflightPath = Join-Path $PSScriptRoot 'test-watchdog-m5c-preflight-8c40.ps1'
$preflight = Get-Content $preflightPath -Raw

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

Write-Host 'HP 8C40 M5C no-write preflight invariant self-test: PASS' -ForegroundColor Green
