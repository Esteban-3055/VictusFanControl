$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$harnessPath = Join-Path $PSScriptRoot 'test-watchdog-m5a-controller-death-8c40.ps1'
$armPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M5AControllerDeathArmTest.cs'
$cliPath = Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs'
$programPath = Join-Path $repoRoot 'src\VictusFanControl\Program.cs'

$harness = Get-Content $harnessPath -Raw
$arm = Get-Content $armPath -Raw
$cli = Get-Content $cliPath -Raw
$program = Get-Content $programPath -Raw

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw $Message
    }
}

Assert-Contains $harness 'VictusFanControlWatchdogM4' 'M5A must use the isolated HP 8C40 M4 service.'
Assert-Contains $harness '8C40-M5A-CONTROLLER-DEATH30' 'M5A explicit controller-death token is missing.'
Assert-Contains $harness "TargetProfileId -cne 'HP-8C40-9D0R1LA-F18'" 'M5A must pin the exact HP 8C40 target id in the journal check.'
Assert-Contains $harness 'Test-JournalOwnedPhase' 'M5A must require durable OWNED phase before fault injection.'
Assert-Contains $harness '[int]$journal.Owned.Cpu -ne 30' 'M5A must require durable CPU OWNED 30.'
Assert-Contains $harness '[int]$journal.Owned.Gpu -ne 30' 'M5A must require durable GPU OWNED 30.'
Assert-Contains $harness '[int]$journal.Controller.ProcessId -ne $controller.Id' 'M5A must bind the journal to the exact controller PID.'
Assert-Contains $harness '[long]$journal.Controller.ProcessStartUtcTicks -ne $controllerStartTicks' 'M5A must bind the journal to exact process creation time.'
Assert-Contains $harness 'Stop-Process -Id $controller.Id -Force' 'M5A must hard-kill only the exact controller process.'
Assert-Contains $harness '(Get-ServiceProcessId) -ne $servicePidBefore' 'M5A must require the original watchdog service PID to survive the controller-death test.'
Assert-Contains $harness 'RestoredFirmware' 'M5A must require causal RestoredFirmware service evidence.'
Assert-Contains $harness 'Wait-ForJournalGone' 'M5A must require durable journal deletion after recovery.'
Assert-Contains $harness 'Read-8C40Setpoint' 'M5A must independently verify final FF/FF.'

if ($harness -match '--restore-hp-auto' -or
    $harness -match 'Hp8C40BiosFanControl' -or
    $harness -match 'SetFanLevel\(') {
    throw 'M5A parent harness must not contain direct HP restore or ordinary fan-write authority.'
}

Assert-Contains $arm 'NamedPipeFanControlWatchdogLeaseClient' 'M5A child must use the real target-bound watchdog lease client.'
Assert-Contains $arm 'FanControlCoordinator' 'M5A child must use the real coordinator.'
Assert-Contains $arm 'QualificationLevel = 30' 'M5A child level must remain pinned to 30/30.'
Assert-Contains $arm 'backend-ec+tachs+watchdog-owned' 'M5A READY marker must encode backend EC+tachs plus watchdog ownership.'
Assert-Contains $arm 'coordinator.EnforceSafetyAsync' 'M5A child must keep the normal safety/heartbeat supervision active before kill.'
Assert-Contains $arm 'M5A managed-exit fallback' 'Any managed M5A child exit must restore locally.'
Assert-Contains $arm 'Process.Kill()' 'M5A source documentation must explicitly distinguish the hard-kill path from managed cleanup.'

Assert-Contains $cli '--8c40-m5a-controller-death-arm' 'M5A CLI switch is missing.'
Assert-Contains $cli '--8c40-m5a-ready-path' 'M5A READY path CLI option is missing.'
Assert-Contains $program 'Hp8C40M5AControllerDeathArmTest.RequiredToken' 'M5A Program token gate is missing.'
Assert-Contains $program 'Hp8C40M5AControllerDeathArmTest.RunAsync' 'M5A Program dispatch is missing.'

Write-Host 'HP 8C40 M5A controller-death invariant self-test: PASS' -ForegroundColor Green
