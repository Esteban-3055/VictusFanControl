$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProgramPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$gateGStatePath = Join-Path $repoRoot 'src\VictusFanControl.App\GateG1WatchdogState.cs'
$probe88Path = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp88F8EcControlStateProbe.cs'
$factoryPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs'
$backend8Path = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$m9GatePath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs'
$m2InstallerPath = Join-Path $PSScriptRoot 'install-watchdog-m2-8c40.ps1'
$cleanupPath = Join-Path $PSScriptRoot 'cleanup-watchdog-88f8-services.ps1'

function Assert-ContainsLiteral {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Description
    )

    if (-not $Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw "8C40 isolation invariant missing: $Description"
    }

    Write-Host "PASS  $Description"
}

function Assert-NotContainsLiteral {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Description
    )

    if ($Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw "8C40 isolation invariant violated: $Description"
    }

    Write-Host "PASS  $Description"
}

$appProgram = Get-Content $appProgramPath -Raw
$mainForm = Get-Content $mainFormPath -Raw
$gateGState = Get-Content $gateGStatePath -Raw
$probe88 = Get-Content $probe88Path -Raw
$factory = Get-Content $factoryPath -Raw
$backend8 = Get-Content $backend8Path -Raw
$m9Gate = Get-Content $m9GatePath -Raw
$m2Installer = Get-Content $m2InstallerPath -Raw
$cleanup = Get-Content $cleanupPath -Raw

Write-Host 'VictusFanControl - HP 8C40 / legacy 88F8 isolation invariant self-test'

$legacyGroupStart = $appProgram.IndexOf(
    'var legacy88F8HardwareHarnessRequested =',
    [StringComparison]::Ordinal)
$legacyGuardStart = $appProgram.IndexOf(
    'if (legacy88F8HardwareHarnessRequested)',
    [Math]::Max(0, $legacyGroupStart),
    [StringComparison]::Ordinal)

if ($legacyGroupStart -lt 0 -or $legacyGuardStart -le $legacyGroupStart) {
    throw '8C40 isolation invariant missing: legacy 88F8 hardware-mode grouping/guard.'
}

$legacyGroup = $appProgram.Substring(
    $legacyGroupStart,
    $legacyGuardStart - $legacyGroupStart)

foreach ($flag in @(
    'suspendHardwareTest',
    'gateDHardwareTest',
    'gateEHardwareTest',
    'gateF1HardwareTest',
    'gateF2HardwareTest',
    'gateG1HardwareTest',
    'gateG2HardwareTest'
)) {
    Assert-ContainsLiteral -Text $legacyGroup -Needle $flag -Description "legacy grouping includes $flag"
}

$legacyGuardTail = $appProgram.Substring($legacyGuardStart)
Assert-ContainsLiteral -Text $legacyGuardTail -Needle 'Hp88F8TargetProfile.Matches(' -Description 'legacy hardware modes require the exact HP 88F8 fingerprint before MainForm/backend creation'
Assert-ContainsLiteral -Text $legacyGuardTail -Needle 'Use the dedicated 8C40 M-series qualification gates instead' -Description 'legacy mode refusal directs 8C40 hardware to M-series gates'

$backendBlockStart = $mainForm.IndexOf('IFanControlBackend backend;', [StringComparison]::Ordinal)
$backendBlockEnd = $mainForm.IndexOf('_fanCoordinator = new FanControlCoordinator(backend);', [Math]::Max(0, $backendBlockStart), [StringComparison]::Ordinal)
if ($backendBlockStart -lt 0 -or $backendBlockEnd -le $backendBlockStart) {
    throw '8C40 isolation invariant could not isolate MainForm backend initialization.'
}
$backendBlock = $mainForm.Substring($backendBlockStart, $backendBlockEnd - $backendBlockStart)

foreach ($flag in @(
    '_suspendLifecycleHardwareTest',
    '_gateDHardwareTest',
    '_gateEHardwareTest',
    '_gateF1HardwareTest',
    '_gateF2HardwareTest',
    '_gateG1HardwareTest',
    '_gateG2HardwareTest'
)) {
    Assert-ContainsLiteral -Text $backendBlock -Needle $flag -Description "MainForm secondary legacy guard includes $flag"
}

Assert-ContainsLiteral -Text $backendBlock -Needle 'Hp88F8TargetProfile.Instance.Id' -Description 'MainForm secondary legacy guard requires the exact 88F8 target id'

$reopenStart = $mainForm.IndexOf('private async Task ReopenFanAdmissionAfterHealthyAsync()', [StringComparison]::Ordinal)
$buildUiStart = $mainForm.IndexOf('private System.Windows.Forms.Control BuildUi()', [Math]::Max(0, $reopenStart), [StringComparison]::Ordinal)
if ($reopenStart -lt 0 -or $buildUiStart -le $reopenStart) {
    throw '8C40 isolation invariant could not isolate generic admission reopen path.'
}
$reopenMethod = $mainForm.Substring($reopenStart, $buildUiStart - $reopenStart)

