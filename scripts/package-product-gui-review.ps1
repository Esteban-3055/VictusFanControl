param(
    [Parameter(Mandatory=$true)][string]$RcArtifactDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceHead
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$zip = Join-Path $RcArtifactDirectory 'VictusFanControl-0.4.0-rc.1-win-x64.zip'
& (Join-Path $PSScriptRoot 'verify-p14-package.ps1') -ZipPath $zip -Sha256Path ($zip + '.sha256') -ExpectedSourceHead $SourceHead
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory; existing packages are never overwritten.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Expand-Archive -LiteralPath $zip -DestinationPath $OutputDirectory
Copy-Item -LiteralPath (Join-Path $root 'release/Start-ProductGui.ps1') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $root 'docs/GUI_VICTUS_VALIDATION.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $root 'docs/GUI_PRODUCT_PHASE.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $root 'docs/QUIET_PRODUCT_PRESETS.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $root 'docs/AUTOMATIC_PRODUCT_SESSION.md') -Destination $OutputDirectory
$entries = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring([IO.Path]::GetFullPath($OutputDirectory).Length+1).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
$manifest = [ordered]@{
    schemaVersion=1;kind='VictusFanControl.ProductGuiReview';sourceHead=$SourceHead
    inheritedRcZipSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    physicalGuiValidation='pending';normalAutomatic='closed';customGpuClock='configurable-210-to-2500';productAutomaticReview='explicit-only-300s-10-to-50';productAutomaticPerformance='required-both-before-fans';diagnostics='per-process-session-with-telemetry';curveMarkers='applied-request-and-draft-preview';files=$entries
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'PRODUCT-GUI-MANIFEST.json') -Encoding utf8
& (Join-Path $OutputDirectory 'Start-ProductGui.ps1') -Mode Verify
Write-Host 'Product GUI review package: PASS. No hardware execution or gate promotion performed.'
