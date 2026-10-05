param(
    [ValidateSet('ac', 'battery', 'manual')]
    [string]$Label = 'manual',

    [string]$OutputPath = '',

    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceProbe\VictusFanControl.PerformanceProbe.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceProbe\bin\Release\net8.0-windows\VictusFanControl.PerformanceProbe.exe'

Write-Host '1. Building read-only Windows performance power-source probe.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'Performance power-source probe build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running source mapping self-test. No hardware I/O.'
    & $exe --self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'Performance power-source probe self-test failed.'
    }
    return
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $logs = Join-Path $root 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null

    $stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
    $OutputPath = Join-Path $logs ("performance-power-source_{0}_{1}.json" -f $stamp, $Label)
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)

Write-Host '2. Reading Windows GetSystemPowerStatus once. No hardware writes.'
& $exe --observe-power-source --label $Label --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw ("Performance power-source probe failed with exit code {0}." -f $LASTEXITCODE)
}

Write-Host ("Power-source report: {0}" -f $OutputPath)
