param(
    [ValidateRange(90, 300)]
    [int]$FailsafeDelaySeconds = 120
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogGateB'
$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$ready = Join-Path $root 'suspend-custom.ready'
$armResult = Join-Path $root 'suspend-custom.result'
$resultPath = Join-Path $env:ProgramData 'VictusFanControl\Watchdog\state\gate-b.result.json'
$serviceLog = Join-Path $env:ProgramData ("VictusFanControl\Watchdog\logs\watchdog-gate-b-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$failsafeLog = Join-Path $root 'watchdog-gate-b-failsafe.log'
$failsafeScript = Join-Path $PSScriptRoot 'watchdog-gate-b-failsafe.ps1'

$app = Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'

$proc = $null
$failsafe = $null
$readyReached = $false
$servicePassed = $false
$failure = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This hardware gate must be run from an elevated PowerShell.'
    }
}

function Read-EcState {
    $output = (& dotnet $cli --probe-88f8-ec-state 2>&1 | Out-String)
    $line = ($output -split "[\r\n]+" |
        Where-Object { $_ -match '^level CPU=' } |
        Select-Object -Last 1)

    if (-not $line) {
        throw "Could not parse EC state. Raw output: $output"
    }

    $match = [regex]::Match(
        $line,
        'level CPU=(\d+) GPU=(\d+).*manual=0x([0-9A-Fa-f]{2}) countdown=(\d+).*RPM CPU=(\d+) GPU=(\d+)')

    if (-not $match.Success) {
        throw "Could not parse EC state line: $line"
    }

    [pscustomobject]@{
        Cpu = [int]$match.Groups[1].Value
        Gpu = [int]$match.Groups[2].Value
        Manual = $match.Groups[3].Value.ToUpperInvariant()
        Countdown = [int]$match.Groups[4].Value
        CpuRpm = [int]$match.Groups[5].Value
        GpuRpm = [int]$match.Groups[6].Value
        Raw = $line
    }
}

function Restore-Owned-TestStateIfNeeded {
    $state = Read-EcState
    Write-Host "Cleanup EC state: $($state.Raw)"

    if ($state.Cpu -eq 255 -and $state.Gpu -eq 255) {
        return $true
    }

    if ($state.Cpu -ne 30 -or $state.Gpu -ne 30) {
        Write-Warning "Cleanup REFUSED: EC has unexpected setpoint $($state.Cpu)/$($state.Gpu); not clearing ambiguous external ownership."
        return $false
    }

    Write-Host 'Cleanup takeover: test-owned 30/30 remains; invoking validated HP-auto restore...' -ForegroundColor Yellow
    & dotnet $cli --restore-hp-auto
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Cleanup restore returned exit code $LASTEXITCODE."
        return $false
    }

    $after = Read-EcState
    Write-Host "Cleanup verification: $($after.Raw)"
    return ($after.Cpu -eq 255 -and $after.Gpu -eq 255)
}

Assert-Administrator

Write-Host 'VictusFanControl - WATCHDOG GATE B (SERVICE-ONLY EMERGENCY RESTORE)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test intentionally creates the already-validated Custom 30/30 state,' -ForegroundColor Yellow
Write-Host 'force-kills only VictusFanControl.App, then requires the LocalSystem service' -ForegroundColor Yellow
Write-Host 'to execute FF,FF -> LegacyDefault and verify EC FF/FF by itself.' -ForegroundColor Yellow
Write-Host ''
Write-Host "An independent ownership-safe delayed fallback is armed BEFORE 30/30."
Write-Host "Fallback delay: $FailsafeDelaySeconds seconds."
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and the normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    $running = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($running) {
        Write-Error "Refusing Gate B while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: build + synthetic regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-b-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: read-only live hardware preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$baseline = Read-EcState
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    Write-Error "Gate B requires firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu)."
    exit 2
}

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Gate B will arm 30/30, kill only the VFC GUI, then start the restore-only service.' -ForegroundColor Yellow
$confirm = Read-Host 'Type GATEB30 to continue'
if ($confirm -cne 'GATEB30') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Step 3: install Gate B service in STOPPED state...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-gate-b.ps1')

New-Item -ItemType Directory -Force -Path $root | Out-Null
Remove-Item $ready, $armResult, $resultPath, $failsafeLog -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Step 4: arm independent ownership-safe delayed fallback BEFORE any 30/30 write...' -ForegroundColor Cyan
$failsafe = Start-Process powershell.exe -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $failsafeScript,
    '-Cli', $cli,
    '-RepoRoot', $repoRoot,
    '-LogPath', $failsafeLog,
    '-DelaySeconds', $FailsafeDelaySeconds
) -WindowStyle Hidden -PassThru

Write-Host "Fallback PID: $($failsafe.Id)"

