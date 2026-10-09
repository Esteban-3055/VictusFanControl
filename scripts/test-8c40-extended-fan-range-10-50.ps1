$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 EXTENDED FAN-RANGE QUALIFICATION 10-50' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware characterization.' -ForegroundColor Yellow
Write-Host 'For safety, execution does NOT begin blindly at level 10.'
Write-Host 'Order: 30 anchor -> 29 down toward 10 -> 31 up toward 50.'
Write-Host 'Every attempted level is restored to FF/FF + LegacyDefault before the next one.'
Write-Host 'The downward sweep stops at the first conservative fan-running floor.'
Write-Host 'The upper sweep stops at the first hard command/ACK/feedback failure.'
Write-Host 'This test does not automatically expand the production 30-36 range.'
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
$confirm = Read-Host 'Type exactly 8C40-QUAL10-50 to begin the guarded 10..50 characterization'
if ($confirm -cne '8C40-QUAL10-50') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --8c40-extended-range-qualification --8c40-extended-range-token 8C40-QUAL10-50
exit $LASTEXITCODE
