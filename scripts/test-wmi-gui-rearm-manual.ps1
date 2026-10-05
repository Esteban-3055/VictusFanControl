param(
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$expectedBranch = 'feature/victus-8c40-automatic-final-qualification'
$appExe = Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$modulesDir = Join-Path $repoRoot 'modules'
$appLogPath = Join-Path $env:LOCALAPPDATA ("VictusFanControl\logs\events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$sessionRoot = Join-Path $env:LOCALAPPDATA 'VictusFanControl\FanWmi\gui'
$guiLeasePath = Join-Path $env:ProgramData 'VictusFanControl\WmiFanGui\lease.json'
$legacyLeasePath = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'
$experimentLeasePath = Join-Path $env:ProgramData 'VictusFanControl\WmiFanExperiment\lease.json'

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot = Join-Path $repoRoot ("logs\wmi-rearm-manual_{0}" -f $stamp)
$summaryPath = Join-Path $evidenceRoot 'wmi-rearm-summary.json'
$appSegmentPath = Join-Path $evidenceRoot 'application-events.log'
$sessionAEvidence = Join-Path $evidenceRoot 'session-a'
$sessionBEvidence = Join-Path $evidenceRoot 'session-b'
$zipPath = "$evidenceRoot.zip"
$shaPath = "$zipPath.sha256"

$head = $null
$app = $null
$appPid = 0
$appStartTicks = 0L
$appBaselineLines = 0
$sessionA = $null
$sessionB = $null
$readyA = $null
$readyB = $null
$reportA = $null
$reportB = $null
$pass = $false
$failure = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'WMI rearm test must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch = (& git branch --show-current 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch) {
        throw "WMI rearm test requires branch '$expectedBranch'; observed '$branch'."
    }

    $local = (& git rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream = (& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $local -notmatch '^[0-9a-f]{40}$' -or
        $local -cne $upstream) {
        throw "WMI rearm test requires local HEAD == upstream HEAD. local=$local upstream=$upstream"
    }

    $status = (& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw 'WMI rearm test could not inspect git status.'
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
        throw 'WMI rearm test requires committed source/config state; only untracked logs evidence is allowed.'
    }

    return $local
}

function Assert-ExactTarget {
    $board = Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios = Get-CimInstance Win32_BIOS -ErrorAction Stop
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
        $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)') {
        throw 'WMI rearm test exact-target fingerprint mismatch.'
    }
}

