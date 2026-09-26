param(
    [ValidateSet('idle','browser','video','cpu-burst','cpu-sustained','gaming','custom')]
    [string]$Scenario = 'idle',
    [ValidateRange(1, 240)]
    [int]$Minutes = 15,
    [ValidateRange(250, 60000)]
    [int]$IntervalMs = 1000
)

$ErrorActionPreference = 'Stop'

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$output = ".\logs\${stamp}_${Scenario}_hp-auto.csv"
$seconds = $Minutes * 60

Write-Host "Scenario: $Scenario"
Write-Host "Duration: $Minutes minute(s)"
Write-Host "Output:   $output"
Write-Host ''

dotnet run --project .\src\VictusFanControl -c Release -- `
    --interval-ms $IntervalMs `
    --duration-seconds $seconds `
    --output $output

exit $LASTEXITCODE
