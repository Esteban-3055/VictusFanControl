$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$cliProgramPath = Join-Path $repoRoot 'src\VictusFanControl\Program.cs'
$cliOptionsPath = Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$gateG1StatePath = Join-Path $repoRoot 'src\VictusFanControl.App\GateG1WatchdogState.cs'
$telemetryPath = Join-Path $repoRoot 'src\VictusFanControl.App\TelemetryWorker.cs'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'
$backendPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp88F8FanControlBackend.cs'
$coordinatorPath = Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlCoordinator.cs'
$leaseClientPath = Join-Path $repoRoot 'src\VictusFanControl\Control\NamedPipeFanControlWatchdogLeaseClient.cs'
$activeClockPath = Join-Path $repoRoot 'src\VictusFanControl\Runtime\ActiveTimeClock.cs'
$harnessPath = Join-Path $PSScriptRoot 'test-watchdog-gate-g1.ps1'

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "Gate G1 invariant missing: $Description"
    }

    Write-Host "PASS  $Description"
}

function Assert-NotContains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    if ($Text -match $Pattern) {
        throw "Gate G1 invariant violated: $Description"
    }

    Write-Host "PASS  $Description"
}

function Assert-Ordered {
    param(
        [string]$Text,
        [string[]]$Needles,
        [string]$Description
    )

    $cursor = 0
    foreach ($needle in $Needles) {
        $index = $Text.IndexOf($needle, $cursor, [StringComparison]::Ordinal)
        if ($index -lt 0) {
            throw "Gate G1 invariant missing ordered token '$needle': $Description"
        }

        $cursor = $index + $needle.Length
    }

    Write-Host "PASS  $Description"
}

$program = Get-Content $programPath -Raw
$cliProgram = Get-Content $cliProgramPath -Raw
$cliOptions = Get-Content $cliOptionsPath -Raw
$mainForm = Get-Content $mainFormPath -Raw
$gateG1State = Get-Content $gateG1StatePath -Raw
$telemetry = Get-Content $telemetryPath -Raw
$manager = Get-Content $managerPath -Raw
$backend = Get-Content $backendPath -Raw
$coordinator = Get-Content $coordinatorPath -Raw
$leaseClient = Get-Content $leaseClientPath -Raw
$activeClock = Get-Content $activeClockPath -Raw
$harness = Get-Content $harnessPath -Raw

Write-Host 'VictusFanControl - GATE G1 LIFECYCLE INVARIANT SELF-TEST'

Assert-Contains -Text $program -Pattern '--gate-g1-suspend-test' -Description 'Gate G1 has a dedicated application mode'
Assert-Contains -Text $program -Pattern '--gate-g1-test-token' -Description 'Gate G1 requires a dedicated token option'
Assert-Contains -Text $program -Pattern '88F8-GATEG1-30' -Description 'Gate G1 requires the exact hardware opt-in token'
Assert-Contains -Text $program -Pattern '\(gateG1HardwareTest \? 1 : 0\)' -Description 'Gate G1 participates in hardware-test mutual exclusion'
Assert-Contains -Text $cliOptions -Pattern 'Probe88F8Setpoint' -Description 'CLI exposes a dedicated narrow 88F8 ownership probe'
Assert-Contains -Text $cliOptions -Pattern '--probe-88f8-setpoint' -Description 'CLI parses the dedicated narrow 88F8 ownership option'
Assert-Contains -Text $cliProgram -Pattern 'Probe88F8Setpoint[\s\S]*ReadSetpoint\(\)[\s\S]*setpoint CPU=' -Description 'CLI narrow ownership probe reads only the validated 0x34/0x35 setpoint pair'

Assert-Contains -Text $mainForm -Pattern 'else if \(_gateDHardwareTest \|\|[\s\S]*_gateG1HardwareTest \|\|[\s\S]*_gateG2HardwareTest\)[\s\S]*new NamedPipeFanControlWatchdogLeaseClient\(\)' -Description 'Gate G1/G2 use the real named-pipe production watchdog lease'
Assert-Contains -Text $gateG1State -Pattern 'CommonApplicationData' -Description 'Gate G1 reads watchdog state from machine-level ProgramData'
Assert-Contains -Text $gateG1State -Pattern 'gate-d\.status\.json' -Description 'Gate G1 consumes the production watchdog status marker'
Assert-Contains -Text $gateG1State -Pattern 'lease\.json' -Description 'Gate G1 checks the production durable lease journal'
Assert-Contains -Text $gateG1State -Pattern 'FileOptions\.WriteThrough' -Description 'Gate G1 lifecycle markers use write-through persistence'
Assert-Contains -Text $gateG1State -Pattern 'Flush\(flushToDisk: true\)' -Description 'Gate G1 lifecycle markers are flushed to disk'

