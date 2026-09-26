$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$cliProgramPath = Join-Path $repoRoot 'src\VictusFanControl\Program.cs'
$cliOptionsPath = Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$telemetryPath = Join-Path $repoRoot 'src\VictusFanControl.App\TelemetryWorker.cs'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'
$coordinatorPath = Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlCoordinator.cs'
$leaseHardwarePath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\GateDLeaseHardware.cs'
$ecReaderPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\PawnIo\AcpiEcReader.cs'
$backendPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp88F8FanControlBackend.cs'
$harnessPath = Join-Path $PSScriptRoot 'test-watchdog-gate-g2.ps1'

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "Gate G2 invariant missing: $Description"
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
        throw "Gate G2 invariant violated: $Description"
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
            throw "Gate G2 invariant missing ordered token '$needle': $Description"
        }

        $cursor = $index + $needle.Length
    }

    Write-Host "PASS  $Description"
}

$program = Get-Content $programPath -Raw
$cliProgram = Get-Content $cliProgramPath -Raw
$cliOptions = Get-Content $cliOptionsPath -Raw
$mainForm = Get-Content $mainFormPath -Raw
$telemetry = Get-Content $telemetryPath -Raw
$manager = Get-Content $managerPath -Raw
$coordinator = Get-Content $coordinatorPath -Raw
$leaseHardware = Get-Content $leaseHardwarePath -Raw
$ecReader = Get-Content $ecReaderPath -Raw
$backend = Get-Content $backendPath -Raw
$harness = Get-Content $harnessPath -Raw

Write-Host 'VictusFanControl - GATE G2 5/5 LIFECYCLE INVARIANT SELF-TEST'

Assert-Contains -Text $program -Pattern '--gate-g2-suspend-repeat-test' -Description 'Gate G2 has a dedicated repeat-lifecycle application mode'
Assert-Contains -Text $program -Pattern '--gate-g2-test-token' -Description 'Gate G2 requires a dedicated token option'
Assert-Contains -Text $program -Pattern '88F8-GATEG2-30' -Description 'Gate G2 requires the exact hardware opt-in token'
Assert-Contains -Text $program -Pattern '\(gateG2HardwareTest \? 1 : 0\)' -Description 'Gate G2 participates in hardware-test mutual exclusion'
Assert-Contains -Text $cliOptions -Pattern 'Probe88F8Setpoint' -Description 'CLI exposes a dedicated narrow 88F8 ownership probe'
Assert-Contains -Text $cliOptions -Pattern '--probe-88f8-setpoint' -Description 'CLI parses the dedicated narrow 88F8 ownership option'
Assert-Contains -Text $cliProgram -Pattern 'Probe88F8Setpoint[\s\S]*ReadSetpoint\(\)[\s\S]*setpoint CPU=' -Description 'CLI narrow ownership probe reads only the validated 0x34/0x35 setpoint pair'

Assert-Contains -Text $mainForm -Pattern 'GateG2TargetCycles\s*=\s*5' -Description 'Gate G2 target is exactly five consecutive cycles'
Assert-Contains -Text $mainForm -Pattern 'GateGHardwareTest\s*=>\s*_gateG1HardwareTest\s*\|\|\s*_gateG2HardwareTest' -Description 'Gate G1 and G2 share the validated lifecycle path'
Assert-Contains -Text $mainForm -Pattern 'gate-g2\.cycle-\{_gateGCurrentCycle\}\.ready' -Description 'Gate G2 READY evidence is cycle-scoped'
Assert-Contains -Text $mainForm -Pattern 'gate-g2\.cycle-\{_gateGCurrentCycle\}\.presleep' -Description 'Gate G2 pre-sleep evidence is cycle-scoped'
Assert-Contains -Text $mainForm -Pattern 'gate-g2\.cycle-\{_gateGCurrentCycle\}\.reentry' -Description 'Gate G2 re-entry evidence is cycle-scoped'
Assert-Contains -Text $mainForm -Pattern 'gate-g2\.cycle-\{_gateGCurrentCycle\}\.result' -Description 'Gate G2 result evidence is cycle-scoped'
Assert-Contains -Text $mainForm -Pattern 'gate-g2\.result' -Description 'Gate G2 publishes a final 5/5 result marker'

