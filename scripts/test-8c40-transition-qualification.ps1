$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 LARGE-TRANSITION QUALIFICATION' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware qualification.' -ForegroundColor Yellow
Write-Host 'Sequence: firmware -> 10 -> 30 -> 50 -> 30 -> 10 -> firmware.'
Write-Host 'The fixed override remains active between transition steps.'
Write-Host 'A verified FF/FF + LegacyDefault restore is mandatory at completion/abort.'
Write-Host 'This test does NOT expand the production 30-36 envelope.'
Write-Host ''
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Keep the AC adapter connected. Do not run games, benchmarks or stress tests.'
Write-Host ''

Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 2: synthetic safety/backend regression tests...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 3: read-only telemetry preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
$confirm = Read-Host 'Type exactly 8C40-TRANSITION10-50 to begin the guarded transition gate'
if ($confirm -cne '8C40-TRANSITION10-50') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --8c40-transition-qualification --8c40-transition-token 8C40-TRANSITION10-50

exit $LASTEXITCODE
