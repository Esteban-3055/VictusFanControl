param(
    [int]$DurationSeconds = 90,
    [string]$ModulesDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($DurationSeconds -lt 10 -or $DurationSeconds -gt 600) {
    throw "DurationSeconds must be between 10 and 600."
}

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ModulesDir)) {
    $ModulesDir = Join-Path $root "modules"
}
$ModulesDir = [System.IO.Path]::GetFullPath($ModulesDir)

$intelModule = Join-Path $ModulesDir "IntelMSR.bin"
if (-not (Test-Path -LiteralPath $intelModule)) {
    throw "IntelMSR.bin was not found at $intelModule."
}

$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logDir = Join-Path $root ("logs\rapl-p05_" + $timestamp)
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$evidence = Join-Path $logDir "rapl-p05.txt"

Write-Host "VictusFanControl - Intel RAPL P0.5 READ-ONLY load stability gate"
Write-Host ("Duration: {0} s" -f $DurationSeconds)
Write-Host ("Evidence: {0}" -f $evidence)
Write-Host ""
Write-Host "This test performs NO MSR writes."
Write-Host "Start the CPU/game workload you want to characterize BEFORE confirming."
Write-Host "Recommended first pass: Cinebench multi-core or another sustained CPU workload."
Write-Host ""
$confirmation = Read-Host "When the workload is already active, type P05 and press Enter"
if ($confirmation -ne "P05") {
    throw "Cancelled: exact confirmation P05 was not entered."
}

Push-Location $root
try {
    & dotnet build ".\VictusFanControl.sln" -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }

    $probeOutput = & dotnet run --project ".\src\VictusFanControl\VictusFanControl.csproj" -c Release --no-build -- --rapl-observe --duration-seconds $DurationSeconds --modules-dir $ModulesDir 2>&1
    $exitCode = $LASTEXITCODE
    $probeOutput | Tee-Object -FilePath $evidence

    if ($exitCode -ne 0) {
        throw ("RAPL P0.5 observer failed closed with exit code {0}. See {1}." -f $exitCode, $evidence)
    }

    Write-Host ""
    Write-Host "P0.5 completed. No RAPL write was performed."
    Write-Host ("Evidence saved to: {0}" -f $evidence)
}
finally {
    Pop-Location
}