Assert-Contains -Text $mainForm -Pattern '_gateG1HardwareTest \|\|[\s\S]*_gateG2HardwareTest\)[\s\S]*new NamedPipeFanControlWatchdogLeaseClient\(\)' -Description 'Gate G2 uses the real production watchdog named-pipe lease'

$resumeStart = $mainForm.IndexOf('private void HandleResumeLifecycle', [StringComparison]::Ordinal)
$reopenStart = $mainForm.IndexOf('private async Task ReopenFanAdmissionAfterHealthyAsync', $resumeStart, [StringComparison]::Ordinal)
if ($resumeStart -lt 0 -or $reopenStart -le $resumeStart) {
    throw 'Gate G2 invariant could not isolate resume lifecycle handler.'
}
$resumeHandler = $mainForm.Substring($resumeStart, $reopenStart - $resumeStart)

Assert-Contains -Text $mainForm -Pattern 'private enum GateGResumeProofState[\s\S]*Pending = 0[\s\S]*Verifying = 1[\s\S]*Verified = 2[\s\S]*Failed = 3' -Description 'Gate G2 uses a monotonic per-cycle resume proof latch'

$proofHelperStart = $resumeHandler.IndexOf('private bool EnsureGateGHandoffVerifiedBeforeResumeAcceptance', [StringComparison]::Ordinal)
$verifyHelperStart = $resumeHandler.IndexOf('private bool VerifyGateGHandoffBeforeResumeAcceptance', $proofHelperStart, [StringComparison]::Ordinal)
if ($proofHelperStart -lt 0 -or $verifyHelperStart -le $proofHelperStart) {
    throw 'Gate G2 invariant could not isolate the one-shot resume proof helper.'
}
$proofHelper = $resumeHandler.Substring($proofHelperStart, $verifyHelperStart - $proofHelperStart)

Assert-Ordered -Text $proofHelper -Needles @(
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
    ': GateGResumeProofState.Failed'
) -Description 'only the first resume can claim and complete Gate G2 causal handoff verification'

Assert-Contains -Text $proofHelper -Pattern 'catch[\s\S]*GateGResumeProofState\.Failed[\s\S]*throw;' -Description 'Gate G2 proof exceptions remain permanently fail-closed for the cycle'

$verifyCallCount = [regex]::Matches(
    $mainForm,
    'VerifyGateGHandoffBeforeResumeAcceptance\(').Count
if ($verifyCallCount -ne 2) {
    throw "Gate G2 invariant violated: expected exactly one production call plus one definition for VerifyGateGHandoffBeforeResumeAcceptance; found $verifyCallCount."
}
Write-Host 'PASS  Gate G2 handoff verification has exactly one production call site'

Assert-Ordered -Text $resumeHandler -Needles @(
    'EnsureGateGHandoffVerifiedBeforeResumeAcceptance(',
    'if (_gateG1HardwareTestResumeObserved)',
    'duplicate resume signal ignored after the cycle already accepted one resume',
    'return;',
    'var accepted = _worker.NotifyResume(source);',
    'if (!accepted)',
    'return;',
    '_gateG1AcceptedResumeCount++'
) -Description 'Gate G2 proves handoff once before telemetry resume and rejects later duplicate Windows resume notifications'

$advanceStart = $mainForm.IndexOf('private async Task AdvanceGateG1HardwareTestAsync', [StringComparison]::Ordinal)
$armStart = $mainForm.IndexOf('private async Task ArmGateGHardwareTestCycleAsync', $advanceStart, [StringComparison]::Ordinal)
$resetStart = $mainForm.IndexOf('private void ResetGateGHardwareTestCycleState', $armStart, [StringComparison]::Ordinal)
$cycleResultStart = $mainForm.IndexOf('private void WriteGateG2CycleResult', $resetStart, [StringComparison]::Ordinal)
$completeStart = $mainForm.IndexOf('private void CompleteGateGHardwareTest', $cycleResultStart, [StringComparison]::Ordinal)
$lightLoadStart = $mainForm.IndexOf('private static void EnsureSuspendHardwareTestLightLoad', $completeStart, [StringComparison]::Ordinal)

