$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$harnessPath = Join-Path $PSScriptRoot 'test-watchdog-m5e-write-armed-double-death-8c40.ps1'
$failsafePath = Join-Path $PSScriptRoot 'watchdog-m5e-service-failsafe-8c40.ps1'
$m5dChildPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M5DWriteArmedCrashTest.cs'

$harness = Get-Content $harnessPath -Raw
$failsafe = Get-Content $failsafePath -Raw
$m5dChild = Get-Content $m5dChildPath -Raw

function Assert-Contains {
    param([string]$Text,[string]$Needle,[string]$Message)
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message }
}

function Assert-NotContains {
    param([string]$Text,[string]$Needle,[string]$Message)
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message }
}

Assert-Contains $harness '8C40-M5E-WRITE-ARMED-DOUBLE-DEATH30' 'M5E explicit parent token is missing.'
Assert-Contains $harness '8C40-M5D-WRITE-ARMED-CRASH30' 'M5E must reuse the M5D post-WMI/pre-Commit child.'
Assert-Contains $harness '--8c40-m5d-write-armed-crash-controller' 'M5E must use the already-qualified M5D child mode.'
Assert-Contains $harness 'WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT' 'M5E must require the exact M5D post-WMI/pre-Commit stage.'
Assert-Contains $harness 'real-wmi+ec+tachs;watchdog-commit-not-dispatched' 'M5E must require real hardware ACK and undispatched Commit.'
Assert-Contains $harness '[long]$Journal.Generation -ne 2' 'M5E must require generation-2 WRITE_ARMED.'
Assert-Contains $harness '$null -ne $Journal.PreviousOwned' 'M5E must require no previous owned target.'
Assert-Contains $harness '[int]$Journal.Pending.Cpu -ne 30' 'M5E must require pending CPU 30.'
Assert-Contains $harness '[int]$Journal.Pending.Gpu -ne 30' 'M5E must require pending GPU 30.'
Assert-Contains $harness '$null -ne $Journal.Owned' 'M5E must require no committed Owned target.'
Assert-Contains $harness '[int]$Journal.Controller.ProcessId -ne $ControllerProcessId' 'M5E must bind journal to exact controller PID.'
Assert-Contains $harness '[long]$Journal.Controller.ProcessStartUtcTicks -ne $ControllerStartTicks' 'M5E must bind journal to exact controller creation time.'
Assert-NotContains $harness 'function Assert-WriteArmedJournal($j,[int]$pid' 'M5E must never collide with PowerShell automatic $PID.'
Assert-Contains $harness '$preKillEc = Read-8C40Setpoint' 'M5E must independently prove real EC 30/30 before the fault boundary.'
Assert-Contains $harness '$preKillEc.Cpu -ne 30 -or $preKillEc.Gpu -ne 30' 'M5E must require pre-kill EC 30/30.'
Assert-Contains $harness 'watchdog-m5e-service-failsafe-8c40.ps1' 'M5E must arm its independent delayed safety fallback.'
Assert-Contains $harness '$failsafe = Start-DelayedFailsafe' 'M5E delayed fallback must be armed before controller launch.'
Assert-Contains $harness '$failsafe.Refresh()' 'M5E must prove delayed fallback liveness.'
Assert-Contains $harness 'Test-FailsafeTakeover' 'M5E must invalidate PASS if fallback takeover occurs.'
Assert-Contains $harness '[ValidateRange(10, 100)][int]$MaxKillDeltaMs = 50' 'M5E must retain a bounded double-kill request interval.'
Assert-Contains $harness '[ValidateRange(3000, 10000)][int]$RestartDelayMs = 5000' 'M5E must preserve a pre-restart evidence window.'
Assert-Contains $harness 'Wait-ServiceAbsent -Milliseconds 1200' 'M5E must observe service absence before replacement recovery.'
Assert-Contains $harness '$postDeath = Read-8C40Setpoint' 'M5E must independently prove post-double-death EC before replacement.'
Assert-Contains $harness '$postDeath.Cpu -ne 30 -or $postDeath.Gpu -ne 30' 'M5E pre-restart proof must require EC 30/30.'
Assert-Contains $harness 'Assert-WriteArmedJournal -Journal $retained' 'M5E must prove the exact durable WRITE_ARMED journal remains before replacement.'
Assert-Contains $harness 'Wait-ReplacementRecovery' 'M5E must require a distinct replacement watchdog.'
Assert-Contains $harness 'RecoveryDisposition -ceq ''RestoredFirmware''' 'M5E replacement must publish RestoredFirmware.'
Assert-Contains $harness 'WATCHDOG COMMIT ACK controller PID={0}' 'M5E must explicitly search for forbidden Commit evidence.'
Assert-Contains $harness 'if ($commit)' 'M5E must fail if Commit occurred for the killed controller.'
Assert-Contains $harness 'M4 STARTUP RECOVERY disposition=RestoredFirmware' 'M5E must require startup-recovery log evidence.'
Assert-Contains $harness 'journalRetained=False' 'M5E must require journal deletion only after verified recovery.'
Assert-Contains $harness 'Independent final EC' 'M5E must independently prove final FF/FF.'
Assert-Contains $harness 'Restore-M4Baseline' 'M5E must remove temporary SCM recovery policy after firmware safety.'
Assert-Contains $harness 'Emergency M5E fallback cancelled only after FF/FF + cleared journal proof.' 'M5E must keep fallback alive until firmware safety is independently proven.'

