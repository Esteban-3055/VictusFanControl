param(
    [int]$TimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$expectedBranch = 'feature/victus-8c40-automatic-final-qualification'
$targetProfile = 'HP-8C40-9D0R1LA-F18'
$token = '8C40-AUTOMATIC-FINAL'
$modulesDir = Join-Path $repoRoot 'modules'
$appExe = Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$configPath = Join-Path $env:LOCALAPPDATA 'VictusFanControl\fan-configuration.json'
$journalPath = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot = Join-Path $repoRoot ("logs\automatic-final-normal_{0}" -f $stamp)
$readyPath = Join-Path $evidenceRoot 'automatic-final.ready.json'
$eventsPath = Join-Path $evidenceRoot 'automatic-final.events.jsonl'
$resultPath = Join-Path $evidenceRoot 'automatic-final.result.json'
$summaryPath = Join-Path $evidenceRoot 'automatic-final.harness-summary.json'

$gui = $null
$head = $null
$pass = $false
$failure = $null
$zipPath = $null
$zipSha256 = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Final Automatic qualification must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch = (& git branch --show-current 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch) {
        throw "Qualification requires branch '$expectedBranch'; observed '$branch'."
    }

    $local = (& git rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream = (& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $local -notmatch '^[0-9a-f]{40}$' -or
        $local -cne $upstream) {
        throw "Qualification requires local HEAD == upstream HEAD. local=$local upstream=$upstream"
    }

    $status = (& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect git status.'
    }

    $blocking = @(
        $status -split "[\r\n]+" |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                -not $_.StartsWith('?? logs/', [StringComparison]::Ordinal)
            }
    )

    if ($blocking.Count -gt 0) {
        $blocking | ForEach-Object { Write-Host $_ }
        throw 'Qualification requires committed source/config state; only untracked logs evidence is allowed.'
    }

    return $local
}

function Assert-ExactTarget {
    $board = Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios = Get-CimInstance Win32_BIOS -ErrorAction Stop
    $cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
    $gpu = Get-CimInstance Win32_VideoController -ErrorAction Stop |
        Where-Object { ([string]$_.Name) -match 'RTX 4060.*Laptop' } |
        Select-Object -First 1

    $sku = ([string]$system.SystemSKUNumber).Trim()
    $skuBase = ($sku -split '#', 2)[0].Trim()
    $biosText = @(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    if (([string]$board.Manufacturer).Trim() -cne 'HP' -or
        ([string]$board.Product).Trim() -cne '8C40' -or
        ([string]$board.Version).Trim() -cne '63.43' -or
        ([string]$system.Manufacturer).Trim() -cne 'HP' -or
        ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
        $skuBase -cne '9D0R1LA' -or
        $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)' -or
        ([string]$cpu.Name) -notmatch 'i7-13700H' -or
        $null -eq $gpu) {
        throw 'Exact HP-8C40-9D0R1LA-F18 / i7-13700H / RTX 4060 Laptop fingerprint mismatch.'
    }
}

