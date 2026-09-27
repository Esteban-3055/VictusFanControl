$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 ADJACENT FAN-LEVEL QUALIFICATION' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware qualification.' -ForegroundColor Yellow
Write-Host 'Sequence: equal 30/30 -> restore -> 31/31 -> restore -> 32/32 -> restore.'
Write-Host 'Production backend remains 30-only until the results are reviewed.'
Write-Host ''
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host 'Do NOT suspend/hibernate or terminate the process through Task Manager.'
Write-Host ''

Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: read-only telemetry preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'The next step writes fan levels 30, 31 and 32, restoring HP firmware after EACH level.' -ForegroundColor Yellow
$confirm = Read-Host 'Type exactly 8C40-QUAL32 to continue'
if ($confirm -cne '8C40-QUAL32') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --8c40-fan-level-qualification --8c40-qualification-token 8C40-QUAL32
exit $LASTEXITCODE
