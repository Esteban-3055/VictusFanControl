param(
    [Parameter(Mandatory=$true)][string]$RcArtifactDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceHead
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$zip=Join-Path $RcArtifactDirectory 'VictusFanControl-0.4.0-rc.1-win-x64.zip'
& (Join-Path $PSScriptRoot 'verify-p14-package.ps1') -ZipPath $zip -Sha256Path ($zip+'.sha256') -ExpectedSourceHead $SourceHead
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new package directory.'}
$publish=Join-Path ([IO.Path]::GetTempPath()) ('Victus-v1-publish-'+[Guid]::NewGuid().ToString('N'))
$modules=Join-Path ([IO.Path]::GetTempPath()) ('Victus-v1-modules-'+[Guid]::NewGuid().ToString('N'))
try {
    & (Join-Path $PSScriptRoot 'build-p14-publish-layout.ps1') -OutputRoot $publish -ProductRelease
    Expand-Archive -LiteralPath $zip -DestinationPath $modules
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $OutputDirectory -Recurse
    $prior=Join-Path $modules 'VictusFanControl-0.4.0-rc.1-win-x64'
    Copy-Item -LiteralPath (Join-Path $prior 'app/modules') -Destination (Join-Path $OutputDirectory 'app/modules') -Recurse
    Copy-Item -LiteralPath (Join-Path $prior 'watchdog/modules') -Destination (Join-Path $OutputDirectory 'watchdog/modules') -Recurse
    foreach($file in Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter 'VictusFanControl*.dll') {
        if([Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).FileVersion -ne '1.1.0.0'){throw "Version mismatch: $($file.Name)"}
    }
    foreach($file in @('Start-ProductGui.ps1','Start-PlatformThermalTest.ps1','Install-VictusFanControl.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repo ('release/'+$file)) -Destination $OutputDirectory
    }
    foreach($file in @('PRODUCT_V1.md','PRODUCT_PLATFORM_RETENTION_2026-10-09.md','PERFORMANCE_GUI_RECOVERY.md','PLATFORM_PHYSICAL_TEST_2026-10-08.md')) {
        Copy-Item -LiteralPath (Join-Path $repo ('docs/'+$file)) -Destination $OutputDirectory
    }
    foreach($file in @('LICENSE','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $OutputDirectory}
    $release=Get-Content -LiteralPath (Join-Path $repo 'release/product-v1.json') -Raw | ConvertFrom-Json
    $release | Add-Member -NotePropertyName sourceHead -NotePropertyValue $SourceHead
    $release | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'PRODUCT-RELEASE.json') -Encoding utf8
    New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'evidence') | Out-Null
    foreach($file in @($release.physicalEvidence)+@($release.offlinePolicyEvidence)) {
        Copy-Item -LiteralPath (Join-Path $repo ('release/'+$file)) -Destination (Join-Path $OutputDirectory 'evidence')
    }
    Copy-Item -LiteralPath (Join-Path $repo 'docs/evidence/product-quiet-curve-001126-2026-10-09') -Destination (Join-Path $OutputDirectory 'evidence') -Recurse
    $entries=@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring([IO.Path]::GetFullPath($OutputDirectory).Length+1).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    [ordered]@{
        schemaVersion=1;kind='VictusFanControl.ProductGuiRelease';version='1.1.0';appDirectory='app';sourceHead=$SourceHead
        inheritedModulesZipSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
        releaseStage='target-specific-release';finalReleaseReady=$true;normalAutomatic='authorized-exact-target';physicalGuiValidation='partial-pending-current-target-observations'
        productPlatformRetention='optional-disabled-default-AC-Battery-plus2-60s-fresh3s';platformThermalExperiment='explicit-physical-AC-ABBA-2580s-TZ01-DTT3-retention'
        manualAutomaticRetry='explicit-clean-release-fresh-runtime-same-gui-three-fresh-observations';startup='optional-one-attempt-30s-three-fresh-samples-coupled-performance';sessionRestart='explicit-clean-release-new-process-firmware'
        customGpuClock='configurable-210-to-2500';productAutomaticReview='explicit-only-300s-10-to-50';productAutomaticExtendedReview='explicit-only-2700s-10-to-50-16MiB-diagnostics';productAutomaticPerformance='required-both-before-fans';productAutomaticThermal='cpu-start90-active95-confirm2000ms-cpu99-immediate-raw-response';productAutomaticSourceTransition='bounded-4000ms-fresh-guardian-preserves-inertia';diagnostics='per-process-session-with-telemetry';curveMarkers='applied-request-and-draft-preview';performanceRecovery='explicit-release-only-exact-session-backups';files=$entries
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'PRODUCT-GUI-MANIFEST.json') -Encoding utf8
    & (Join-Path $OutputDirectory 'Start-ProductGui.ps1') -Mode FinalCheck
} finally {
    Remove-Item -LiteralPath $publish,$modules -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Product v1.0 package: PASS. Version/hash checks and packaged zero-hardware fixtures; no physical qualification performed.'