Assert-Contains -Text $activeClock -Pattern 'QueryUnbiasedInterruptTime' -Description 'Gate G resumable operations use the Windows sleep-excluding active-time clock'
Assert-Contains -Text $activeClock -Pattern 'HundredNanosecondsPerMillisecond\s*=\s*10_000UL' -Description 'Gate G active-time clock converts QueryUnbiasedInterruptTime with the exact 100 ns to ms divisor'
Assert-NotContains -Text $backend -Pattern 'Stopwatch\.GetTimestamp|Stopwatch\.GetElapsedTime' -Description 'HP backend acknowledgement timeouts never count S3 through Stopwatch/QPC'
Assert-Contains -Text $backend -Pattern 'ActiveTimeClock\.HasElapsed\(' -Description 'HP setpoint/tach acknowledgement deadlines are driven by sleep-excluding active time'
Assert-NotContains -Text $leaseClient -Pattern '\.CancelAfter\(' -Description 'watchdog IPC timeouts never use .NET 8 CancelAfter timers that can expire across S3'
Assert-Contains -Text $leaseClient -Pattern 'ActiveTimeClock\.CancelAfterActiveTimeAsync\(' -Description 'watchdog connect/request/release timeouts are driven by sleep-excluding active time'

$suspendStart = $mainForm.IndexOf('private void HandleSuspendLifecycle', [StringComparison]::Ordinal)
$resumeStart = $mainForm.IndexOf('private void HandleResumeLifecycle', [StringComparison]::Ordinal)
if ($suspendStart -lt 0 -or $resumeStart -le $suspendStart) {
    throw 'Gate G1 invariant could not isolate the suspend lifecycle handler.'
}
$suspendHandler = $mainForm.Substring($suspendStart, $resumeStart - $suspendStart)

Assert-Ordered -Text $suspendHandler -Needles @(
    '_fanCoordinator.CloseCustomAdmissionForLifecycleBoundary();',
    '_worker.NotifySuspend(source);',
    '_gateGTelemetrySuspendedBeforeRestore =',
    '_fanCoordinator.BlockCustomAdmissionAndRestoreAsync('
) -Description 'Gate G closes admission, marks telemetry Suspended, then starts potentially blocking restore IO'

$notifySuspend = $suspendHandler.IndexOf('_worker.NotifySuspend(source);', [StringComparison]::Ordinal)
$restoreDispatch = $suspendHandler.IndexOf('_fanCoordinator.BlockCustomAdmissionAndRestoreAsync(', $notifySuspend, [StringComparison]::Ordinal)
if ($notifySuspend -lt 0 -or $restoreDispatch -le $notifySuspend) {
    throw 'Gate G1 invariant violated: telemetry must be marked Suspended before restore IO begins.'
}
Write-Host 'PASS  Gate G telemetry suspend boundary is established before restore IO can be frozen by S3'

$gateGPostRestoreStart = $suspendHandler.IndexOf('if (GateGHardwareTest &&', $restoreDispatch, [StringComparison]::Ordinal)
if ($gateGPostRestoreStart -ge 0) {
    $gateGPostRestore = $suspendHandler.Substring($gateGPostRestoreStart)
    Assert-NotContains -Text $gateGPostRestore -Pattern 'LastRestoreEvidence|LastFirmwareAuthorityAtUtc|GateG1WatchdogStateReader\.Read\(|WriteDurableMarker\(' -Description 'suspend handler performs no Gate-G proof work after the blocking restore returns'
}
else {
    Write-Host 'PASS  suspend handler has no Gate-G post-restore proof block'
}

Assert-Contains -Text $coordinator -Pattern 'public void CloseCustomAdmissionForLifecycleBoundary\(\)[\s\S]*_lifecycleFenceRequested = true;[\s\S]*CancelActiveCommand\(\);' -Description 'coordinator exposes a synchronous lifecycle fence/cancel phase'
Assert-Contains -Text $coordinator -Pattern 'BlockCustomAdmissionAndRestoreAsync[\s\S]*CloseCustomAdmissionForLifecycleBoundary\(\);' -Description 'full lifecycle restore idempotently reasserts the same fence before waiting'
Assert-Contains -Text $coordinator -Pattern 'LastFirmwareAuthorityAtUtc' -Description 'coordinator exposes the last Firmware transition timestamp'
Assert-Contains -Text $coordinator -Pattern 'FanControlStaleSafetyException' -Description 'coordinator classifies stale command safety before any hardware dispatch'
Assert-Contains -Text $coordinator -Pattern 'IsSafetyEvaluationCurrent\(SafetyGateResult safety\)' -Description 'Gate G can distinguish a superseded admission evaluation from a real admission denial'
Assert-Contains -Text $mainForm -Pattern 'private async Task<bool> TryEnterGateGCustomAuthorityAsync' -Description 'Gate G has a bounded fresh-safety admission retry helper'
Assert-Contains -Text $mainForm -Pattern 'const int maxAttempts = 5' -Description 'Gate G stale-safety retry count is bounded'
Assert-Contains -Text $mainForm -Pattern 'if \(_fanCoordinator\.IsSafetyEvaluationCurrent\(safety\)\)[\s\S]*return false;' -Description 'Gate G retries admission only when the attempted evaluation was actually superseded'
Assert-Contains -Text $mainForm -Pattern 'catch \(FanControlStaleSafetyException\)[\s\S]*when \(_fanCoordinator\.Authority == FanAuthority\.Custom\)' -Description 'Gate G retries stale command safety only while Custom authority is still intact'
Assert-Contains -Text $mainForm -Pattern 'TryEnterGateGCustomAuthorityAsync\([\s\S]*initial admission' -Description 'initial Gate G admission uses fresh-safety retry handling'
Assert-Contains -Text $mainForm -Pattern 'TryEnterGateGCustomAuthorityAsync\([\s\S]*controlled post-resume re-entry' -Description 'post-resume Gate G re-entry uses fresh-safety retry handling'
Assert-Contains -Text $mainForm -Pattern 'ApplyGateGCommandWithFreshSafetyAsync\(' -Description 'Gate G 30/30 commands retry only typed stale-safety races before dispatch'
Assert-Ordered -Text $coordinator -Needles @(
    'var changedAt = DateTimeOffset.UtcNow;',
    '_authority = next;',
    'if (next == FanAuthority.Firmware)',
    'Interlocked.Exchange(',
    'changedAt.UtcDateTime.Ticks',
    'AuthorityChanged?.Invoke('
) -Description 'Firmware transition timestamp is captured in the coordinator before event delivery'

