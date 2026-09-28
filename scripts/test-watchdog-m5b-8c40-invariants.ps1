$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$harnessPath = Join-Path $PSScriptRoot 'test-watchdog-m5b-watchdog-death-8c40.ps1'
$controllerPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M5BWatchdogDeathControllerTest.cs'
$cliPath = Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs'
$programPath = Join-Path $repoRoot 'src\VictusFanControl\Program.cs'

$harness = Get-Content $harnessPath -Raw
$controller = Get-Content $controllerPath -Raw
$cli = Get-Content $cliPath -Raw
$program = Get-Content $programPath -Raw

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    if (-not $Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw $Message
    }
}

Assert-Contains $harness 'VictusFanControlWatchdogM4' 'M5B must use the isolated HP 8C40 M4 service.'
Assert-Contains $harness '8C40-M5B-WATCHDOG-DEATH30' 'M5B explicit token is missing.'
Assert-Contains $harness 'Stop-Process -Id $servicePidBefore -Force' 'M5B must hard-kill only the exact watchdog service PID.'
Assert-Contains $harness 'if ($controller.HasExited)' 'M5B must prove the controller remains alive around the watchdog-death boundary.'
Assert-Contains $harness '$local.Reason -notmatch ''WATCHDOG_IPC_LOSS''' 'M5B must require watchdog IPC loss as the causal local-restore reason.'
Assert-Contains $harness '[int]$local.CpuSetpoint -ne 255' 'M5B local restore proof must require CPU FF.'
Assert-Contains $harness '[int]$local.GpuSetpoint -ne 255' 'M5B local restore proof must require GPU FF.'
Assert-Contains $harness '[bool]$local.WatchdogReleaseVerified' 'M5B must prove watchdog Release was unavailable while the service was dead.'
Assert-Contains $harness 'M5B durable OWNED journal disappeared while watchdog was absent' 'M5B must require retained durable ownership evidence before restart.'
Assert-Contains $harness 'Start-Service -Name $serviceName' 'M5B parent must restart the recovery service after local restore proof.'
Assert-Contains $harness 'AllowedRecoveryDispositions @(''RestoredFirmware'')' 'M5B replacement startup must require RestoredFirmware.'
Assert-Contains $harness 'Wait-ForJournalGone' 'M5B must require durable journal removal after replacement startup recovery.'
Assert-Contains $harness 'Read-8C40Setpoint' 'M5B must independently verify local and final FF/FF.'
Assert-Contains $harness 'M5B-PARENT-COMPLETE' 'M5B controller may exit only after parent restart-recovery proof.'

if ($harness -match '--restore-hp-auto' -or
    $harness -match 'Hp8C40BiosFanControl' -or
    $harness -match 'SetFanLevel\(') {
    throw 'M5B parent harness must not contain direct HP restore or ordinary fan-write authority.'
}

Assert-Contains $controller 'NamedPipeFanControlWatchdogLeaseClient' 'M5B controller must use the real target-bound watchdog lease.'
Assert-Contains $controller 'FanControlCoordinator' 'M5B controller must use the real coordinator.'
Assert-Contains $controller 'QualificationLevel = 30' 'M5B controller target must remain pinned to 30/30.'
Assert-Contains $controller 'coordinator.EnforceSafetyAsync' 'M5B watchdog loss must be detected through ordinary continuous supervision.'
Assert-Contains $controller 'ContainsWatchdogIpcLoss' 'M5B controller must explicitly classify WATCHDOG_IPC_LOSS.'
Assert-Contains $controller 'backend.LastRestoreEvidence' 'M5B controller must inspect backend local restore evidence.'
Assert-Contains $controller '!restoreEvidence.LocalFirmwareAckVerified' 'M5B must require verified local firmware acknowledgement.'
Assert-Contains $controller 'restoreEvidence.WatchdogReleaseVerified' 'M5B must require watchdog Release to remain unverified while watchdog is dead.'
Assert-Contains $controller 'Authority:' 'M5B markers must include coordinator authority.'
Assert-Contains $controller 'File.Exists(completionPath)' 'M5B controller must remain alive until parent completion signal.'

Assert-Contains $cli '--8c40-m5b-watchdog-death-controller' 'M5B CLI mode is missing.'
Assert-Contains $cli '--8c40-m5b-local-restore-path' 'M5B local-restore marker CLI path is missing.'
Assert-Contains $cli '--8c40-m5b-completion-path' 'M5B completion marker CLI path is missing.'
Assert-Contains $program 'Hp8C40M5BWatchdogDeathControllerTest.RequiredToken' 'M5B Program token gate is missing.'
Assert-Contains $program 'Hp8C40M5BWatchdogDeathControllerTest.RunAsync' 'M5B Program dispatch is missing.'

Write-Host 'HP 8C40 M5B watchdog-death invariant self-test: PASS' -ForegroundColor Green