if ($advanceStart -lt 0 -or $armStart -le $advanceStart -or $resetStart -le $armStart -or $cycleResultStart -le $resetStart -or $completeStart -le $cycleResultStart -or $lightLoadStart -le $completeStart) {
    throw 'Gate G2 invariant could not isolate Gate G lifecycle methods.'
}

$advanceMethod = $mainForm.Substring($advanceStart, $armStart - $advanceStart)
$armMethod = $mainForm.Substring($armStart, $resetStart - $armStart)
$resetMethod = $mainForm.Substring($resetStart, $cycleResultStart - $resetStart)
$cycleResultMethod = $mainForm.Substring($cycleResultStart, $completeStart - $cycleResultStart)
$completeMethod = $mainForm.Substring($completeStart, $lightLoadStart - $completeStart)

Assert-Ordered -Text $armMethod -Needles @(
    'GateG1WatchdogStateReader.Read()',
    'if (_gateG1WatchdogPid == 0)',
    '_gateG1WatchdogPid = watchdog.ProcessId',
    'else',
    'GateG1WatchdogStateReader.RequireReady(',
    '_gateG1WatchdogPid',
    'if (watchdog.JournalPresent)',
    'new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint()',
    'TryEnterGateGCustomAuthorityAsync(',
    'ApplyGateGCommandWithFreshSafetyAsync(',
    '_gateG1HardwareTestBackendAckVerified = true',
    '_gateG1HardwareTestArmed = true',
    'GateGReadyPath'
) -Description 'every Gate G2 cycle reuses the original watchdog PID and acquires exact watchdog-backed 30/30 only from a clean FF/FF baseline through fresh-safety helpers'
Assert-NotContains -Text $armMethod -Pattern 'Hp88F8EcControlStateProbe\(_modulesDirectory\)\.Read\(\)' -Description 'Gate G2 initial ownership proof does not require a full EC diagnostic snapshot'
Assert-Contains -Text $advanceMethod -Pattern 'ReadSetpoint\(\)' -Description 'Gate G2 post-resume and final ownership proofs use the narrow setpoint reader'
Assert-NotContains -Text $advanceMethod -Pattern 'Hp88F8EcControlStateProbe\(_modulesDirectory\)\.Read\(\)' -Description 'Gate G2 lifecycle continuation does not require full EC diagnostic snapshots'

Assert-Ordered -Text $advanceMethod -Needles @(
    'if (_gateG1AcceptedResumeCount != 1)',
    'SystemState.Healthy',
    'GateG1WatchdogStateReader.RequireReady(',
    '_gateG1WatchdogPid',
    'if (watchdogAfterResume.JournalPresent)',
    '_fanCoordinator.AllowCustomAdmissionAfterRecoveryAsync(',
    'TryEnterGateGCustomAuthorityAsync(',
    'ApplyGateGCommandWithFreshSafetyAsync(',
    'GateGReentryPath',
    '_fanCoordinator.RestoreFirmwareAsync(',
    'watchdogFinal.JournalPresent',
    'WriteGateG2CycleResult(',
    '_gateGCurrentCycle++',
    'ResetGateGHardwareTestCycleState();',
    'await WaitForGateGInterCycleTelemetryStabilityAsync(',
    'await ArmGateGHardwareTestCycleAsync();'
) -Description 'cycles validate recovery, perform one fresh-safety re-entry, restore, persist PASS, require post-restore telemetry stability, then advance without restarting the GUI'