function Assert-NoConflictingController {
    $conflicts = @(
        Get-Process OmenMon, OmenMon-Reborn, VictusFanControl.App -ErrorAction SilentlyContinue
    )
    if ($conflicts.Count -gt 0) {
        throw ("Close all existing OmenMon/OmenMon-Reborn/VictusFanControl.App processes before the test. Found: " +
            (($conflicts | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join ', '))
    }
}

function Assert-NoPendingLease {
    foreach ($path in @($guiLeasePath, $legacyLeasePath, $experimentLeasePath)) {
        if (Test-Path -LiteralPath $path) {
            throw "Pending fan lease blocks the test. Do not delete it manually: $path"
        }
    }
}

function Get-AppSegment {
    if (-not (Test-Path -LiteralPath $appLogPath -PathType Leaf)) {
        return @()
    }

    return @(
        Get-Content -LiteralPath $appLogPath |
            Select-Object -Skip $appBaselineLines
    )
}

function Wait-AppMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$StartIndex,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $segment = @(Get-AppSegment)
        if ($StartIndex -lt $segment.Count) {
            for ($i = $StartIndex; $i -lt $segment.Count; $i++) {
                if ([string]$segment[$i] -match $Pattern) {
                    return [pscustomobject]@{
                        Index = $i
                        Line = [string]$segment[$i]
                    }
                }
            }
        }

        if ($app) {
            $app.Refresh()
            if ($app.HasExited) {
                throw "GUI exited before $Label."
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for $Label."
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Wait-ReadySession {
    param(
        [Parameter(Mandatory = $true)][int]$OwnerPid,
        [Parameter(Mandatory = $true)][long]$OwnerStartTicks,
        [string[]]$Exclude = @()
    )

    $excludeFull = @($Exclude | ForEach-Object { [IO.Path]::GetFullPath($_) })
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $sessionRoot -PathType Container) {
            foreach ($dir in @(Get-ChildItem -LiteralPath $sessionRoot -Directory -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTimeUtc -Descending)) {
                $full = [IO.Path]::GetFullPath($dir.FullName)
                if ($excludeFull -contains $full) {
                    continue
                }

                $readyPath = Join-Path $dir.FullName 'ready.json'
                if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
                    continue
                }

                try {
                    $ready = Read-JsonFile $readyPath
                    if ([int]$ready.OwnerPid -eq $OwnerPid -and
                        [long]$ready.OwnerStartUtcTicks -eq $OwnerStartTicks -and
                        [bool]$ready.DirectEcProhibited) {
                        return [pscustomobject]@{
                            Directory = $dir.FullName
                            Ready = $ready
                        }
                    }
                }
                catch {
                }
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Timed out waiting for a new WMI guardian READY session bound to the GUI identity.'
}

function Wait-GuardianReport {
    param([Parameter(Mandatory = $true)][string]$SessionDirectory)

    $path = Join-Path $SessionDirectory 'guardian-report.json'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try {
                return Read-JsonFile $path
            }
            catch {
            }
        }
        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for guardian report: $path"
}

function Assert-GuardianReport {
    param(
        [Parameter(Mandatory = $true)]$Report,
        [Parameter(Mandatory = $true)][int]$OwnerPid,
        [Parameter(Mandatory = $true)][long]$OwnerStartTicks,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([int]$Report.SchemaVersion -ne 1 -or
        [string]$Report.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        [int]$Report.OwnerPid -ne $OwnerPid -or
        [long]$Report.OwnerStartUtcTicks -ne $OwnerStartTicks -or
        [string]$Report.ExitReason -cne 'CLIENT_RELEASE' -or
        -not [bool]$Report.ReleaseRequestAccepted -or
        -not [bool]$Report.LegacyDefaultRequestAccepted -or
        -not [bool]$Report.GuardianLeaseRetired -or
        [bool]$Report.IndependentFirmwareOwnershipVerified -or
        -not [bool]$Report.DirectEcProhibited -or
        $null -ne $Report.Failure) {
        throw "$Label guardian report failed release/identity checks."
    }
}

function Wait-LeaseAbsent {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path -LiteralPath $guiLeasePath)) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "WMI GUI lease was not retired: $guiLeasePath"
}

function Copy-SessionEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
}

function Assert-AppAudit {
    $segment = @(Get-AppSegment)
    $segment | Set-Content -LiteralPath $appSegmentPath -Encoding UTF8

    $manualModes = @($segment | Where-Object { ([string]$_) -match 'P13 mode request Manual: action=HoldFirmware; authorized=True; authority=Firmware;' })
    $manual30 = @($segment | Where-Object { ([string]$_) -match 'P13 manual request 30/30: action=EnterCustomAndApply; authorized=True; authority=Custom;' })
    $manual31 = @($segment | Where-Object { ([string]$_) -match 'P13 manual request 31/31: action=EnterCustomAndApply; authorized=True; authority=Custom;' })
    $firmwareModes = @($segment | Where-Object { ([string]$_) -match 'P13 mode request Firmware: action=RestoreFirmware; authorized=True; authority=Firmware;' })
    $automaticModes = @($segment | Where-Object { ([string]$_) -match 'P13 mode request Automatic:' })
    $accepted30 = @($segment | Where-Object { ([string]$_) -match 'WMI FAN REQUEST ACCEPTED: target=30/30;' })
    $accepted31 = @($segment | Where-Object { ([string]$_) -match 'WMI FAN REQUEST ACCEPTED: target=31/31;' })
    $failures = @($segment | Where-Object {
        ([string]$_) -match 'P13 (mode|manual) request .*FAILED CLOSED:' -or
        ([string]$_) -match 'P13 control interaction blocked before production adapter access:'
    })

    if ($manualModes.Count -ne 2 -or
        $manual30.Count -ne 1 -or
        $manual31.Count -ne 1 -or
        $firmwareModes.Count -ne 2 -or
        $automaticModes.Count -ne 0 -or
        $accepted30.Count -ne 1 -or
        $accepted31.Count -ne 1 -or
        $failures.Count -ne 0) {
        throw ("App interaction audit failed: ManualMode={0} Manual30={1} Manual31={2} Firmware={3} Automatic={4} Ack30={5} Ack31={6} Failures={7}" -f
            $manualModes.Count, $manual30.Count, $manual31.Count, $firmwareModes.Count,
            $automaticModes.Count, $accepted30.Count, $accepted31.Count, $failures.Count)
    }

    $orderedPatterns = @(
        'P13 mode request Manual: action=HoldFirmware;',
        'WMI FAN REQUEST ACCEPTED: target=30/30;',
        'P13 manual request 30/30: action=EnterCustomAndApply;',
        'P13 mode request Firmware: action=RestoreFirmware;',
        'P13 mode request Manual: action=HoldFirmware;',
        'WMI FAN REQUEST ACCEPTED: target=31/31;',
        'P13 manual request 31/31: action=EnterCustomAndApply;',
        'P13 mode request Firmware: action=RestoreFirmware;'
    )

    $cursor = -1
    foreach ($pattern in $orderedPatterns) {
        $found = -1
        for ($i = $cursor + 1; $i -lt $segment.Count; $i++) {
            if ([string]$segment[$i] -match $pattern) {
                $found = $i
                break
            }
        }
        if ($found -lt 0) {
            throw "App causal ordering missing after index $cursor: $pattern"
        }
        $cursor = $found
    }
}

