$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$clockPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\MonotonicClock.cs'
$managerPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\WatchdogLeaseManager.cs'
$gateCPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\GateCLeaseSelfTest.cs'
$probePath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\GateG0ClockProbe.cs'
$programPath = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\Program.cs'
$physicalScriptPath = Join-Path $PSScriptRoot 'test-watchdog-gate-g0-clock.ps1'

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
$probe = Get-Content $probePath -Raw
$program = Get-Content $programPath -Raw
$physicalScript = Get-Content $physicalScriptPath -Raw

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

Assert-Contains -Text $probe -Pattern 'new WindowsMonotonicClock\(\)' -Description 'physical probe executes the production clock implementation in .NET 8'
Assert-Contains -Text $probe -Pattern 'DateTimeOffset\.UtcNow' -Description 'physical probe compares unbiased time against UTC wall time'
Assert-NotContains -Text $probe -Pattern 'PawnIo|Hp88F8|SetFanLevel|ILeaseJournal|WatchdogLeaseManager' -Description 'physical probe has no EC/WMI/lease hardware path'

$probeDispatch = $program.IndexOf('GateG0ClockProbe.IsRequested(args)', [StringComparison]::Ordinal)
$optionsParse = $program.IndexOf('WatchdogOptions.Parse(args)', [StringComparison]::Ordinal)
if ($probeDispatch -lt 0 -or $optionsParse -lt 0 -or $probeDispatch -ge $optionsParse) {
    throw 'Gate G0 clock invariant violated: the read-only clock probe must dispatch before WatchdogOptions/service host construction.'
}
Write-Host 'PASS  Gate G0 clock probe dispatches before service/watchdog host construction'

Assert-Contains -Text $physicalScript -Pattern '&\s+dotnet\s+\$assemblyPath[\s\S]*--gate-g0-clock-probe' -Description 'PowerShell harness launches the compiled .NET 8 watchdog probe'
Assert-NotContains -Text $physicalScript -Pattern 'Assembly\]::LoadFrom|Activator\]::CreateInstance|GetProperty\(' -Description 'PowerShell harness never reflection-loads the net8.0 watchdog assembly'
Assert-Contains -Text $physicalScript -Pattern '\[System\.IO\.Path\]::GetTempPath\(\)' -Description 'physical harness builds into an isolated temporary directory'
Assert-Contains -Text $physicalScript -Pattern '\[Guid\]::NewGuid\(\)\.ToString\(''N''\)' -Description 'each physical probe build gets a unique output directory'
Assert-Contains -Text $physicalScript -Pattern 'VictusFanControl\.Watchdog\.csproj[\s\S]*-o\s+\$probeBuildRoot' -Description 'physical harness builds the watchdog project directly into isolated output'
Assert-NotContains -Text $physicalScript -Pattern 'dotnet\s+build\s+\.\\VictusFanControl\.sln' -Description 'physical harness does not rebuild the default solution output that a stale shell may lock'
Assert-Contains -Text $physicalScript -Pattern 'Remove-Item\s+\$probeBuildRoot\s+-Recurse\s+-Force' -Description 'isolated probe output is cleaned after the child process exits'

Write-Host ''
Write-Host 'Gate G0 watchdog clock invariant self-test: PASS' -ForegroundColor Green
exit 0