$resumeHandlerStart = $mainForm.IndexOf('private void HandleResumeLifecycle', [StringComparison]::Ordinal)
$reopenHandlerStart = $mainForm.IndexOf('private async Task ReopenFanAdmissionAfterHealthyAsync', $resumeHandlerStart, [StringComparison]::Ordinal)
if ($resumeHandlerStart -lt 0 -or $reopenHandlerStart -le $resumeHandlerStart) {
    throw 'Gate G1 invariant could not isolate resume + causal proof persistence.'
}
$resumeHandler = $mainForm.Substring($resumeHandlerStart, $reopenHandlerStart - $resumeHandlerStart)

Assert-Contains -Text $mainForm -Pattern 'private enum GateGResumeProofState[\s\S]*Pending = 0[\s\S]*Verifying = 1[\s\S]*Verified = 2[\s\S]*Failed = 3' -Description 'Gate G models resume handoff proof as a monotonic per-cycle latch'

$proofHelperStart = $resumeHandler.IndexOf('private bool EnsureGateGHandoffVerifiedBeforeResumeAcceptance', [StringComparison]::Ordinal)
$verifyHelperStart = $resumeHandler.IndexOf('private bool VerifyGateGHandoffBeforeResumeAcceptance', $proofHelperStart, [StringComparison]::Ordinal)
if ($proofHelperStart -lt 0 -or $verifyHelperStart -le $proofHelperStart) {
    throw 'Gate G1 invariant could not isolate the one-shot Gate G resume proof helper.'
}
$proofHelper = $resumeHandler.Substring($proofHelperStart, $verifyHelperStart - $proofHelperStart)

Assert-Ordered -Text $proofHelper -Needles @(
    'Volatile.Read(',
    'case GateGResumeProofState.Verified:',
    'return true;',
    'case GateGResumeProofState.Failed:',
    'return false;',
    'case GateGResumeProofState.Verifying:',
    'return false;',
    'case GateGResumeProofState.Pending:',
    'Interlocked.CompareExchange(',
    '(int)GateGResumeProofState.Verifying',
    '(int)GateGResumeProofState.Pending',
    'VerifyGateGHandoffBeforeResumeAcceptance(',
    '? GateGResumeProofState.Verified',
    ': GateGResumeProofState.Failed',
    'return verified;'
) -Description 'Gate G claims one causal proof atomically and latches its first terminal result'

Assert-Contains -Text $proofHelper -Pattern 'catch[\s\S]*GateGResumeProofState\.Failed[\s\S]*throw;' -Description 'an unexpected proof exception is latched fail-closed and cannot be retried by a duplicate resume'

$verifyCallCount = [regex]::Matches(
    $mainForm,
    'VerifyGateGHandoffBeforeResumeAcceptance\(').Count
if ($verifyCallCount -ne 2) {
    throw "Gate G1 invariant violated: expected exactly one production call plus one definition for VerifyGateGHandoffBeforeResumeAcceptance; found $verifyCallCount."
}
Write-Host 'PASS  Gate G handoff proof has exactly one production call site'

Assert-Ordered -Text $resumeHandler -Needles @(
    'if (GateGHardwareTest &&',
    'EnsureGateGHandoffVerifiedBeforeResumeAcceptance(',
    'if (_gateG1HardwareTestResumeObserved)',
    'duplicate resume signal ignored after the cycle already accepted one resume',
    'return;',
    'var accepted = _worker.NotifyResume(source);',
    'if (!accepted)',
    '_gateG1HardwareTestResumeObserved = true;',
    '_gateG1AcceptedResumeCount++;'
) -Description 'Gate G latches the handoff before telemetry resume and blocks later Windows resume notifications from mutating the cycle'

