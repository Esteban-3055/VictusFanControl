param(
    [switch]$SelfTest,
    [string]$ConfirmTargetProfile = '',
    [switch]$ConfirmCpuHardwareWrites,
    [switch]$ConfirmExclusiveGpuController,
    [switch]$ConfirmOwnerProcessKill,
    [string]$ModulesDir = '',
    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 90,
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

Write-Host '1. Building Step 6J parent-death qualification.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Step 6J build failed.' }

if ($SelfTest) {
    Write-Host '2. Running Step 6J argument-gate self-test. Zero hardware I/O.'
    & $exe --parent-death-gate-6j-self-test
    if ($LASTEXITCODE -ne 0) { throw 'Step 6J self-test failed.' }
    exit 0
}

$expected = 'HP-8C40-9D0R1LA-F18'
if ($ConfirmTargetProfile -cne $expected) { throw "Step 6J requires -ConfirmTargetProfile $expected" }
if (-not $ConfirmCpuHardwareWrites) { throw 'Step 6J requires -ConfirmCpuHardwareWrites.' }
if (-not $ConfirmExclusiveGpuController) { throw 'Step 6J requires -ConfirmExclusiveGpuController. Close nvidia-smi -lgc, MSI Afterburner or any other locked-clock controller.' }
if (-not $ConfirmOwnerProcessKill) { throw 'Step 6J requires -ConfirmOwnerProcessKill because the test intentionally terminates a disposable owner process while CPU/GPU limits are active.' }

if ([string]::IsNullOrWhiteSpace($ModulesDir)) { $ModulesDir = Join-Path $root 'modules' }
$module = Join-Path ([IO.Path]::GetFullPath($ModulesDir)) 'IntelMSR.bin'
if (-not (Test-Path -LiteralPath $module)) { throw "Missing qualified IntelMSR.bin: $module" }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root ('logs\performance-guardian-6j-parent-death_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location) $OutputDirectory
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Write-Host ''
Write-Host 'STEP 6J COMBINED CPU + GPU PARENT-DEATH QUALIFICATION' -ForegroundColor Yellow
Write-Host ' - This test WILL write CPU MSR 0x610 and NVIDIA locked GPU clocks.'
Write-Host ' - Keep the charger CONNECTED for the whole test.'
Write-Host ' - The test starts a disposable owner process and intentionally kills ONLY that owner.'
Write-Host ' - The detached Guardian must survive long enough to detect parent loss and clean both domains.'
Write-Host ' - Expected writes: CPU 2 (apply + restore), GPU 2 (Set + Reset).'
Write-Host ' - Do NOT kill the Guardian process manually.'
Write-Host ' - Do not use ThrottleStop, Intel XTU, nvidia-smi -lgc, MSI Afterburner or change OMEN performance mode.'
Write-Host ' - A stale journal at startup aborts the gate; there is no automatic crash recovery in this test.'
Write-Host ''

& $exe --parent-death-gate-6j --confirm-target $expected --confirm-cpu-hardware-writes --confirm-exclusive-gpu-controller --confirm-owner-process-kill --module $module --timeout-seconds $TimeoutSeconds --output-directory $OutputDirectory
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host ("Step 6J parent-death qualification failed with exit code {0}." -f $exitCode) -ForegroundColor Yellow
    Write-Host ("Preserve: {0}" -f $OutputDirectory) -ForegroundColor Yellow
    exit $exitCode
}

Write-Host ''
Write-Host 'Step 6J combined parent-death qualification: PASS.' -ForegroundColor Green
Write-Host ("Evidence directory: {0}" -f $OutputDirectory)
