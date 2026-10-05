param(
    [switch]$SelfTest,
    [string]$ConfirmTargetProfile,
    [switch]$ConfirmExclusiveGpuController,
    [ValidateRange(15,300)]
    [int]$TimeoutSeconds = 60,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

function New-Step6FPhysicalArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetProfile,
        [Parameter(Mandatory = $true)]
        [int]$Timeout,
        [string]$OutputPath
    )

    $physicalArguments = @(
        '--gpu-gate-6f',
        '--confirm-target', $TargetProfile,
        '--confirm-exclusive-controller',
        '--timeout-seconds', $Timeout.ToString()
    )

    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $physicalArguments += @(
            '--output-directory',
            (Join-Path (Get-Location) $OutputPath)
        )
    }

    return $physicalArguments
}

Write-Host '1. Building PerformanceGuardian Step 6F GPU qualification gate.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'PerformanceGuardian Step 6F build failed.'
}

$expected = 'HP-8C40-9D0R1LA-F18'

if ($SelfTest) {
    $forwardingProbe = @(New-Step6FPhysicalArguments -TargetProfile $expected -Timeout 60)

    if ($forwardingProbe -notcontains '--confirm-exclusive-controller') {
        throw 'Step 6F script self-test failed: physical launcher does not forward --confirm-exclusive-controller.'
    }

    Write-Host '2. Running Step 6F argument/target-gate self-test. No NVML load and no hardware I/O.'
    & $exe --gpu-gate-6f-self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'PerformanceGuardian Step 6F self-test failed.'
    }

    Write-Host 'Step 6F GPU harness self-test: PASS'
    exit 0
}

if ($ConfirmTargetProfile -ne $expected) {
    throw "Physical Step 6F requires -ConfirmTargetProfile $expected"
}

if (-not $ConfirmExclusiveGpuController) {
    throw 'Physical Step 6F requires -ConfirmExclusiveGpuController. Public NVML cannot prove that another locked-clock owner is absent.'
}

$physicalArguments = @(New-Step6FPhysicalArguments -TargetProfile $expected -Timeout $TimeoutSeconds -OutputPath $OutputDirectory)
$preflightRoot = Join-Path $root 'logs'
New-Item -ItemType Directory -Force -Path $preflightRoot | Out-Null
$preflightPath = Join-Path $preflightRoot ('performance-guardian-6f-gpu-preflight_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '.json')

Write-Host '2. Running read-only exact-target/NVML/mutex/journal preflight. Hardware write gate is CLOSED.'
& $exe --gpu-gate-6f-preflight --confirm-target $expected --output $preflightPath
if ($LASTEXITCODE -ne 0) {
    throw "Step 6F read-only preflight failed with exit code $LASTEXITCODE. No hardware qualification was started."
}

Write-Host ''
Write-Host 'STEP 6F GPU PHYSICAL QUALIFICATION' -ForegroundColor Yellow
Write-Host 'Requirements:' -ForegroundColor Yellow
Write-Host ' - Run PowerShell as Administrator.'
Write-Host ' - Start with the charger CONNECTED.'
Write-Host ' - -ConfirmExclusiveGpuController means YOU verified there is no pre-existing or concurrent locked-clock controller.'
Write-Host ' - Do not run nvidia-smi -lgc, MSI Afterburner or another GPU clock controller during the test.'
Write-Host ' - CPU RAPL is NOT enabled by this gate.'
Write-Host ' - The harness will ask you to disconnect and reconnect the charger.'
Write-Host ''

& $exe @physicalArguments
if ($LASTEXITCODE -ne 0) {
    throw "Step 6F GPU physical qualification failed with exit code $LASTEXITCODE. Preserve the generated evidence and do not rerun blindly."
}