Assert-NotContains $harness '--restore-hp-auto' 'M5E parent must never invoke a direct HP restore CLI.'
Assert-NotContains $harness 'Hp8C40BiosFanControl' 'M5E parent must not instantiate HP BIOS write authority.'
Assert-NotContains $harness 'SetFanLevel(' 'M5E parent must not directly issue an ordinary fan write.'

$firstKill = $harness.IndexOf('$watchdogProcess.Kill()', [StringComparison]::Ordinal)
$secondKill = $harness.IndexOf('$controller.Kill()', [StringComparison]::Ordinal)

if ($firstKill -lt 0 -or $secondKill -lt 0 -or $firstKill -ge $secondKill) {
    throw 'M5E must issue watchdog kill before controller kill.'
}

$betweenKills = $harness.Substring($firstKill, $secondKill - $firstKill)

foreach ($forbidden in @(
    'Start-Sleep',
    'Read-8C40Setpoint',
    'Get-ServiceProcessId',
    'Get-CimInstance',
    'Get-Content',
    'Test-Path'
)) {
    if ($betweenKills.IndexOf($forbidden,[StringComparison]::Ordinal) -ge 0) {
        throw "M5E must not perform '$forbidden' between the two fault requests."
    }
}

$armIndex = $harness.IndexOf('$failsafe = Start-DelayedFailsafe', [StringComparison]::Ordinal)
$controllerIndex = $harness.IndexOf('$controller = Start-Process', [StringComparison]::Ordinal)

if ($armIndex -lt 0 -or $controllerIndex -lt 0 -or $armIndex -ge $controllerIndex) {
    throw 'M5E must arm delayed safety fallback before launching the write-capable controller.'
}

Assert-Contains $failsafe 'Test-WriteArmedPhase' 'M5E failsafe must require WRITE_ARMED.'
Assert-Contains $failsafe '[long]$journal.Generation -ne 2' 'M5E failsafe must require generation 2.'
Assert-Contains $failsafe '$null -ne $journal.PreviousOwned' 'M5E failsafe must require null PreviousOwned.'
Assert-Contains $failsafe '[int]$journal.Pending.Cpu -ne 30' 'M5E failsafe must require pending CPU 30.'
Assert-Contains $failsafe '[int]$journal.Pending.Gpu -ne 30' 'M5E failsafe must require pending GPU 30.'
Assert-Contains $failsafe '$null -ne $journal.Owned' 'M5E failsafe must refuse a committed Owned journal.'
Assert-Contains $failsafe '$journal.Controller.ProcessId' 'M5E failsafe must bind to exact journal controller PID.'
Assert-Contains $failsafe '$journal.Controller.ProcessStartUtcTicks' 'M5E failsafe must bind to exact controller creation time.'
Assert-Contains $failsafe '$process.Kill()' 'M5E failsafe may neutralize only exact journal-bound controller.'
Assert-Contains $failsafe 'Start-Service -Name $serviceName' 'M5E failsafe may start only the already-qualified recovery service.'
Assert-Contains $failsafe 'Restart-Service -Name $serviceName -Force' 'M5E failsafe may restart the qualified service only after exact owner neutralization and retained journal.'
Assert-Contains $failsafe 'Wait-JournalGone' 'M5E failsafe must leave durable journal deletion to watchdog recovery.'
Assert-NotContains $failsafe 'SetFanLevel(' 'M5E failsafe must not issue ordinary fan targets.'
Assert-NotContains $failsafe '--restore-hp-auto' 'M5E failsafe must not invoke direct HP-auto restore.'
Assert-NotContains $failsafe 'Hp8C40BiosFanControl' 'M5E failsafe must have no direct HP BIOS authority.'

Assert-Contains $m5dChild 'WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT' 'M5E depends on the M5D exact pre-Commit hook.'
Assert-Contains $m5dChild 'Timeout.InfiniteTimeSpan' 'M5D child must still hold the boundary until parent force-kill.'
Assert-Contains $m5dChild 'ConfirmFirmwareAutoAfterAdmissionAnomalyAsync' 'M5E reused child must harden transient asymmetric admission reads without adding a write path.'
Assert-Contains $m5dChild 'RequiredConsecutiveFirmwareAutoSamples = 2' 'M5E reused child must require consecutive FF/FF before retrying admission.'
Assert-Contains $m5dChild 'ecProbe.ReadSetpoint()' 'M5E reused-child admission retry must remain read-only.'
Assert-NotContains $m5dChild 'RestoreFirmwareAuto()' 'M5E reused child must not directly restore firmware during admission retry.'

Write-Host 'HP 8C40 M5E WRITE_ARMED double-death invariant self-test: PASS' -ForegroundColor Green
