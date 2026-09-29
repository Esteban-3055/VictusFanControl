$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$preflightPath=Join-Path $PSScriptRoot 'test-8c40-modern-standby-m6-preflight.ps1'
$physicalPath=Join-Path $PSScriptRoot 'test-8c40-modern-standby-m6.ps1'
$appPath=Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'

$preflight=Get-Content $preflightPath -Raw
$physical=Get-Content $physicalPath -Raw
$app=Get-Content $appPath -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}

# No-write preflight must remain incapable of fan ownership or service mutation.
Assert-Contains $preflight 'M6 MODERN STANDBY NO-WRITE PREFLIGHT' 'M6 no-write preflight identity is missing.'
Assert-Contains $preflight 'Assert-Exact8C40Target' 'M6 preflight must exact-match hardware.'
Assert-Contains $preflight 'test-8c40-modern-standby-m6-invariants.ps1' 'M6 preflight must run lifecycle invariants.'
Assert-Contains $preflight 'ModernStandbyProbe' 'M6 preflight must retain the M0 observer regression.'
Assert-Contains $preflight 'Assert-StableFirmwareBaseline' 'M6 preflight must prove stable firmware-owned baseline read-only.'
Assert-Contains $preflight "if (Test-Path $journalPath)" 'M6 preflight must explicitly refuse retained journal evidence.'
Assert-NotContains $preflight 'SetFanLevel(' 'M6 no-write preflight must contain no direct fan write.'
Assert-NotContains $preflight '--coordinator-write-token' 'M6 no-write preflight must not invoke a coordinator fan-write mode.'
Assert-NotContains $preflight 'Start-Service' 'M6 no-write preflight must not start the watchdog service.'
Assert-NotContains $preflight 'Stop-Service' 'M6 no-write preflight must not stop the watchdog service.'
Assert-NotContains $preflight 'Restart-Service' 'M6 no-write preflight must not restart the watchdog service.'
Assert-NotContains $preflight 'sc.exe failure' 'M6 no-write preflight must not mutate SCM recovery policy.'
Assert-NotContains $preflight 'SetSuspendState' 'M6 no-write preflight must not request sleep.'

# Physical harness: user initiates sleep, parent has no direct HP restore/write.
Assert-Contains $physical '8C40-M6-MODERN-STANDBY30' 'M6 physical harness explicit token is missing.'
Assert-Contains $physical 'Start-DelayedFailsafe' 'M6 physical harness must arm an independent delayed safety fallback.'
Assert-Contains $physical '$failsafe = Start-DelayedFailsafe' 'M6 fallback must be armed before launching the lifecycle controller.'
Assert-Contains $physical '--8c40-m6-modern-standby-test' 'M6 physical harness must launch the dedicated app mode.'
Assert-Contains $physical 'Start -> Power -> Sleep' 'M6 physical harness must instruct a user-initiated sleep.'
Assert-Contains $physical 'Wait-ModernStandbyKernelEvidence' 'M6 must require OS-level Modern Standby evidence.'
Assert-Contains $physical '$_.Id -eq 506' 'M6 must require Kernel-Power 506.'
Assert-Contains $physical '$_.Id -eq 507' 'M6 must require Kernel-Power 507.'
Assert-Contains $physical 'source=GUID_SESSION_DISPLAY_STATUS/Off' 'M6 must require the primary display-Off handoff marker.'
Assert-Contains $physical 'primaryDisplaySignal=True' 'M6 must reject a PBT fallback as primary lifecycle proof.'
Assert-Contains $physical 'watchdogRelease=True' 'M6 must require durable watchdog Release before sleep handoff acceptance.'
Assert-Contains $physical 'resumeAutomaticWhileOff=True' 'M6 must validate maintenance/automatic resume deferral when observed.'
Assert-Contains $physical 'resumeSuspendWhileOff=True' 'M6 must validate resume-suspend deferral when observed.'
Assert-Contains $physical 'acceptedUserResumes=1' 'M6 must require exactly one user-facing display-On resume.'
Assert-Contains $physical 'M6 controlled post-resume re-entry marker is incomplete.' 'M6 must verify controlled post-resume 30/30 re-entry.'
Assert-Contains $physical 'Wait-StableFirmwareAuto' 'M6 parent final EC proof must tolerate one torn EC read without accepting it.'
Assert-Contains $physical 'Restore-M4Baseline' 'M6 must restore M4 Manual/stopped after firmware safety.'
Assert-Contains $physical 'M6 emergency fallback cancelled only after journal absence + stable independent FF/FF proof.' 'M6 must keep the delayed fallback until independent safety proof.'
Assert-Contains $physical 'collect-power-transition-diagnostics.ps1' 'M6 must capture post-transition Windows power diagnostics.'
Assert-NotContains $physical 'SetSuspendState' 'M6 parent must not programmatically request sleep.'
Assert-NotContains $physical '--restore-hp-auto' 'M6 parent must not invoke direct HP restore.'
Assert-NotContains $physical 'SetFanLevel(' 'M6 parent must not directly issue a fan command.'

$armIndex=$physical.IndexOf('$failsafe = Start-DelayedFailsafe',[StringComparison]::Ordinal)
$appIndex=$physical.IndexOf('$app = Start-Process',[StringComparison]::Ordinal)
if($armIndex-lt 0 -or $appIndex-lt 0 -or $armIndex-ge $appIndex){
    throw 'M6 delayed failsafe must be armed before launching the write-capable app.'
}

# App-specific lifecycle ordering and anti-maintenance-wake invariants.
Assert-Contains $app '_m6ResumeAutomaticObservedWhileDisplayOff = true' 'M6 app must record automatic resume while display remains Off.'
Assert-Contains $app '_m6ResumeSuspendObservedWhileDisplayOff = true' 'M6 app must record resume-suspend while display remains Off.'
Assert-Contains $app 'SESSION_DISPLAY_STATUS On accepted as the sole user-facing resume boundary' 'M6 app must gate telemetry recovery on display On.'
Assert-Contains $app '_worker.NotifyResume(source)' 'M6 display-On path must explicitly resume telemetry.'
Assert-Contains $app 'AllowCustomAdmissionAfterRecoveryAsync' 'M6 must explicitly reopen the lifecycle fence after fresh Healthy recovery.'
Assert-Contains $app 'M6WatchdogStateReader.RequireOwned30' 'M6 must prove durable OWNED 30/30 around controlled writes.'

Write-Host 'HP 8C40 M6 preflight/physical harness invariant self-test: PASS' -ForegroundColor Green