function Assert-PowerAndConflicts {
    Add-Type -AssemblyName System.Windows.Forms
    $power = [System.Windows.Forms.SystemInformation]::PowerStatus
    if ([string]$power.PowerLineStatus -cne 'Online') {
        throw "Qualification requires AC online; observed '$($power.PowerLineStatus)'."
    }

    $battery = if ($power.BatteryLifePercent -ge 0) {
        [math]::Round([double]$power.BatteryLifePercent * 100, 0)
    } else {
        $null
    }

    if ($null -eq $battery -or $battery -lt 20) {
        throw "Qualification requires readable battery >=20%; observed '$battery'."
    }

    $conflicts = @(
        Get-Process OmenMon, OmenMon-Reborn, VictusFanControl.App -ErrorAction SilentlyContinue
    )
    if ($conflicts.Count -gt 0) {
        throw (
            'Close existing fan-control applications first: ' +
            (($conflicts | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join ', ')
        )
    }
}

function Assert-QualificationConfiguration {
    if (-not (Test-Path -LiteralPath $configPath)) {
        throw "Missing persisted fan configuration: $configPath"
    }

    $c = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $t = $c.tuning

    $errors = @()
    if ([int]$t.cpuTemperatureSource -ne 3) { $errors += 'CPU source must be HottestPerformanceCoresAverage (3).' }
    if ([int]$t.hottestPerformanceCoreCount -ne 3) { $errors += 'Hottest P-Core count must be 3.' }
    if ([int]$t.minimumLevel -ne 30 -or [int]$t.maximumLevel -ne 50) { $errors += 'Fan envelope must be 30..50.' }
    if ([int]$t.normalMaximumUpStepLevels -ne 1 -or [int]$t.maximumDownStepLevels -ne 1) { $errors += 'Normal fan step must be +1/-1.' }
    if ([int]$t.normalPollingDelayMilliseconds -ne 1000) { $errors += 'Normal polling delay must be 1000 ms.' }
    if ([bool]$t.rememberThermalDemand) { $errors += 'Thermal-demand memory must be disabled for A1.' }
    if (-not [bool]$t.adaptiveDescentEnabled) { $errors += 'Adaptive descent must be enabled.' }

    $expected = @{
        riseTimeConstantSeconds = 8
        increaseConfirmationSeconds = 3
        shortLoadFallTimeConstantSeconds = 6
        shortLoadDecreaseConfirmationSeconds = 4
        fallTimeConstantSeconds = 20
        decreaseConfirmationSeconds = 16
        sustainedLoadSeconds = 1200
        loadThresholdPercent = 50
        cpuLoadPowerThresholdW = 25
        gpuLoadPowerThresholdW = 40
        loadPauseToleranceSeconds = 30
        sustainedLoadCooldownSeconds = 120
        cpuThermalOverrideC = 85
        gpuThermalOverrideC = 78
    }

    foreach ($name in $expected.Keys) {
        $actual = [double]$t.$name
        if ([math]::Abs($actual - [double]$expected[$name]) -gt 0.0001) {
            $errors += "$name must be $($expected[$name]); observed $actual."
        }
    }

    if ($errors.Count -gt 0) {
        Write-Host ''
        Write-Host 'Persisted qualification profile is not ready:' -ForegroundColor Yellow
        $errors | ForEach-Object { Write-Host (" - " + $_) -ForegroundColor Yellow }
        throw 'Configure and save the qualification profile in the GUI before running this gate.'
    }
}

function Read-Setpoint {
    $raw = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Setpoint probe failed. Raw: $raw"
    }

    $line = $raw -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1

    $m = [regex]::Match([string]$line, '^setpoint CPU=(\d+) GPU=(\d+)$')
    if (-not $m.Success) {
        throw "Could not parse setpoint probe. Raw: $raw"
    }

    [pscustomobject]@{
        Cpu = [int]$m.Groups[1].Value
        Gpu = [int]$m.Groups[2].Value
        Raw = [string]$line
    }
}

function Assert-StableFirmware {
    $consecutive = 0
    $samples = @()

    for ($i = 1; $i -le 8; $i++) {
        $s = Read-Setpoint
        $samples += $s
        Write-Host ("Final firmware proof {0}/8: {1}" -f $i, $s.Raw)

        if ($s.Cpu -eq 255 -and $s.Gpu -eq 255) {
            $consecutive++
            if ($consecutive -ge 2) {
                return $samples
            }
        } else {
            $consecutive = 0
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Final independent proof did not observe two consecutive FF/FF setpoints.'
}

function Wait-ForFile([string]$Path, [int]$Seconds, [string]$Label) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }

        if ($gui) {
            $gui.Refresh()
            if ($gui.HasExited) {
                throw "GUI exited before $Label. ExitCode=$($gui.ExitCode)"
            }
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for $Label."
}

function Get-DecisionStats {
    if (-not (Test-Path -LiteralPath $eventsPath)) {
        return [pscustomobject]@{ Decisions = 0; Writes = 0; Holds = 0 }
    }

    $rows = @()
    foreach ($line in Get-Content -LiteralPath $eventsPath -ErrorAction SilentlyContinue) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $rows += ($line | ConvertFrom-Json) } catch {}
    }

    $decisions = @($rows | Where-Object { $_.kind -eq 'automatic-decision' })
    $writes = @(
        $decisions |
            Where-Object {
                $_.action -eq 'EnterCustomAndApply' -or
                $_.action -eq 'ApplyChangedLevel'
            }
    )

    [pscustomobject]@{
        Decisions = $decisions.Count
        Writes = $writes.Count
        Holds = @($decisions | Where-Object { $_.action -eq 'HoldCustom' }).Count
    }
}