Assert-Contains -Text $resumeHandler -Pattern '_fanCoordinator\.LastRestoreEvidence' -Description 'resume-side proof reads backend restore evidence captured by the production transaction'
Assert-Contains -Text $resumeHandler -Pattern '_fanCoordinator\.LastFirmwareAuthorityAtUtc' -Description 'resume-side proof reads the coordinator Firmware-transition timestamp'
Assert-Contains -Text $resumeHandler -Pattern 'GateG1WatchdogStateReader\.Read\(\)' -Description 'resume-side proof independently reads watchdog Ready/journal state after wake'
Assert-Contains -Text $resumeHandler -Pattern 'GateG1WatchdogStateReader\.RequireReady\(' -Description 'resume-side proof requires the original watchdog Ready before telemetry recovery'
Assert-Contains -Text $resumeHandler -Pattern '_worker\.StateMachine\.State == SystemState\.Suspended' -Description 'resume-side proof requires telemetry to remain Suspended until handoff validation completes'
Assert-Contains -Text $resumeHandler -Pattern 'telemetryMs <= GateGSuspendProofBudgetMs' -Description 'only the pre-block fence/telemetry work is constrained by the PBT_APMSUSPEND budget'
Assert-NotContains -Text $resumeHandler -Pattern 'restoreMs <= GateGSuspendProofBudgetMs|firmwareMs <= GateGSuspendProofBudgetMs|criticalHandoffMs <= GateGSuspendProofBudgetMs' -Description 'Gate G does not pretend Windows guarantees completion of blocking restore IO before physical S3'
Assert-Contains -Text $resumeHandler -Pattern 'handoffProof=completed-before-resume-acceptance' -Description 'Gate G marker states the supported suspend safety boundary explicitly'
Assert-Contains -Text $resumeHandler -Pattern 'ecProof=production-backend-restore-ack' -Description 'Gate G marker identifies production backend FF/FF acknowledgement'
Assert-Contains -Text $resumeHandler -Pattern 'watchdogRelease=\{watchdogReleaseVerified\}' -Description 'Gate G marker records causal watchdog Release acknowledgement'
Assert-Contains -Text $resumeHandler -Pattern 'watchdogState=Ready' -Description 'Gate G marker records post-wake watchdog Ready proof'
Assert-Contains -Text $resumeHandler -Pattern 'journalProof=watchdog-release-response\+post-resume-ready-check' -Description 'Gate G journal absence is backed by Release plus an independent post-wake Ready/journal check'
Assert-Contains -Text $resumeHandler -Pattern 'telemetryProof=pre-restore-state-transition' -Description 'Gate G marker records that telemetry Suspended was established before restore IO'
Assert-Contains -Text $resumeHandler -Pattern 'acceptedResumesBeforeProof=\{_gateG1AcceptedResumeCount\}' -Description 'Gate G marker records zero accepted resumes before handoff proof'
Assert-Contains -Text $resumeHandler -Pattern 'preBlockMs=\{FormatInvariantMs\(telemetryMs\)\}' -Description 'Gate G marker records culture-invariant pre-block latency'
Assert-Contains -Text $mainForm -Pattern 'GateGSuspendProofBudgetMs\s*=\s*1800' -Description 'Gate G keeps margin inside the approximately two-second Windows suspend notification budget for non-blocking pre-work'
Assert-NotContains -Text $suspendHandler -Pattern 'GateG1WatchdogStateReader\.WriteDurableMarker\(' -Description 'Gate G never persists proof from PBT_APMSUSPEND'
Assert-Contains -Text $resumeHandler -Pattern 'GateG1WatchdogStateReader\.WriteDurableMarker\(' -Description 'Gate G persists causal proof only after wake and before resume acceptance'

$restoreWithWatchdogStart = $backend.IndexOf('private async ValueTask RestoreWithWatchdogLockedAsync', [StringComparison]::Ordinal)
$restoreLockedStart = $backend.IndexOf('private async ValueTask RestoreLockedAsync', $restoreWithWatchdogStart, [StringComparison]::Ordinal)
$waitForSetpointStart = $backend.IndexOf('private async ValueTask<Hp88F8EcControlState> WaitForSetpointAsync', $restoreLockedStart, [StringComparison]::Ordinal)
if ($restoreWithWatchdogStart -lt 0 -or $restoreLockedStart -le $restoreWithWatchdogStart -or $waitForSetpointStart -le $restoreLockedStart) {
    throw 'Gate G1 invariant could not isolate production HP restore/evidence primitives.'
}
$restoreWithWatchdog = $backend.Substring($restoreWithWatchdogStart, $restoreLockedStart - $restoreWithWatchdogStart)
$restoreLocked = $backend.Substring($restoreLockedStart, $waitForSetpointStart - $restoreLockedStart)

Assert-Ordered -Text $restoreLocked -Needles @(
    '_hardware!.RestoreFirmwareAuto();',
    'WaitForSetpointAsync(',
    'byte.MaxValue,',
    'byte.MaxValue,',
    '_ownedSetpoint = null;',
    '_customModeActive = false;'
) -Description 'production backend cannot return a successful local restore before FF/FF acknowledgement'

