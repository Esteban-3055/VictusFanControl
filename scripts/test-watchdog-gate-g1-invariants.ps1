$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$gateG1StatePath = Join-Path $repoRoot 'src\VictusFanControl.App\GateG1WatchdogState.cs'
$telemetryPath = Join-Path $repoRoot 'src\VictusFanControl.App\TelemetryWorker.cs'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'
$backendPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp88F8FanControlBackend.cs'
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
$mainForm = Get-Content $mainFormPath -Raw
$gateG1State = Get-Content $gateG1StatePath -Raw
$telemetry = Get-Content $telemetryPath -Raw
$manager = Get-Content $managerPath -Raw
$backend = Get-Content $backendPath -Raw
$harness = Get-Content $harnessPath -Raw

Write-Host 'VictusFanControl - GATE G1 LIFECYCLE INVARIANT SELF-TEST'

Assert-Contains -Text $program -Pattern '--gate-g1-suspend-test' -Description 'Gate G1 has a dedicated application mode'
Assert-Contains -Text $program -Pattern '--gate-g1-test-token' -Description 'Gate G1 requires a dedicated token option'
Assert-Contains -Text $program -Pattern '88F8-GATEG1-30' -Description 'Gate G1 requires the exact hardware opt-in token'
Assert-Contains -Text $program -Pattern '\(gateG1HardwareTest \? 1 : 0\)' -Description 'Gate G1 participates in hardware-test mutual exclusion'

Assert-Contains -Text $mainForm -Pattern 'else if \(_gateDHardwareTest \|\|[\s\S]*_gateG1HardwareTest \|\|[\s\S]*_gateG2HardwareTest\)[\s\S]*new NamedPipeFanControlWatchdogLeaseClient\(\)' -Description 'Gate G1/G2 use the real named-pipe production watchdog lease'
Assert-Contains -Text $gateG1State -Pattern 'CommonApplicationData' -Description 'Gate G1 reads watchdog state from machine-level ProgramData'
Assert-Contains -Text $gateG1State -Pattern 'gate-d\.status\.json' -Description 'Gate G1 consumes the production watchdog status marker'
Assert-Contains -Text $gateG1State -Pattern 'lease\.json' -Description 'Gate G1 checks the production durable lease journal'
Assert-Contains -Text $gateG1State -Pattern 'FileOptions\.WriteThrough' -Description 'Gate G1 lifecycle markers use write-through persistence'
Assert-Contains -Text $gateG1State -Pattern 'Flush\(flushToDisk: true\)' -Description 'Gate G1 lifecycle markers are flushed to disk'

$suspendStart = $mainForm.IndexOf('private void HandleSuspendLifecycle', [StringComparison]::Ordinal)
$resumeStart = $mainForm.IndexOf('private void HandleResumeLifecycle', [StringComparison]::Ordinal)
if ($suspendStart -lt 0 -or $resumeStart -le $suspendStart) {
    throw 'Gate G1 invariant could not isolate the suspend lifecycle handler.'
}
$suspendHandler = $mainForm.Substring($suspendStart, $resumeStart - $suspendStart)

Assert-Ordered -Text $suspendHandler -Needles @(
    '_fanCoordinator.BlockCustomAdmissionAndRestoreAsync(',
    '_worker.NotifySuspend(source);',
    'var restoreEvidence =',
    '_fanCoordinator.LastRestoreEvidence',
    'var resumeObservedBeforeProof =',
    'restoreEvidence.Value.LocalFirmwareAckVerified',
    'restoreEvidence.Value.WatchdogReleaseVerified',
    'telemetrySuspended',
    '!resumeObservedBeforeProof',
    'handlerElapsedMs <= GateGSuspendProofBudgetMs',
    '_gateGPendingPreSleepMarker = marker'
) -Description 'Gate G1/G2 restore first, mark telemetry Suspended, then capture only in-memory causal proof before WndProc returns'

$notifySuspend = $suspendHandler.IndexOf('_worker.NotifySuspend(source);', [StringComparison]::Ordinal)
$gateGVerify = $suspendHandler.IndexOf('if (GateGHardwareTest &&', $notifySuspend, [StringComparison]::Ordinal)
if ($notifySuspend -lt 0 -or $gateGVerify -le $notifySuspend) {
    throw 'Gate G1 invariant could not isolate the post-restore Gate G proof-capture block.'
}
$gateGVerifyBlock = $suspendHandler.Substring($gateGVerify)