try {
    Write-Host ''
    Write-Host 'Step 5: arm validated production coordinator/backend path at 30/30...' -ForegroundColor Cyan
    $proc = Start-Process -FilePath $app -ArgumentList @(
        '--suspend-custom-test',
        '--suspend-test-token',
        '88F8-SUSPEND30'
    ) -PassThru

    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $ready)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $armResult) {
                Get-Content $armResult -Raw
            } else {
                'no result marker'
            }

            throw "VictusFanControl.App exited before READY. ExitCode=$($proc.ExitCode). $detail"
        }

        if ((Get-Date) -gt $deadline) {
            throw 'Timed out waiting for validated Custom 30/30 READY.'
        }

        Start-Sleep -Milliseconds 250
    }

    $readyReached = $true
    Write-Host 'Validated READY marker:' -ForegroundColor Green
    Get-Content $ready

    $armedState = Read-EcState
    Write-Host "Independent EC verification before kill: $($armedState.Raw)"

    if ($armedState.Cpu -ne 30 -or $armedState.Gpu -ne 30) {
        throw "READY marker exists but EC is not 30/30: $($armedState.Cpu)/$($armedState.Gpu)."
    }

    Write-Host ''
    Write-Host "Step 6: FORCE-KILL VictusFanControl.App PID $($proc.Id)..." -ForegroundColor Yellow
    Stop-Process -Id $proc.Id -Force
    $proc.WaitForExit()
    Write-Host 'VFC GUI is terminated; its managed restore cannot execute.' -ForegroundColor Yellow

    $orphaned = Read-EcState
    Write-Host "Orphaned state before service start: $($orphaned.Raw)"

    if ($orphaned.Cpu -ne 30 -or $orphaned.Gpu -ne 30) {
        throw "Gate B requires orphaned 30/30 immediately before service start; read $($orphaned.Cpu)/$($orphaned.Gpu)."
    }

    Write-Host ''
    Write-Host 'Step 7: start LocalSystem Gate B one-shot restore service...' -ForegroundColor Cyan

    Remove-Item $resultPath -Force -ErrorAction SilentlyContinue

    try {
        Start-Service -Name $serviceName
    }
    catch {
        Write-Warning "Start-Service reported: $($_.Exception.Message)"
    }

    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $resultPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }

    if (-not (Test-Path $resultPath)) {
        throw 'Timed out waiting for Gate B service result.'
    }

    $result = Get-Content $resultPath -Raw | ConvertFrom-Json

    Write-Host ''
    Write-Host 'Gate B service result:' -ForegroundColor Cyan
    Write-Host "Success              : $($result.Success)"
    Write-Host "Session              : $($result.SessionId)"
    Write-Host "Account              : $($result.AccountName)"
    Write-Host "Before setpoint      : $($result.BeforeCpuSetpoint)/$($result.BeforeGpuSetpoint)"
    Write-Host "Restore call success : $($result.RestoreCallSucceeded)"
    Write-Host "Verified FF/FF       : $($result.VerifiedFfFf)"
    Write-Host "After setpoint       : $($result.AfterCpuSetpoint)/$($result.AfterGpuSetpoint)"
    Write-Host "Elapsed              : $([math]::Round([double]$result.ElapsedMilliseconds,1)) ms"
    Write-Host "Failure              : $($result.Failure)"

    if (-not $result.Success) {
        throw "Gate B service classified restore as failure: $($result.Failure)"
    }

    if ([int]$result.SessionId -ne 0 -or
        $result.AccountName -notmatch 'SYSTEM$' -or
        [int]$result.BeforeCpuSetpoint -ne 30 -or
        [int]$result.BeforeGpuSetpoint -ne 30 -or
        -not $result.RestoreCallSucceeded -or
        -not $result.VerifiedFfFf -or
        [int]$result.AfterCpuSetpoint -ne 255 -or
        [int]$result.AfterGpuSetpoint -ne 255) {
        throw 'Gate B result did not satisfy the strict service-only restore contract.'
    }

    $final = Read-EcState
    Write-Host "Independent final EC verification: $($final.Raw)"

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        throw "Service reported PASS but independent EC verification is not FF/FF: $($final.Cpu)/$($final.Gpu)."
    }

    $servicePassed = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($proc -and -not $proc.HasExited) {
        if ($readyReached) {
            Write-Warning 'Gate B cleanup: VFC reached READY but is still alive; force-killing exact test PID before cleanup.'
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            try { $proc.WaitForExit(5000) | Out-Null } catch {}
        }
        else {
            try {
                $proc.CloseMainWindow() | Out-Null
                $proc.WaitForExit(5000) | Out-Null
            } catch {}
        }
    }

    $firmwareSafe = $false
    try {
        $firmwareSafe = Restore-Owned-TestStateIfNeeded
    }
    catch {
        Write-Warning "Gate B immediate cleanup encountered: $($_.Exception.Message)"
    }

    if ($firmwareSafe -and $failsafe -and -not $failsafe.HasExited) {
        Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
        Write-Host 'Delayed fallback cancelled only after FF/FF was independently verified.' -ForegroundColor Green
    }
    elseif (-not $firmwareSafe -and $failsafe) {
        Write-Warning "FF/FF could not be proven. Ownership-safe fallback PID $($failsafe.Id) remains armed."
    }
}

if (-not $servicePassed) {
    Write-Host ''
    Write-Host 'Gate B did NOT pass. Hardware cleanup was attempted/verified separately.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent Gate B service log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 40
    }

    exit 91
}

Write-Host ''
Write-Host 'Step 8: recent Gate B service log...' -ForegroundColor Cyan
if (Test-Path $serviceLog) {
    Get-Content $serviceLog | Select-Object -Last 40
}

Write-Host ''
Write-Host 'Check the undervolt value in OMEN Gaming Hub again.' -ForegroundColor Yellow
$post = Read-Host 'Type SAME if it is unchanged, or CHANGED if it changed'

if ($post -cne 'SAME') {
    if ($post -ceq 'CHANGED') {
        Write-Warning 'Undervolt preservation FAILED/CHANGED.'
        exit 92
    }

    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 93
}

Write-Host ''
Write-Host 'PASS: Gate B service independently restored orphaned 30/30 to HP firmware authority.' -ForegroundColor Green
Write-Host 'Verified path: LocalSystem Session 0 -> FF,FF -> LegacyDefault -> EC FF/FF.' -ForegroundColor Green
Write-Host 'OMEN Gaming Hub undervolt: SAME (user-confirmed).' -ForegroundColor Green
exit 0
