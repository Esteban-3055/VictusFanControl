param(
    [string]$ConfirmTargetProfile = '',

    [ValidateRange(1, 30)]
    [int]$HoldSeconds = 3,

    [string]$OutputPath = '',

    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedTarget = 'HP-8C40-9D0R1LA-F18'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.GpuProbe\VictusFanControl.GpuProbe.csproj'
$exe = Join-Path $root 'src\VictusFanControl.GpuProbe\bin\Release\net8.0-windows\VictusFanControl.GpuProbe.exe'

Write-Host '1. Building GPU locked-clock transition qualification harness.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'GPU clock transition qualification harness build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running transition harness token/argument self-test. No NVML load and no hardware I/O.'
    & $exe --clock-transition-self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'GPU clock transition qualification harness self-test failed.'
    }
    return
}

if ($ConfirmTargetProfile -ne $expectedTarget) {
    throw "Exact -ConfirmTargetProfile $expectedTarget is required. No write attempted."
}

$argsList = @(
    '--clock-transition-test',
    '--confirm-target', $ConfirmTargetProfile,
    '--hold-seconds', $HoldSeconds.ToString()
)

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $argsList += @('--output', [IO.Path]::GetFullPath($OutputPath))
}

Write-Host ''
Write-Host '2. DESTRUCTIVE TRANSITION QUALIFICATION' -ForegroundColor Yellow
Write-Host 'This invokes NVML directly; it does not spawn nvidia-smi.'
Write-Host ("Target : {0}" -f $ConfirmTargetProfile)
Write-Host ("Hold   : {0} s per enabled preset" -f $HoldSeconds)
Write-Host 'Sequence: AC 210..1850 -> Battery 210..1200 -> AC 210..1850 -> final Reset'
Write-Host 'There is no intermediate Reset between enabled presets.'
Write-Host 'A durable GPU clock journal is updated before each Set/Reset.'

& $exe @argsList
if ($LASTEXITCODE -ne 0) {
    throw ("GPU clock transition qualification failed with exit code {0}. Review logs/session journal before retrying." -f $LASTEXITCODE)
}