Assert-NotContains -Text $gateGVerifyBlock -Pattern 'Hp88F8EcControlStateProbe|GateG1WatchdogStateReader\.Read\(|WriteDurableMarker\(|AppLog\.Write\(' -Description 'Gate G suspend critical path performs no post-restore EC/watchdog/filesystem transaction'
Assert-Contains -Text $gateGVerifyBlock -Pattern 'ecProof=production-backend-restore-ack' -Description 'Gate G marker identifies production backend FF/FF acknowledgement'
Assert-Contains -Text $gateGVerifyBlock -Pattern 'watchdogRelease=\{watchdogReleaseVerified\}' -Description 'Gate G marker records causal watchdog Release acknowledgement'
Assert-Contains -Text $gateGVerifyBlock -Pattern 'journalProof=watchdog-release-response' -Description 'Gate G journal proof is the successful watchdog Release response, not a new file read'
Assert-Contains -Text $gateGVerifyBlock -Pattern 'resumeObservedBeforeProof=\{resumeObservedBeforeProof\}' -Description 'Gate G marker records whether resume raced ahead of proof capture'
Assert-Contains -Text $gateGVerifyBlock -Pattern 'handlerElapsedMs\.ToString\("0\.0", CultureInfo\.InvariantCulture\)' -Description 'Gate G timing evidence is culture-invariant'
Assert-Contains -Text $mainForm -Pattern 'GateGSuspendProofBudgetMs\s*=\s*1800' -Description 'Gate G keeps margin inside the approximately two-second Windows suspend notification budget'
Assert-Contains -Text $mainForm -Pattern '_gateGPendingPreSleepMarker\s*=\s*marker' -Description 'pre-sleep proof is retained in memory instead of persisted during PBT_APMSUSPEND'

$resumeHandlerStart = $mainForm.IndexOf('private void HandleResumeLifecycle', [StringComparison]::Ordinal)
$reopenHandlerStart = $mainForm.IndexOf('private async Task ReopenFanAdmissionAfterHealthyAsync', $resumeHandlerStart, [StringComparison]::Ordinal)
if ($resumeHandlerStart -lt 0 -or $reopenHandlerStart -le $resumeHandlerStart) {
    throw 'Gate G1 invariant could not isolate resume + deferred proof persistence.'
}
$resumeHandler = $mainForm.Substring($resumeHandlerStart, $reopenHandlerStart - $resumeHandlerStart)
Assert-Ordered -Text $resumeHandler -Needles @(
    'var accepted = _worker.NotifyResume(source);',
    'PersistPendingGateGPreSleepProof();',
    'if (!accepted)',
    '_gateG1HardwareTestResumeObserved = true;',
    '_gateG1AcceptedResumeCount++;'
) -Description 'captured pre-sleep proof is persisted only after wake and before Gate G accepted-resume accounting advances'
Assert-Contains -Text $resumeHandler -Pattern 'GateG1WatchdogStateReader\.WriteDurableMarker\(' -Description 'deferred Gate G proof persistence occurs in the resume-side helper'

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

$recoverStart = $manager.IndexOf('RecoverPotentialWriteLockedAsync(', [StringComparison]::Ordinal)
$recoverDefinition = $manager.IndexOf('RecoverPotentialWriteLockedAsync(', $recoverStart + 1, [StringComparison]::Ordinal)
while ($recoverDefinition -ge 0 -and
       $manager.Substring([Math]::Max(0, $recoverDefinition - 80), [Math]::Min(80, $recoverDefinition)) -notmatch 'ValueTask<LeaseRecoveryResult>') {
    $recoverDefinition = $manager.IndexOf('RecoverPotentialWriteLockedAsync(', $recoverDefinition + 1, [StringComparison]::Ordinal)
}
if ($recoverDefinition -lt 0) {
    throw 'Gate G1 invariant could not locate watchdog recovery implementation.'
}
$recoverTail = $manager.Substring($recoverDefinition)
Assert-Ordered -Text $recoverTail -Needles @(
    'await _hardware.RestoreFirmwareAutoAsync(',
    'if (!after.IsFirmwareOwned)',
    'await _journal.DeleteAsync(cancellationToken)',
    '_active = null;',
    'LeaseRecoveryDisposition.RestoredFirmware'
) -Description 'watchdog successful Release recovery deletes the journal only after verified firmware ownership'

