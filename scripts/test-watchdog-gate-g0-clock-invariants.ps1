$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$clockPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\MonotonicClock.cs'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'
$gateCPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\GateCLeaseSelfTest.cs'

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "Gate G0 clock invariant missing: $Description"
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
        throw "Gate G0 clock invariant violated: $Description"
    }

    Write-Host "PASS  $Description"
}

$clock = Get-Content $clockPath -Raw
$manager = Get-Content $managerPath -Raw
$gateC = Get-Content $gateCPath -Raw

Write-Host 'VictusFanControl - GATE G0 WATCHDOG CLOCK INVARIANT SELF-TEST'

Assert-Contains -Text $clock -Pattern 'QueryUnbiasedInterruptTime' -Description 'production watchdog uses QueryUnbiasedInterruptTime'
Assert-NotContains -Text $clock -Pattern 'GetTickCount64' -Description 'production watchdog no longer uses GetTickCount64'
Assert-NotContains -Text $clock -Pattern 'Environment\.TickCount64' -Description 'production watchdog does not depend on runtime TickCount sleep semantics'
Assert-Contains -Text $clock -Pattern 'HundredNanosecondsPerMillisecond\s*=\s*10_000UL' -Description '100 ns units are converted with the exact 10,000 ticks/ms divisor'
Assert-Contains -Text $clock -Pattern 'unbiasedTime100ns\s*/\s*HundredNanosecondsPerMillisecond' -Description 'conversion divides before further arithmetic to avoid overflow'

Assert-Contains -Text $manager -Pattern 'OwnedHeartbeatTimeout[\s\S]*TimeSpan\.FromSeconds\(5\)' -Description 'OWNED timeout remains 5 seconds'
Assert-Contains -Text $manager -Pattern 'WriteArmedDeadline[\s\S]*TimeSpan\.FromSeconds\(12\)' -Description 'WRITE_ARMED deadline remains 12 seconds'
Assert-Contains -Text $manager -Pattern 'RestoringDeadline[\s\S]*TimeSpan\.FromSeconds\(8\)' -Description 'RESTORING deadline remains 8 seconds'

Assert-Contains -Text $gateC -Pattern 'private sealed class FakeClock\s*:\s*IMonotonicClock' -Description 'Gate C still injects a deterministic fake monotonic clock'
Assert-Contains -Text $gateC -Pattern 'HeartbeatTimeoutAsync' -Description 'Gate C still covers OWNED heartbeat timeout'
Assert-Contains -Text $gateC -Pattern 'WriteArmedTimeoutAsync' -Description 'Gate C still covers WRITE_ARMED deadline'
Assert-Contains -Text $gateC -Pattern 'RestoringTimeoutAsync' -Description 'Gate C still covers RESTORING deadline'

Write-Host ''
Write-Host 'Gate G0 watchdog clock invariant self-test: PASS' -ForegroundColor Green
exit 0