Assert-Ordered -Text $restoreWithWatchdog -Needles @(
    'await RestoreLockedAsync(cancellationToken)',
    'await _watchdogLease.ReleaseAsync(',
    'watchdogReleaseVerified = true;',
    '_lastRestoreEvidence = new FanFirmwareRestoreEvidence(',
    'LocalFirmwareAckVerified: true',
    'WatchdogReleaseVerified: watchdogReleaseVerified',
    'CompletedAtUtc: DateTimeOffset.UtcNow'
) -Description 'backend restore evidence is published only after local FF/FF and the watchdog Release response'

$recoverDefinition = $manager.IndexOf(
    'private async ValueTask<LeaseRecoveryResult>',
    [StringComparison]::Ordinal)
if ($recoverDefinition -lt 0) {
    throw 'Gate G1 invariant could not locate watchdog recovery implementation.'
}
$recoverTail = $manager.Substring($recoverDefinition)
Assert-Ordered -Text $recoverTail -Needles @(
    'await _hardware.RestoreFirmwareAutoAsync(',
    'if (after.IsFirmwareOwned)',
    'await _journal.DeleteAsync(cancellationToken)',
    '_active = null;',
    'LeaseRecoveryDisposition.RestoredFirmware'
) -Description 'watchdog successful Release recovery polls for verified firmware ownership before deleting the journal'

Assert-Contains -Text $resumeHandler -Pattern '_gateGSuspendWasCustom\s*&&[\s\S]*_gateGSuspendBackendAckVerified\s*&&[\s\S]*_gateGTelemetrySuspendedBeforeRestore[\s\S]*telemetryTransitionFresh[\s\S]*telemetryStillSuspended[\s\S]*telemetryMs <= GateGSuspendProofBudgetMs[\s\S]*localFirmwareAckVerified[\s\S]*watchdogReleaseVerified[\s\S]*firmwareTransitionFresh[\s\S]*firmwareAuthorityCurrent[\s\S]*noAcceptedResumeYet[\s\S]*!watchdog\.JournalPresent' -Description 'Gate G PASS requires prompt pre-block fencing plus completed local/watchdog/Firmware handoff while telemetry remains Suspended and before resume acceptance'

$healthyStart = $mainForm.IndexOf('private async Task HandleHealthyStateAsync', [StringComparison]::Ordinal)
$gateDStart = $mainForm.IndexOf('private async Task AdvanceGateDHardwareTestAsync', $healthyStart, [StringComparison]::Ordinal)
if ($healthyStart -lt 0 -or $gateDStart -le $healthyStart) {
    throw 'Gate G1 invariant could not isolate HandleHealthyStateAsync.'
}
$healthyHandler = $mainForm.Substring($healthyStart, $gateDStart - $healthyStart)
$g1AdvanceIndex = $healthyHandler.IndexOf('await AdvanceGateG1HardwareTestAsync();', [StringComparison]::Ordinal)
$genericReopenIndex = $healthyHandler.IndexOf('await ReopenFanAdmissionAfterHealthyAsync();', [StringComparison]::Ordinal)
if ($g1AdvanceIndex -lt 0 -or $genericReopenIndex -lt 0 -or $g1AdvanceIndex -ge $genericReopenIndex) {
    throw 'Gate G1 invariant violated: Gate G1 must verify watchdog recovery before the generic lifecycle fence can reopen.'
}
Write-Host 'PASS  Gate G1 intercepts Healthy before generic admission reopen'

$g1Start = $mainForm.IndexOf('private async Task AdvanceGateG1HardwareTestAsync', [StringComparison]::Ordinal)
$lightLoadStart = $mainForm.IndexOf('private static void EnsureSuspendHardwareTestLightLoad', $g1Start, [StringComparison]::Ordinal)
if ($g1Start -lt 0 -or $lightLoadStart -le $g1Start) {
    throw 'Gate G1 invariant could not isolate AdvanceGateG1HardwareTestAsync.'
}
$g1Method = $mainForm.Substring($g1Start, $lightLoadStart - $g1Start)

$armStart = $mainForm.IndexOf('private async Task ArmGateGHardwareTestCycleAsync', [StringComparison]::Ordinal)
$resetStart = $mainForm.IndexOf('private void ResetGateGHardwareTestCycleState', $armStart, [StringComparison]::Ordinal)
if ($armStart -lt 0 -or $resetStart -le $armStart) {
    throw 'Gate G1 invariant could not isolate ArmGateGHardwareTestCycleAsync.'
}
$armMethod = $mainForm.Substring($armStart, $resetStart - $armStart)

