param(
    [ValidateRange(1, 1440)]
    [int]$Minutes = 30,

    [ValidateRange(250, 60000)]
    [int]$IntervalMs = 1000
)

$ErrorActionPreference = 'Stop'

Write-Host "VictusFanControl telemetry health soak" -ForegroundColor Cyan
Write-Host "Minutes:  $Minutes"
Write-Host "Interval: $IntervalMs ms"
Write-Host ''

dotnet run --project .\src\VictusFanControl -c Release -- `
    --health-test-minutes $Minutes `
    --interval-ms $IntervalMs

exit $LASTEXITCODE
