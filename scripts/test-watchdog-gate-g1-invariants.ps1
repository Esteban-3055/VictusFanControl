$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$gateG1StatePath = Join-Path $repoRoot 'src\VictusFanControl.App\GateG1WatchdogState.cs'
$telemetryPath = Join-Path $repoRoot 'src\VictusFanControl.App\TelemetryWorker.cs'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'

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

Write-Host 'VictusFanControl - GATE G1 LIFECYCLE INVARIANT SELF-TEST'

Assert-Contains -Text $program -Pattern '--gate-g1-suspend-test' -Description 'Gate G1 has a dedicated application mode'
Assert-Contains -Text $program -Pattern '--gate-g1-test-token' -Description 'Gate G1 requires a dedicated token option'
Assert-Contains -Text $program -Pattern '88F8-GATEG1-30' -Description 'Gate G1 requires the exact hardware opt-in token'
Assert-Contains -Text $program -Pattern '\(gateG1HardwareTest \? 1 : 0\)' -Description 'Gate G1 participates in hardware-test mutual exclusion'

Assert-Contains -Text $mainForm -Pattern 'else if \(_gateDHardwareTest \|\|[\s\S]*_gateG1HardwareTest\)[\s\S]*new NamedPipeFanControlWatchdogLeaseClient\(\)' -Description 'Gate G1 uses the real named-pipe production watchdog lease'
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
    'GATE G1: PBT_APMSUSPEND entered',
    '_fanCoordinator.BlockCustomAdmissionAndRestoreAsync(',
    'new Hp88F8EcControlStateProbe(_modulesDirectory).Read()',
    'GateG1WatchdogStateReader.Read()',
    'GateG1WatchdogStateReader.RequireReady(',
    '!watchdog.JournalPresent',
    'GateG1WatchdogStateReader.WriteDurableMarker(',
    'GateG1PreSleepPath',
    '_worker.NotifySuspend(source)'
) -Description 'Gate G1 proves Custom/ACK, performs the coordinator handoff, then verifies EC + watchdog/journal before NotifySuspend returns'

$g1Capture = $suspendHandler.IndexOf('GATE G1: PBT_APMSUSPEND entered', [StringComparison]::Ordinal)
$handoff = $suspendHandler.IndexOf('_fanCoordinator.BlockCustomAdmissionAndRestoreAsync(', [StringComparison]::Ordinal)
$g1EcProbe = $suspendHandler.IndexOf('new Hp88F8EcControlStateProbe(_modulesDirectory).Read()', $handoff, [StringComparison]::Ordinal)
if ($g1Capture -lt 0 -or $handoff -lt 0 -or $g1EcProbe -lt 0 -or $g1EcProbe -le $handoff) {
    throw 'Gate G1 invariant violated: the Gate G1 EC verification must occur only after the coordinator handoff returns.'
}
Write-Host 'PASS  Gate G1 issues no Gate-G1 EC verification while Custom is still active'

Assert-Contains -Text $suspendHandler -Pattern 'gateG1WasCustom\s*&&[\s\S]*gateG1BackendAckVerified\s*&&[\s\S]*_fanCoordinator\.Authority == FanAuthority\.Firmware[\s\S]*after\.CpuSetpoint == byte\.MaxValue[\s\S]*after\.GpuSetpoint == byte\.MaxValue[\s\S]*!watchdog\.JournalPresent' -Description 'pre-sleep PASS requires prior Custom ACK, Firmware authority, EC FF/FF and no journal'

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

Assert-Ordered -Text $g1Method -Needles @(
    'GateG1WatchdogStateReader.RequireReady(watchdog)',
    'if (watchdog.JournalPresent)',
    'new Hp88F8EcControlStateProbe(_modulesDirectory).Read()',
    '_fanCoordinator.TryEnterCustomAsync(',
    '_fanCoordinator.ApplyAsync(',
    '_gateG1HardwareTestBackendAckVerified = true',
    '_gateG1HardwareTestArmed = true',
    'GateG1HardwareTestReadyPath'
) -Description 'initial Gate G1 READY follows clean watchdog/FF baseline and real watchdog-backed 30/30 acknowledgement'

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
    'GateG1ReentryPath',
    '_fanCoordinator.RestoreFirmwareAsync(',
    'GateG1WatchdogStateReader.RequireReady(',
    'watchdogFinal.JournalPresent',
    'CompleteGateG1HardwareTest('
) -Description 'Gate G1 performs one controlled post-recovery re-entry then restores and verifies final watchdog/firmware state'

Assert-Contains -Text $telemetry -Pattern 'ResumeHealthySamplesRequired\s*=\s*5' -Description 'resume recovery still requires five complete post-boundary telemetry snapshots'
Assert-Contains -Text $manager -Pattern 'OwnedHeartbeatTimeout[\s\S]*TimeSpan\.FromSeconds\(5\)' -Description 'OWNED timeout remains 5 seconds'
Assert-Contains -Text $manager -Pattern 'WriteArmedDeadline[\s\S]*TimeSpan\.FromSeconds\(12\)' -Description 'WRITE_ARMED deadline remains 12 seconds'
Assert-Contains -Text $manager -Pattern 'RestoringDeadline[\s\S]*TimeSpan\.FromSeconds\(8\)' -Description 'RESTORING deadline remains 8 seconds'

Assert-NotContains -Text $g1Method -Pattern '--restore-hp-auto' -Description 'Gate G1 application path never invokes the parent/CLI restore command'

Write-Host ''
Write-Host 'Gate G1 lifecycle invariant self-test: PASS' -ForegroundColor Green
exit 0
