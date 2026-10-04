param(
    [switch]$SelfTest,
    [string]$ConfirmTargetProfile,
    [ValidateRange(15,300)]
    [int]$TimeoutSeconds = 60,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

Write-Host '1. Building PerformanceGuardian Step 6F GPU qualification gate.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'PerformanceGuardian Step 6F build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running Step 6F argument/target-gate self-test. No NVML load and no hardware I/O.'
    & $exe --gpu-gate-6f-self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'PerformanceGuardian Step 6F self-test failed.'
    }

    Write-Host 'Step 6F GPU harness self-test: PASS'
    exit 0
}

$expected = 'HP-8C40-9D0R1LA-F18'
if ($ConfirmTargetProfile -ne $expected) {
    throw "Physical Step 6F requires -ConfirmTargetProfile $expected"
}

$args = @(
    '--gpu-gate-6f',
    '--confirm-target', $expected,
    '--timeout-seconds', $TimeoutSeconds.ToString()
)

if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $args += @('--output-directory', (Join-Path (Get-Location) $OutputDirectory))
}

Write-Host ''
Write-Host 'STEP 6F GPU PHYSICAL QUALIFICATION' -ForegroundColor Yellow
Write-Host 'Requirements:' -ForegroundColor Yellow
Write-Host ' - Run PowerShell as Administrator.'
Write-Host ' - Start with the charger CONNECTED.'
Write-Host ' - Do not run nvidia-smi -lgc, MSI Afterburner or another GPU clock controller during the test.'
Write-Host ' - CPU RAPL is NOT enabled by this gate.'
Write-Host ' - The harness will ask you to disconnect and reconnect the charger.'
Write-Host ''

& $exe @args
if ($LASTEXITCODE -ne 0) {
    throw "Step 6F GPU physical qualification failed with exit code $LASTEXITCODE. Preserve the generated evidence and do not rerun blindly."
}
