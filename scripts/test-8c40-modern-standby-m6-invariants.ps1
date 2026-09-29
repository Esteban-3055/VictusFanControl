$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$appPath=Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$programPath=Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$readerPath=Join-Path $repoRoot 'src\VictusFanControl.App\M6WatchdogState.cs'
$workerPath=Join-Path $repoRoot 'src\VictusFanControl.App\TelemetryWorker.cs'
$backendPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$factoryPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs'

$app=Get-Content $appPath -Raw
$program=Get-Content $programPath -Raw
$reader=Get-Content $readerPath -Raw
$worker=Get-Content $workerPath -Raw
$backend=Get-Content $backendPath -Raw
$factory=Get-Content $factoryPath -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}

Assert-Contains $program '--8c40-m6-modern-standby-test' 'M6 app mode is missing.'
Assert-Contains $program '--8c40-m6-test-token' 'M6 explicit token option is missing.'
Assert-Contains $program 'Hp8C40FanControlBackend.LifecycleQualificationToken' 'M6 must share the backend qualification token.'
Assert-Contains $program 'Hp8C40TargetProfile.Matches' 'M6 must exact-match HP 8C40 before MainForm/backend construction.'

Assert-Contains $backend 'LifecycleQualificationToken' 'M6 qualification token is missing from HP 8C40 backend.'
Assert-Contains $backend 'CreateLifecycleQualificationBackend' 'M6 qualification-only backend factory is missing.'
Assert-Contains $backend 'Exact HP 8C40 M6 Modern Standby lifecycle qualification backend.' 'M6 backend must identify its qualification-only scope.'
Assert-Contains $backend 'HP 8C40 watchdog recovery is not yet physically validated.' 'Ordinary public HP 8C40 watchdog construction must remain blocked before lifecycle qualification.'
Assert-Contains $factory 'HP 8C40 matched, but watchdog/service recovery has not yet' 'Production factory must remain blocked from watchdog-backed HP 8C40.'
Assert-NotContains $factory 'CreateLifecycleQualificationBackend' 'Production backend factory must not route through the M6 qualification bypass.'

