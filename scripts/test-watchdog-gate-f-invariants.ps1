$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

$programPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$harnessPath = Join-Path $PSScriptRoot 'test-watchdog-gate-f1.ps1'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'
$gateCSelfTestPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\GateCLeaseSelfTest.cs'

$program = Get-Content $programPath -Raw
$mainForm = Get-Content $mainFormPath -Raw
$harness = Get-Content $harnessPath -Raw
$manager = Get-Content $managerPath -Raw
$gateC = Get-Content $gateCSelfTestPath -Raw

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "Gate F invariant missing: $Description"
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
        throw "Gate F invariant violated: $Description"
    }

    Write-Host "PASS  $Description"
}

Write-Host 'VictusFanControl - GATE F DOUBLE-DEATH INVARIANT SELF-TEST'

Assert-Contains -Text $program -Pattern '\-\-gate-f1-owned-double-death-test' -Description 'Gate F1 has an explicit opt-in application mode'
Assert-Contains -Text $program -Pattern '88F8-GATEF1-30' -Description 'Gate F1 requires an explicit hardware-test token'
Assert-Contains -Text $mainForm -Pattern 'gate-f1-owned\.ready' -Description 'Gate F1 publishes a dedicated READY marker'
Assert-Contains -Text $mainForm -Pattern 'ack=backend-ec\+tachs\+watchdog-owned' -Description 'Gate F1 READY is emitted only after backend EC+tachs ACK and watchdog OWNED'
Assert-Contains -Text $mainForm -Pattern 'e\.Previous == FanAuthority\.Custom[\s\S]*e\.Current == FanAuthority\.Restoring[\s\S]*GateF1LocalRestoreStartedPath' -Description 'Gate F1 records any live-GUI restore start synchronously at Custom -> Restoring'
Assert-NotContains -Text $harness -Pattern '(?m)^\s*(?:&\s*)?dotnet\s+\$cli\s+--restore-hp-auto\b' -Description 'Gate F1 parent harness never invokes the HP restore CLI'

$readyIndex = $harness.IndexOf("Write-Host 'Gate F1 READY marker:'", [StringComparison]::Ordinal)
$killIndex = $harness.IndexOf("Write-Host 'Step 6: DOUBLE-KILL", [StringComparison]::Ordinal)

if ($readyIndex -lt 0 -or $killIndex -le $readyIndex) {
    throw 'Gate F invariant violated: could not isolate READY -> double-kill segment.'
}

$preKillSegment = $harness.Substring(
    $readyIndex,
    $killIndex - $readyIndex)

Assert-NotContains -Text $preKillSegment -Pattern 'Read-EcState|--probe-88f8-ec-state' -Description 'no out-of-band EC probe exists between Gate F1 READY and double kill'
Assert-NotContains -Text $preKillSegment -Pattern 'Start-Sleep' -Description 'no artificial dwell exists between Gate F1 READY and double kill'
Assert-Contains -Text $preKillSegment -Pattern '\$servicePidAtKillBoundary\s*=\s*Get-ServiceProcessId' -Description 'watchdog PID is captured explicitly as a scalar at the kill boundary'
Assert-Contains -Text $preKillSegment -Pattern '\$serviceStartTicksAtBoundary\s*=\s*Get-ProcessStartTicks' -Description 'watchdog creation time is revalidated at the kill boundary'

$step6Index = $killIndex
$step7Index = $harness.IndexOf("Write-Host 'Step 7: require SCM restart", [StringComparison]::Ordinal)

if ($step7Index -le $step6Index) {
    throw 'Gate F invariant violated: could not isolate the double-kill segment.'
}

$killSegment = $harness.Substring(
    $step6Index,
    $step7Index - $step6Index)

$watchdogKillIndex = $killSegment.IndexOf('$watchdogProcess.Kill()', [StringComparison]::Ordinal)
$guiKillIndex = $killSegment.IndexOf('$proc.Kill()', [StringComparison]::Ordinal)

if ($watchdogKillIndex -lt 0 -or
    $guiKillIndex -lt 0 -or
    $watchdogKillIndex -ge $guiKillIndex) {
    throw 'Gate F invariant violated: watchdog Kill() must be issued before GUI Kill().'
}

Write-Host 'PASS  watchdog Kill() is issued before GUI Kill()'

$betweenKills = $killSegment.Substring(
    $watchdogKillIndex,
    $guiKillIndex - $watchdogKillIndex)

Assert-NotContains -Text $betweenKills -Pattern 'Start-Sleep|Read-EcState|Get-ServiceProcessId|Get-CimInstance' -Description 'no sleep, EC probe or SCM query exists between watchdog and GUI kill calls'
Assert-Contains -Text $killSegment -Pattern '\$killDeltaMs\s+-gt\s+\$MaxKillDeltaMs' -Description 'double-kill issue latency is measured and bounded'
Assert-Contains -Text $harness -Pattern 'Gate F1 live GUI began a local restore before its death' -Description 'any live-GUI restore attempt invalidates Gate F1 causality'
Assert-Contains -Text $harness -Pattern "RequiredRecovery 'RestoredFirmware'" -Description 'Gate F1 requires restart recovery from the durable journal'
Assert-Contains -Text $harness -Pattern 'Wait-ForJournalGone' -Description 'Gate F1 requires durable journal deletion only after restart recovery'
Assert-Contains -Text $harness -Pattern '\$final\.Cpu -ne 255 -or \$final\.Gpu -ne 255' -Description 'Gate F1 independently verifies final EC FF/FF'
Assert-Contains -Text $harness -Pattern '1000\\s\*ms\.\*5000\\s\*ms\.\*10000\\s\*ms' -Description 'Gate F1 verifies production SCM recovery ordering 1s/5s/10s'
Assert-Contains -Text $harness -Pattern 'watchdog-gate-b-failsafe\.ps1' -Description 'Gate F1 arms the independent delayed emergency fallback'
Assert-Contains -Text $manager -Pattern '!AllowedSetpoints\(record\)\.Contains\(observed\)' -Description 'startup recovery refuses an unknown setpoint not authorized by the durable lease'
Assert-Contains -Text $manager -Pattern 'LeaseRecoveryDisposition\.OwnershipAmbiguous[\s\S]*RestoreAttempted: false[\s\S]*JournalRetained: true' -Description 'ambiguous ownership is fail-closed with no blind restore and retained evidence'
Assert-Contains -Text $gateC -Pattern 'restart after Commit restores OWNED target' -Description 'synthetic Gate C covers restart from durable OWNED'
Assert-Contains -Text $gateC -Pattern 'unknown setpoint with active lease is not cleared' -Description 'synthetic Gate C covers unknown external setpoint preservation'

Write-Host 'Gate F double-death invariant self-test: PASS' -ForegroundColor Green
exit 0