Assert-Contains -Text $mainForm -Pattern 'GateGInterCycleStableSnapshotsRequired\s*=\s*2' -Description 'Gate G2 requires two distinct stable post-restore telemetry snapshots before the next cycle'
Assert-Contains -Text $mainForm -Pattern 'GateGInterCycleStabilityTimeout[\s\S]*TimeSpan\.FromSeconds\(15\)' -Description 'Gate G2 inter-cycle stabilization wait is bounded'
Assert-Contains -Text $mainForm -Pattern 'private async Task<TelemetrySnapshot> WaitForGateGInterCycleTelemetryStabilityAsync' -Description 'Gate G2 has a dedicated inter-cycle stabilization barrier'
Assert-Contains -Text $mainForm -Pattern 'snapshot\.Timestamp > finalFirmwareAt' -Description 'Gate G2 stabilization accepts only telemetry captured after the previous Firmware transition'
Assert-Contains -Text $mainForm -Pattern 'snapshot\.IsComplete' -Description 'Gate G2 stabilization requires complete telemetry'
Assert-Contains -Text $mainForm -Pattern '_worker\.StateMachine\.State == SystemState\.Healthy' -Description 'Gate G2 stabilization requires Healthy runtime state'
Assert-Contains -Text $mainForm -Pattern 'displaySafety\.CustomControlPermitted' -Description 'Gate G2 stabilization requires SafetyGate permission without consuming control ordering'
Assert-Contains -Text $mainForm -Pattern '_fanCoordinator\.Authority == FanAuthority\.Firmware' -Description 'Gate G2 stabilization occurs only while firmware still owns the fans'
Assert-Contains -Text $coordinator -Pattern 'DescribeSafetyDenialLocked' -Description 'safety-supervisor handoffs include explicit denial diagnostics'
Assert-Contains -Text $leaseHardware -Pattern '\.ReadSetpoint\(\)' -Description 'watchdog lease hardware uses the narrow setpoint probe'
Assert-NotContains -Text $leaseHardware -Pattern '\.Read\(\)' -Description 'watchdog lease hardware does not open the full 88F8 control-state probe'
Assert-Contains -Text $ecReader -Pattern 'ReadHp88F8Setpoint\(\)[\s\S]*ReadRegisterLocked\(0x34\)[\s\S]*ReadRegisterLocked\(0x35\)' -Description 'narrow watchdog EC snapshot reads only the validated 0x34/0x35 ownership pair'
Assert-Contains -Text $ecReader -Pattern 'ReadHp88F8FanControlGuard\(\)[\s\S]*ReadRegisterLocked\(0xEC\)[\s\S]*ReadRegisterLocked\(0xF4\)' -Description 'production control guard reads only MaxFan and FanSwitch'
$fanHardwareStart = $backend.IndexOf('internal sealed class Hp88F8FanHardware', [StringComparison]::Ordinal)
$backendClassStart = $backend.IndexOf('public sealed class Hp88F8FanControlBackend', $fanHardwareStart, [StringComparison]::Ordinal)
if ($fanHardwareStart -lt 0 -or $backendClassStart -le $fanHardwareStart) {
    throw 'Gate G2 invariant could not isolate the production HP hardware adapter.'
}
$fanHardware = $backend.Substring($fanHardwareStart, $backendClassStart - $fanHardwareStart)
Assert-NotContains -Text $fanHardware -Pattern 'ReadHp88F8ControlState\(\)' -Description 'production fan-control hardware adapter never opens the broad diagnostic EC snapshot'
Assert-Contains -Text $fanHardware -Pattern 'ReadHp88F8Setpoint\(\)' -Description 'production fan-control hardware adapter reads ownership through 0x34/0x35 only'
Assert-Contains -Text $fanHardware -Pattern 'ReadHp88F8FanControlGuard\(\)' -Description 'production fan-control hardware adapter preserves MaxFan/FanSwitch safety separately'
Assert-Contains -Text $fanHardware -Pattern 'ReadFanTachometers\(\)' -Description 'production fan-control hardware adapter preserves dual-tach feedback separately'
Assert-Contains -Text $backend -Pattern 'WaitForSetpointAsync[\s\S]*_hardware!\.ReadSetpoint\(\)' -Description 'production setpoint acknowledgement polling does not reopen full EC state'
Assert-Contains -Text $backend -Pattern 'Custom fan authority admission failed before any fan write was attempted:[\s\S]*ex\.GetType\(\)\.Name[\s\S]*ex\.Message' -Description 'no-write admission failures preserve their precise root cause for physical Gate G diagnosis'
Assert-Contains -Text $manager -Pattern 'FirmwareRestoreVerificationTimeout[\s\S]*TimeSpan\.FromSeconds\(5\)' -Description 'watchdog restore verification has a bounded five-second FF/FF acknowledgement window'
Assert-Contains -Text $manager -Pattern 'FirmwareRestoreVerificationPollInterval[\s\S]*TimeSpan\.FromMilliseconds\(250\)' -Description 'watchdog restore verification polls at a bounded 250 ms cadence'
Assert-Contains -Text $manager -Pattern 'RestoreFirmwareAutoAsync\([\s\S]*while \(true\)[\s\S]*ReadSetpointAsync' -Description 'watchdog recovery polls EC after the HP restore command instead of trusting one immediate sample'
Assert-Contains -Text $manager -Pattern 'if \(after\.IsFirmwareOwned\)[\s\S]*_journal\.DeleteAsync' -Description 'watchdog deletes the durable lease only after verified FF/FF'
Assert-Contains -Text $manager -Pattern '!AllowedSetpoints\(record\)\.Contains\(after\)[\s\S]*OwnershipAmbiguous' -Description 'post-restore polling stops fail-closed on an unexpected external fixed setpoint'
Assert-Contains -Text $mainForm -Pattern 'finalReleaseVerified[\s\S]*WatchdogReleaseVerified' -Description 'Gate G final re-entry handoff requires causal watchdog Release evidence'
Assert-Contains -Text $mainForm -Pattern '!finalReleaseVerified[\s\S]*watchdogFinal\.JournalPresent' -Description 'Gate G final PASS requires both Release acknowledgement and journal absence'