Assert-Contains $app 'FanControlWatchdogLeaseContract.Hp8C40M4PipeName' 'M6 must use the isolated HP 8C40 M4 pipe.'
Assert-Contains $app 'Hp8C40FanControlBackend.CreateLifecycleQualificationBackend' 'M6 app mode must use the explicit qualification backend.'
Assert-Contains $app 'RegisterPowerSettingNotification' 'M6 must register for SESSION_DISPLAY_STATUS.'
Assert-Contains $app 'RegisterSuspendResumeNotification' 'M6 must explicitly register for suspend/resume notifications on Modern Standby.'
Assert-Contains $app 'GuidSessionDisplayStatus' 'M6 session-display GUID is missing.'
Assert-Contains $app 'GUID_SESSION_DISPLAY_STATUS/Off' 'M6 primary proactive release boundary must be session-display Off.'
Assert-Contains $app 'GUID_SESSION_DISPLAY_STATUS/On' 'M6 user-facing resume boundary must be session-display On.'
Assert-Contains $app 'PBT_APMRESUMEAUTOMATIC observed while SESSION_DISPLAY_STATUS remains Off' 'M6 must explicitly defer maintenance/automatic resume while display is Off.'
Assert-Contains $app 'PBT_APMRESUMESUSPEND observed while SESSION_DISPLAY_STATUS remains Off' 'M6 must explicitly defer resume-suspend while display is Off.'
Assert-Contains $app 'M6 user-visible resume gate' 'M6 must advance the freshness fence at display On.'
Assert-Contains $app '_worker.NotifyResume(source)' 'M6 display-On path must explicitly start telemetry recovery.'
Assert-Contains $app 'five-snapshot Healthy telemetry recovery' 'M6 re-entry must be coupled to full telemetry recovery.'
Assert-Contains $app 'M6WatchdogStateReader.RequireOwned30' 'M6 must prove durable OWNED 30/30 before sleep and after controlled re-entry.'
Assert-Contains $app 'M6WatchdogStateReader.RequireReady' 'M6 must require same ready watchdog across lifecycle.'
Assert-Contains $app 'ReadStableM6FirmwareAutoProof' 'M6 must use bounded stable FF/FF read proof.'
Assert-Contains $app 'consecutiveFirmwareAuto >= 2' 'M6 stable firmware proof must require two consecutive FF/FF reads.'
Assert-Contains $app 'unexpectedSamples >= 2' 'M6 must not classify a single torn EC pair as terminal external ownership.'
Assert-Contains $app 'WatchdogReleaseVerified: true' 'M6 pre-sleep and final handoff must require watchdog Release acknowledgement.'
Assert-Contains $app 'AllowCustomAdmissionAfterRecoveryAsync' 'M6 must explicitly reopen admission only after recovery.'
Assert-Contains $app 'recoveryTimestamp <=' 'M6 must require telemetry newer than display-On boundary.'
Assert-Contains $app 'Automatic policy remains OFF' 'M6 qualification mode must not enable automatic policy.'
Assert-Contains $app 'registered-WM_POWERBROADCAST/PBT_APMSUSPEND' 'M6 registered PBT suspend must be the synchronous pre-suspend completion barrier.'
Assert-Contains $app 'WM_POWERBROADCAST/PBT_APMSUSPEND fallback' 'M6 must retain a safety-only PBT suspend fallback.'
Assert-Contains $app 'primaryDisplaySignal: false' 'PBT suspend fallback must not masquerade as primary session-display proof.'
Assert-Contains $app 'WaitForHardwareReadQuiescenceAsync' 'M6 must quiesce telemetry hardware reads before restore IO.'
Assert-NotContains $app 'SetSuspendState' 'M6 app must not dispatch suspend itself.'
$m6Start=$app.IndexOf('private async Task AdvanceM6ModernStandbyHardwareTestAsync()',[StringComparison]::Ordinal)
$m6End=$app.IndexOf('private async Task AdvanceGateDHardwareTestAsync()',[StringComparison]::Ordinal)
if($m6Start-lt 0 -or $m6End-le $m6Start){throw 'M6 method scope is unavailable for isolation checks.'}
$m6Block=$app.Substring($m6Start,$m6End-$m6Start)
Assert-NotContains $m6Block '88F8-GATEG' 'M6-specific lifecycle implementation must not depend on historical 88F8 Gate G tokens.'

$offMethod=$app.IndexOf('private void HandleM6DisplayOffBoundary(',[StringComparison]::Ordinal)
$onMethod=$app.IndexOf('private void HandleM6SessionDisplayOn(',[StringComparison]::Ordinal)
if($offMethod-lt 0 -or $onMethod-lt 0){throw 'M6 lifecycle methods are missing.'}