Assert-Ordered -Text $armMethod -Needles @(
    'GateG1WatchdogStateReader.Read()',
    'GateG1WatchdogStateReader.RequireReady',
    'if (watchdog.JournalPresent)',
    'new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint()',
    'TryEnterGateGCustomAuthorityAsync(',
    'ApplyGateGCommandWithFreshSafetyAsync(',
    '_gateG1HardwareTestBackendAckVerified = true',
    '_gateG1HardwareTestArmed = true',
    'GateGReadyPath'
) -Description 'initial Gate G1/G2 READY follows clean watchdog/FF baseline and real watchdog-backed 30/30 acknowledgement through bounded fresh-safety helpers'
Assert-NotContains -Text $armMethod -Pattern 'Hp88F8EcControlStateProbe\(_modulesDirectory\)\.Read\(\)' -Description 'Gate G1/G2 initial ownership proof does not require a full EC diagnostic snapshot'
Assert-Contains -Text $g1Method -Pattern 'ReadSetpoint\(\)' -Description 'Gate G1 post-resume and final ownership proofs use the narrow setpoint reader'
Assert-NotContains -Text $g1Method -Pattern 'Hp88F8EcControlStateProbe\(_modulesDirectory\)\.Read\(\)' -Description 'Gate G1 lifecycle continuation does not require full EC diagnostic snapshots'

Assert-Ordered -Text $g1Method -Needles @(
    'if (!_gateG1HardwareTestSuspendObserved)',
    'if (!_gateG1HardwareTestPreSleepVerified)',
    'if (_gateG1AcceptedResumeCount != 1)',
    'SystemState.Healthy',
    'GateG1WatchdogStateReader.Read()',
    'GateG1WatchdogStateReader.RequireReady(',
    'if (watchdogAfterResume.JournalPresent)',
    '_fanCoordinator.AllowCustomAdmissionAfterRecoveryAsync('
) -Description 'post-resume fence stays closed until suspend causality, one accepted resume, Healthy telemetry and same watchdog Ready/journal-absent are proven'

Assert-Ordered -Text $g1Method -Needles @(
    '_fanCoordinator.AllowCustomAdmissionAfterRecoveryAsync(',
    'TryEnterGateGCustomAuthorityAsync(',
    'ApplyGateGCommandWithFreshSafetyAsync(',
    'GateGReentryPath',
    '_fanCoordinator.RestoreFirmwareAsync(',
    'GateG1WatchdogStateReader.RequireReady(',
    'watchdogFinal.JournalPresent',
    'CompleteGateGHardwareTest('
) -Description 'Gate G1/G2 perform one controlled post-recovery re-entry per cycle through bounded fresh-safety helpers, then restore and verify final watchdog/firmware state'

Assert-Contains -Text $telemetry -Pattern 'ResumeHealthySamplesRequired\s*=\s*5' -Description 'resume recovery still requires five complete post-boundary telemetry snapshots'
Assert-Contains -Text $manager -Pattern 'OwnedHeartbeatTimeout[\s\S]*TimeSpan\.FromSeconds\(5\)' -Description 'OWNED timeout remains 5 seconds'
Assert-Contains -Text $manager -Pattern 'WriteArmedDeadline[\s\S]*TimeSpan\.FromSeconds\(12\)' -Description 'WRITE_ARMED deadline remains 12 seconds'
Assert-Contains -Text $manager -Pattern 'RestoringDeadline[\s\S]*TimeSpan\.FromSeconds\(8\)' -Description 'RESTORING deadline remains 8 seconds'

$fanHardwareStart = $backend.IndexOf('internal sealed class Hp88F8FanHardware', [StringComparison]::Ordinal)
$backendClassStart = $backend.IndexOf('public sealed class Hp88F8FanControlBackend', $fanHardwareStart, [StringComparison]::Ordinal)
if ($fanHardwareStart -lt 0 -or $backendClassStart -le $fanHardwareStart) {
    throw 'Gate G1 invariant could not isolate the production HP hardware adapter.'
}
$fanHardware = $backend.Substring($fanHardwareStart, $backendClassStart - $fanHardwareStart)
Assert-NotContains -Text $fanHardware -Pattern 'ReadHp88F8ControlState\(\)' -Description 'production fan-control hardware adapter never opens the broad diagnostic EC snapshot'
Assert-Contains -Text $fanHardware -Pattern 'ReadHp88F8Setpoint\(\)' -Description 'production fan-control hardware adapter reads ownership through the narrow setpoint path'
Assert-Contains -Text $fanHardware -Pattern 'ReadHp88F8FanControlGuard\(\)' -Description 'production fan-control hardware adapter preserves MaxFan/FanSwitch safety separately'
Assert-Contains -Text $fanHardware -Pattern 'ReadFanTachometers\(\)' -Description 'production fan-control hardware adapter preserves dual-tach feedback separately'
Assert-Contains -Text $backend -Pattern 'WaitForSetpointAsync[\s\S]*_hardware!\.ReadSetpoint\(\)' -Description 'firmware and command setpoint acknowledgement polling stays ownership-only'

Assert-NotContains -Text $g1Method -Pattern '--restore-hp-auto' -Description 'Gate G1 application path never invokes the parent/CLI restore command'

Assert-NotContains -Text $harness -Pattern '(?im)^\s*\$pid\s*=' -Description 'physical Gate G1 never assigns to PowerShell automatic read-only $PID'
Assert-Contains -Text $harness -Pattern '\$serviceProcessId\s*=\s*\[int\]\$svc\.ProcessId' -Description 'physical Gate G1 uses a non-reserved local service PID variable'

