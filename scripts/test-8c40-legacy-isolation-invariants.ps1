$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProgramPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$gateGStatePath = Join-Path $repoRoot 'src\VictusFanControl.App\GateG1WatchdogState.cs'
$probe88Path = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp88F8EcControlStateProbe.cs'
$factoryPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs'
$backend8Path = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$m2InstallerPath = Join-Path $PSScriptRoot 'install-watchdog-m2-8c40.ps1'
$cleanupPath = Join-Path $PSScriptRoot 'cleanup-watchdog-88f8-services.ps1'

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "8C40 isolation invariant missing: $Description"
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
$m2Installer = Get-Content $m2InstallerPath -Raw
$cleanup = Get-Content $cleanupPath -Raw

Write-Host 'VictusFanControl - HP 8C40 / legacy 88F8 isolation invariant self-test'

Assert-Contains -Text $appProgram -Pattern 'legacy88F8HardwareHarnessRequested[sS]*suspendHardwareTest[sS]*gateDHardwareTest[sS]*gateG2HardwareTest' -Description 'all historical suspend/Gate D-G app modes are grouped behind one legacy 88F8 guard'
Assert-Contains -Text $appProgram -Pattern 'legacy88F8HardwareHarnessRequested[sS]*Hp88F8TargetProfile.Matches(' -Description 'legacy hardware modes require the exact HP 88F8 fingerprint before MainForm/backend creation'
Assert-Contains -Text $appProgram -Pattern 'Use the dedicated 8C40 M-series qualification gates instead' -Description 'legacy mode refusal directs 8C40 hardware to M-series gates'

Assert-Contains -Text $mainForm -Pattern '_suspendLifecycleHardwareTest ||[sS]*_gateG2HardwareTest)[sS]*Hp88F8TargetProfile.Instance.Id' -Description 'MainForm keeps a second exact-target defense for all historical hardware harnesses'
Assert-Contains -Text $mainForm -Pattern 'WindowsSleepModel.ModernStandbyS0LowPowerIdle[sS]*generic S3-era lifecycle recovery will not reopen Custom admission' -Description '8C40 Modern Standby cannot reopen Custom from the generic S3-era Healthy path'
Assert-Contains -Text $mainForm -Pattern 'Validated target: {_targetProfile.DisplayName} ({_targetProfile.Id})' -Description 'normal GUI fan page identifies the resolved target instead of claiming 88F8 unconditionally'
Assert-NotContains -Text $mainForm -Pattern 'Text = "The validated HP 88F8 backend is integrated behind FanControlCoordinator' -Description 'stale 88F8-only normal GUI label is removed'

Assert-Contains -Text $gateGState -Pattern 'TargetProfileId' -Description 'legacy Gate G consumes watchdog target identity'
Assert-Contains -Text $gateGState -Pattern 'Hp88F8TargetProfile.Instance.Id' -Description 'legacy Gate G requires exact 88F8 watchdog target identity'

Assert-Contains -Text $probe88 -Pattern 'Hp88F8TargetProfile.Matches(' -Description 'legacy 88F8 EC diagnostics require the full exact target fingerprint'
Assert-NotContains -Text $probe88 -Pattern 'hardware.BoardProduct,[sS]*"88F8"' -Description 'legacy EC diagnostics no longer accept board product alone'

Assert-Contains -Text $m2Installer -Pattern 'VictusFanControlWatchdogGateA[sS]*VictusFanControlWatchdogGateB[sS]*VictusFanControlWatchdog' -Description 'M2 installer enumerates historical watchdog service registrations'
Assert-Contains -Text $m2Installer -Pattern 'historical 88F8 watchdog[sS]*cleanup-watchdog-88f8-services.ps1' -Description 'M2 installer refuses silent coexistence with historical services'
Assert-Contains -Text $cleanup -Pattern 'sc.exe config $name start= disabled' -Description 'cleanup disables legacy service startup before deletion'
Assert-Contains -Text $cleanup -Pattern 'sc.exe delete $name' -Description 'cleanup removes legacy service registrations'
Assert-Contains -Text $cleanup -Pattern 'Historical ProgramData logs/journals are NOT deleted' -Description 'cleanup preserves historical forensic evidence'

Assert-Contains -Text $factory -Pattern 'HP 8C40 matched, but watchdog/service recovery has not yet[sS]*throw new NotSupportedException' -Description 'production factory still rejects a watchdog lease on 8C40'
Assert-Contains -Text $backend8 -Pattern 'HP 8C40 watchdog recovery is not yet physically validated[sS]*throw new NotSupportedException' -Description '8C40 backend independently rejects watchdog lease construction'

Write-Host ''
Write-Host 'HP 8C40 / legacy 88F8 isolation invariant self-test: PASS' -ForegroundColor Green
exit 0
