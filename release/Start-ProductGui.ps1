param(
    [ValidateSet('Verify','FinalCheck','SelfTest','Soak','Open','AutomaticReview','AutomaticExtendedReview','RecoverPerformance','RecoverySelfTest','Install')][string]$Mode = 'Open',
    [Guid]$ExpectedCpuSession = [Guid]::Empty,
    [Guid]$ExpectedGpuSession = [Guid]::Empty,
    [switch]$ConfirmExclusiveGpuController
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'PRODUCT-GUI-MANIFEST.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.kind -ne 'VictusFanControl.ProductGuiRelease' -or $manifest.version -ne '1.1.2' -or $manifest.appDirectory -ne 'app') { throw 'Invalid v1.0 product manifest.' }
$listed = @{}
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Invalid manifest path.' }
    if($listed.ContainsKey($relative)){throw "Duplicate manifest path: $relative"};$listed[$relative]=$true
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $relative" }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant() -ne $entry.sha256 -or (Get-Item -LiteralPath $file).Length -ne $entry.size) { throw "Package integrity failed: $relative" }
}
$actual=@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $root 'PRODUCT-GUI-MANIFEST.json')})
if($actual.Count -ne $listed.Count){throw 'Unexpected package files.'}
foreach($file in $actual){if(-not $listed.ContainsKey($file.FullName.Substring($root.Length+1).Replace('\','/'))){throw 'Unlisted package file.'}}
$candidate = Get-Content -LiteralPath (Join-Path $root 'PRODUCT-RELEASE.json') -Raw | ConvertFrom-Json
if ($manifest.releaseStage -ne 'target-specific-release' -or $manifest.finalReleaseReady -ne $true -or $manifest.normalAutomatic -ne 'authorized-exact-target' -or
    $candidate.kind -ne 'VictusFanControl.ProductRelease' -or $candidate.version -ne '1.1.2' -or $candidate.sourceHead -ne $manifest.sourceHead -or $candidate.stableReleaseAuthorized -ne $true -or $candidate.physicalPassClaimed -ne $false) { throw 'Invalid target release authorization.' }
if ($manifest.productPlatformRetention -ne 'optional-disabled-default-AC-Battery-plus2-60s-fresh3s' -or
    $candidate.experimentalPlatformRetention.defaultEnabled -ne $false -or $candidate.experimentalPlatformRetention.scope -ne 'optional-ac-and-battery' -or
    $candidate.experimentalPlatformRetention.maximumExtraRawLevels -ne 2 -or $candidate.experimentalPlatformRetention.maximumSupplementSeconds -ne 60) { throw 'Invalid optional platform retention contract.' }
if($manifest.automaticProtectionControls -ne 'independent-cpu-gpu-power-handoffs-clean-resume-10-30-60s-three-attempts' -or $candidate.automaticProtectionControls -ne $manifest.automaticProtectionControls){throw 'Invalid configurable protections contract.'}
if($manifest.manualAutomaticRetry -ne 'explicit-clean-release-fresh-runtime-same-gui-three-fresh-observations' -or $candidate.reentryAfterInterruption -ne 'explicit-clean-release-fresh-runtime-same-gui-three-fresh-observations'){throw 'Invalid manual Automatic retry contract.'}
if($manifest.guidedPerformanceRecovery -ne 'gui-installer-exact-owner-release-only'){throw 'Invalid guided recovery contract.'}
if($manifest.guidedFanRecovery -ne 'wmi-gui-experiment-orphan-and-target-bound-legacy-release-only'){throw 'Invalid guided fan recovery contract.'}
Write-Host "Verified VictusFanControl v1.1.2 build $($manifest.sourceHead). Exact HP 8C40/F.18 release; evidence limits remain in PRODUCT-RELEASE.json."
if ($manifest.customGpuClock -ne 'configurable-210-to-2500' -or $manifest.diagnostics -ne 'per-process-session-with-telemetry' -or $manifest.curveMarkers -ne 'applied-request-and-draft-preview') { throw 'This launcher requires the session diagnostic and live marker package.' }
if ($Mode -eq 'Verify') { return }
if ($Mode -eq 'FinalCheck') {
    # All three entries use explicit zero-hardware fixtures against the exact packaged binaries.
    foreach ($check in @('SelfTest','Soak','RecoverySelfTest')) {
        & $PSCommandPath -Mode $check
    }
    & $PSCommandPath -Mode Verify
    Write-Host 'v1.0 software checks: PASS. No hardware activation or physical qualification performed.'
    return
}
if ($Mode -in @('AutomaticReview','AutomaticExtendedReview') -and $manifest.productAutomaticReview -ne 'explicit-only-300s-10-to-50') { throw 'This package does not authorize the supervised Automatic review entry.' }
if ($Mode -in @('AutomaticReview','AutomaticExtendedReview') -and $manifest.productAutomaticThermal -ne 'cpu-start90-active95-confirm2000ms-cpu99-immediate-raw-response') { throw 'This package does not include the final candidate thermal contract.' }
if ($Mode -in @('AutomaticReview','AutomaticExtendedReview') -and $manifest.productAutomaticPerformance -ne 'required-both-before-fans') { throw 'This package does not authorize the coupled CPU/GPU Automatic entry.' }
if ($Mode -in @('AutomaticReview','AutomaticExtendedReview') -and $manifest.productAutomaticSourceTransition -ne 'bounded-4000ms-fresh-guardian-preserves-inertia') { throw 'This package does not include the bounded AC/Battery curve transition review.' }
if ($Mode -eq 'AutomaticExtendedReview' -and $manifest.productAutomaticExtendedReview -ne 'explicit-only-2700s-10-to-50-16MiB-diagnostics') { throw 'This package does not authorize the supervised extended Automatic review entry.' }
$app = Join-Path $root 'app'
if($Mode -eq 'Install'){ & (Join-Path $root 'Install-VictusFanControl.ps1');return }
if ($Mode -eq 'RecoverPerformance' -or $Mode -eq 'RecoverySelfTest') {
    $guardian = Join-Path $app 'performance-guardian/VictusFanControl.PerformanceGuardian.exe'
    if (-not (Test-Path -LiteralPath $guardian -PathType Leaf)) { throw "Missing packaged Performance Guardian: $guardian" }
}
if ($Mode -eq 'RecoverySelfTest') {
    & $guardian --gui-recovery-self-test
    if ($LASTEXITCODE -ne 0) { throw 'Packaged recovery fixtures failed.' }
    & $guardian --product-fan-recovery-self-test
    if ($LASTEXITCODE -ne 0) { throw 'Packaged fan recovery fixtures failed.' }
    return
}
if ($Mode -eq 'RecoverPerformance') {
    if ($manifest.performanceRecovery -ne 'explicit-release-only-exact-session-backups') { throw 'This package does not include explicit Performance recovery.' }
    if ($ExpectedCpuSession -eq [Guid]::Empty -and $ExpectedGpuSession -eq [Guid]::Empty) { throw 'Specify the expected pending session IDs; use an empty GUID only for an absent domain.' }
    if (-not $ConfirmExclusiveGpuController) { throw 'Close other GPU clock controllers (Afterburner, nvidia-smi clock scripts) and specify -ConfirmExclusiveGpuController.' }
    $directory = Join-Path ([Environment]::GetFolderPath('Desktop')) ('Victus-Performance-recovery-' + [Guid]::NewGuid().ToString('N'))
    Write-Host 'Explicit release-only recovery. Close other Victus applications normally. CPU restores only still-owned PL fields; GPU requests one NVIDIA default Reset. No fan writes or Automatic activation. Original journals are backed up before release.'
    & $guardian --recover-gui-session --confirm-target HP-8C40-9D0R1LA-F18 --confirm-cpu-hardware-writes --confirm-exclusive-gpu-controller --module (Join-Path $app 'modules/IntelMSR.bin') --cpu-session $ExpectedCpuSession.ToString('D') --gpu-session $ExpectedGpuSession.ToString('D') --output-directory $directory
    $recoveryExit = $LASTEXITCODE
    Write-Host "Recovery evidence: $directory"
    if ($recoveryExit -ne 0) { throw "Recovery did not complete (code $recoveryExit). Retain the evidence and pending journals." }
    return
}
Push-Location $app
try {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $app 'VictusFanControl.App.exe'
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $app
    if ($Mode -in @('SelfTest','Soak')) {
        # Render/report outputs must not mutate the manifest-bound package.
        $fixtureOutput = Join-Path ([IO.Path]::GetTempPath()) ('Victus-Product-' + $Mode + '-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $fixtureOutput | Out-Null
        $start.WorkingDirectory = $fixtureOutput
        Write-Host "Zero-hardware fixture output: $fixtureOutput"
    }
    $start.Arguments = if ($Mode -eq 'SelfTest') { '--product-gui-self-test' } elseif ($Mode -eq 'Soak') { '--product-gui-soak-self-test' } else { '--modules-dir "' + (Join-Path $app 'modules') + '"' }
    if ($Mode -in @('AutomaticReview','AutomaticExtendedReview')) {
        $minutes = if ($Mode -eq 'AutomaticExtendedReview') { 45 } else { 5 }
        $start.Arguments += if ($Mode -eq 'AutomaticExtendedReview') { ' --product-automatic-extended-review' } else { ' --product-automatic-review' }
        Write-Host "Supervised Automatic review: exact HP 8C40/F.18, levels 10-50, maximum $minutes minutes per activation. Starts in Firmware. Selecting Automatic first applies both CPU/GPU limits, then arms the curve after confirmation. Firmware cancels preparation; limits retain their separate supervised session."
    }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "GUI exited with code $($process.ExitCode); retain logs and recovery journals." }
    } finally { $process.Dispose() }
} finally { Pop-Location }