$offEnd=$app.IndexOf('private void HandleM6SessionDisplayOn(',[StringComparison]::Ordinal)
$offBlock=$app.Substring($offMethod,$offEnd-$offMethod)
$fenceIndex=$offBlock.IndexOf('_fanCoordinator.CloseCustomAdmissionForLifecycleBoundary();',[StringComparison]::Ordinal)
$suspendIndex=$offBlock.IndexOf('_worker.NotifySuspend(source);',[StringComparison]::Ordinal)
$taskIndex=$offBlock.IndexOf('_m6PreSleepRestoreTask =',[StringComparison]::Ordinal)
$quiesceIndex=$offBlock.IndexOf('WaitForHardwareReadQuiescenceAsync',[StringComparison]::Ordinal)
$restoreIndex=$offBlock.IndexOf('_fanCoordinator.BlockCustomAdmissionAndRestoreAsync(',[StringComparison]::Ordinal)
$completeIndex=$offBlock.IndexOf('private void CompleteM6PreSleepRestore(',[StringComparison]::Ordinal)
if($fenceIndex-lt 0 -or $suspendIndex-lt 0 -or $taskIndex-lt 0 -or
   $quiesceIndex-lt 0 -or $restoreIndex-lt 0 -or $completeIndex-lt 0 -or
   -not ($fenceIndex-lt $suspendIndex -and $suspendIndex-lt $taskIndex -and
         $taskIndex-lt $quiesceIndex -and $quiesceIndex-lt $restoreIndex)){
    throw 'M6 display-Off order must remain fence -> telemetry Suspended -> background hardware quiescence -> restore, with registered PBT as completion barrier.'
}
Assert-Contains $offBlock 'restoreTrigger=' 'M6 pre-sleep marker must record the registered suspend completion trigger.'
Assert-Contains $offBlock 'displayOffAt=' 'M6 pre-sleep marker must retain the primary Display-Off boundary timestamp.'

$onEnd=$app.IndexOf('private (bool Verified, byte Cpu, byte Gpu, int Samples, string Detail)',[StringComparison]::Ordinal)
$onBlock=$app.Substring($onMethod,$onEnd-$onMethod)
$onFenceIndex=$onBlock.IndexOf('_fanCoordinator.BlockCustomAdmissionAndRestoreAsync(',[StringComparison]::Ordinal)
$notifyResumeIndex=$onBlock.IndexOf('_worker.NotifyResume(source)',[StringComparison]::Ordinal)
if($onFenceIndex-lt 0 -or $notifyResumeIndex-lt 0 -or $onFenceIndex-ge $notifyResumeIndex){
    throw 'M6 display-On order must advance the lifecycle freshness fence before telemetry NotifyResume.'
}

Assert-Contains $worker '_hardwareReadGate' 'M6 telemetry worker must serialize hardware snapshot IO.'
Assert-Contains $worker 'WaitForHardwareReadQuiescenceAsync' 'M6 telemetry worker must expose a bounded hardware-read quiescence barrier.'
Assert-Contains $worker 'ReadSnapshotIfCurrentAsync' 'Telemetry reads must be centralized behind the lifecycle-aware hardware gate.'
$directReads=[regex]::Matches($worker,'_reader!?\s*\.ReadSnapshot\(\)').Count
if($directReads-ne 1){
    throw "TelemetryWorker must contain exactly one direct ReadSnapshot call behind the hardware gate; found $directReads."
}
Assert-Contains $reader 'WatchdogM4' 'M6 watchdog reader must use the HP 8C40 M4 service root.'
Assert-Contains $reader 'm4-8c40.status.json' 'M6 watchdog reader must use the M4 exact-target status marker.'
Assert-Contains $reader 'Hp8C40TargetProfile.Instance.Id' 'M6 watchdog state must exact-match the HP 8C40 target.'
Assert-Contains $reader 'snapshot.SessionId != 0' 'M6 watchdog must remain Session 0.'
Assert-Contains $reader 'SYSTEM' 'M6 watchdog must remain LocalSystem.'
Assert-Contains $reader 'ProcessStartUtcTicks' 'M6 watchdog identity must include creation time.'
Assert-Contains $reader 'lease.SchemaVersion != 2' 'M6 durable journal proof must require schema 2.'
Assert-Contains $reader 'lease.Phase != 2' 'M6 durable journal proof must require OWNED.'
Assert-Contains $reader 'lease.Generation != 3' 'M6 initial/re-entry journal proof must require generation 3.'
Assert-Contains $reader 'lease.OwnedCpu != 30' 'M6 journal proof must require CPU 30.'
Assert-Contains $reader 'lease.OwnedGpu != 30' 'M6 journal proof must require GPU 30.'

Write-Host 'HP 8C40 M6 Modern Standby lifecycle invariant self-test: PASS' -ForegroundColor Green
