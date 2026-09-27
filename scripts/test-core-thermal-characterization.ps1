$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - CPU PHYSICAL-CORE THERMAL CHARACTERIZATION' -ForegroundColor Cyan
Write-Host ''
Write-Host 'READ-ONLY fan path: no fan command and no EC register-value write.'
Write-Host 'One representative logical processor per physical core is loaded for 4 seconds.'
Write-Host 'The test aborts if effective CPU temperature reaches 90 C.'
Write-Host ''
Write-Host 'Close games, benchmarks and other deliberate CPU loads before continuing.'
Write-Host ''

Write-Host 'Step 1: build with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: run bounded per-core thermal characterization...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --core-thermal-characterization

exit $LASTEXITCODE
