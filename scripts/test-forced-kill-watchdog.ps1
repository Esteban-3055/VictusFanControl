$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - FORCED PROCESS KILL / EC WATCHDOG GATE' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test intentionally kills VictusFanControl.App while it owns 30/30.' -ForegroundColor Yellow
Write-Host 'An independent delayed HP-auto restore is armed BEFORE the kill.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and the normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    $running = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($running) {
        Write-Error "Refusing test while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: building with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: synthetic safety/coordinator/backend regressions...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 3: read-only live hardware preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-88f8-ec-state
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'The next confirmation arms 30/30 and FORCE-KILLS ONLY the VFC GUI process.' -ForegroundColor Yellow
$confirm = Read-Host 'Type KILL30 to continue'
if ($confirm -cne 'KILL30') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

$repoRoot = (Get-Location).Path
$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$ready = Join-Path $root 'suspend-custom.ready'
$result = Join-Path $root 'suspend-custom.result'
$failsafeLog = Join-Path $root 'forced-kill-failsafe.log'
$failsafeScriptPath = Join-Path $root 'forced-kill-failsafe.ps1'
$app = (Resolve-Path '.\src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe').Path
$cli = (Resolve-Path '.\src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll').Path

New-Item -ItemType Directory -Path $root -Force | Out-Null
Remove-Item $ready -Force -ErrorAction SilentlyContinue
Remove-Item $result -Force -ErrorAction SilentlyContinue
Remove-Item $failsafeLog -Force -ErrorAction SilentlyContinue
Remove-Item $failsafeScriptPath -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Step 4: launching the already-validated 30/30 arming path...' -ForegroundColor Cyan
$proc = Start-Process -FilePath $app -ArgumentList @(
    '--suspend-custom-test',
    '--suspend-test-token',
    '88F8-SUSPEND30'
) -PassThru

$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $ready)) {
    if ($proc.HasExited) {
        Write-Error "VictusFanControl.App exited before 30/30 armed. ExitCode=$($proc.ExitCode)"
        exit 72
    }

    if ((Get-Date) -gt $deadline) {
        try { $proc.CloseMainWindow() | Out-Null } catch {}
        Write-Error 'Timed out waiting for validated 30/30 READY state.'
        exit 73
    }

    Start-Sleep -Milliseconds 250
}

Write-Host 'GUI reports validated READY:' -ForegroundColor Green
Get-Content $ready

Write-Host ''
Write-Host 'Step 5: arming independent 300-second emergency restore...' -ForegroundColor Cyan

$escapedRepo = $repoRoot.Replace("'", "''")
$escapedCli = $cli.Replace("'", "''")
$escapedLog = $failsafeLog.Replace("'", "''")
$failsafeContents = @(
    'Start-Sleep -Seconds 300',
    "Set-Location -LiteralPath '$escapedRepo'",
    "& dotnet '$escapedCli' --restore-hp-auto *>&1 | Out-File -FilePath '$escapedLog' -Encoding utf8"
)
Set-Content -Path $failsafeScriptPath -Value $failsafeContents -Encoding UTF8

$failsafe = Start-Process powershell.exe -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $failsafeScriptPath
) -WindowStyle Hidden -PassThru

Write-Host "Emergency restore PID: $($failsafe.Id)"
Write-Host ''
Write-Host "Step 6: FORCE-KILLING VictusFanControl.App PID $($proc.Id)..." -ForegroundColor Yellow
Stop-Process -Id $proc.Id -Force
$proc.WaitForExit()
Write-Host 'VictusFanControl.App is confirmed terminated. Managed cleanup could not run.' -ForegroundColor Yellow

function Read-EcState {
    $output = (& dotnet $cli --probe-88f8-ec-state 2>&1 | Out-String)
    $line = ($output -split "[\r\n]+" |
        Where-Object { $_ -match '^level CPU=' } |
        Select-Object -Last 1)

    if (-not $line) {
        throw "Could not parse EC state. Raw output: $output"
    }

    $pattern = 'level CPU=(\d+) GPU=(\d+).*manual=0x([0-9A-Fa-f]{2}) countdown=(\d+).*RPM CPU=(\d+) GPU=(\d+)'
    $m = [regex]::Match($line, $pattern)
    if (-not $m.Success) {
        throw "Could not parse EC state line: $line"
    }

    [pscustomobject]@{
        Cpu = [int]$m.Groups[1].Value
        Gpu = [int]$m.Groups[2].Value
        Manual = $m.Groups[3].Value.ToUpperInvariant()
        Countdown = [int]$m.Groups[4].Value
        CpuRpm = [int]$m.Groups[5].Value
        GpuRpm = [int]$m.Groups[6].Value
        Raw = $line
    }
}

