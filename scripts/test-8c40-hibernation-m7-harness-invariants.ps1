$ErrorActionPreference='Stop'
$preflight=Get-Content (Join-Path $PSScriptRoot 'test-8c40-hibernation-m7-preflight.ps1') -Raw
$physical=Get-Content (Join-Path $PSScriptRoot 'test-8c40-hibernation-m7.ps1') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if(-not $Text.Contains($Needle,[StringComparison]::Ordinal)){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.Contains($Needle,[StringComparison]::Ordinal)){throw $Message}
}

Assert-Contains $preflight 'M7 HIBERNATION NO-WRITE PREFLIGHT' 'M7 preflight identity is missing.'
Assert-Contains $preflight 'Assert-HibernationAvailable' 'M7 preflight must prove Windows hibernation capability.'
Assert-Contains $preflight 'Assert-StableFirmwareBaseline' 'M7 preflight must prove stable FF/FF.'
Assert-Contains $preflight 'test-8c40-hibernation-m7-invariants.ps1' 'M7 preflight must run M7 source invariants.'
Assert-Contains $preflight 'test-8c40-hibernation-m7-harness-invariants.ps1' 'M7 preflight must run M7 harness invariants.'
Assert-NotContains $preflight 'SetFanLevel(' 'M7 preflight must contain no direct fan write.'
Assert-NotContains $preflight 'Start-Service' 'M7 preflight must not start the watchdog service.'
Assert-NotContains $preflight '& shutdown.exe /h' 'M7 preflight must not hibernate the machine.'

Assert-Contains $physical '8C40-M7-HIBERNATION30' 'M7 physical token is missing.'
Assert-Contains $physical '--8c40-m7-hibernation-test' 'M7 must launch the dedicated app mode.'
Assert-Contains $physical '--8c40-m7-test-token' 'M7 must pass the dedicated token.'
Assert-Contains $physical 'transitionMode=hibernation' 'M7 markers must be bound to hibernation.'
Assert-Contains $physical 'Start-DelayedFailsafe' 'M7 must arm a delayed independent fallback.'
Assert-Contains $physical 'Wait-HibernationKernelEvidence' 'M7 must require OS hibernation evidence.'
Assert-Contains $physical '$_.Id -eq 42' 'M7 must inspect Kernel-Power 42.'
Assert-Contains $physical '$_.Id -eq 507' 'M7 must inspect Kernel-Power 507.'
Assert-Contains $physical '$_.Message -match ''(?i)hibern''' 'M7 must require hibernate-resume semantics.'
Assert-Contains $physical '$_.Id -eq 524' 'M7 must reject critical-battery events.'
Assert-Contains $physical '(?i)(battery|bater[ií]a)' 'M7 must reject battery-triggered transitions.'
Assert-Contains $physical 'source=GUID_SESSION_DISPLAY_STATUS/Off' 'M7 must require proactive display-Off handoff.'
Assert-Contains $physical 'restoreTrigger=registered-WM_POWERBROADCAST/PBT_APMSUSPEND' 'M7 must require registered pre-transition completion.'
Assert-Contains $physical 'watchdogRelease=True' 'M7 must require watchdog Release.'
Assert-Contains $physical 'acceptedUserResumes=1' 'M7 must require one accepted display-On resume.'
Assert-Contains $physical 'Wait-StableFirmwareAuto' 'M7 final proof must require stable FF/FF.'
Assert-Contains $physical 'Restore-M4Baseline' 'M7 must restore M4 Manual/stopped.'
Assert-Contains $physical 'collect-power-transition-diagnostics.ps1' 'M7 must collect post-transition power diagnostics.'
Assert-NotContains $physical '--restore-hp-auto' 'M7 parent must not invoke direct HP restore.'
Assert-NotContains $physical 'SetFanLevel(' 'M7 parent must not directly issue a fan command.'

$shutdownInvocations=[regex]::Matches($physical,'(?m)^\s*& shutdown\.exe /h\s*$').Count
if($shutdownInvocations -ne 1){
    throw "M7 must contain exactly one executable shutdown.exe /h invocation; observed $shutdownInvocations."
}

Write-Host 'HP 8C40 M7 hibernation preflight/physical harness invariant self-test: PASS' -ForegroundColor Green