Assert-Contains -Text $harness -Pattern 'Assert-DefaultWatchdogOutputUnlocked' -Description 'physical Gate G1 detects the legacy same-shell watchdog DLL lock before build'
Assert-Contains -Text $harness -Pattern 'journal to be absent BEFORE service reinstall/start' -Description 'physical Gate G1 refuses to hide a pre-existing durable lease by reinstalling the service'

$lockCheck = $harness.IndexOf('Assert-DefaultWatchdogOutputUnlocked', [StringComparison]::Ordinal)
$physicalBuild = $harness.IndexOf('dotnet build .\VictusFanControl.sln', [StringComparison]::Ordinal)
if ($lockCheck -lt 0 -or $physicalBuild -lt 0 -or $lockCheck -ge $physicalBuild) {
    throw 'Gate G1 invariant violated: stale-shell DLL lock detection must occur before the physical build.'
}
Write-Host 'PASS  physical Gate G1 checks the known stale-shell DLL lock before build'

$preExistingJournalCheck = $harness.IndexOf('journal to be absent BEFORE service reinstall/start', [StringComparison]::Ordinal)
$serviceInstall = $harness.IndexOf('install-watchdog-gate-d.ps1', [StringComparison]::Ordinal)
if ($preExistingJournalCheck -lt 0 -or $serviceInstall -lt 0 -or $preExistingJournalCheck -ge $serviceInstall) {
    throw 'Gate G1 invariant violated: pre-existing durable journal must be rejected before service reinstall.'
}
Write-Host 'PASS  physical Gate G1 rejects a pre-existing journal before service reinstall'

Assert-Contains -Text $harness -Pattern 'install-watchdog-gate-d\.ps1' -Description 'physical Gate G1 installs the validated production watchdog service'
Assert-Contains -Text $harness -Pattern 'Assert-ScmRecoveryPolicy' -Description 'physical Gate G1 verifies production SCM 1s/5s/10s recovery policy'
Assert-Contains -Text $harness -Pattern 'Test-JournalOwnedPhase' -Description 'physical Gate G1 validates durable OWNED state before suspend'
Assert-Contains -Text $harness -Pattern 'ProcessStartUtcTicks' -Description 'physical Gate G1 binds the durable lease to exact GUI process identity'
Assert-Contains -Text $harness -Pattern '--gate-g1-suspend-test' -Description 'physical harness launches the dedicated Gate G1 app mode'
Assert-Contains -Text $harness -Pattern '--gate-g1-test-token[\s\S]*88F8-GATEG1-30' -Description 'physical harness supplies the exact Gate G1 hardware token'
Assert-Contains -Text $harness -Pattern '--gate-g0-clock-probe[\s\S]*--gate-g0-auto-s3-token[\s\S]*88F8-G0-AUTO-S3' -Description 'physical harness reuses the validated read-only G0 helper as external S3 requester'
Assert-Contains -Text $harness -Pattern 'gate-g1\.presleep' -Description 'physical harness requires the durable pre-sleep handoff marker'
Assert-Contains -Text $harness -Pattern 'ecProof=production-backend-restore-ack' -Description 'physical Gate G1 requires backend-return FF/FF proof without a redundant suspend-time EC reader'
Assert-Contains -Text $harness -Pattern 'watchdogRelease=True' -Description 'physical Gate G1 requires successful watchdog Release acknowledgement'
Assert-Contains -Text $harness -Pattern 'watchdogState=Ready' -Description 'physical Gate G1 requires the original watchdog Ready after wake before resume acceptance'
Assert-Contains -Text $harness -Pattern 'journalProof=watchdog-release-response' -Description 'physical Gate G1 requires watchdog Release journal proof'
Assert-Contains -Text $harness -Pattern 'post-resume-ready-check' -Description 'physical Gate G1 requires independent post-wake Ready/journal proof'
Assert-Contains -Text $harness -Pattern 'telemetry=Suspended' -Description 'physical Gate G1 requires telemetry to remain Suspended through handoff proof before resume acceptance'
Assert-Contains -Text $harness -Pattern 'telemetryProof=pre-restore-state-transition' -Description 'physical Gate G1 requires proof that telemetry entered Suspended before restore IO'
Assert-Contains -Text $harness -Pattern 'telemetryMs=' -Description 'physical Gate G1 records telemetry suspend-boundary latency'
Assert-Contains -Text $harness -Pattern 'restoreMs=' -Description 'physical Gate G1 records backend restore completion latency'
Assert-Contains -Text $harness -Pattern 'firmwareMs=' -Description 'physical Gate G1 records coordinator Firmware-transition latency'
Assert-Contains -Text $harness -Pattern 'handoffProof=completed-before-resume-acceptance' -Description 'physical Gate G1 requires completed handoff before telemetry accepts resume'
Assert-Contains -Text $harness -Pattern 'acceptedResumesBeforeProof=0' -Description 'physical Gate G1 requires zero accepted resumes before handoff proof'
Assert-Contains -Text $harness -Pattern 'budgetMs=1800' -Description 'physical Gate G1 enforces the explicit suspend-handler proof budget'

