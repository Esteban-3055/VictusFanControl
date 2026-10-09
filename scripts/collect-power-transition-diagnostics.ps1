param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Continue'

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $outDir = Join-Path $PSScriptRoot '..\logs'
} else {
    $outDir = [IO.Path]::GetFullPath($OutputDirectory)
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$txt = Join-Path $outDir ("power-transition-diagnostics_$stamp.txt")
$systemPowerHtml = Join-Path $outDir ("system-power-report_$stamp.html")
$systemSleepHtml = Join-Path $outDir ("system-sleep-diagnostics_$stamp.html")
$sleepStudyHtml = Join-Path $outDir ("sleepstudy_$stamp.html")

function Write-Section([string]$title) {
    "" | Tee-Object -FilePath $txt -Append
    ("===== " + $title + " =====") | Tee-Object -FilePath $txt -Append
}

"VictusFanControl power-transition diagnostics" | Tee-Object -FilePath $txt
("Captured: " + (Get-Date).ToString('o')) | Tee-Object -FilePath $txt -Append

Write-Section 'powercfg /a'
powercfg /a 2>&1 | Tee-Object -FilePath $txt -Append

Write-Section 'active scheme'
powercfg /getactivescheme 2>&1 | Tee-Object -FilePath $txt -Append

Write-Section 'power requests'
powercfg /requests 2>&1 | Tee-Object -FilePath $txt -Append

Write-Section 'request overrides'
powercfg /requestsoverride 2>&1 | Tee-Object -FilePath $txt -Append

Write-Section 'sleep subgroup'
powercfg /query SCHEME_CURRENT SUB_SLEEP 2>&1 | Tee-Object -FilePath $txt -Append

Write-Section 'battery subgroup'
powercfg /query SCHEME_CURRENT SUB_BATTERY 2>&1 | Tee-Object -FilePath $txt -Append

Write-Section 'Win32_Battery'
try {
    Get-CimInstance Win32_Battery -ErrorAction Stop |
        Select-Object Name, Status, BatteryStatus, EstimatedChargeRemaining,
            EstimatedRunTime, DesignVoltage |
        Format-List | Out-String -Width 240 |
        Tee-Object -FilePath $txt -Append
} catch {
    ("Win32_Battery query failed: " + $_.Exception.Message) | Tee-Object -FilePath $txt -Append
}

Write-Section 'last wake'
powercfg /lastwake 2>&1 | Tee-Object -FilePath $txt -Append

Write-Section 'recent Kernel-Power events'
try {
    Get-WinEvent -FilterHashtable @{
        LogName='System';
        ProviderName='Microsoft-Windows-Kernel-Power';
        StartTime=(Get-Date).AddHours(-3)
    } -ErrorAction Stop |
        Select-Object -First 80 TimeCreated, Id, LevelDisplayName, Message |
        Format-List | Out-String -Width 240 |
        Tee-Object -FilePath $txt -Append
} catch {
    ("Kernel-Power query failed: " + $_.Exception.Message) | Tee-Object -FilePath $txt -Append
}

Write-Section 'recent Power-Troubleshooter events'
try {
    Get-WinEvent -FilterHashtable @{
        LogName='System';
        ProviderName='Microsoft-Windows-Power-Troubleshooter';
        StartTime=(Get-Date).AddHours(-3)
    } -ErrorAction Stop |
        Select-Object -First 40 TimeCreated, Id, LevelDisplayName, Message |
        Format-List | Out-String -Width 240 |
        Tee-Object -FilePath $txt -Append
} catch {
    ("Power-Troubleshooter query failed: " + $_.Exception.Message) | Tee-Object -FilePath $txt -Append
}

Write-Section 'system power report'
try {
    powercfg /systempowerreport /output $systemPowerHtml 2>&1 |
        Tee-Object -FilePath $txt -Append

    ("Report: " + (Resolve-Path $systemPowerHtml -ErrorAction SilentlyContinue)) |
        Tee-Object -FilePath $txt -Append
} catch {
    ("systempowerreport failed: " + $_.Exception.Message) |
        Tee-Object -FilePath $txt -Append
}

Write-Section 'system sleep diagnostics'
try {
    powercfg /systemsleepdiagnostics /output $systemSleepHtml 2>&1 |
        Tee-Object -FilePath $txt -Append

    ("Report: " + (Resolve-Path $systemSleepHtml -ErrorAction SilentlyContinue)) |
        Tee-Object -FilePath $txt -Append
} catch {
    ("systemsleepdiagnostics failed: " + $_.Exception.Message) |
        Tee-Object -FilePath $txt -Append
}

Write-Section 'sleepstudy report'
try {
    powercfg /sleepstudy /output $sleepStudyHtml 2>&1 |
        Tee-Object -FilePath $txt -Append

    ("Report: " + (Resolve-Path $sleepStudyHtml -ErrorAction SilentlyContinue)) |
        Tee-Object -FilePath $txt -Append
} catch {
    ("sleepstudy failed: " + $_.Exception.Message) |
        Tee-Object -FilePath $txt -Append
}

Write-Host ''
Write-Host 'Diagnostics saved to:' -ForegroundColor Cyan
Write-Host (Resolve-Path $txt)
if (Test-Path $systemPowerHtml) { Write-Host (Resolve-Path $systemPowerHtml) }
if (Test-Path $systemSleepHtml) { Write-Host (Resolve-Path $systemSleepHtml) }
if (Test-Path $sleepStudyHtml) { Write-Host (Resolve-Path $sleepStudyHtml) }
