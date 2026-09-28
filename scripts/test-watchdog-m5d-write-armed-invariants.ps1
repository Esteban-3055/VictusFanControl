$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$harnessPath=Join-Path $PSScriptRoot 'test-watchdog-m5d-write-armed-crash-8c40.ps1'
$childPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M5DWriteArmedCrashTest.cs'
$backendPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$cliPath=Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs'
$programPath=Join-Path $repoRoot 'src\VictusFanControl\Program.cs'

$harness=Get-Content $harnessPath -Raw
$child=Get-Content $childPath -Raw
$backend=Get-Content $backendPath -Raw
$cli=Get-Content $cliPath -Raw
$program=Get-Content $programPath -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}

function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}

Assert-Contains $harness 'VictusFanControlWatchdogM4' 'M5D must use the isolated M4 watchdog.'
Assert-Contains $harness '8C40-M5D-WRITE-ARMED-CRASH30' 'M5D explicit token is missing.'
Assert-Contains $harness "TargetProfileId -cne 'HP-8C40-9D0R1LA-F18'" 'M5D journal must be exact-target bound.'
Assert-Contains $harness 'Is-WriteArmed' 'M5D must explicitly require WRITE_ARMED phase.'
Assert-Contains $harness '[long]$j.Generation -ne 2' 'M5D must require generation 2 WRITE_ARMED.'
Assert-Contains $harness '$null -ne $j.PreviousOwned' 'M5D first-write gate must require PreviousOwned null.'
Assert-Contains $harness '[int]$j.Pending.Cpu -ne 30' 'M5D must require pending CPU 30.'
Assert-Contains $harness '[int]$j.Pending.Gpu -ne 30' 'M5D must require pending GPU 30.'
Assert-Contains $harness '$null -ne $j.Owned' 'M5D must prove no durable OWNED/Commit exists.'
Assert-Contains $harness '[int]$j.Controller.ProcessId -ne $pid' 'M5D must bind journal to exact controller PID.'
Assert-Contains $harness '[long]$j.Controller.ProcessStartUtcTicks -ne $ticks' 'M5D must bind journal to exact controller creation time.'
Assert-Contains $harness '$ec=Read-Setpoint' 'M5D must independently read the real EC before controller kill.'
Assert-Contains $harness '$ec.Cpu -ne 30 -or $ec.Gpu -ne 30' 'M5D must require real EC 30/30 while journal is WRITE_ARMED.'
Assert-Contains $harness 'Stop-Process -Id $controller.Id -Force' 'M5D must kill only the exact controller.'
Assert-Contains $harness '(Get-ServicePid)-ne $servicePidBefore' 'M5D must require the original watchdog PID to remain alive.'
Assert-Contains $harness 'WATCHDOG COMMIT ACK controller PID={0}' 'M5D must search for forbidden Commit evidence.'
Assert-Contains $harness 'if($commit)' 'M5D must fail if Commit occurred before controller death.'
Assert-Contains $harness 'RestoredFirmware' 'M5D must require causal watchdog firmware restore evidence.'
Assert-Contains $harness 'Independent final EC' 'M5D must independently prove final FF/FF.'
Assert-Contains $harness 'Wait-JournalGone' 'M5D must require durable journal deletion after recovery.'

Assert-NotContains $harness '--restore-hp-auto' 'M5D parent must not directly invoke HP-auto restore.'
Assert-NotContains $harness 'Hp8C40BiosFanControl' 'M5D parent must not instantiate direct HP BIOS authority.'
Assert-NotContains $harness 'SetFanLevel(' 'M5D parent must not issue a fan write directly.'

Assert-Contains $child 'IHp8C40FanWriteQualificationHook' 'M5D child must use the qualification-only backend hook.'
Assert-Contains $child 'NamedPipeFanControlWatchdogLeaseClient' 'M5D child must use the real M4 named-pipe lease.'
Assert-Contains $child 'Hp8C40FanHardware' 'M5D child must use real HP 8C40 hardware.'
Assert-Contains $child 'FanControlCoordinator' 'M5D child must use the real coordinator.'
Assert-Contains $child 'QualificationLevel = 30' 'M5D child target must stay pinned to 30/30.'
Assert-Contains $child 'WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT' 'M5D marker must identify the exact crash stage.'
Assert-Contains $child 'real-wmi+ec+tachs;watchdog-commit-not-dispatched' 'M5D marker must encode real hardware ACK with Commit undispatched.'
Assert-Contains $child 'Timeout.InfiniteTimeSpan' 'M5D child must hold the pre-Commit boundary until parent kill or managed cancellation.'
Assert-Contains $child 'ProcessStartUtcTicks' 'M5D READY must include process creation time.'
Assert-NotContains $child 'CommitAsync(' 'M5D child must not manually dispatch watchdog Commit.'

Assert-Contains $backend 'IHp8C40FanWriteQualificationHook' 'Backend qualification hook interface is missing.'
Assert-Contains $backend 'AfterHardwareAcknowledgedBeforeWatchdogCommitAsync' 'Backend pre-Commit hook call is missing.'
Assert-Contains $backend '_qualificationHook = null;' 'Production constructor must hard-disable the internal qualification hook.'

$intentIndex=$backend.IndexOf('await _watchdogLease.WriteIntentAsync(',[StringComparison]::Ordinal)
$wmiIndex=$backend.IndexOf('_hardware.SetFanLevel(cpuTarget, gpuTarget);',[StringComparison]::Ordinal)
$tachIndex=$backend.IndexOf('var tachAck = await WaitForTachometerResponseAsync(',[StringComparison]::Ordinal)
$hookIndex=$backend.IndexOf('.AfterHardwareAcknowledgedBeforeWatchdogCommitAsync(',[StringComparison]::Ordinal)
$commitIndex=$backend.IndexOf('await _watchdogLease.CommitAsync(',[StringComparison]::Ordinal)

if($intentIndex-lt 0 -or $wmiIndex-lt 0 -or $tachIndex-lt 0 -or $hookIndex-lt 0 -or $commitIndex-lt 0){
    throw 'M5D backend ordering anchors are incomplete.'
}

if(-not ($intentIndex -lt $wmiIndex -and $wmiIndex -lt $tachIndex -and $tachIndex -lt $hookIndex -and $hookIndex -lt $commitIndex)){
    throw 'M5D qualification hook must remain strictly WriteIntent -> WMI -> tach ACK -> hook -> Commit.'
}

Assert-Contains $cli '--8c40-m5d-write-armed-crash-controller' 'M5D CLI mode is missing.'
Assert-Contains $cli '--8c40-m5d-token' 'M5D CLI token is missing.'
Assert-Contains $cli '--8c40-m5d-ready-path' 'M5D CLI READY path is missing.'
Assert-Contains $program 'Hp8C40M5DWriteArmedCrashTest.RequiredToken' 'M5D Program token gate is missing.'
Assert-Contains $program 'Hp8C40M5DWriteArmedCrashTest.RunAsync' 'M5D Program dispatch is missing.'

Write-Host 'HP 8C40 M5D WRITE_ARMED post-WMI/pre-Commit invariant self-test: PASS' -ForegroundColor Green