Assert-Contains -Text $resetMethod -Pattern '_gateG1AcceptedResumeCount\s*=\s*0' -Description 'accepted-resume count resets between Gate G2 cycles'
Assert-Contains -Text $resetMethod -Pattern '_gateGResumeProofState[\s\S]*GateGResumeProofState\.Pending' -Description 'Gate G2 resets the one-shot resume proof latch between cycles'
Assert-Contains -Text $resetMethod -Pattern '_gateG1HardwareTestSuspendObserved\s*=\s*false' -Description 'suspend evidence resets between Gate G2 cycles'
Assert-Contains -Text $resetMethod -Pattern '_gateG1HardwareTestPreSleepVerified\s*=\s*false' -Description 'pre-sleep evidence resets between Gate G2 cycles'
Assert-Contains -Text $resetMethod -Pattern '_gateGSuspendBoundaryUtc\s*=\s*null' -Description 'per-cycle suspend boundary resets between Gate G2 cycles'
Assert-Contains -Text $resetMethod -Pattern '_gateGSuspendSource\s*=\s*null' -Description 'per-cycle suspend source resets between Gate G2 cycles'
Assert-Contains -Text $resetMethod -Pattern '_gateGTelemetrySuspendedBeforeRestore\s*=\s*false' -Description 'pre-restore telemetry proof resets between Gate G2 cycles'
Assert-Contains -Text $resetMethod -Pattern '_gateGTelemetrySuspendMarkedAtUtc\s*=\s*null' -Description 'telemetry suspend timestamp resets between Gate G2 cycles'
Assert-NotContains -Text $resetMethod -Pattern '_gateG1WatchdogPid\s*=' -Description 'watchdog PID is never reset between Gate G2 cycles'
Assert-NotContains -Text $resetMethod -Pattern '_gateGCurrentCycle\s*=' -Description 'cycle reset helper cannot rewind the Gate G2 cycle index'

