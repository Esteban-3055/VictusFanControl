$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 FAN-LEVEL QUALIFICATION 33-36' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware qualification.' -ForegroundColor Yellow
Write-Host 'Sequence: 33/33 -> restore -> 34/34 -> restore -> 35/35 -> restore -> 36/36 -> restore.'
Write-Host 'Production backend remains capped at 32 until results are reviewed.'
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
Write-Host 'The next step writes 33, 34, 35 and 36, restoring HP firmware after EACH level.' -ForegroundColor Yellow
$confirm = Read-Host 'Type exactly 8C40-QUAL36 to continue'
if ($confirm -cne '8C40-QUAL36') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --8c40-upper-fan-level-qualification --8c40-upper-qualification-token 8C40-QUAL36
exit $LASTEXITCODE
