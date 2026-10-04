param(
    [switch]$SelfTest,
    [string]$ConfirmTargetProfile = '',
    [switch]$ConfirmCpuHardwareWrites,
    [switch]$ConfirmExclusiveGpuController,
    [switch]$ConfirmModernStandby,
    [string]$ModulesDir = '',
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

Write-Host '1. Building Step 6I combined Modern Standby qualification.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Step 6I build failed.' }

if ($SelfTest) {
    Write-Host '2. Running Step 6I argument-gate self-test. Zero hardware I/O.'
    & $exe --standby-gate-6i-self-test
    if ($LASTEXITCODE -ne 0) { throw 'Step 6I self-test failed.' }
    exit 0
}

$expected = 'HP-8C40-9D0R1LA-F18'
if ($ConfirmTargetProfile -cne $expected) { throw "Step 6I requires -ConfirmTargetProfile $expected" }
if (-not $ConfirmCpuHardwareWrites) { throw 'Step 6I requires -ConfirmCpuHardwareWrites.' }
if (-not $ConfirmExclusiveGpuController) { throw 'Step 6I requires -ConfirmExclusiveGpuController. Close nvidia-smi -lgc, MSI Afterburner or any other locked-clock controller.' }
if (-not $ConfirmModernStandby) { throw 'Step 6I requires -ConfirmModernStandby because this test performs a real user-initiated Windows Sleep/Modern Standby cycle.' }

if ([string]::IsNullOrWhiteSpace($ModulesDir)) { $ModulesDir = Join-Path $root 'modules' }
$module = Join-Path ([IO.Path]::GetFullPath($ModulesDir)) 'IntelMSR.bin'
if (-not (Test-Path -LiteralPath $module)) { throw "Missing qualified IntelMSR.bin: $module" }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root ('logs\performance-guardian-6i-standby_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location) $OutputDirectory
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Write-Host ''
Write-Host 'STEP 6I COMBINED CPU + GPU MODERN STANDBY QUALIFICATION' -ForegroundColor Yellow
Write-Host ' - This test WILL write CPU MSR 0x610 and NVIDIA locked GPU clocks.'
Write-Host ' - Keep the charger CONNECTED for the whole cycle.'
Write-Host ' - The script does NOT call SetSuspendState or force sleep.'
Write-Host ' - When the Guardian says READY, choose Windows Start -> Power -> Sleep manually.'
Write-Host ' - Leave the notebook in Modern Standby for at least 30 seconds, then wake normally.'
Write-Host ' - SESSION_DISPLAY_STATUS Off must restore CPU 45/115 and Reset GPU before sleep.'
Write-Host ' - Resume broadcasts while display remains Off must NOT reacquire.'
Write-Host ' - SESSION_DISPLAY_STATUS On must create fresh CPU/GPU sessions.'
Write-Host ' - Do not use ThrottleStop, Intel XTU, nvidia-smi -lgc, MSI Afterburner or change OMEN performance mode.'
Write-Host ' - On failure preserve the evidence directory and any remaining journals.'
Write-Host ''

& $exe --standby-gate-6i --confirm-target $expected --confirm-cpu-hardware-writes --confirm-exclusive-gpu-controller --confirm-modern-standby --module $module --output-directory $OutputDirectory
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host ("Step 6I standby qualification failed with exit code {0}." -f $exitCode) -ForegroundColor Yellow
    Write-Host ("Preserve: {0}" -f $OutputDirectory) -ForegroundColor Yellow
    exit $exitCode
}

Write-Host ''
Write-Host 'Step 6I combined Modern Standby qualification: PASS.' -ForegroundColor Green
Write-Host ("Evidence directory: {0}" -f $OutputDirectory)
