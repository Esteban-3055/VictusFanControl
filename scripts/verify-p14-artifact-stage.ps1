param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSourceHead,
    [Parameter(Mandatory = $true)][string]$ExpectedRepository,
    [Parameter(Mandatory = $true)][string]$ExpectedRef,
    [Parameter(Mandatory = $true)][ValidateRange(1, [long]::MaxValue)][long]$ExpectedRunId,
    [Parameter(Mandatory = $true)][ValidateRange(1, [int]::MaxValue)][int]$ExpectedRunNumber,
    [Parameter(Mandatory = $true)][string]$ExpectedArtifactName
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = [IO.Path]::GetFullPath($ArtifactDirectory)
$release = Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$zipName = [string]$release.productization.packageName
$zipPath = Join-Path $dir $zipName
$shaPath = $zipPath + '.sha256'
$attestationPath = Join-Path $dir 'P14-ARTIFACT-ATTESTATION.json'

$expectedFiles = @($zipName, ($zipName + '.sha256'), 'P14-ARTIFACT-ATTESTATION.json')
$actualFiles = @(Get-ChildItem -LiteralPath $dir -File | ForEach-Object { $_.Name } | Sort-Object)
if ($actualFiles.Count -ne $expectedFiles.Count) { throw "P14.4 artifact stage must contain exactly three files; found $($actualFiles.Count)." }
foreach ($name in $expectedFiles) { if ($actualFiles -notcontains $name) { throw "P14.4 artifact stage missing expected file: $name" } }

& (Join-Path $root 'scripts\verify-p14-package.ps1') -ZipPath $zipPath -Sha256Path $shaPath -ExpectedSourceHead $ExpectedSourceHead

$att = Get-Content -LiteralPath $attestationPath -Raw | ConvertFrom-Json
if ([int]$att.schemaVersion -ne 1) { throw 'P14.4 attestation schema mismatch.' }
if ([string]$att.kind -ne 'VictusFanControl.P14.4.CiArtifactAttestation') { throw 'P14.4 attestation kind mismatch.' }
if ([string]$att.version -ne [string]$release.version) { throw 'P14.4 attestation version mismatch.' }
if ([string]$att.targetProfileId -ne [string]$release.targetProfileId) { throw 'P14.4 attestation target mismatch.' }
if ([string]$att.sourceHead -ne $ExpectedSourceHead.ToLowerInvariant()) { throw 'P14.4 attestation source HEAD mismatch.' }
if ([string]$att.github.repository -ne $ExpectedRepository) { throw 'P14.4 attestation repository mismatch.' }
if ([string]$att.github.ref -ne $ExpectedRef) { throw 'P14.4 attestation ref mismatch.' }
if ([long]$att.github.runId -ne $ExpectedRunId) { throw 'P14.4 attestation run ID mismatch.' }
if ([int]$att.github.runNumber -ne $ExpectedRunNumber) { throw 'P14.4 attestation run number mismatch.' }
if ([string]$att.github.artifactName -ne $ExpectedArtifactName) { throw 'P14.4 attestation artifact name mismatch.' }

$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
$shaHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $shaPath).Hash.ToLowerInvariant()
if ([string]$att.package.fileName -ne $zipName) { throw 'P14.4 attestation package file mismatch.' }
if ([string]$att.package.sha256 -ne $zipHash) { throw 'P14.4 attestation payload SHA-256 mismatch.' }
if ([string]$att.package.sha256FileName -ne [IO.Path]::GetFileName($shaPath)) { throw 'P14.4 attestation SHA file name mismatch.' }
if ([string]$att.package.sha256FileSha256 -ne $shaHash) { throw 'P14.4 attestation SHA file digest mismatch.' }
if ([string]$att.pinnedExternalInputs.pawnIoModulesVersion -ne [string]$release.productization.pawnIoModulesVersion) { throw 'P14.4 attestation PawnIO version mismatch.' }
if ([string]$att.pinnedExternalInputs.pawnIoModulesArchiveSha256 -ne ([string]$release.productization.pawnIoModulesArchiveSha256).ToLowerInvariant()) { throw 'P14.4 attestation PawnIO archive SHA-256 mismatch.' }

if (-not [bool]$att.safetyBoundary.softwareOnly) { throw 'P14.4 attestation must remain software-only.' }
if ([bool]$att.safetyBoundary.hardwareExecution) { throw 'P14.4 attestation must record zero hardware execution.' }
if ([bool]$att.safetyBoundary.controlEnabledByDefault) { throw 'P14.4 attestation cannot enable default control.' }
if ([bool]$att.safetyBoundary.automaticPolicyEnabled) { throw 'P14.4 attestation cannot enable automatic policy.' }
if ([bool]$att.safetyBoundary.manualExecutionAuthorized) { throw 'P14.4 attestation cannot authorize Manual execution.' }
if ([bool]$att.safetyBoundary.automaticExecutionAuthorized) { throw 'P14.4 attestation cannot authorize Automatic execution.' }

Write-Host "HP 8C40 P14.4 retained-artifact stage verification: PASS ($zipHash)" -ForegroundColor Green
