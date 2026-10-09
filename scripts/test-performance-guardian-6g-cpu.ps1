param(
    [switch]$SelfTest,
    [string]$ConfirmTargetProfile = '',
    [string]$ModulesDir = '',
    [string]$OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

Write-Host '1. Building PerformanceGuardian Step 6G CPU read-only preflight.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'PerformanceGuardian Step 6G build failed.'
}

if ($SelfTest) {
    Write-Host '2. Running Step 6G parser/target-gate self-test. No PawnIO load and no hardware I/O.'
    & $exe --cpu-gate-6g-self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'PerformanceGuardian Step 6G self-test failed.'
    }

    Write-Host 'Step 6G CPU preflight harness self-test: PASS'
    exit 0
}

$expected = 'HP-8C40-9D0R1LA-F18'
if ($ConfirmTargetProfile -ne $expected) {
    throw "Step 6G CPU preflight requires -ConfirmTargetProfile $expected"
}

if ([string]::IsNullOrWhiteSpace($ModulesDir)) {
    $ModulesDir = Join-Path $root 'modules'
}

$module = Join-Path ([IO.Path]::GetFullPath($ModulesDir)) 'IntelMSR.bin'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root ('logs\performance-guardian-6g-cpu-preflight_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '.json')
} elseif (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path (Get-Location) $OutputPath
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)

Write-Host ''
Write-Host 'STEP 6G CPU READ-ONLY PREFLIGHT'
Write-Host ' - Exact HP-8C40-9D0R1LA-F18 target required.'
Write-Host ' - AC power required.'
Write-Host ' - Reads MSR 0x606 / 0x610 / 0x614 through PawnIO.'
Write-Host ' - Validates AC 35/60 W and Battery 8/15 W against the LIVE reported RAPL minimum.'
Write-Host ' - Hardware write authorization remains CLOSED.'
Write-Host ' - This step performs ZERO MSR writes.'
Write-Host ''

& $exe --cpu-gate-6g-preflight --confirm-target $expected --module $module --output $OutputPath

$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    throw "Step 6G CPU read-only preflight failed with exit code $exitCode. Preserve the report; do not proceed to the physical CPU gate."
}

Write-Host ''
Write-Host 'Step 6G CPU read-only preflight: PASS. No MSR write was performed.'
Write-Host ('Evidence: ' + $OutputPath)
