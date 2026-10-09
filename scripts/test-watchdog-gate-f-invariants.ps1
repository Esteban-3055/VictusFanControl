$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

$programPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$f1HarnessPath = Join-Path $PSScriptRoot 'test-watchdog-gate-f1.ps1'
$f2HarnessPath = Join-Path $PSScriptRoot 'test-watchdog-gate-f2.ps1'
$f2WrapperPath = Join-Path $repoRoot 'src\VictusFanControl.App\GateF2CommitHoldWatchdogLeaseClient.cs'
$backendPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp88F8FanControlBackend.cs'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'
$gateCSelfTestPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\GateCLeaseSelfTest.cs'

$program = Get-Content $programPath -Raw
$mainForm = Get-Content $mainFormPath -Raw
$f1Harness = Get-Content $f1HarnessPath -Raw
$f2Harness = Get-Content $f2HarnessPath -Raw
$f2Wrapper = Get-Content $f2WrapperPath -Raw
$backend = Get-Content $backendPath -Raw
$manager = Get-Content $managerPath -Raw
$gateC = Get-Content $gateCSelfTestPath -Raw

function Assert-Contains {
    param([string]$Text, [string]$Pattern, [string]$Description)
    if ($Text -notmatch $Pattern) {
        throw "Gate F invariant missing: $Description"
    }
    Write-Host "PASS  $Description"
}

function Assert-NotContains {
    param([string]$Text, [string]$Pattern, [string]$Description)
    if ($Text -match $Pattern) {
        throw "Gate F invariant violated: $Description"
    }
    Write-Host "PASS  $Description"
}

function Assert-DoubleKillHarness {
    param(
        [string]$Harness,
        [string]$GateName
    )

    $readyIndex = $Harness.IndexOf("Write-Host '$GateName READY marker:'", [StringComparison]::Ordinal)
    $killIndex = $Harness.IndexOf("Write-Host 'Step 6: DOUBLE-KILL", [StringComparison]::Ordinal)

    if ($readyIndex -lt 0 -or $killIndex -le $readyIndex) {
        throw "Gate F invariant violated: could not isolate $GateName READY -> double-kill segment."
    }

    $preKillSegment = $Harness.Substring(
        $readyIndex,
        $killIndex - $readyIndex)

    Assert-NotContains -Text $preKillSegment -Pattern 'Read-EcState|--probe-88f8-ec-state' -Description "no out-of-band EC probe exists between $GateName READY and double kill"
    Assert-NotContains -Text $preKillSegment -Pattern 'Start-Sleep' -Description "no artificial dwell exists between $GateName READY and double kill"
    Assert-Contains -Text $preKillSegment -Pattern '\$servicePidAtKillBoundary\s*=\s*Get-ServiceProcessId' -Description "$GateName captures watchdog PID explicitly as a scalar at the kill boundary"
    Assert-Contains -Text $preKillSegment -Pattern '\$serviceStartTicksAtBoundary\s*=\s*Get-ProcessStartTicks' -Description "$GateName revalidates watchdog creation time at the kill boundary"
    Assert-Contains -Text $preKillSegment -Pattern '\$failsafe\.Refresh\(\)[\s\S]*\$failsafe\.HasExited' -Description "$GateName proves the independent emergency fallback is alive at the kill boundary"

    $step7Index = $Harness.IndexOf("Write-Host 'Step 7: require SCM restart", [StringComparison]::Ordinal)
    if ($step7Index -le $killIndex) {
        throw "Gate F invariant violated: could not isolate $GateName double-kill segment."
    }

    $killSegment = $Harness.Substring(
        $killIndex,
        $step7Index - $killIndex)

    $watchdogKillIndex = $killSegment.IndexOf('$watchdogProcess.Kill()', [StringComparison]::Ordinal)
    $guiKillIndex = $killSegment.IndexOf('$proc.Kill()', [StringComparison]::Ordinal)

    if ($watchdogKillIndex -lt 0 -or
        $guiKillIndex -lt 0 -or
        $watchdogKillIndex -ge $guiKillIndex) {
        throw "Gate F invariant violated: $GateName must issue watchdog Kill() before GUI Kill()."
    }

    Write-Host "PASS  $GateName issues watchdog Kill() before GUI Kill()"

    $betweenKills = $killSegment.Substring(
        $watchdogKillIndex,
        $guiKillIndex - $watchdogKillIndex)

    Assert-NotContains -Text $betweenKills -Pattern 'Start-Sleep|Read-EcState|Get-ServiceProcessId|Get-CimInstance' -Description "$GateName has no sleep, EC probe or SCM query between kill calls"
    Assert-Contains -Text $killSegment -Pattern '\$killDeltaMs\s+-gt\s+\$MaxKillDeltaMs' -Description "$GateName measures and bounds double-kill issue latency"
    Assert-Contains -Text $Harness -Pattern "RequiredRecovery 'RestoredFirmware'" -Description "$GateName requires restart recovery from the durable journal"
    Assert-Contains -Text $Harness -Pattern 'Wait-ForJournalGone' -Description "$GateName requires journal deletion only after restart recovery"
    Assert-Contains -Text $Harness -Pattern '\$final\.Cpu -ne 255 -or \$final\.Gpu -ne 255' -Description "$GateName independently verifies final EC FF/FF"
    Assert-Contains -Text $Harness -Pattern '1000\\s\*ms\.\*5000\\s\*ms\.\*10000\\s\*ms' -Description "$GateName verifies production SCM recovery ordering 1s/5s/10s"
    Assert-Contains -Text $Harness -Pattern 'watchdog-gate-b-failsafe\.ps1' -Description "$GateName arms the independent delayed emergency fallback"
    Assert-NotContains -Text $Harness -Pattern '(?m)^\s*(?:&\s*)?dotnet\s+\$cli\s+--restore-hp-auto\b' -Description "$GateName parent harness never invokes the HP restore CLI"
}

