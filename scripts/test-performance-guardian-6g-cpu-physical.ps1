param(
    [switch]$SelfTest,
    [string]$ConfirmTargetProfile = '',
    [switch]$ConfirmCpuHardwareWrites,
    [string]$ModulesDir = '',
    [ValidateRange(15, 300)]
    [int]$TimeoutSeconds = 60,
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

Write-Host '1. Building PerformanceGuardian Step 6G CPU physical qualification.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'PerformanceGuardian Step 6G physical build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running Step 6G physical harness parser/write-confirmation self-test. No PawnIO load and no hardware I/O.'
    & $exe --cpu-gate-6g-physical-self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'PerformanceGuardian Step 6G physical harness self-test failed.'
    }
    exit 0
}

$expected = 'HP-8C40-9D0R1LA-F18'

if ($ConfirmTargetProfile -ne $expected) {
    throw "Step 6G requires -ConfirmTargetProfile $expected"
}

if (-not $ConfirmCpuHardwareWrites) {
    throw 'Step 6G physical qualification requires -ConfirmCpuHardwareWrites.'
}

if ([string]::IsNullOrWhiteSpace($ModulesDir)) {
    $ModulesDir = Join-Path $root 'modules'
}

$module = Join-Path ([IO.Path]::GetFullPath($ModulesDir)) 'IntelMSR.bin'

if (-not (Test-Path -LiteralPath $module)) {
    throw "Missing qualified IntelMSR.bin: $module"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root ('logs\performance-guardian-6g-cpu_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location) $OutputDirectory
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$preflight = Join-Path $OutputDirectory 'preflight.json'

Write-Host ''
Write-Host 'STEP 6G CPU PHYSICAL QUALIFICATION' -ForegroundColor Yellow
Write-Host ' - This test WILL write MSR 0x610 through PawnIO.'
Write-Host ' - Fixed sequence: AC 35/60 W -> Battery 8/15 W -> AC 35/60 W -> restore.'
Write-Host ' - Expected CPU write attempts: exactly 4.'
Write-Host ' - GPU/NVML write authority stays disabled.'
Write-Host ' - Do not run ThrottleStop, Intel XTU, another RAPL tool, or manually change CPU power limits during the test.'
Write-Host ' - Do not change OMEN performance mode during the sequence.'
Write-Host ' - Start with the charger connected.'
Write-Host ' - If the test fails, preserve all evidence and any remaining CPU journal; do not rerun blindly.'
Write-Host ''

Write-Host '2. Re-running the read-only Step 6G preflight immediately before write authority.'
& $exe --cpu-gate-6g-preflight --confirm-target $expected --module $module --output $preflight
if ($LASTEXITCODE -ne 0) {
    throw 'Immediate Step 6G read-only preflight failed. No physical CPU qualification was started.'
}

Write-Host '3. Starting the bounded detached CPU Guardian qualification.'
& $exe --cpu-gate-6g --confirm-target $expected --confirm-cpu-hardware-writes --module $module --timeout-seconds $TimeoutSeconds --output-directory $OutputDirectory

$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host ("Step 6G CPU physical qualification failed with exit code {0}." -f $exitCode) -ForegroundColor Yellow
    Write-Host ("Preserve this directory: {0}" -f $OutputDirectory) -ForegroundColor Yellow
    Write-Host 'Do not delete a remaining cpu-power-session.json or rerun until it is reviewed.' -ForegroundColor Yellow
    exit $exitCode
}

Write-Host ''
Write-Host 'Step 6G CPU physical qualification: PASS.' -ForegroundColor Green
Write-Host ("Evidence directory: {0}" -f $OutputDirectory)
