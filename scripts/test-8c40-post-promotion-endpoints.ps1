$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 POST-PROMOTION PRODUCTION ENDPOINT GATE' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware validation through the REAL production backend.' -ForegroundColor Yellow
Write-Host 'Sequence: firmware -> 10 -> firmware, then firmware -> 50 -> firmware.'
Write-Host 'Route: SafetyGate -> FanControlCoordinator -> Hp8C40FanControlBackend -> WMI -> EC/tachs -> terminal convergence -> restore.'
Write-Host 'No qualification envelope injection is used. Automatic policy remains OFF.'
Write-Host ''
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Keep AC connected and battery above 20%. Do not run games/benchmarks.'
Write-Host 'Do not suspend/hibernate, close the lid, or kill the process during either endpoint.'
Write-Host ''

Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 2: full synthetic production regression...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --bios-contract-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 3: read-only telemetry preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
$confirm = Read-Host 'Type exactly 8C40-PROD10-50 to run BOTH promoted production endpoints'
if ($confirm -cne '8C40-PROD10-50') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Step 4: production endpoint 10/10...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --integrated-coordinator-test --coordinator-write-token 8C40-COORD10
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Endpoint 10/10 failed. Endpoint 50/50 will NOT be attempted.'
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host 'Step 5: production endpoint 50/50...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --integrated-coordinator-test --coordinator-write-token 8C40-COORD50
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'RESULT: PASS - both promoted production endpoints completed through the real backend and restored firmware.' -ForegroundColor Green
exit 0