Assert-Contains -Text $cycleResultMethod -Pattern 'watchdogPid=\{_gateG1WatchdogPid\}' -Description 'every Gate G2 cycle result records stable watchdog identity'
Assert-Contains -Text $cycleResultMethod -Pattern 'guiPid=\{Environment\.ProcessId\}' -Description 'every Gate G2 cycle result records stable GUI identity'
Assert-Contains -Text $completeMethod -Pattern 'GateGFinalResultPath' -Description 'Gate G2 writes one final result after the fifth cycle'

Assert-Contains -Text $telemetry -Pattern 'ResumeHealthySamplesRequired\s*=\s*5' -Description 'each resume still requires five complete post-boundary telemetry snapshots'
Assert-Contains -Text $manager -Pattern 'OwnedHeartbeatTimeout[\s\S]*TimeSpan\.FromSeconds\(5\)' -Description 'OWNED timeout remains 5 seconds'
Assert-Contains -Text $manager -Pattern 'WriteArmedDeadline[\s\S]*TimeSpan\.FromSeconds\(12\)' -Description 'WRITE_ARMED deadline remains 12 seconds'
Assert-Contains -Text $manager -Pattern 'RestoringDeadline[\s\S]*TimeSpan\.FromSeconds\(8\)' -Description 'RESTORING deadline remains 8 seconds'

Assert-Contains -Text $harness -Pattern '\$targetCycles\s*=\s*5' -Description 'physical Gate G2 harness requires exactly five cycles'
Assert-Contains -Text $harness -Pattern '--gate-g2-suspend-repeat-test' -Description 'physical harness launches Gate G2 mode'
Assert-Contains -Text $harness -Pattern '--gate-g2-test-token[\s\S]*88F8-GATEG2-30' -Description 'physical harness supplies the exact Gate G2 token'
Assert-Contains -Text $harness -Pattern '\$guiProcessId\s*=\s*\[int\]\$proc\.Id' -Description 'physical harness captures one persistent GUI PID'
Assert-Contains -Text $harness -Pattern '\$guiStartTicks\s*=\s*\[long\]\$proc\.StartTime' -Description 'physical harness captures exact persistent GUI creation time'
Assert-Contains -Text $harness -Pattern 'for \(\$cycle = 1; \$cycle -le \$targetCycles; \$cycle\+\+\)' -Description 'physical harness performs a single five-cycle loop'
Assert-Contains -Text $harness -Pattern 'Assert-GuiIdentity' -Description 'physical harness verifies unchanged GUI identity between cycles'
Assert-Contains -Text $harness -Pattern 'Assert-ProductionServiceReady -ExpectedPid \$servicePidBefore' -Description 'physical harness verifies unchanged watchdog PID throughout'
Assert-Contains -Text $harness -Pattern 'ProcessStartUtcTicks' -Description 'physical harness binds every OWNED lease to exact GUI process identity'
Assert-Contains -Text $harness -Pattern 'journal\.Owned\.Cpu -ne 30' -Description 'physical harness requires exact OWNED CPU target 30'
Assert-Contains -Text $harness -Pattern 'journal\.Owned\.Gpu -ne 30' -Description 'physical harness requires exact OWNED GPU target 30'
Assert-Contains -Text $harness -Pattern 'Wait-ForPowerCycleEvidence' -Description 'every Gate G2 cycle requires Kernel-Power suspend/resume causality'
Assert-Contains -Text $harness -Pattern 'Gate G0 production clock measurement: PASS' -Description 'every physical cycle crosses the validated S3 clock probe'
Assert-Contains -Text $harness -Pattern 'exactly one resume was accepted' -Description 'physical harness requires one logical accepted resume per cycle'
Assert-Contains -Text $harness -Pattern 'telemetry recovered to Healthy' -Description 'physical harness requires Healthy recovery per cycle'
Assert-Contains -Text $harness -Pattern 'ecProof=production-backend-restore-ack' -Description 'physical harness requires backend-return FF/FF proof without a redundant suspend-time EC reader'
Assert-Contains -Text $harness -Pattern 'watchdogRelease=True' -Description 'physical Gate G2 requires successful watchdog Release acknowledgement per cycle'
Assert-Contains -Text $harness -Pattern 'watchdogState=Ready' -Description 'physical Gate G2 requires the same watchdog Ready after wake before resume acceptance'
Assert-Contains -Text $harness -Pattern 'journalProof=watchdog-release-response' -Description 'physical Gate G2 requires watchdog Release journal proof'
Assert-Contains -Text $harness -Pattern 'post-resume-ready-check' -Description 'physical Gate G2 requires independent post-wake Ready/journal proof'
Assert-Contains -Text $harness -Pattern 'telemetry=Suspended' -Description 'physical harness requires telemetry to remain Suspended through handoff proof before resume acceptance'
Assert-Contains -Text $harness -Pattern 'telemetryProof=pre-restore-state-transition' -Description 'physical harness requires proof that telemetry entered Suspended before restore IO'
Assert-Contains -Text $harness -Pattern 'telemetryMs=' -Description 'physical harness requires per-cycle telemetry boundary timing'
Assert-Contains -Text $harness -Pattern 'restoreMs=' -Description 'physical harness requires per-cycle backend restore timing'
Assert-Contains -Text $harness -Pattern 'firmwareMs=' -Description 'physical harness requires per-cycle Firmware transition timing'
Assert-Contains -Text $harness -Pattern 'handoffProof=completed-before-resume-acceptance' -Description 'physical harness requires completed handoff before telemetry accepts resume'
Assert-Contains -Text $harness -Pattern 'acceptedResumesBeforeProof=0' -Description 'physical harness requires zero accepted resumes before handoff proof'
Assert-Contains -Text $harness -Pattern 'preBlockMs=\(\[0-9\]\+\(\?:\\\.\[0-9\]\+\)\?\).*budgetMs=1800' -Description 'physical harness constrains only pre-block fence/telemetry work to the 1800 ms suspend budget'

