param(
    [ValidateRange(1, 3)]
    [int]$Cycles = 1,

    [ValidateRange(3, 30)]
    [int]$TimeoutMinutes = 15,

    [ValidateRange(30, 1800)]
    [int]$RecommendedSleepSeconds = 60
)

$ErrorActionPreference = 'Stop'

if ($RecommendedSleepSeconds -ge (($TimeoutMinutes * 60) - 30)) {
    throw 'RecommendedSleepSeconds must leave at least 30 seconds of margin before TimeoutMinutes expires.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$probeExe = Join-Path $repoRoot 'src\VictusFanControl.ModernStandbyProbe\bin\Release\net8.0-windows\VictusFanControl.ModernStandbyProbe.exe'
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$outDir = Join-Path $repoRoot ("logs\modern-standby-m0_{0}" -f $stamp)
$preflightPath = Join-Path $outDir 'preflight.txt'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This M0 observer must be run from an elevated PowerShell.'
    }
}

function Assert-Exact8C40Target {
    $board = Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios = Get-CimInstance Win32_BIOS -ErrorAction Stop

    $boardManufacturer = ([string]$board.Manufacturer).Trim()
    $boardProduct = ([string]$board.Product).Trim()
    $boardVersion = ([string]$board.Version).Trim()
    $systemManufacturer = ([string]$system.Manufacturer).Trim()
    $systemModel = ([string]$system.Model).Trim()
    $sku = ([string]$system.SystemSKUNumber).Trim()
    $skuBase = ($sku -split '#', 2)[0].Trim()
    $biosText = @(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    Write-Host ("Board               : {0} / {1} / {2}" -f $boardManufacturer, $boardProduct, $boardVersion)
    Write-Host ("System              : {0} / {1}" -f $systemManufacturer, $systemModel)
    Write-Host ("SKU                 : {0}" -f $sku)
    Write-Host ("BIOS                : {0}" -f $biosText)

    if ($boardManufacturer -cne 'HP' -or
        $boardProduct -cne '8C40' -or
        $boardVersion -cne '63.43') {
        throw 'M0 is restricted to the exact HP 8C40 / board version 63.43 target.'
    }

    if ($systemManufacturer -cne 'HP' -or
        $systemModel -cne 'Victus by HP Gaming Laptop 15-fa1xxx') {
        throw 'M0 system-product fingerprint does not match the qualified HP Victus 15-fa1xxx target.'
    }

    if ($skuBase -cne '9D0R1LA') {
        throw "M0 SKU base mismatch: expected 9D0R1LA, read '$skuBase'."
    }

    if ($biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)') {
        throw "M0 BIOS mismatch: expected F.18, read '$biosText'."
    }
}

function Wait-ForFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [int]$Seconds,

        [System.Diagnostics.Process]$Process
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while (-not (Test-Path $Path) -and
           (Get-Date) -lt $deadline) {
        if ($Process -and $Process.HasExited) {
            return $false
        }

        Start-Sleep -Milliseconds 200
    }

    return (Test-Path $Path)
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 MODERN STANDBY M0 OBSERVER' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Scope: read-only Modern Standby event/timing characterization.' -ForegroundColor Yellow
Write-Host 'This gate does NOT open PawnIO, EC, HP fan WMI, NVML, watchdog leases or the fan backend.' -ForegroundColor Yellow
Write-Host 'It does NOT call SetSuspendState and does NOT acquire ES_SYSTEM_REQUIRED.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "Refusing M0 while process '$name' is running. Close it and retry."
    }
}

Write-Host 'Step 1: exact-target read-only fingerprint...' -ForegroundColor Cyan
Assert-Exact8C40Target

