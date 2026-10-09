param(
    [string]$Label = 'manual',
    [string]$OutputPath = '',
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.GpuProbe\VictusFanControl.GpuProbe.csproj'
$exe = Join-Path $root 'src\VictusFanControl.GpuProbe\bin\Release\net8.0-windows\VictusFanControl.GpuProbe.exe'

Write-Host '1. Building read-only GPU NVML clock + power-limit observability probe.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'GPU observability probe build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running fixture-only probe self-test. No NVML load and no hardware I/O.'
    & $exe --self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'GPU observability probe self-test failed.'
    }
    return
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $safeLabel = ($Label -replace '[^A-Za-z0-9_.-]', '_')
    $name = 'gpu-nvml-observe_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '_' + $safeLabel + '.json'
    $OutputPath = Join-Path $root ('logs\' + $name)
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)

Write-Host ''
Write-Host '2. READ-ONLY observation.' -ForegroundColor Cyan
Write-Host 'No NVML clock Set/Reset, power-limit setter, or nvidia-smi command is issued by this script.'
Write-Host ("Label : {0}" -f $Label)
Write-Host ("Output: {0}" -f $OutputPath)

& $exe --observe --label $Label --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw ("GPU observability probe failed with exit code {0}." -f $LASTEXITCODE)
}