Assert-Contains -Text $harness -Pattern 'gate-g2\.cycle-\{0\}\.reentry' -Description 'physical harness requires one controlled re-entry marker per cycle'
Assert-Contains -Text $harness -Pattern 'cycles=5/5' -Description 'physical harness requires final 5/5 result'
Assert-Contains -Text $harness -Pattern 'SCM recovery' -Description 'physical harness re-verifies SCM recovery configuration'
Assert-Contains -Text $harness -Pattern 'OWNED heartbeat timeout' -Description 'physical harness rejects OWNED timeout evidence'
Assert-Contains -Text $harness -Pattern 'WRITE_ARMED operation deadline' -Description 'physical harness rejects WRITE_ARMED deadline evidence'
Assert-Contains -Text $harness -Pattern 'RESTORING takeover deadline' -Description 'physical harness rejects RESTORING deadline evidence'
Assert-Contains -Text $harness -Pattern 'OwnershipAmbiguous' -Description 'physical harness rejects ownership ambiguity'
Assert-Contains -Text $harness -Pattern 'WATCHDOG OWNER LOSS:' -Description 'physical harness rejects owner-loss recovery'
Assert-Contains -Text $harness -Pattern 'GATE D RECOVERY' -Description 'physical harness rejects watchdog recovery'
Assert-NotContains -Text $harness -Pattern '--restore-hp-auto' -Description 'parent Gate G2 harness never invokes the HP restore CLI'
Assert-NotContains -Text $harness -Pattern '(?im)^\s*\$pid\s*=' -Description 'physical Gate G2 never assigns to PowerShell automatic read-only PID'

$serviceInstall = $harness.IndexOf('install-watchdog-gate-d.ps1', [StringComparison]::Ordinal)
$appLaunch = $harness.IndexOf('$proc = Start-Process -FilePath $app', [StringComparison]::Ordinal)
$loopStart = $harness.IndexOf('for ($cycle = 1;', [StringComparison]::Ordinal)
if ($serviceInstall -lt 0 -or $appLaunch -lt 0 -or $loopStart -lt 0 -or $serviceInstall -ge $appLaunch -or $appLaunch -ge $loopStart) {
    throw 'Gate G2 invariant violated: service must be installed once, then one GUI launched, then the five-cycle loop begins.'
}
Write-Host 'PASS  Gate G2 does not intentionally restart GUI/service between cycles'

