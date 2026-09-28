$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 ENDPOINT COORDINATOR QUALIFICATION' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE qualification hardware gate.' -ForegroundColor Yellow
Write-Host 'Sequence: firmware -> 10 -> firmware, then firmware -> 50 -> firmware.'
Write-Host 'Uses SafetyGate -> Coordinator -> HP 8C40 backend logic with qualification-only endpoint envelope.'
Write-Host 'Production defaults remain 30-36 until this gate and a later promotion regression pass.'
Write-Host ''
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Keep the AC adapter connected. Do not run games, benchmarks or stress tests.'
Write-Host ''

Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 2: synthetic regression tests...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 3: read-only telemetry preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
$confirm = Read-Host 'Type exactly 8C40-ENDPOINT10-50 to begin the guarded endpoint coordinator gate'
if ($confirm -cne '8C40-ENDPOINT10-50') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --8c40-endpoint-coordinator-qualification --8c40-endpoint-coordinator-token 8C40-ENDPOINT10-50

exit $LASTEXITCODE
