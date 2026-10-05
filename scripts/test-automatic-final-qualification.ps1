param(
    [int]$TimeoutMinutes = 15,
    [switch]$WithPerformanceLimits
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
$journalPath = Join-Path $env:ProgramData 'VictusFanControl\WmiFanGui\lease.json'
$legacyJournalPath = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'
$experimentJournalPath = Join-Path $env:ProgramData 'VictusFanControl\WmiFanExperiment\lease.json'

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot = Join-Path $repoRoot ("logs\automatic-final-normal_{0}" -f $stamp)
$readyPath = Join-Path $evidenceRoot 'automatic-final.ready.json'
$eventsPath = Join-Path $evidenceRoot 'automatic-final.events.jsonl'
$resultPath = Join-Path $evidenceRoot 'automatic-final.result.json'
$summaryPath = Join-Path $evidenceRoot 'automatic-final.harness-summary.json'
$applicationLogDirectory = Join-Path $env:LOCALAPPDATA 'VictusFanControl\logs'

$gui = $null
$guiStartedUtc = $null
$guiStartUtcTicks = $null
$head = $null
$pass = $false
$failure = $null
$zipPath = "$evidenceRoot.zip"
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

function Export-QualificationEvidence {
    if (Test-Path -LiteralPath $readyPath) {
        $r = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
        if ($r.fanGuardianReportPath -and (Test-Path -LiteralPath $r.fanGuardianReportPath)) {
            Copy-Item -LiteralPath $r.fanGuardianReportPath -Destination (Join-Path $evidenceRoot 'fan-guardian-report.json') -Force
        }
    }
    if ($WithPerformanceLimits -and $gui) {
        $sessions = Join-Path $env:LOCALAPPDATA 'VictusFanControl\Performance\gui'
        if (Test-Path -LiteralPath $sessions) {
            foreach ($reportFile in @(Get-ChildItem -LiteralPath $sessions -Recurse -Filter 'guardian-report.json')) {
                $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
                if ($report.OwnerPid -eq $gui.Id -and
                    ([datetime]$reportFile.LastWriteTimeUtc) -ge ([datetime]$guiStartedUtc)) {
                    $destination = Join-Path $evidenceRoot ('performance-' + $reportFile.Directory.Name)
                    New-Item -ItemType Directory -Path $destination -Force | Out-Null
                    Copy-Item -LiteralPath $reportFile.FullName -Destination $destination
                    Copy-Item -LiteralPath (Join-Path $reportFile.Directory.FullName 'configuration.json') -Destination $destination
                }
            }
        }
    }
    # Preserve the exception log too: a dispatched operation can fail before
    # returning a decision, so decision counters alone do not describe it.
    $applicationLog = Join-Path $applicationLogDirectory ('events-{0}.log' -f (Get-Date -Format 'yyyy-MM-dd'))
    if (Test-Path -LiteralPath $applicationLog) {
        try {
            Copy-Item -LiteralPath $applicationLog -Destination (Join-Path $evidenceRoot 'application-events.log') -Force -ErrorAction Stop
        } catch {
            Write-Warning ('Could not capture application log: ' + $_.Exception.Message)
        }
    }

    $script:zipPath = "$evidenceRoot.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $evidenceRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $script:zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ASCII -Value ("{0}  {1}" -f $zipSha256, (Split-Path -Leaf $zipPath))
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
        gate = 'HP-8C40-AUTOMATIC-WMI-NORMAL-HARNESS'
        result = $Result
        failure = $Failure
        timestampUtc = (Get-Date).ToUniversalTime().ToString('O')
        sourceHead = $head
        branch = $expectedBranch
        performanceLimitsRequired = [bool]$WithPerformanceLimits
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

    foreach ($pending in @($journalPath, $legacyJournalPath, $experimentJournalPath)) {
        if (Test-Path -LiteralPath $pending) { throw "Pending fan lease blocks WMI qualification: $pending" }
    }

    foreach ($required in @(
        (Join-Path $modulesDir 'IntelMSR.bin')
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
    $guiArguments = @(
        '--8c40-automatic-final-qualification',
        '--8c40-automatic-test-token', $token,
        '--8c40-automatic-marker-root', $evidenceRoot,
        '--modules-dir', $modulesDir
    )
    if ($WithPerformanceLimits) { $guiArguments += '--automatic-performance-limits' }
    $gui = Start-Process -FilePath $appExe -ArgumentList $guiArguments -PassThru

    $guiStartedUtc = $gui.StartTime.ToUniversalTime()
    $guiStartUtcTicks = $guiStartedUtc.Ticks

    Wait-ForFile $readyPath 120 'AUTOMATIC FINAL READY'

    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.result -ne 'READY' -or
        $ready.targetProfileId -ne $targetProfile -or
        -not $ready.backendCanWrite -or
        $ready.authority -ne 'Firmware' -or
        $ready.normalUserAutomaticAuthorized -or
        -not $ready.dedicatedAutomaticAuthorized -or
        $ready.manualAuthorized -or
        $ready.journalPresent -or -not $ready.directEcProhibited -or
        $ready.gate -ne 'HP-8C40-AUTOMATIC-WMI-NORMAL') {
        throw 'READY marker did not prove the expected isolated qualification boundary.'
    }

    Write-Host ''
    Write-Host 'READY. Do these actions in the GUI:' -ForegroundColor Green
    if ($WithPerformanceLimits) {
        Write-Host '  0) In Rendimiento, save your CPU limits; keep CPU and GPU selected; click Aplicar.'
        Write-Host '     Wait for CPU Active and GPU ActiveUnverified. Do this BEFORE Automatic.'
    }
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

    if ($WithPerformanceLimits) {
        $performanceRoot = Join-Path $env:LOCALAPPDATA 'VictusFanControl\Performance'
        $domainRoot = Join-Path $performanceRoot $targetProfile
        foreach ($journal in @('cpu-power-session.json', 'gpu-clock-session.json')) {
            if (Test-Path -LiteralPath (Join-Path $domainRoot $journal)) { throw "Performance journal remains: $journal" }
        }
        $bound = @(Get-Content -LiteralPath $eventsPath | ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object { $_.kind -eq 'performance-session-bound' }) | Select-Object -Last 1
        if (-not $bound.required -or -not $bound.status.sessionEnabled -or
            $bound.status.cpuState -ne 'Active' -or $bound.status.gpuState -ne 'ActiveUnverified') {
            throw 'Performance session was not bound to the Automatic run.'
        }
        $guardianReportPath = [IO.Path]::GetFullPath([string]$bound.guardianReportPath)
        $sessionRoot = [IO.Path]::GetFullPath((Join-Path $performanceRoot 'gui')) + [IO.Path]::DirectorySeparatorChar
        if (-not $guardianReportPath.StartsWith($sessionRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $guardianReportPath)) { throw 'Matching performance release report is missing.' }
        $guardian = Get-Content -LiteralPath $guardianReportPath -Raw | ConvertFrom-Json
        if ($guardian.OwnerPid -ne $gui.Id -or $guardian.OwnerStartUtcTicks -ne $guiStartUtcTicks -or
            $guardian.ExitReason -ne 'CLIENT_SHUTDOWN' -or $guardian.FinalPhase -ne 'Stopped' -or
            $guardian.CpuDomainState -ne 'Disabled' -or $guardian.GpuDomainState -ne 'Disabled' -or
            $guardian.SourceRuntimeActive -or $guardian.Failure -or $guardian.SourceFailure -or
            $guardian.CpuHardwareWriteAttempts -lt 2 -or $guardian.GpuHardwareWriteAttempts -lt 2) {
            throw 'Performance Guardian did not prove normal CPU/GPU release for this GUI process.'
        }
    }
    if (-not $result.directEcProhibited -or $result.deniedEcAccesses -ne 0 -or
        -not $result.releaseRequestAccepted -or -not $result.legacyDefaultRequestAccepted -or
        -not $result.guardianLeaseRetired -or $result.independentFirmwareOwnershipVerified) {
        throw 'WMI-only result lacks truthful release evidence or contains EC access attempts.'
    }
    $fanReportPath = [IO.Path]::GetFullPath([string]$ready.fanGuardianReportPath)
    $fanRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'VictusFanControl\FanWmi\gui')) + [IO.Path]::DirectorySeparatorChar
    if (-not $fanReportPath.StartsWith($fanRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $fanReportPath)) { throw 'Matching WMI fan guardian report missing.' }
    $fanReport = Get-Content -LiteralPath $fanReportPath -Raw | ConvertFrom-Json
    if ($fanReport.OwnerPid -ne $gui.Id -or $fanReport.OwnerStartUtcTicks -ne $guiStartUtcTicks -or
        $fanReport.ExitReason -ne 'CLIENT_RELEASE' -or $fanReport.Failure -or
        -not $fanReport.ReleaseRequestAccepted -or -not $fanReport.LegacyDefaultRequestAccepted -or
        -not $fanReport.GuardianLeaseRetired -or -not $fanReport.DirectEcProhibited -or
        $fanReport.IndependentFirmwareOwnershipVerified) { throw 'WMI fan guardian release report failed validation.' }

    $pass = $true
    Write-Summary 'PASS' ''
    Export-QualificationEvidence

    Write-Host ''
    Write-Host 'AUTOMATIC WMI NORMAL PATH: PASS (requests accepted; independent firmware ownership unverified)' -ForegroundColor Green
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
        # GUI FAIL_CLOSED queues normal fan/Guardian release. Wait for that
        # cleanup before collecting reports; never terminate either process.
        if ($gui -and (Test-Path -LiteralPath $resultPath)) {
            $gui.Refresh()
            if (-not $gui.HasExited) {
                $closedNormally = $gui.WaitForExit(30000)
                if (-not $closedNormally) {
                    Write-Warning 'Normal GUI cleanup is still pending; release evidence may be incomplete.'
                }
            }
        }
        Write-Summary 'FAIL_CLOSED' $failure
        Export-QualificationEvidence
        Write-Host "Evidence: $evidenceRoot"
        Write-Host "ZIP:      $zipPath"
        Write-Host "SHA256:   $zipSha256"
    } catch {
        Write-Warning ('Could not package failure evidence: ' + $_.Exception.Message)
    }

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
