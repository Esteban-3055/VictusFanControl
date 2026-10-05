param(
    [switch]$SelfTest,
    [string]$ConfirmTargetProfile = '',
    [switch]$ConfirmCpuHardwareWrites,
    [switch]$ConfirmExclusiveGpuController,
    [string]$ModulesDir = '',
    [ValidateRange(15, 300)]
    [int]$TimeoutSeconds = 120,
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

Write-Host '1. Building Step 6H combined CPU+GPU Guardian qualification.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'Step 6H build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running Step 6H parser/dual-confirmation self-test. Zero hardware I/O.'
    & $exe --combined-gate-6h-self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'Step 6H self-test failed.'
    }
    exit 0
}

$expected = 'HP-8C40-9D0R1LA-F18'

if ($ConfirmTargetProfile -ne $expected) {
    throw "Step 6H requires -ConfirmTargetProfile $expected"
}

if (-not $ConfirmCpuHardwareWrites) {
    throw 'Step 6H requires -ConfirmCpuHardwareWrites.'
}

if (-not $ConfirmExclusiveGpuController) {
    throw 'Step 6H requires -ConfirmExclusiveGpuController. Confirm no nvidia-smi -lgc, MSI Afterburner or other locked-clock controller is active.'
}

if ([string]::IsNullOrWhiteSpace($ModulesDir)) {
    $ModulesDir = Join-Path $root 'modules'
}

$module = Join-Path ([IO.Path]::GetFullPath($ModulesDir)) 'IntelMSR.bin'

if (-not (Test-Path -LiteralPath $module)) {
    throw "Missing qualified IntelMSR.bin: $module"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root ('logs\performance-guardian-6h-combined_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location) $OutputDirectory
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Write-Host ''
Write-Host 'STEP 6H COMBINED CPU + GPU PHYSICAL QUALIFICATION' -ForegroundColor Yellow
Write-Host ' - This test WILL write CPU MSR 0x610 and NVIDIA locked GPU clocks.'
Write-Host ' - Start with the charger CONNECTED.'
Write-Host ' - CPU sequence: 35/60 W -> 8/15 W -> 35/60 W -> restore 45/115 W.'
Write-Host ' - GPU sequence: 210..1850 -> 210..1200 -> 210..1850 -> Reset.'
Write-Host ' - Expected writes: CPU exactly 4; GPU exactly 4.'
Write-Host ' - Do not use ThrottleStop, Intel XTU, nvidia-smi -lgc, MSI Afterburner or another power/clock controller.'
Write-Host ' - Do not change OMEN performance mode during the sequence.'
Write-Host ' - A stale GPU qualification journal may be recovered once with an explicit NVML Reset because this launcher already requires the exclusive-controller confirmation.'
Write-Host ' - CPU journals are NEVER auto-recovered here; a stale CPU journal remains fail-closed.'
Write-Host ' - On failure preserve the evidence directory and any remaining CPU/GPU journal. Do not rerun blindly.'
Write-Host ''

& $exe --combined-gate-6h --confirm-target $expected --confirm-cpu-hardware-writes --confirm-exclusive-gpu-controller --recover-stale-gpu-qualification-journal --module $module --timeout-seconds $TimeoutSeconds --output-directory $OutputDirectory

$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host ("Step 6H combined qualification failed with exit code {0}." -f $exitCode) -ForegroundColor Yellow
    Write-Host ("Preserve: {0}" -f $OutputDirectory) -ForegroundColor Yellow
    exit $exitCode
}

Write-Host ''
Write-Host 'Step 6H combined CPU+GPU physical qualification: PASS.' -ForegroundColor Green
Write-Host ("Evidence directory: {0}" -f $OutputDirectory)
