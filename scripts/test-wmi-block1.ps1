param([int]$TimeoutSeconds = 240, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$expectedBranch = 'feature/victus-8c40-automatic-final-qualification'
$target = 'HP-8C40-9D0R1LA-F18'
$guiLeasePath = Join-Path $env:ProgramData 'VictusFanControl\WmiFanGui\lease.json'
$legacyLeasePath = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'
$experimentLeasePath = Join-Path $env:ProgramData 'VictusFanControl\WmiFanExperiment\lease.json'
$app = $null
$appStartTicks = 0L
$state = $null
$pass = $false
$failure = $null
$head = $null
$ci = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'WMI Block1 test must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch = (& git branch --show-current 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch) {
        throw "WMI Block1 test requires branch '$expectedBranch'; observed '$branch'."
    }

    $local = (& git rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream = (& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $local -notmatch '^[0-9a-f]{40}$' -or
        $local -cne $upstream) {
        throw "WMI Block1 test requires local HEAD == upstream HEAD. local=$local upstream=$upstream"
    }

    $status = (& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw 'WMI Block1 test could not inspect git status.'
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
        throw 'WMI Block1 test requires committed source/config state; only untracked logs evidence is allowed.'
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
        throw 'WMI Block1 test exact-target fingerprint mismatch.'
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

function Assert-NativeAudit {
    param($Calls, [int]$Initial, [int]$Final, [int]$MinimumCommands)
    $writes = @($Calls | Where-Object { [int]$_.commandType -ne 0x2D })
    $normal = @($writes | Where-Object { [int]$_.commandType -eq 0x2E -and [int]$_.payload[0] -ne 255 })
    if ($normal.Count -lt $MinimumCommands -or [int]$normal[0].payload[0] -ne $Initial -or
        [int]$normal[-1].payload[0] -ne $Final -or $writes.Count -ne ($normal.Count + 2)) {
        throw 'Native audit command counts/targets are invalid.'
    }
    foreach ($call in $normal) {
        if ($call.recovering -or $call.payload.Count -ne 4 -or
            [int]$call.payload[0] -lt 30 -or [int]$call.payload[0] -gt 50 -or
            [int]$call.payload[0] -ne [int]$call.payload[1] -or
            [int]$call.payload[2] -ne 0 -or [int]$call.payload[3] -ne 0) {
            throw 'Native audit normal command outside WMI lifecycle/envelope.'
        }
    }
    # All normal commands must precede the single release/default tail.
    for ($i = 0; $i -lt $normal.Count; $i++) {
        if ([int]$writes[$i].commandType -ne 0x2E -or [int]$writes[$i].payload[0] -eq 255) {
            throw 'Native audit detected an intermediate release during Custom handoff.'
        }
    }
    $release = $writes[-2]; $default = $writes[-1]
    if ([int]$release.commandType -ne 0x2E -or ($release.payload -join ',') -cne '255,255,0,0' -or
        [int]$default.commandType -ne 0x1A -or ($default.payload -join ',') -cne '255,0,0,0' -or
        -not $release.recovering -or -not $default.recovering) {
        throw 'Native audit release/default tail is invalid.'
    }
}

function Assert-ReleaseReport {
    param($Report, [int]$OwnerPid, [long]$StartTicks)
    foreach ($name in @('SchemaVersion','TargetProfileId','OwnerPid','OwnerStartUtcTicks','ExitReason',
        'ReleaseRequestAccepted','LegacyDefaultRequestAccepted','GuardianLeaseRetired',
        'IndependentFirmwareOwnershipVerified','DirectEcProhibited','Failure')) {
        if ($null -eq $Report.PSObject.Properties[$name]) { throw "Missing guardian report field: $name" }
    }
    if ($Report.SchemaVersion -ne 1 -or $Report.TargetProfileId -cne $target -or
        [int]$Report.OwnerPid -ne $OwnerPid -or [long]$Report.OwnerStartUtcTicks -ne $StartTicks -or
        $Report.ExitReason -cne 'CLIENT_RELEASE' -or -not $Report.ReleaseRequestAccepted -or
        -not $Report.LegacyDefaultRequestAccepted -or -not $Report.GuardianLeaseRetired -or
        $Report.IndependentFirmwareOwnershipVerified -or -not $Report.DirectEcProhibited -or $null -ne $Report.Failure) {
        throw 'Guardian release report failed identity/completion validation.'
    }
}

if ($SelfTest) {
    $programSource = Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
    $mainSource = Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
    $gateSource = Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
    $bridgeSource = Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.Block1Qualification.cs') -Raw
    foreach ($required in @('--8c40-block1-test','--8c40-block1-token','--8c40-block1-root',
        '(block1Test ? 1 : 0)','automaticFinalQualificationHardwareTest || block1Test')) {
        if (-not $programSource.Contains($required)) { throw "Missing Block1 startup isolation: $required" }
    }
    if (-not $gateSource.Contains('AutomaticExecutionAuthorized = false') -or
        -not $mainSource.Contains('Block1Enabled ? AuthorizeBlock1') -or
        -not $mainSource.Contains('RequestBlock1TrayExit();') -or
        -not $mainSource.Contains('CompleteBlock1Shutdown();') -or
        -not $bridgeSource.Contains('CaptureBlock1Identity() != _block1A') -or
        -not $bridgeSource.Contains('IndependentFirmwareOwnershipVerified: false')) {
        throw 'Block1 mode, guardian identity or shutdown isolation invariant failed.'
    }
    function Call([int]$Type, $Payload, [bool]$Recovery = $false) {
        return [pscustomobject]@{commandType=$Type;payload=$Payload;recovering=$Recovery}
    }
    $good = @((Call 0x2E @(40,40,0,0)),(Call 0x2D @(0,0,0,0)),(Call 0x2E @(30,30,0,0)),
        (Call 0x2E @(31,31,0,0)),(Call 0x2E @(255,255,0,0) $true),(Call 0x1A @(255,0,0,0) $true))
    Assert-NativeAudit $good 40 31 3
    $bad = @($good[0],$good[4],$good[2],$good[3],$good[5])
    $rejected = $false
    try { Assert-NativeAudit $bad 40 31 3 } catch { $rejected = $true }
    if (-not $rejected) { throw 'Intermediate release fixture accepted.' }
    $report = [pscustomobject]@{SchemaVersion=1;TargetProfileId=$target;OwnerPid=123;OwnerStartUtcTicks=456L;
        ExitReason='CLIENT_RELEASE';ReleaseRequestAccepted=$true;LegacyDefaultRequestAccepted=$true;
        GuardianLeaseRetired=$true;IndependentFirmwareOwnershipVerified=$false;DirectEcProhibited=$true;Failure=$null}
    Assert-ReleaseReport $report 123 456L
    $rejected = $false
    try { Assert-ReleaseReport $report 124 456L } catch { $rejected = $true }
    if (-not $rejected) { throw 'Wrong owner fixture accepted.' }
    $report.IndependentFirmwareOwnershipVerified = $true
    $rejected = $false
    try { Assert-ReleaseReport $report 123 456L } catch { $rejected = $true }
    if (-not $rejected) { throw 'False WMI ownership claim accepted.' }
    Write-Host 'Block1 harness evidence fixtures: PASS (no hardware IO).'
    exit 0
}

if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 300) { throw 'TimeoutSeconds must be 30..300 per action.' }
Set-Location $repoRoot
Assert-Administrator
& git fetch origin $expectedBranch
if ($LASTEXITCODE -ne 0) { throw 'Could not refresh upstream before qualification.' }
$head = Assert-RepositoryProvenance
Assert-ExactTarget
Assert-NoConflictingController
Assert-NoPendingLease
$domainRoot = Join-Path $env:LOCALAPPDATA "VictusFanControl\Performance\$target"
foreach ($journal in @('cpu-power-session.json','gpu-clock-session.json')) {
    if (Test-Path (Join-Path $domainRoot $journal)) { throw "Pending performance journal: $journal" }
}
$uri = "https://api.github.com/repos/Esteban-3055/VictusFanControl/actions/runs?head_sha=$head&per_page=50"
$ci = Invoke-RestMethod -Uri $uri -Headers @{'User-Agent'='VictusFanControl-Block1';Accept='application/vnd.github+json'}
foreach ($name in @('build','cpu-rapl','wmi-fan-experiment')) {
    $run = @($ci.workflow_runs | Where-Object { $_.name -ceq $name -and $_.head_sha -ceq $head } |
        Sort-Object run_number -Descending) | Select-Object -First 1
    if (-not $run -or $run.status -cne 'completed' -or $run.conclusion -cne 'success') {
        throw "Same-HEAD CI not successful: $name. Wait for GitHub checks; no hardware started."
    }
}

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot = Join-Path $repoRoot ("logs\wmi-block1_{0}_{1}" -f $stamp,([Guid]::NewGuid().ToString('N').Substring(0,6)))
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
$statePath = Join-Path $evidenceRoot 'block1.state.json'
$zipPath = "$evidenceRoot.zip"
$ci | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $evidenceRoot 'same-head-ci.json') -Encoding UTF8
$head | Set-Content (Join-Path $evidenceRoot 'source-head.txt') -Encoding ASCII
$logPath = Join-Path $env:LOCALAPPDATA ("VictusFanControl\logs\events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$baseline = if (Test-Path $logPath) { @(Get-Content $logPath).Count } else { 0 }

function Read-State {
    if (-not (Test-Path $statePath)) { return $null }
    try { $s = Get-Content $statePath -Raw | ConvertFrom-Json } catch { return $null }
    if ($s.schemaVersion -ne 1 -or $s.gate -cne 'HP-8C40-WMI-BLOCK1' -or
        $s.guiPid -ne $app.Id -or [long]$s.guiStartUtcTicks -ne $appStartTicks -or
        $s.targetProfileId -cne $target -or -not $s.directEcProhibited -or $s.normalAutomaticAuthorized) {
        throw 'Block1 state identity/isolation mismatch.'
    }
    return $s
}
function Wait-Phase([string]$Expected) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $s = Read-State
        if ($s -and $s.phase -ceq 'Failed') { throw "Block1 failed: $($s.failure)" }
        if ($s -and $s.phase -ceq $Expected) { return $s }
        $app.Refresh()
        if ($app.HasExited) { throw "GUI exited before phase $Expected; exit=$($app.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    throw "Timeout waiting for $Expected."
}
function Prompt-Step([string]$Text) { Write-Host "`n$Text" -ForegroundColor Yellow }
function Copy-BoundEvidence($s) {
    if (-not $s) { return }
    $fanRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'VictusFanControl\FanWmi\gui')) + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in @(@{Name='session-a';Identity=$s.sessionA},@{Name='session-b';Identity=$s.sessionB})) {
        if ($entry.Identity) {
            $path = [IO.Path]::GetFullPath([string]$entry.Identity.Directory)
            if (-not $path.StartsWith($fanRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fan evidence path.' }
            if (Test-Path $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $evidenceRoot $entry.Name) -Recurse -Force }
        }
    }
    if ($s.performanceGuardianReportPath) {
        $path = [IO.Path]::GetFullPath([string]$s.performanceGuardianReportPath)
        $root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'VictusFanControl\Performance\gui')) + [IO.Path]::DirectorySeparatorChar
        if (-not $path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected performance evidence path.' }
        if (Test-Path $path) { Copy-Item $path (Join-Path $evidenceRoot 'performance-guardian-report.json') -Force }
        $config = Join-Path (Split-Path $path) 'configuration.json'
        if (Test-Path $config) { Copy-Item $config (Join-Path $evidenceRoot 'performance-configuration.json') -Force }
    }
}
try {
    Write-Host 'VictusFanControl - WMI BLOCK 1 (isolated Automatic / no direct EC)' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Same-HEAD Release build failed.' }
    $exe = Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
    $modules = Join-Path $repoRoot 'modules'
    if (-not (Test-Path (Join-Path $modules 'IntelMSR.bin'))) { throw 'IntelMSR.bin is required; no EC module is required.' }
    $arguments = @('--modules-dir',('"{0}"' -f $modules),'--8c40-block1-test','--8c40-block1-token','8C40-WMI-BLOCK1',
        '--8c40-block1-root',('"{0}"' -f $evidenceRoot))
    $app = Start-Process $exe -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru
    $appStartTicks = [long]$app.StartTime.ToUniversalTime().Ticks
    Prompt-Step 'SETUP: Keep AC connected. In Rendimiento select CPU + GPU, set CPU AC PL1=20 W / PL2=40 W and click Apply. Wait for READY.'
    $state = Wait-Phase 'Ready'
    Prompt-Step '1/6 READY: Select Manual ONCE, choose 40 and click Apply ONCE. Wait for this console.'
    $state = Wait-Phase 'ManualActiveA'
    Prompt-Step '2/6 Select Automatic ONCE. Use light normal activity until it sends a changed target. Do not select another mode yet.'
    $state = Wait-Phase 'AutomaticChanged'
    Prompt-Step '3/6 Select Manual ONCE; WAIT for this console to tell you the target before applying.'
    $state = Wait-Phase 'ManualReturnedA'
    Prompt-Step ("Now choose Manual level {0} and click Apply ONCE." -f $state.returnManualLevel)
    $state = Wait-Phase 'ManualChangedA'
    Prompt-Step '4/6 Select Firmware ONCE. Wait for the verified release.'
    $state = Wait-Phase 'FirmwareReleasedA'
    Prompt-Step '5/6 Without closing the GUI, select Manual ONCE, choose 31 and click Apply ONCE.'
    $state = Wait-Phase 'ManualActiveB'
    Prompt-Step '6/6 With Manual still active, right-click the tray icon and choose Exit ONCE. Do not select Firmware first.'
    if (-not $app.WaitForExit($TimeoutSeconds * 1000)) { throw 'Tray Exit did not complete within the deadline.' }
    $state = Read-State
    if ($app.ExitCode -ne 0 -or -not $state -or $state.phase -cne 'Completed' -or
        -not $state.handoffPassed -or -not $state.rearmPassed -or -not $state.closePassed -or
        -not $state.trayExitRequested -or $state.restoreTransitions -ne 2 -or $state.deniedEcAccesses -ne 0 -or
        $state.automaticCommands -lt 1 -or $state.journalPresent) { throw "Block1 completion failed: $($state.failure)" }
    Assert-NoPendingLease
    foreach ($journal in @('cpu-power-session.json','gpu-clock-session.json')) {
        if (Test-Path (Join-Path $domainRoot $journal)) { throw "Performance journal remains: $journal" }
    }
    if ($state.sessionA.Directory -ceq $state.sessionB.Directory -or
        ($state.sessionA.GuardianPid -eq $state.sessionB.GuardianPid -and
         $state.sessionA.GuardianStartUtcTicks -eq $state.sessionB.GuardianStartUtcTicks)) { throw 'Rearm identities are not distinct.' }
    foreach ($entry in @($state.sessionA,$state.sessionB)) {
        $report = Get-Content (Join-Path $entry.Directory 'guardian-report.json') -Raw | ConvertFrom-Json
        Assert-ReleaseReport $report $app.Id $appStartTicks
    }
    $callsA = @(Get-Content (Join-Path $state.sessionA.Directory 'native-dispatch.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    $callsB = @(Get-Content (Join-Path $state.sessionB.Directory 'native-dispatch.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-NativeAudit $callsA 40 $state.returnManualLevel 3
    Assert-NativeAudit $callsB 31 31 1
    $p = Get-Content $state.performanceGuardianReportPath -Raw | ConvertFrom-Json
    if ($p.OwnerPid -ne $app.Id -or [long]$p.OwnerStartUtcTicks -ne $appStartTicks -or
        $p.TargetProfileId -cne $target -or $p.ExitReason -cne 'CLIENT_SHUTDOWN' -or $p.FinalPhase -cne 'Stopped' -or
        $p.CpuDomainState -cne 'Disabled' -or $p.GpuDomainState -cne 'Disabled' -or
        $p.CpuDomainStatus -cne 'GUARDIAN_CPU_RELEASED_TO_PRESESSION_TARGET' -or
        $p.GpuDomainStatus -cne 'GUARDIAN_GPU_RELEASED_TO_NVIDIA_DEFAULT' -or
        $p.SourceRuntimeActive -or $null -ne $p.Failure -or $null -ne $p.SourceFailure -or
        $p.CpuHardwareWriteAttempts -lt 2 -or $p.GpuHardwareWriteAttempts -lt 2) { throw 'Independent CPU/GPU release report failed.' }
    $pass = $true
}
catch {
    $failure = $_.Exception.Message
    Write-Host "FAIL_CLOSED: $failure" -ForegroundColor Red
    if ($app -and -not $app.HasExited) {
        Set-Content (Join-Path $evidenceRoot 'block1.abort.signal') $failure -Encoding UTF8
        # Allow normal release to produce reports; never kill the GUI/Guardian.
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline -and (Test-Path $guiLeasePath)) { Start-Sleep -Milliseconds 200 }
    }
}
finally {
    try {
        if ($app) { $state = Read-State }
        Copy-BoundEvidence $state
        if (Test-Path $logPath) { Get-Content $logPath | Select-Object -Skip $baseline | Set-Content (Join-Path $evidenceRoot 'application-events.log') -Encoding UTF8 }
        if (Test-Path $guiLeasePath) { Copy-Item $guiLeasePath (Join-Path $evidenceRoot 'retained-lease.json') -Force }
        [ordered]@{schemaVersion=1;gate='HP-8C40-WMI-BLOCK1-HARNESS';result=$(if($pass){'PASS'}else{'FAIL_CLOSED'});
            failure=$failure;sourceHead=$head;branch=$expectedBranch;timestampUtc=(Get-Date).ToUniversalTime().ToString('O');
            handoffPassed=[bool]$state.handoffPassed;rearmPassed=[bool]$state.rearmPassed;closePassed=[bool]$state.closePassed;
            finalLeasePresent=[bool](Test-Path $guiLeasePath);independentFirmwareOwnershipVerified=$false} |
            ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidenceRoot 'block1.harness-summary.json') -Encoding UTF8
        Compress-Archive -Path (Join-Path $evidenceRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
        $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content "$zipPath.sha256" -Encoding ASCII
        Write-Host "Evidence ZIP: $zipPath"
        Write-Host "SHA-256: $hash"
    }
    catch { $pass=$false; Write-Warning "Evidence packaging failed: $($_.Exception.Message). Preserve $evidenceRoot" }
}
if (-not $pass) {
    Write-Warning 'If the GUI remains open, use Firmware / tray Exit normally. Never delete retained leases or kill Guardians to bypass recovery.'
    exit 1
}
Write-Host 'BLOCK1 PASS: handoff + release/rearm + active tray exit with CPU/GPU cleanup. Automatic normal remains CLOSED.' -ForegroundColor Green
exit 0