Assert-ContainsLiteral -Text $reopenMethod -Needle 'WindowsSleepModel.ModernStandbyS0LowPowerIdle' -Description 'generic admission reopen identifies Modern Standby targets'
Assert-ContainsLiteral -Text $reopenMethod -Needle 'generic S3-era lifecycle recovery will not reopen Custom admission' -Description '8C40 Modern Standby cannot reopen Custom from the generic S3-era Healthy path'
Assert-ContainsLiteral -Text $mainForm -Needle 'Validated target: {_targetProfile.DisplayName} ({_targetProfile.Id})' -Description 'normal GUI fan page identifies the resolved target instead of claiming 88F8 unconditionally'
Assert-NotContainsLiteral -Text $mainForm -Needle 'Text = "The validated HP 88F8 backend is integrated behind FanControlCoordinator.' -Description 'stale 88F8-only normal GUI label is removed'

Assert-ContainsLiteral -Text $gateGState -Needle 'TargetProfileId' -Description 'legacy Gate G consumes watchdog target identity'
Assert-ContainsLiteral -Text $gateGState -Needle 'Hp88F8TargetProfile.Instance.Id' -Description 'legacy Gate G requires exact 88F8 watchdog target identity'

Assert-ContainsLiteral -Text $probe88 -Needle 'Hp88F8TargetProfile.Matches(' -Description 'legacy 88F8 EC diagnostics require the full exact target fingerprint'
Assert-NotContainsLiteral -Text $probe88 -Needle 'hardware.BoardProduct,' -Description 'legacy EC diagnostics no longer accept board product alone'

foreach ($serviceName in @(
    'VictusFanControlWatchdogGateA',
    'VictusFanControlWatchdogGateB',
    'VictusFanControlWatchdog'
)) {
    Assert-ContainsLiteral -Text $m2Installer -Needle $serviceName -Description "M2 installer recognizes legacy service $serviceName"
    Assert-ContainsLiteral -Text $cleanup -Needle $serviceName -Description "cleanup recognizes legacy service $serviceName"
}

Assert-ContainsLiteral -Text $m2Installer -Needle 'cleanup-watchdog-88f8-services.ps1' -Description 'M2 installer refuses silent coexistence and points to explicit cleanup'
Assert-ContainsLiteral -Text $cleanup -Needle 'sc.exe config $name start= disabled' -Description 'cleanup disables legacy service startup before deletion'
Assert-ContainsLiteral -Text $cleanup -Needle 'sc.exe delete $name' -Description 'cleanup removes legacy service registrations'
Assert-ContainsLiteral -Text $cleanup -Needle 'Historical ProgramData logs/journals are NOT deleted' -Description 'cleanup preserves historical forensic evidence'

Assert-ContainsLiteral -Text $factory -Needle 'RequireProductionConstructionAuthorized' -Description 'production factory routes supplied HP 8C40 watchdog leases through the closed M9 gate'
Assert-ContainsLiteral -Text $m9Gate -Needle 'public static readonly bool ProductionConstructionAuthorized = false;' -Description 'M9 production watchdog construction remains compile-time blocked'
Assert-ContainsLiteral -Text $m9Gate -Needle 'WatchdogRecoveryValidated=false' -Description 'M9 gate also requires explicit WatchdogRecoveryValidated promotion'
Assert-ContainsLiteral -Text $backend8 -Needle 'RequireProductionConstructionAuthorized' -Description '8C40 public backend independently applies the M9 watchdog gate'

$watchdogGuardStart = $backend8.IndexOf(
    'if (_targetSupported && watchdogLease is not null)',
    [StringComparison]::Ordinal)
$watchdogGuardEnd = $backend8.IndexOf(
    '_watchdogLease = watchdogLease;',
    [Math]::Max(0, $watchdogGuardStart),
    [StringComparison]::Ordinal)
if ($watchdogGuardStart -lt 0 -or $watchdogGuardEnd -le $watchdogGuardStart) {
    throw '8C40 isolation invariant missing: backend watchdog-construction guard.'
}
$watchdogGuard = $backend8.Substring($watchdogGuardStart, $watchdogGuardEnd - $watchdogGuardStart)
Assert-ContainsLiteral -Text $watchdogGuard -Needle 'RequireProductionConstructionAuthorized' -Description '8C40 backend rejects watchdog lease through the closed M9 gate before storing it'

Write-Host ''
Write-Host 'HP 8C40 / legacy 88F8 isolation invariant self-test: PASS' -ForegroundColor Green
exit 0
