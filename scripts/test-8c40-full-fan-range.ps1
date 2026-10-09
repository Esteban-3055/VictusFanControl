$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 FULL FAN-RANGE VERIFICATION 30-40' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware verification.' -ForegroundColor Yellow
Write-Host 'Every equal level 30 through 40 will be tested.'
Write-Host 'Each level is isolated by FF/FF + LegacyDefault restore and EC verification.'
Write-Host 'Levels 37-40 remain qualification-only until reviewed; this script does not promote them automatically.'
Write-Host ''
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Do not run games, benchmarks or stress tests.'
Write-Host ''

Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 2: read-only telemetry preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
$confirm = Read-Host 'Type exactly 8C40-VERIFY40 to verify every level 30 through 40'
if ($confirm -cne '8C40-VERIFY40') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --8c40-full-range-verification --8c40-full-range-token 8C40-VERIFY40
exit $LASTEXITCODE