Write-Host ''
Write-Host 'Step 2: build + M0 synthetic self-test...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet run --project .\src\VictusFanControl.ModernStandbyProbe -c Release --no-build -- --self-test
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path $probeExe)) {
    throw "M0 observer executable was not produced: $probeExe"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host ''
Write-Host 'Step 3: Windows power-capability/request baseline...' -ForegroundColor Cyan
"VictusFanControl HP 8C40 M0 preflight" | Set-Content -Path $preflightPath -Encoding UTF8
("Captured: " + (Get-Date).ToString('o')) | Add-Content -Path $preflightPath -Encoding UTF8

'' | Tee-Object -FilePath $preflightPath -Append
'===== powercfg /a =====' | Tee-Object -FilePath $preflightPath -Append
powercfg /a 2>&1 | Tee-Object -FilePath $preflightPath -Append

'' | Tee-Object -FilePath $preflightPath -Append
'===== powercfg /requests =====' | Tee-Object -FilePath $preflightPath -Append
powercfg /requests 2>&1 | Tee-Object -FilePath $preflightPath -Append

Write-Host ''
Write-Host 'M0 requires a real user-initiated Modern Standby transition.' -ForegroundColor Yellow
Write-Host 'Use Windows Start -> Power -> Sleep after the observer reports READY.' -ForegroundColor Yellow
Write-Host 'Do not use a benchmark/game during the capture.' -ForegroundColor Yellow
Write-Host 'The capture deliberately does not classify display-off alone as a Modern Standby PASS.' -ForegroundColor Yellow
Write-Host ''

$confirm = Read-Host 'Type 8C40-M0 to arm the read-only observer'
if ($confirm -cne '8C40-M0') {
    Write-Host 'Cancelled. No fan/hardware write was issued.'
    exit 1
}

for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    $reportPath = Join-Path $outDir ("cycle-{0}.json" -f $cycle)
    $readyPath = Join-Path $outDir ("cycle-{0}.ready.json" -f $cycle)

    Remove-Item $reportPath, $readyPath -Force -ErrorAction SilentlyContinue

    Write-Host ''
    Write-Host ("===== M0 CYCLE {0}/{1} =====" -f $cycle, $Cycles) -ForegroundColor Cyan

    $q = [char]34
    $arguments = @(
        '--output',
        "$q$reportPath$q",
        '--ready',
        "$q$readyPath$q",
        '--auto-exit-after-display-cycle',
        '--timeout-minutes',
        $TimeoutMinutes
    )

    $proc = Start-Process -FilePath $probeExe -ArgumentList $arguments -WindowStyle Hidden -PassThru

    if (-not (Wait-ForFile -Path $readyPath -Seconds 15 -Process $proc)) {
        if (-not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }

        $exitDetail = if ($proc.HasExited) {
            " ExitCode=$($proc.ExitCode)."
        } else {
            ''
        }

        throw "M0 observer did not publish READY within 15 seconds.$exitDetail"
    }

    $ready = Get-Content $readyPath -Raw | ConvertFrom-Json

    if (-not $ready.Ready -or -not $ready.ReadOnly) {
        throw 'M0 READY marker is not a valid read-only observer marker.'
    }

    Write-Host ("Observer PID        : {0}" -f $ready.ProcessId)
    Write-Host ("Observer Session    : {0}" -f $ready.SessionId)
    Write-Host ("READY detail        : {0}" -f $ready.Detail)
    Write-Host ''
    Write-Host ("Now select Windows Sleep. Keep the machine asleep for about {0} seconds before waking it manually." -f $RecommendedSleepSeconds) -ForegroundColor Yellow
    Write-Host 'The observer keeps transition events in memory and exits about 10 seconds after session display returns On so delayed resume notifications can also be captured.' -ForegroundColor Yellow
    [void](Read-Host 'Press Enter when you are ready to perform the Sleep transition')

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes).AddSeconds(30)

    while (-not $proc.HasExited -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }

    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        throw "M0 observer timed out after $TimeoutMinutes minute(s) without completing the display cycle."
    }

    if (-not (Test-Path $reportPath)) {
        throw "M0 observer exited without writing its report. ExitCode=$($proc.ExitCode)."
    }

    $report = Get-Content $reportPath -Raw | ConvertFrom-Json

    Write-Host ''
    Write-Host ("Observer exit        : {0} (code {1})" -f $report.ExitReason, $proc.ExitCode)
    Write-Host ("Display Off seen     : {0}" -f $report.SessionDisplayOffSeen)
    Write-Host ("Display On-after-Off : {0}" -f $report.SessionDisplayOnAfterOffSeen)
    Write-Host ("PBT_APMSUSPEND       : {0}" -f $report.SuspendSeen)
    Write-Host ("Resume automatic     : {0}" -f $report.ResumeAutomaticSeen)
    Write-Host ("Resume suspend       : {0}" -f $report.ResumeSuspendSeen)
    Write-Host ("Resume critical      : {0}" -f $report.ResumeCriticalSeen)
    Write-Host ("Dropped events       : {0}" -f $report.DroppedEventCount)

    if ($report.CycleTiming) {
        Write-Host ("Wall Off->On         : {0:N0} ms" -f [double]$report.CycleTiming.WallMilliseconds)

        if ($null -ne $report.CycleTiming.UnbiasedMilliseconds) {
            Write-Host ("Unbiased Off->On     : {0:N0} ms" -f [double]$report.CycleTiming.UnbiasedMilliseconds)
        } else {
            Write-Host 'Unbiased Off->On     : unavailable'
        }

        Write-Host ("TickCount Off->On    : {0:N0} ms" -f [double]$report.CycleTiming.TickCountMilliseconds)
        Write-Host ("QPC Off->On          : {0:N0} ms" -f [double]$report.CycleTiming.QueryPerformanceCounterMilliseconds)
    }

    Write-Host ''
    Write-Host 'Captured event timeline:' -ForegroundColor Cyan
    $report.Events |
        Select-Object Sequence, Utc, Source, Event, Detail |
        Format-Table -AutoSize -Wrap |
        Out-String -Width 260 |
        Write-Host

    if (-not $report.RegistrationSucceeded -or
        -not $report.SessionDisplayOffSeen -or
        -not $report.SessionDisplayOnAfterOffSeen) {
        throw "M0 cycle $cycle did not capture a complete registered session-display Off->On cycle."
    }

    if ($cycle -lt $Cycles) {
        $next = Read-Host 'Type NEXT to arm the next independent M0 cycle'
        if ($next -cne 'NEXT') {
            Write-Host 'Stopped after the completed capture(s). No hardware write was issued.'
            break
        }
    }
}

Write-Host ''
Write-Host 'Step 4: collect post-transition Windows diagnostics...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'collect-power-transition-diagnostics.ps1') -OutputDirectory $outDir

Write-Host ''
Write-Host 'M0 CAPTURE COMPLETE.' -ForegroundColor Green
Write-Host 'This is NOT yet a Modern Standby lifecycle PASS.' -ForegroundColor Yellow
Write-Host 'The JSON timeline plus SleepStudy/System Power/System Sleep reports must be reviewed before selecting the production lifecycle boundary.' -ForegroundColor Yellow
Write-Host ("Evidence directory: {0}" -f (Resolve-Path $outDir))
exit 0
