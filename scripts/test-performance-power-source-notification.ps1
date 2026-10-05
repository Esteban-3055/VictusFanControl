param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('ac', 'battery')]
    [string]$Expect,

    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 60,

    [string]$OutputPath = '',

    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceProbe\VictusFanControl.PerformanceProbe.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceProbe\bin\Release\net8.0-windows\VictusFanControl.PerformanceProbe.exe'

Write-Host '1. Building read-only Windows power-source notification qualification harness.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'Performance power-source notification harness build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running notification harness argument self-test. No listener registration and no hardware I/O.'
    & $exe --notification-self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'Performance power-source notification harness self-test failed.'
    }
    return
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $logs = Join-Path $root 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
    $OutputPath = Join-Path $logs ("performance-power-notification_{0}_to-{1}.json" -f $stamp, $Expect)
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)

Write-Host ''
Write-Host '2. READ-ONLY POWER-SOURCE NOTIFICATION QUALIFICATION'
Write-Host ("Expected destination: {0}" -f $Expect)
Write-Host ("Timeout: {0} s" -f $TimeoutSeconds)
Write-Host 'After the harness starts waiting, physically change the charger state.'
Write-Host 'The Windows notification is used only as a trigger; GetSystemPowerStatus confirms the source.'
Write-Host 'No CPU/GPU hardware write occurs.'

& $exe --watch-power-source --expect $Expect --timeout-seconds $TimeoutSeconds --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw ("Performance power-source notification qualification failed with exit code {0}." -f $LASTEXITCODE)
}