Assert-Contains -Text $harness -Pattern 'gate-g1\.reentry' -Description 'physical harness requires the controlled re-entry marker'
Assert-Contains -Text $harness -Pattern 'Kernel-Power' -Description 'physical harness requires Windows suspend/resume event evidence'
Assert-Contains -Text $harness -Pattern 'Id\s*=\s*42,\s*107' -Description 'physical harness checks ordered Kernel-Power 42/107'
Assert-Contains -Text $harness -Pattern 'OWNED heartbeat timeout' -Description 'physical harness rejects OWNED timeout evidence'
Assert-Contains -Text $harness -Pattern 'WRITE_ARMED operation deadline' -Description 'physical harness rejects WRITE_ARMED deadline recovery'
Assert-Contains -Text $harness -Pattern 'RESTORING takeover deadline' -Description 'physical harness rejects RESTORING deadline recovery'
Assert-Contains -Text $harness -Pattern 'OwnershipAmbiguous' -Description 'physical harness rejects ownership ambiguity'
Assert-Contains -Text $harness -Pattern 'WATCHDOG OWNER LOSS:' -Description 'physical harness rejects owner-loss recovery in a normal lifecycle'
Assert-Contains -Text $harness -Pattern 'GATE D RECOVERY' -Description 'physical harness rejects watchdog recovery in a normal lifecycle'
Assert-NotContains -Text $harness -Pattern '--restore-hp-auto' -Description 'parent Gate G1 harness never invokes the HP restore CLI'

$readyBoundary = $harness.IndexOf('$readyText = Get-Content $readyPath -Raw', [StringComparison]::Ordinal)
$suspendRequest = $harness.IndexOf('$sleepOutput =', $readyBoundary, [StringComparison]::Ordinal)
if ($readyBoundary -lt 0 -or $suspendRequest -le $readyBoundary) {
    throw 'Gate G1 invariant could not isolate READY -> suspend-request parent-harness boundary.'
}
$readyToSuspend = $harness.Substring($readyBoundary, $suspendRequest - $readyBoundary)
Assert-NotContains -Text $readyToSuspend -Pattern 'Read-EcSetpoint' -Description 'parent harness performs no out-of-band EC probe between durable OWNED READY and suspend dispatch'
Assert-Contains -Text $harness -Pattern 'function Read-EcSetpoint' -Description 'physical Gate G1 uses the narrow ownership-only EC helper'
Assert-Contains -Text $harness -Pattern '--probe-88f8-setpoint' -Description 'physical Gate G1 invokes the narrow 0x34/0x35 CLI ownership probe'
Assert-Contains -Text $harness -Pattern '\[int\]\$Attempts = 3' -Description 'physical Gate G1 bounds transient EC setpoint retries to three attempts'
Assert-Contains -Text $harness -Pattern '\[int\]\$RetryDelayMs = 250' -Description 'physical Gate G1 spaces transient EC setpoint retries by 250 ms'
Assert-NotContains -Text $harness -Pattern '--probe-88f8-ec-state' -Description 'physical Gate G1 does not require a full EC snapshot for ownership proof'

$fallbackStart = $harness.IndexOf('$failsafe = Start-Process powershell.exe', [StringComparison]::Ordinal)
$appStart = $harness.IndexOf('$proc = Start-Process -FilePath $app', [StringComparison]::Ordinal)
if ($fallbackStart -lt 0 -or $appStart -lt 0 -or $fallbackStart -ge $appStart) {
    throw 'Gate G1 invariant violated: emergency fallback must be armed before the test GUI can acquire Custom.'
}
Write-Host 'PASS  physical Gate G1 arms emergency fallback before any Custom admission'

Assert-Ordered -Text $harness -Needles @(
    '$preSleep = Get-Content $preSleepPath -Raw',
    "authority=Firmware",
    "ec=255/255",
    "journal=absent",
    '$resultText = Get-Content $resultPath -Raw',
    '$reentryText = Get-Content $reentryPath -Raw',
    '$appExited = $proc.WaitForExit(15000)',
    'Assert-ProductionServiceReady -ExpectedPid $servicePidBefore',
    'if (Test-Path $journalPath)',
    '$finalEc = Read-EcSetpoint'
) -Description 'parent final EC verification occurs only after causal markers, GUI exit, same watchdog PID and journal absence checks'

Assert-Contains -Text $harness -Pattern 'if \(\$failsafe\.HasExited\)' -Description 'Gate G1 PASS rejects an emergency fallback that reached its execution boundary'
Assert-Contains -Text $harness -Pattern 'Emergency fallback cancelled only after journal absence \+ independent EC FF/FF were proven' -Description 'fallback cancellation requires independently proven firmware safety'
Assert-Contains -Text $harness -Pattern 'Type UNDERVOLT-OK' -Description 'physical Gate G1 requires pre-test OGH undervolt confirmation'
Assert-Contains -Text $harness -Pattern 'Type SAME if the CPU undervolt is unchanged' -Description 'physical Gate G1 requires post-test OGH undervolt confirmation'

Write-Host ''
Write-Host 'Gate G1 lifecycle invariant self-test: PASS' -ForegroundColor Green
exit 0
