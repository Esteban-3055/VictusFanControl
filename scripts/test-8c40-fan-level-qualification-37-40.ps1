$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP 8C40 FAN-LEVEL QUALIFICATION 37-40' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ACTIVE hardware qualification.' -ForegroundColor Yellow
Write-Host 'Sequence: 37/37 -> restore -> 38/38 -> restore -> 39/39 -> restore -> 40/40 -> restore.'
Write-Host 'Production backend remains capped at 36 until results are reviewed.'
Write-Host ''
Write-Host 'Close OmenMon, OmenMon-Reborn and the VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''
Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Step 2: read-only telemetry preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'The next step writes 37, 38, 39 and 40, restoring HP firmware after EACH level.' -ForegroundColor Yellow
$confirm = Read-Host 'Type exactly 8C40-QUAL40 to continue'
if ($confirm -cne '8C40-QUAL40') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --8c40-higher-fan-level-qualification --8c40-higher-qualification-token 8C40-QUAL40
exit $LASTEXITCODE