$loopEnd = $harness.IndexOf('if (-not (Wait-ForFile -Path $finalResultPath', $loopStart, [StringComparison]::Ordinal)
if ($loopEnd -le $loopStart) {
    throw 'Gate G2 invariant could not isolate physical cycle loop.'
}
$loopBody = $harness.Substring($loopStart, $loopEnd - $loopStart)
Assert-NotContains -Text $loopBody -Pattern 'install-watchdog-gate-d\.ps1|Start-Service|Start-Process -FilePath \$app' -Description 'no service or GUI restart exists inside the 5-cycle loop'

$readyBoundary = $loopBody.IndexOf('$readyText = Get-Content $readyPath -Raw', [StringComparison]::Ordinal)
$suspendBoundary = $loopBody.IndexOf('$sleepOutput =', $readyBoundary, [StringComparison]::Ordinal)
if ($readyBoundary -lt 0 -or $suspendBoundary -le $readyBoundary) {
    throw 'Gate G2 invariant could not isolate READY -> suspend dispatch inside the cycle loop.'
}
$readyToSuspend = $loopBody.Substring($readyBoundary, $suspendBoundary - $readyBoundary)
Assert-NotContains -Text $readyToSuspend -Pattern '(?im)^\s*\$[A-Za-z_][A-Za-z0-9_]*\s*=\s*Read-EcSetpoint\b' -Description 'parent performs no out-of-band EC probe while per-cycle Custom OWNED is active'
Assert-Contains -Text $harness -Pattern 'function Read-EcSetpoint' -Description 'physical Gate G2 uses the narrow ownership-only EC helper'
Assert-Contains -Text $harness -Pattern '--probe-88f8-setpoint' -Description 'physical Gate G2 invokes the narrow 0x34/0x35 CLI ownership probe'
Assert-Contains -Text $harness -Pattern '\[int\]\$Attempts = 3' -Description 'physical Gate G2 bounds transient EC setpoint retries to three attempts'
Assert-Contains -Text $harness -Pattern '\[int\]\$RetryDelayMs = 250' -Description 'physical Gate G2 spaces transient EC setpoint retries by 250 ms'
Assert-NotContains -Text $harness -Pattern '--probe-88f8-ec-state' -Description 'physical Gate G2 does not require a full EC snapshot for ownership proof'

$fallbackStart = $harness.IndexOf('$failsafe = Start-Process powershell.exe', [StringComparison]::Ordinal)
if ($fallbackStart -lt 0 -or $fallbackStart -ge $appLaunch) {
    throw 'Gate G2 invariant violated: emergency fallback must be armed before the persistent GUI can acquire Custom.'
}
Write-Host 'PASS  Gate G2 fallback is armed before the first Custom acquisition'

Assert-Ordered -Text $harness -Needles @(
    '$finalResult = Get-Content $finalResultPath -Raw',
    '$appExited = $proc.WaitForExit(15000)',
    'Assert-ProductionServiceReady -ExpectedPid $servicePidBefore',
    'if (Test-Path $journalPath)',
    '$finalEc = Read-EcSetpoint'
) -Description 'final independent EC verification occurs only after final 5/5 result, GUI exit, same watchdog PID and journal absence'

Assert-Contains -Text $harness -Pattern 'Type UNDERVOLT-OK' -Description 'Gate G2 requires pre-test OGH undervolt confirmation'
Assert-Contains -Text $harness -Pattern 'Type SAME if the CPU undervolt is unchanged' -Description 'Gate G2 requires post-test OGH undervolt confirmation'
Assert-Contains -Text $harness -Pattern 'Emergency fallback cancelled only after journal absence \+ independent EC FF/FF were proven' -Description 'Gate G2 cancels fallback only after independently proven firmware safety'

Write-Host ''
Write-Host 'Gate G2 5/5 lifecycle invariant self-test: PASS' -ForegroundColor Green
exit 0
