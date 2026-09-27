$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - INTEGRATED COORDINATOR HARDWARE GATE' -ForegroundColor Cyan
Write-Host ''

$board = (Get-CimInstance Win32_BaseBoard -ErrorAction Stop).Product
switch ($board) {
    '88F8' {
        $targetName = 'HP 88F8'
        $token = '88F8-COORD30'
        $legacyEcProbe = $true
    }
    '8C40' {
        $targetName = 'HP 8C40'
        $token = '8C40-COORD30'
        $legacyEcProbe = $false
    }
    default {
        Write-Error "Unsupported board '$board'. No hardware write was attempted."
        exit 40
    }
}

Write-Host "Detected target candidate: $targetName"
Write-Host 'The executable will still require the full board/SKU/BIOS/GPU fingerprint.'
Write-Host ''
Write-Host 'Production path under test:'
Write-Host '  SafetyGate -> FanControlCoordinator -> exact-target HP backend'
Write-Host '  -> HP WMI -> EC acknowledgement -> both tachometers -> HP restore'
Write-Host ''
Write-Host 'Automatic fan policy remains OFF.'
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Do not run a game or stress test during this bounded validation.'
Write-Host 'Do NOT suspend/hibernate or terminate the process through Task Manager.' -ForegroundColor Yellow
if ($board -eq '8C40') {
    Write-Host '8C40 watchdog/Modern Standby recovery is NOT part of this test.' -ForegroundColor Yellow
}
Write-Host ''

Write-Host 'Step 1: building with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: synthetic safety/coordinator/backend regressions...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --bios-contract-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 3: read-only live hardware + per-core telemetry preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($legacyEcProbe) {
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-88f8-ec-state
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ''
Write-Host "The next step is an ACTIVE bounded $targetName 30/30 test." -ForegroundColor Yellow
$confirm = Read-Host "Type exactly $token to continue"
if ($confirm -cne $token) {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --integrated-coordinator-test --coordinator-write-token $token
exit $LASTEXITCODE
