param(
    [string]$ModulesDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ModulesDir)) {
    $ModulesDir = Join-Path $root "modules"
}
$ModulesDir = [System.IO.Path]::GetFullPath($ModulesDir)

$intelModule = Join-Path $ModulesDir "IntelMSR.bin"
if (-not (Test-Path -LiteralPath $intelModule)) {
    throw "IntelMSR.bin was not found at '$intelModule'. Run .\scripts\setup-pawnio-modules.ps1 first."
}

$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logDir = Join-Path $root "logs\rapl-p0_$timestamp"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$transcript = Join-Path $logDir "rapl-p0.txt"

Write-Host "VictusFanControl - Intel RAPL P0 READ-ONLY feasibility gate"
Write-Host "Repository: $root"
Write-Host "Modules:    $ModulesDir"
Write-Host "Evidence:   $transcript"
Write-Host ""
Write-Host "SAFETY: this gate only reads MSRs 0x606, 0x610 and 0x614."
Write-Host "It does not write PL1/PL2, fan state, HP WMI, EC state or watchdog state."
Write-Host ""

Push-Location $root
try {
    Write-Host "[1/3] Building solution with warnings as errors..."
    & dotnet build ".\VictusFanControl.sln" -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }

    Write-Host ""
    Write-Host "[2/3] Running synthetic RAPL codec self-test..."
    & dotnet run --project ".\src\VictusFanControl\VictusFanControl.csproj" -c Release --no-build -- --rapl-self-test
    if ($LASTEXITCODE -ne 0) {
        throw "RAPL codec self-test failed."
    }

    Write-Host ""
    Write-Host "[3/3] Running exact-target P0 hardware probe..."
    $probeOutput = & dotnet run --project ".\src\VictusFanControl\VictusFanControl.csproj" -c Release --no-build -- --rapl-probe --modules-dir $ModulesDir 2>&1
    $exitCode = $LASTEXITCODE

    $probeOutput | Tee-Object -FilePath $transcript

    if ($exitCode -ne 0) {
        throw "RAPL P0 probe failed closed with exit code $exitCode. See '$transcript'."
    }

    Write-Host ""
    Write-Host "P0 execution completed. No MSR write was performed."
    Write-Host "Evidence saved to: $transcript"
}
finally {
    Pop-Location
}
