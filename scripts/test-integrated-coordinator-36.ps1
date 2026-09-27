$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 PRODUCTION-PATH LEVEL 36 GATE' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware validation through SafetyGate -> Coordinator -> production backend.' -ForegroundColor Yellow
Write-Host 'Target command: equal 36/36. Automatic policy remains OFF.'
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Do not run games/benchmarks and do NOT suspend/hibernate or kill the process.'
Write-Host ''

Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 2: synthetic safety/coordinator/BIOS/backend tests...' -ForegroundColor Cyan
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
$confirm = Read-Host 'Type exactly 8C40-COORD36 to validate production level 36/36'
if ($confirm -cne '8C40-COORD36') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --integrated-coordinator-test --coordinator-write-token 8C40-COORD36
exit $LASTEXITCODE
