# Scenario C: B telemetry plus ONLY residual EC ownership/guard reads.
[CmdletBinding()]
param(
    [ValidateRange(1,30)][int]$CaptureMinutes=30,
    [ValidateRange(2,60)][int]$EcIntervalSeconds=5,
    [string]$OutputRoot='',
    [switch]$SkipAcpiTrace,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
Write-Host ('ESCENARIO C: RPM WMI + Intel MSR + NVML; EC 34/35/EC/F4 cada '+$EcIntervalSeconds+' s; HP writes y Manual prohibidos.')
& (Join-Path $PSScriptRoot 'Start-Victus-WmiOnlyInvestigation.ps1') -CaptureMinutes $CaptureMinutes -OutputRoot $OutputRoot -SkipAcpiTrace:$SkipAcpiTrace -ScenarioC -EcIntervalSeconds $EcIntervalSeconds -SelfTest:$SelfTest