function Write-Summary([string]$Result, [string]$Failure) {
    $stats = Get-DecisionStats
    [ordered]@{
        schemaVersion = 1
        gate = 'HP-8C40-AUTOMATIC-FINAL-NORMAL-HARNESS'
        result = $Result
        failure = $Failure
        timestampUtc = (Get-Date).ToUniversalTime().ToString('O')
        sourceHead = $head
        branch = $expectedBranch
        targetProfileId = $targetProfile
        guiPid = if ($gui) { $gui.Id } else { 0 }
        decisions = $stats.Decisions
        hardwareCommandDecisions = $stats.Writes
        holdDecisions = $stats.Holds
        readyPresent = Test-Path -LiteralPath $readyPath
        resultPresent = Test-Path -LiteralPath $resultPath
        journalPresent = Test-Path -LiteralPath $journalPath
        evidenceZip = $zipPath
        evidenceZipSha256 = $zipSha256
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

try {
    if ($TimeoutMinutes -lt 3 -or $TimeoutMinutes -gt 60) {
        throw 'TimeoutMinutes must be between 3 and 60.'
    }

    Assert-Administrator
    $head = Assert-RepositoryProvenance
    Assert-ExactTarget
    Assert-PowerAndConflicts
    Assert-QualificationConfiguration

    if (Test-Path -LiteralPath $journalPath) {
        throw "A durable fan watchdog journal already exists: $journalPath"
    }

    foreach ($required in @(
        (Join-Path $modulesDir 'IntelMSR.bin'),
        (Join-Path $modulesDir 'LpcACPIEC.bin')
    )) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Required PawnIO module missing: $required"
        }
    }

    Write-Host 'Building exact qualification HEAD...'
    & dotnet build .\VictusFanControl.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Release build failed.'
    }

    if (-not (Test-Path -LiteralPath $appExe)) {
        throw "GUI executable missing after build: $appExe"
    }
    if (-not (Test-Path -LiteralPath $cli)) {
        throw "CLI assembly missing after build: $cli"
    }

    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null

    Write-Host ''
    Write-Host 'Launching isolated final Automatic qualification...' -ForegroundColor Cyan
    $gui = Start-Process -FilePath $appExe -ArgumentList @(
        '--8c40-automatic-final-qualification',
        '--8c40-automatic-test-token', $token,
        '--8c40-automatic-marker-root', $evidenceRoot,
        '--modules-dir', $modulesDir
    ) -PassThru

    Wait-ForFile $readyPath 120 'AUTOMATIC FINAL READY'

    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.result -ne 'READY' -or
        $ready.targetProfileId -ne $targetProfile -or
        -not $ready.backendCanWrite -or
        $ready.authority -ne 'Firmware' -or
        $ready.normalUserAutomaticAuthorized -or
        -not $ready.dedicatedAutomaticAuthorized -or
        $ready.manualAuthorized -or
        $ready.journalPresent) {
        throw 'READY marker did not prove the expected isolated qualification boundary.'
    }

    Write-Host ''
    Write-Host 'READY. Do these actions in the GUI:' -ForegroundColor Green
    Write-Host '  1) Click Automatic ONCE.'
    Write-Host '  2) Run a representative workload long enough to cause at least one level change.'
    Write-Host '     The gate requires >=30 Automatic decisions and >=2 real fan command decisions.'
    Write-Host '  3) When you are satisfied with the normal behavior, click Firmware ONCE.'
    Write-Host 'Do not select Manual, edit settings, suspend the PC, disconnect AC, or close the GUI during this normal-path gate.'
    Write-Host ''

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $lastReport = [datetime]::MinValue

    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $resultPath) {
            break
        }

        $gui.Refresh()
        if ($gui.HasExited) {
            throw "GUI exited before a terminal result. ExitCode=$($gui.ExitCode)"
        }

        if (((Get-Date) - $lastReport).TotalSeconds -ge 10) {
            $stats = Get-DecisionStats
            Write-Host ("Progress: decisions={0}, fan commands={1}, holds={2}" -f
                $stats.Decisions, $stats.Writes, $stats.Holds)
            $lastReport = Get-Date
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not (Test-Path -LiteralPath $resultPath)) {
        throw "Timed out after $TimeoutMinutes minute(s) waiting for the real Firmware completion."
    }

    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.result -ne 'PASS') {
        throw "GUI qualification returned $($result.result): $($result.failure)"
    }

    $gui.WaitForExit(30000)
    $gui.Refresh()
    if (-not $gui.HasExited) {
        throw 'PASS marker was written but the qualification GUI did not exit within 30 seconds.'
    }

    if (Test-Path -LiteralPath $journalPath) {
        throw 'Durable watchdog journal remains after PASS.'
    }

    [void](Assert-StableFirmware)

    $pass = $true
    $zipPath = "$evidenceRoot.zip"
    Write-Summary 'PASS' ''

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $evidenceRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ASCII -Value ("{0}  {1}" -f $zipSha256, (Split-Path -Leaf $zipPath))

    Write-Host ''
    Write-Host 'AUTOMATIC FINAL NORMAL PATH: PASS' -ForegroundColor Green
    Write-Host "Evidence: $evidenceRoot"
    Write-Host "ZIP:      $zipPath"
    Write-Host "SHA256:   $zipSha256"
}
catch {
    $failure = $_.Exception.Message
    Write-Host ''
    Write-Host ("AUTOMATIC FINAL NORMAL PATH: FAIL_CLOSED - " + $failure) -ForegroundColor Red

    try {
        if (-not (Test-Path -LiteralPath $evidenceRoot)) {
            New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
        }
        Write-Summary 'FAIL_CLOSED' $failure
    } catch {}

    if ($gui) {
        try {
            $gui.Refresh()
            if (-not $gui.HasExited) {
                Write-Host 'GUI is still running. Use Firmware/Exit normally; do not kill the watchdog service.'
            }
        } catch {}
    }

    exit 1
}