Write-Host ''
Write-Host 'Step 7: observing EC setpoints + manual/countdown read-only...' -ForegroundColor Cyan

$started = [System.Diagnostics.Stopwatch]::StartNew()
$hardMaxSeconds = 280
$zeroGraceSeconds = 8
$lastCountdown = $null
$refreshEvents = 0
$zeroSeenAt = $null
$naturalRecovery = $false
$classification = 'UNRESOLVED'

while ($started.Elapsed.TotalSeconds -lt $hardMaxSeconds) {
    try {
        $state = Read-EcState
    } catch {
        Write-Warning "EC read failed at T+$([math]::Round($started.Elapsed.TotalSeconds,1))s: $($_.Exception.Message)"
        Start-Sleep -Seconds 1
        continue
    }

    $elapsed = $started.Elapsed.TotalSeconds
    Write-Host ("T+{0,6:0.0}s  set={1}/{2}  manual=0x{3}  countdown={4,3}s  RPM={5}/{6}" -f
        $elapsed, $state.Cpu, $state.Gpu, $state.Manual,
        $state.Countdown, $state.CpuRpm, $state.GpuRpm)

    if ($state.Cpu -eq 255 -and $state.Gpu -eq 255) {
        $naturalRecovery = $true
        $classification = 'NATURAL_FF_FF_RECOVERY'
        break
    }

    if ($state.Cpu -ne 30 -or $state.Gpu -ne 30) {
        $classification = "EXTERNAL_SETPOINT_CHANGE_$($state.Cpu)_$($state.Gpu)"
        break
    }

    if ($null -ne $lastCountdown -and $state.Countdown -ge ($lastCountdown + 4)) {
        $refreshEvents++
        Write-Host "  -> countdown refresh/increase observed ($lastCountdown -> $($state.Countdown))" -ForegroundColor Yellow

        if ($elapsed -ge 30) {
            $classification = 'EXTERNAL_COUNTDOWN_REFRESH_WITH_SETPOINT_30_30'
            break
        }
    }

    if ($state.Countdown -eq 0) {
        if ($null -eq $zeroSeenAt) {
            $zeroSeenAt = $elapsed
            Write-Host '  -> countdown reached zero; watching a short grace interval for FF/FF.' -ForegroundColor Yellow
        } elseif (($elapsed - $zeroSeenAt) -ge $zeroGraceSeconds) {
            $classification = 'COUNTDOWN_ZERO_WITH_SETPOINT_STILL_30_30'
            break
        }
    } else {
        $zeroSeenAt = $null
    }

    $lastCountdown = $state.Countdown
    Start-Sleep -Seconds 1
}

if ($classification -eq 'UNRESOLVED') {
    $classification = 'HARD_OBSERVATION_TIMEOUT_WITH_SETPOINT_30_30'
}

Write-Host ''
Write-Host "Observation classification: $classification" -ForegroundColor Cyan
Write-Host "Elapsed observation: $([math]::Round($started.Elapsed.TotalSeconds,1)) s"
Write-Host "Countdown upward refresh events: $refreshEvents"

if (-not $naturalRecovery) {
    Write-Host ''
    Write-Host 'Natural FF/FF recovery was not observed. Running explicit HP-auto restore NOW...' -ForegroundColor Yellow
    & dotnet $cli --restore-hp-auto
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Explicit HP-auto restore failed with exit code $LASTEXITCODE. The independent failsafe remains armed."
        exit 74
    }
}

Write-Host ''
Write-Host 'Step 8: final EC verification...' -ForegroundColor Cyan
$final = Read-EcState
Write-Host $final.Raw

if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
    Write-Error "Final EC setpoints are not FF/FF. Independent failsafe PID $($failsafe.Id) remains armed."
    exit 75
}

if (-not $failsafe.HasExited) {
    Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
}
Write-Host 'Independent delayed failsafe cancelled because FF/FF is verified.' -ForegroundColor Green

Write-Host ''
Write-Host 'Check the undervolt value in OMEN Gaming Hub again.' -ForegroundColor Yellow
$post = Read-Host 'Type SAME if it is unchanged, or CHANGED if it changed'

if ($post -ceq 'SAME') {
    Write-Host 'Undervolt preservation: CONFIRMED by user.' -ForegroundColor Green
} elseif ($post -ceq 'CHANGED') {
    Write-Warning 'Undervolt preservation: FAILED/CHANGED.'
    exit 76
} else {
    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 77
}

Write-Host ''
if ($naturalRecovery) {
    Write-Host 'PASS: forced-kill recovery reached EC FF/FF without VictusFanControl managed cleanup.' -ForegroundColor Green
} else {
    Write-Host 'CHARACTERIZATION COMPLETE: natural FF/FF release was not observed; explicit cleanup succeeded.' -ForegroundColor Yellow
}
Write-Host "Classification: $classification"
exit 0
