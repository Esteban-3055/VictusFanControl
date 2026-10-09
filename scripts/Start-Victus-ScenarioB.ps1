# B uses the existing enforced WMI-only CLI, never Manual.
[CmdletBinding()]
param(
    [ValidateRange(1,60)][int]$CaptureMinutes=30,
    [string]$OutputRoot='',
    [switch]$SkipAcpiTrace,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
Write-Host 'ESCENARIO B: RPM HP WMI + Intel MSR + NVML; EC directo y comandos de ventiladores prohibidos.'
& (Join-Path $PSScriptRoot 'Start-Victus-WmiOnlyInvestigation.ps1') -CaptureMinutes $CaptureMinutes -OutputRoot $OutputRoot -SkipAcpiTrace:$SkipAcpiTrace -ScenarioB -SelfTest:$SelfTest
