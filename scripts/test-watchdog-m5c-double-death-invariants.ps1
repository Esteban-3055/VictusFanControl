$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$harnessPath = Join-Path $PSScriptRoot 'test-watchdog-m5c-double-death-8c40.ps1'
$failsafePath = Join-Path $PSScriptRoot 'watchdog-m5c-service-failsafe-8c40.ps1'

$harness = Get-Content $harnessPath -Raw
$failsafe = Get-Content $failsafePath -Raw

function Assert-Contains {
    param([string]$Text,[string]$Needle,[string]$Message)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw $Message
    }
}

function Assert-NotContains {
    param([string]$Text,[string]$Needle,[string]$Message)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -ge 0) {
        throw $Message
    }
}

Assert-Contains $harness '8C40-M5C-DOUBLE-DEATH30' 'M5C requires an explicit destructive-test token.'
Assert-Contains $harness '8C40-M5A-CONTROLLER-DEATH30' 'M5C must reuse the already-qualified exact-target OWNED 30/30 arm path.'
Assert-Contains $harness 'HP-8C40-9D0R1LA-F18' 'M5C must pin the exact 8C40 target profile.'
Assert-Contains $harness 'VictusFanControl.Watchdog.M4.8C40.v2' 'M5C must pin the isolated target-bound watchdog pipe.'
Assert-Contains $harness 'Test-JournalOwnedPhase' 'M5C must require durable OWNED phase.'
Assert-Contains $harness '[int]$Journal.Owned.Cpu -ne 30' 'M5C must require durable CPU OWNED 30.'
Assert-Contains $harness '[int]$Journal.Owned.Gpu -ne 30' 'M5C must require durable GPU OWNED 30.'
Assert-Contains $harness '[long]$Journal.Controller.ProcessStartUtcTicks -ne $ControllerStartTicks' 'M5C must bind the journal to exact controller creation time.'
Assert-Contains $harness 'watchdog-m5c-service-failsafe-8c40.ps1' 'M5C must arm the independent delayed service-start safety fallback.'
Assert-Contains $harness 'restart/$RestartDelayMs/restart/5000/restart/10000' 'M5C must configure ordered SCM replacement recovery.'
Assert-Contains $harness 'Test-FailsafeTakeover' 'M5C must reject a run in which the delayed safety fallback fires.'
Assert-Contains $harness '$postDeath.Cpu -ne 30 -or $postDeath.Gpu -ne 30' 'M5C must prove EC remains 30/30 after both original recovery domains are dead.'
Assert-Contains $harness 'Durable OWNED journal disappeared before replacement startup.' 'M5C must require durable OWNED evidence to survive until replacement startup.'
Assert-Contains $harness '$status.RecoveryDisposition -ceq ''RestoredFirmware''' 'M5C replacement must report RestoredFirmware.'
Assert-Contains $harness 'Wait-ForJournalGone' 'M5C must require journal deletion after replacement recovery.'
Assert-Contains $harness '$final.Cpu -ne 255 -or $final.Gpu -ne 255' 'M5C must independently verify final FF/FF.'
Assert-Contains $harness 'StartType=$($svc.StartType)' 'M5C must restore the Manual/stopped qualification-service baseline.'
Assert-Contains $harness 'Do not reinstall the service or run another fan-write gate until this state is inspected/recovered.' 'M5C must retain unresolved durable evidence fail-closed.'

if ($harness -match '(?m)^\s*(?:&\s*)?dotnet\s+\$cli\s+--restore-hp-auto\b' -or
    $harness -match 'Hp8C40BiosFanControl' -or
    $harness -match 'SetFanLevel\(') {
    throw 'M5C parent harness must not contain direct HP restore or ordinary fan-target authority.'
}

$killStep = $harness.IndexOf("Write-Host 'Step 5: DOUBLE-KILL original watchdog then exact controller...'", [StringComparison]::Ordinal)
$postKillStep = $harness.IndexOf("Write-Host 'Step 6: prove both original recovery domains are gone before replacement...'", [StringComparison]::Ordinal)

if ($killStep -lt 0 -or $postKillStep -le $killStep) {
    throw 'M5C invariant could not isolate the double-kill segment.'
}

$killSegment = $harness.Substring($killStep, $postKillStep - $killStep)
$watchdogKillIndex = $killSegment.IndexOf('$watchdogProcess.Kill()', [StringComparison]::Ordinal)
$controllerKillIndex = $killSegment.IndexOf('$controller.Kill()', [StringComparison]::Ordinal)

if ($watchdogKillIndex -lt 0 -or $controllerKillIndex -lt 0 -or $watchdogKillIndex -ge $controllerKillIndex) {
    throw 'M5C must issue watchdog Kill() before exact controller Kill().'
}

$betweenKills = $killSegment.Substring($watchdogKillIndex, $controllerKillIndex - $watchdogKillIndex)
foreach ($forbidden in @('Start-Sleep','Read-8C40Setpoint','Get-ServiceProcessId','Get-CimInstance','Get-Content','Set-Content')) {
    if ($betweenKills.IndexOf($forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw "M5C must not perform '$forbidden' between the two Kill() calls."
    }
}

Assert-Contains $killSegment '$killDeltaMs -gt $MaxKillDeltaMs' 'M5C must measure and enforce the bounded kill-call interval.'
Assert-Contains $harness '$preRestartElapsedMs -ge ($RestartDelayMs - 500)' 'M5C must prove pre-restart evidence was collected inside the SCM delay window.'
Assert-Contains $harness 'if ((Get-ServiceProcessId) -ne 0)' 'M5C must explicitly prove service absence during the pre-restart window.'

Assert-Contains $failsafe 'HP-8C40-9D0R1LA-F18' 'M5C delayed failsafe must pin the exact target.'
Assert-Contains $failsafe '[int]$journal.Owned.Cpu -ne 30' 'M5C delayed failsafe must require OWNED CPU 30.'
Assert-Contains $failsafe '[int]$journal.Owned.Gpu -ne 30' 'M5C delayed failsafe must require OWNED GPU 30.'
Assert-Contains $failsafe 'Start-Service -Name $serviceName' 'M5C delayed failsafe may recover only by starting the already-qualified service.'
Assert-NotContains $failsafe 'SetFanLevel(' 'M5C delayed failsafe must not issue ordinary fan targets.'
Assert-NotContains $failsafe '--restore-hp-auto' 'M5C delayed failsafe must not invoke direct HP restore CLI.'
Assert-NotContains $failsafe 'Remove-Item $journalPath' 'M5C delayed failsafe must never delete durable ownership evidence.'

Write-Host 'HP 8C40 M5C destructive double-death invariant self-test: PASS' -ForegroundColor Green