function Write-Summary {
    param([string]$Result, [string]$Failure)

    [ordered]@{
        schemaVersion = 1
        gate = 'HP-8C40-WMI-GUI-REARM-MANUAL'
        result = $Result
        failure = $Failure
        sourceHead = $head
        branch = $expectedBranch
        timestampUtc = (Get-Date).ToUniversalTime().ToString('O')
        targetProfileId = 'HP-8C40-9D0R1LA-F18'
        guiPid = $appPid
        guiStartUtcTicks = [string]$appStartTicks
        directEcProhibited = $true
        sessionA = if ($sessionA) { [IO.Path]::GetFileName($sessionA) } else { $null }
        sessionB = if ($sessionB) { [IO.Path]::GetFileName($sessionB) } else { $null }
        sessionDirectoriesDistinct = [bool]($sessionA -and $sessionB -and ([IO.Path]::GetFullPath($sessionA) -cne [IO.Path]::GetFullPath($sessionB)))
        guardianA = if ($readyA) { [ordered]@{
            pid = [int]$readyA.GuardianPid
            startUtcTicks = [string]$readyA.GuardianStartUtcTicks
        }} else { $null }
        guardianB = if ($readyB) { [ordered]@{
            pid = [int]$readyB.GuardianPid
            startUtcTicks = [string]$readyB.GuardianStartUtcTicks
        }} else { $null }
        guardianProcessesDistinct = [bool]($readyA -and $readyB -and
            ([int]$readyA.GuardianPid -ne [int]$readyB.GuardianPid -or
             [long]$readyA.GuardianStartUtcTicks -ne [long]$readyB.GuardianStartUtcTicks))
        releaseA = if ($reportA) { [ordered]@{
            exitReason = [string]$reportA.ExitReason
            releaseRequestAccepted = [bool]$reportA.ReleaseRequestAccepted
            legacyDefaultRequestAccepted = [bool]$reportA.LegacyDefaultRequestAccepted
            guardianLeaseRetired = [bool]$reportA.GuardianLeaseRetired
            independentFirmwareOwnershipVerified = [bool]$reportA.IndependentFirmwareOwnershipVerified
            failure = $reportA.Failure
        }} else { $null }
        releaseB = if ($reportB) { [ordered]@{
            exitReason = [string]$reportB.ExitReason
            releaseRequestAccepted = [bool]$reportB.ReleaseRequestAccepted
            legacyDefaultRequestAccepted = [bool]$reportB.LegacyDefaultRequestAccepted
            guardianLeaseRetired = [bool]$reportB.GuardianLeaseRetired
            independentFirmwareOwnershipVerified = [bool]$reportB.IndependentFirmwareOwnershipVerified
            failure = $reportB.Failure
        }} else { $null }
        finalGuiLeasePresent = [bool](Test-Path -LiteralPath $guiLeasePath)
        legacyLeasePresent = [bool](Test-Path -LiteralPath $legacyLeasePath)
        experimentLeasePresent = [bool](Test-Path -LiteralPath $experimentLeasePath)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator
$head = Assert-RepositoryProvenance
Assert-ExactTarget
Assert-NoConflictingController
Assert-NoPendingLease

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
if (Test-Path -LiteralPath $appLogPath -PathType Leaf) {
    $appBaselineLines = @(Get-Content -LiteralPath $appLogPath).Count
}

$preExistingSessions = @()
if (Test-Path -LiteralPath $sessionRoot -PathType Container) {
    $preExistingSessions = @(
        Get-ChildItem -LiteralPath $sessionRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName }
    )
}

Write-Host 'VictusFanControl - HP 8C40 WMI GUI REARM MANUAL TEST' -ForegroundColor Cyan
Write-Host 'Purpose: prove session A -> verified Firmware release -> fresh session B -> verified Firmware release in one GUI process.' -ForegroundColor Yellow
Write-Host 'No direct EC/setpoint probe is used. Automatic must not be selected.' -ForegroundColor Yellow

try {
    Write-Host ''
    Write-Host 'Step 1: same-HEAD Release build...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit=$LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
        throw "GUI executable missing after build: $appExe"
    }

    Write-Host 'Step 2: launching the normal WMI-only GUI...' -ForegroundColor Cyan
    $app = Start-Process -FilePath $appExe -ArgumentList @('--modules-dir', ('"{0}"' -f $modulesDir)) -WorkingDirectory $repoRoot -PassThru
    $appPid = $app.Id
    $appStartTicks = [long]$app.StartTime.ToUniversalTime().Ticks

    [void](Wait-AppMatch 'Fan backend: HP 8C40 WMI-only / supervised requests; hardware ownership unverified; CanWrite=True;' 0 'normal WMI-only backend startup')
    [void](Wait-AppMatch 'P13 UI: startup mode=Firmware; manualGate=True; automaticGate=False\.' 0 'normal Manual-open / Automatic-closed startup')
    [void](Wait-AppMatch 'Recovery completed; telemetry is healthy after 3 complete snapshots\.' 0 'initial Healthy telemetry')

    Write-Host ''
    Write-Host 'ACTION 1/4:' -ForegroundColor Yellow
    Write-Host '  In Fan Control click Manual ONCE, set level 30, then click Apply ONCE.'
    Write-Host '  Do not click Automatic. Wait for this console to advance.'
    $start = @(Get-AppSegment).Count
    [void](Wait-AppMatch 'P13 mode request Manual: action=HoldFirmware; authorized=True; authority=Firmware;' $start 'first Manual mode selection')
    [void](Wait-AppMatch 'WMI FAN REQUEST ACCEPTED: target=30/30;' $start 'first WMI target 30/30')
    [void](Wait-AppMatch 'P13 manual request 30/30: action=EnterCustomAndApply; authorized=True; authority=Custom;' $start 'first Manual Apply 30/30')

    $sessionAResult = Wait-ReadySession $appPid $appStartTicks $preExistingSessions
    $sessionA = $sessionAResult.Directory
    $readyA = $sessionAResult.Ready
    if (-not (Test-Path -LiteralPath $guiLeasePath -PathType Leaf)) {
        throw 'Session A entered Custom but the durable WMI GUI lease is missing.'
    }

    Write-Host ("Session A armed: {0}; guardian PID={1}" -f ([IO.Path]::GetFileName($sessionA)), [int]$readyA.GuardianPid) -ForegroundColor Green

    Write-Host ''
    Write-Host 'ACTION 2/4:' -ForegroundColor Yellow
    Write-Host '  Click Firmware ONCE. Wait for this console to validate release A.'
    $start = @(Get-AppSegment).Count
    [void](Wait-AppMatch 'P13 mode request Firmware: action=RestoreFirmware; authorized=True; authority=Firmware;' $start 'first Firmware restore')
    $reportA = Wait-GuardianReport $sessionA
    Assert-GuardianReport $reportA $appPid $appStartTicks 'Session A'
    Wait-LeaseAbsent
    Copy-SessionEvidence $sessionA $sessionAEvidence

    Write-Host 'Release A PASS: FF/FF + LegacyDefault accepted, lease retired, independent firmware ownership still unverified.' -ForegroundColor Green

    Write-Host ''
    Write-Host 'ACTION 3/4:' -ForegroundColor Yellow
    Write-Host '  Without closing VictusFanControl, click Manual ONCE, set level 31, then click Apply ONCE.'
    Write-Host '  This is the actual re-entry proof.'
    $start = @(Get-AppSegment).Count
    [void](Wait-AppMatch 'P13 mode request Manual: action=HoldFirmware; authorized=True; authority=Firmware;' $start 'second Manual mode selection')
    [void](Wait-AppMatch 'WMI FAN REQUEST ACCEPTED: target=31/31;' $start 'second WMI target 31/31')
    [void](Wait-AppMatch 'P13 manual request 31/31: action=EnterCustomAndApply; authorized=True; authority=Custom;' $start 'second Manual Apply 31/31')

    $sessionBResult = Wait-ReadySession $appPid $appStartTicks @($preExistingSessions + $sessionA)
    $sessionB = $sessionBResult.Directory
    $readyB = $sessionBResult.Ready

    if ([IO.Path]::GetFullPath($sessionA) -ceq [IO.Path]::GetFullPath($sessionB)) {
        throw 'Session B reused the released session A directory.'
    }
    if ([int]$readyA.GuardianPid -eq [int]$readyB.GuardianPid -and
        [long]$readyA.GuardianStartUtcTicks -eq [long]$readyB.GuardianStartUtcTicks) {
        throw 'Session B reused the released guardian process identity.'
    }
    if (-not (Test-Path -LiteralPath $guiLeasePath -PathType Leaf)) {
        throw 'Session B entered Custom but the durable WMI GUI lease is missing.'
    }

    Write-Host ("Session B armed: {0}; guardian PID={1}" -f ([IO.Path]::GetFileName($sessionB)), [int]$readyB.GuardianPid) -ForegroundColor Green

    Write-Host ''
    Write-Host 'ACTION 4/4:' -ForegroundColor Yellow
    Write-Host '  Click Firmware ONCE. Wait for this console to validate release B.'
    $start = @(Get-AppSegment).Count
    [void](Wait-AppMatch 'P13 mode request Firmware: action=RestoreFirmware; authorized=True; authority=Firmware;' $start 'second Firmware restore')
    $reportB = Wait-GuardianReport $sessionB
    Assert-GuardianReport $reportB $appPid $appStartTicks 'Session B'
    Wait-LeaseAbsent
    Copy-SessionEvidence $sessionB $sessionBEvidence

    Assert-AppAudit

    Write-Host ''
    Write-Host 'Both supervised WMI sessions passed. Now right-click the tray icon and choose Exit ONCE.' -ForegroundColor Yellow
    if (-not $app.WaitForExit($TimeoutSeconds * 1000)) {
        throw 'GUI did not exit after the requested clean tray Exit.'
    }
    if ($app.ExitCode -ne 0) {
        throw "GUI exited with code $($app.ExitCode)."
    }

    Assert-NoPendingLease
    $pass = $true
    Write-Host 'PASS: one GUI process completed session A -> Firmware -> session B -> Firmware with two distinct guardians and two clean lease retirements.' -ForegroundColor Green
}
catch {
    $failure = $_.Exception.Message
    Write-Host ("FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally {
    try {
        @(Get-AppSegment) | Set-Content -LiteralPath $appSegmentPath -Encoding UTF8
    }
    catch {
    }

    try {
        if ($sessionA -and (Test-Path -LiteralPath $sessionA) -and -not (Test-Path -LiteralPath $sessionAEvidence)) {
            Copy-SessionEvidence $sessionA $sessionAEvidence
        }
    }
    catch {
    }

    try {
        if ($sessionB -and (Test-Path -LiteralPath $sessionB) -and -not (Test-Path -LiteralPath $sessionBEvidence)) {
            Copy-SessionEvidence $sessionB $sessionBEvidence
        }
    }
    catch {
    }

    try {
        Write-Summary $(if ($pass) { 'PASS' } else { 'FAIL_CLOSED' }) $failure
    }
    catch {
        Write-Warning ("Could not write summary: {0}" -f $_.Exception.Message)
    }

    try {
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }
        Compress-Archive -Path (Join-Path $evidenceRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
        $sha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$sha  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $shaPath -Encoding ASCII
        Write-Host ("Evidence ZIP: {0}" -f $zipPath)
        Write-Host ("SHA-256: {0}" -f $sha)
    }
    catch {
        Write-Warning ("Evidence packaging failed: {0}" -f $_.Exception.Message)
        if ($pass) {
            $pass = $false
            $failure = 'Physical sequence passed but evidence packaging failed.'
        }
    }

    if (-not $pass -and $app) {
        try {
            $app.Refresh()
            if (-not $app.HasExited) {
                if (Test-Path -LiteralPath $guiLeasePath) {
                    Write-Warning 'GUI remains open with an active WMI lease. Do not kill it blindly; use the Firmware button and let the guardian complete recovery.'
                }
                else {
                    Write-Warning 'GUI remains open without an active WMI lease. Close it normally from the tray after reviewing the failure.'
                }
            }
        }
        catch {
        }
    }
}

if ($pass) {
    exit 0
}

Write-Error ("WMI GUI rearm Manual test failed closed. Evidence preserved under {0}. {1}" -f $evidenceRoot, $failure)
exit 1