Write-Host 'VictusFanControl - GATE F DOUBLE-DEATH INVARIANT SELF-TEST'

Assert-Contains -Text $program -Pattern '\-\-gate-f1-owned-double-death-test' -Description 'Gate F1 has an explicit opt-in application mode'
Assert-Contains -Text $program -Pattern '88F8-GATEF1-30' -Description 'Gate F1 requires an explicit hardware-test token'
Assert-Contains -Text $mainForm -Pattern 'gate-f1-owned\.ready' -Description 'Gate F1 publishes a dedicated READY marker'
Assert-Contains -Text $mainForm -Pattern 'ack=backend-ec\+tachs\+watchdog-owned' -Description 'Gate F1 READY is emitted only after backend EC+tachs ACK and watchdog OWNED'
Assert-Contains -Text $mainForm -Pattern 'GateF1LocalRestoreStartedPath' -Description 'Gate F1 records any live-GUI restore start'
Assert-Contains -Text $f1Harness -Pattern 'Gate F1 live GUI began a local restore before its death' -Description 'any live-GUI restore attempt invalidates Gate F1 causality'
Assert-DoubleKillHarness -Harness $f1Harness -GateName 'Gate F1'

Assert-Contains -Text $program -Pattern '\-\-gate-f2-write-armed-double-death-test' -Description 'Gate F2 has an explicit opt-in application mode'
Assert-Contains -Text $program -Pattern '88F8-GATEF2-30' -Description 'Gate F2 requires an explicit hardware-test token'
Assert-Contains -Text $mainForm -Pattern 'GateF2CommitHoldWatchdogLeaseClient' -Description 'Gate F2 alone installs the test-only Commit hold wrapper'
Assert-Contains -Text $mainForm -Pattern 'GateF2LocalRestoreStartedPath' -Description 'Gate F2 records any live-GUI restore start'

