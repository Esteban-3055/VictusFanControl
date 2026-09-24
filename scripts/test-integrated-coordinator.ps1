$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - INTEGRATED COORDINATOR HARDWARE GATE' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Production path under test:'
Write-Host '  SafetyGate -> FanControlCoordinator -> Hp88F8FanControlBackend'
Write-Host '  -> HP WMI -> EC acknowledgement -> both tachometers -> HP restore'
Write-Host ''
Write-Host 'Automatic fan policy remains OFF.'
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Do not run a game or stress test during this bounded validation.'
Write-Host 'Do NOT terminate the process through Task Manager.' -ForegroundColor Yellow
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
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 3: read-only live hardware preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-88f8-ec-state
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
$confirm = Read-Host 'Type COORD30 to run the bounded integrated 30/30 hardware gate'
if ($confirm -cne 'COORD30') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --integrated-coordinator-test --coordinator-write-token 88F8-COORD30
$testExit = $LASTEXITCODE

Write-Host ''
Write-Host 'Check the undervolt value in OMEN Gaming Hub again.' -ForegroundColor Yellow
$post = Read-Host 'Type SAME if it is unchanged, or CHANGED if it changed'

if ($post -ceq 'SAME') {
    Write-Host 'Undervolt preservation: CONFIRMED by user.' -ForegroundColor Green
} elseif ($post -ceq 'CHANGED') {
    Write-Warning 'Undervolt preservation: FAILED/CHANGED. Stop hardware-control development and investigate.'
    if ($testExit -eq 0) { $testExit = 49 }
} else {
    Write-Warning 'Undervolt preservation was not confirmed.'
    if ($testExit -eq 0) { $testExit = 50 }
}

exit $testExit