Assert-Contains -Text $gateGVerifyBlock -Pattern 'gateG1WasCustom\s*&&[\s\S]*gateG1BackendAckVerified\s*&&[\s\S]*_fanCoordinator\.Authority == FanAuthority\.Firmware[\s\S]*localFirmwareAckVerified[\s\S]*watchdogReleaseVerified[\s\S]*telemetrySuspended[\s\S]*!resumeObservedBeforeProof' -Description 'pre-sleep PASS requires prior Custom ACK, local FF/FF, successful watchdog release, Suspended telemetry and no accepted resume'

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
    'new Hp88F8EcControlStateProbe(_modulesDirectory).Read()',
    '_fanCoordinator.TryEnterCustomAsync(',
    '_fanCoordinator.ApplyAsync(',
    '_gateG1HardwareTestBackendAckVerified = true',
    '_gateG1HardwareTestArmed = true',
    'GateGReadyPath'
) -Description 'initial Gate G1/G2 READY follows clean watchdog/FF baseline and real watchdog-backed 30/30 acknowledgement'

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
    '_fanCoordinator.TryEnterCustomAsync(',
    '_fanCoordinator.ApplyAsync(',
    'GateGReentryPath',
    '_fanCoordinator.RestoreFirmwareAsync(',
    'GateG1WatchdogStateReader.RequireReady(',
    'watchdogFinal.JournalPresent',
    'CompleteGateGHardwareTest('
) -Description 'Gate G1/G2 perform one controlled post-recovery re-entry per cycle, then restore and verify final watchdog/firmware state'

Assert-Contains -Text $telemetry -Pattern 'ResumeHealthySamplesRequired\s*=\s*5' -Description 'resume recovery still requires five complete post-boundary telemetry snapshots'
Assert-Contains -Text $manager -Pattern 'OwnedHeartbeatTimeout[\s\S]*TimeSpan\.FromSeconds\(5\)' -Description 'OWNED timeout remains 5 seconds'
Assert-Contains -Text $manager -Pattern 'WriteArmedDeadline[\s\S]*TimeSpan\.FromSeconds\(12\)' -Description 'WRITE_ARMED deadline remains 12 seconds'
Assert-Contains -Text $manager -Pattern 'RestoringDeadline[\s\S]*TimeSpan\.FromSeconds\(8\)' -Description 'RESTORING deadline remains 8 seconds'

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
Assert-Contains -Text $harness -Pattern 'journalProof=watchdog-release-response' -Description 'physical Gate G1 derives pre-sleep journal absence from the watchdog Release response'
Assert-Contains -Text $harness -Pattern 'telemetry=Suspended' -Description 'physical Gate G1 requires telemetry Suspended before pre-sleep proof completes'
Assert-Contains -Text $harness -Pattern 'resumeObservedBeforeProof=False' -Description 'physical Gate G1 rejects a resume racing ahead of pre-sleep proof'
Assert-Contains -Text $harness -Pattern 'acceptedResumesBeforeProof=0' -Description 'physical Gate G1 requires zero accepted resumes before pre-sleep proof'
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
Assert-NotContains -Text $readyToSuspend -Pattern 'Read-EcState' -Description 'parent harness performs no out-of-band EC probe between durable OWNED READY and suspend dispatch'

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
    '$finalEc = Read-EcState'
) -Description 'parent final EC verification occurs only after causal markers, GUI exit, same watchdog PID and journal absence checks'

Assert-Contains -Text $harness -Pattern 'if \(\$failsafe\.HasExited\)' -Description 'Gate G1 PASS rejects an emergency fallback that reached its execution boundary'
Assert-Contains -Text $harness -Pattern 'Emergency fallback cancelled only after journal absence \+ independent EC FF/FF were proven' -Description 'fallback cancellation requires independently proven firmware safety'
Assert-Contains -Text $harness -Pattern 'Type UNDERVOLT-OK' -Description 'physical Gate G1 requires pre-test OGH undervolt confirmation'
Assert-Contains -Text $harness -Pattern 'Type SAME if the CPU undervolt is unchanged' -Description 'physical Gate G1 requires post-test OGH undervolt confirmation'

Write-Host ''
Write-Host 'Gate G1 lifecycle invariant self-test: PASS' -ForegroundColor Green
exit 0