Assert-Contains -Text $f2Wrapper -Pattern '_inner\.WriteIntentAsync' -Description 'Gate F2 forwards durable WriteIntent through the real named-pipe lease client'
Assert-Contains -Text $f2Wrapper -Pattern 'ack=backend-ec\+tachs-before-commit' -Description 'Gate F2 READY marker identifies the post-ACK/pre-Commit boundary'
Assert-Contains -Text $f2Wrapper -Pattern 'FileOptions\.WriteThrough' -Description 'Gate F2 READY marker is written through before the hold'
Assert-Contains -Text $f2Wrapper -Pattern 'Flush\(flushToDisk: true\)' -Description 'Gate F2 READY marker is flushed before the hold'
Assert-Contains -Text $f2Wrapper -Pattern 'Timeout\.InfiniteTimeSpan[\s\S]*CancellationToken\.None' -Description 'Gate F2 holds the live controller non-cancellably before Commit'
Assert-NotContains -Text $f2Wrapper -Pattern '_inner\.CommitAsync' -Description 'Gate F2 wrapper never forwards Commit'

$setpointAckIndex = $backend.IndexOf('WaitForSetpointAsync(', [StringComparison]::Ordinal)
$tachAckIndex = $backend.IndexOf('WaitForTachometerResponseAsync(', [StringComparison]::Ordinal)
$commitIndex = $backend.IndexOf('_watchdogLease.CommitAsync(', [StringComparison]::Ordinal)

if ($setpointAckIndex -lt 0 -or
    $tachAckIndex -lt 0 -or
    $commitIndex -lt 0 -or
    $setpointAckIndex -ge $commitIndex -or
    $tachAckIndex -ge $commitIndex) {
    throw 'Gate F invariant violated: production backend Commit ordering is no longer EC+tachs ACK before Commit.'
}

Write-Host 'PASS  production backend still performs EC setpoint + dual-tach ACK before Commit'

Assert-Contains -Text $f2Harness -Pattern 'phase=WriteArmed' -Description 'Gate F2 requires the WRITE_ARMED READY boundary'
Assert-Contains -Text $f2Harness -Pattern 'Test-JournalWriteArmedPhase' -Description 'Gate F2 validates durable WRITE_ARMED phase'
Assert-Contains -Text $f2Harness -Pattern '\$journal\.Pending\.Cpu' -Description 'Gate F2 validates pending CPU target'
Assert-Contains -Text $f2Harness -Pattern '\$journal\.Pending\.Gpu' -Description 'Gate F2 validates pending GPU target'
Assert-Contains -Text $f2Harness -Pattern '\$null -ne \$journal\.Owned' -Description 'Gate F2 requires no durable OWNED target before fault injection'
Assert-Contains -Text $f2Harness -Pattern 'Gate F2 live GUI began a local restore before its death' -Description 'any live-GUI restore attempt invalidates Gate F2 causality'
Assert-DoubleKillHarness -Harness $f2Harness -GateName 'Gate F2'

Assert-Contains -Text $manager -Pattern '!AllowedSetpoints\(record\)\.Contains\(observed\)' -Description 'startup recovery refuses an unknown setpoint not authorized by the durable lease'
Assert-Contains -Text $manager -Pattern 'LeaseRecoveryDisposition\.OwnershipAmbiguous[\s\S]*RestoreAttempted: false[\s\S]*JournalRetained: true' -Description 'ambiguous ownership is fail-closed with no blind restore and retained evidence'
Assert-Contains -Text $gateC -Pattern 'restart after WMI before Commit restores pending target' -Description 'synthetic Gate C covers WRITE_ARMED after WMI before Commit'
Assert-Contains -Text $gateC -Pattern 'restart after Commit restores OWNED target' -Description 'synthetic Gate C covers restart from durable OWNED'
Assert-Contains -Text $gateC -Pattern 'unknown setpoint with active lease is not cleared' -Description 'synthetic Gate C covers unknown external setpoint preservation'

Write-Host 'Gate F double-death invariant self-test: PASS' -ForegroundColor Green
exit 0
