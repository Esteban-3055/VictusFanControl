$ErrorActionPreference = 'Stop'
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message } }
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message } }

$root = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\build.yml') -Raw
$writer = Get-Content -LiteralPath (Join-Path $root 'scripts\write-p14-artifact-attestation.ps1') -Raw
$verifier = Get-Content -LiteralPath (Join-Path $root 'scripts\verify-p14-artifact-stage.ps1') -Raw
$package = Get-Content -LiteralPath (Join-Path $root 'scripts\package-p14-rc.ps1') -Raw

if ([string]$release.milestone -ne 'P14.4') { throw 'P14.4 formal closure milestone mismatch.' }
if ([string]$release.status -ne 'P14_4_RETAINED_ARTIFACT_CI_PASS_FORMALLY_CLOSED') { throw 'P14.4 formal closure status mismatch.' }
if (-not [bool]$release.productization.ciArtifactUploadImplemented) { throw 'P14.4 artifact upload implementation must be recorded.' }
if (-not [bool]$release.productization.ciArtifactUploadCiValidated) { throw 'P14.4 retained artifact CI validation must be recorded.' }
if (-not [bool]$release.productization.externalArtifactAttestationImplemented) { throw 'P14.4 external attestation implementation must be recorded.' }
if ([int]$release.productization.ciArtifactRetentionDays -ne 30) { throw 'P14.4 artifact retention mismatch.' }
if (-not [bool]$release.productization.artifactClosure.closed -or [string]$release.productization.artifactClosure.result -ne 'PASS') { throw 'P14.4 artifact closure must be PASS.' }
if ([string]$release.productization.artifactClosure.implementationHead -ne '1c7ee997b5bbac02eb89e1c7d3f91e7130828c1d') { throw 'P14.4 closure implementation HEAD mismatch.' }
if ([int]$release.productization.artifactClosure.sourceCiRunNumber -ne 1080 -or [long]$release.productization.artifactClosure.sourceCiRunId -ne 36907848705) { throw 'P14.4 closure CI identity mismatch.' }
if ([long]$release.productization.artifactClosure.artifactId -ne 11186116708) { throw 'P14.4 closure artifact ID mismatch.' }
if ([string]$release.productization.artifactClosure.githubArtifactDigest -ne 'sha256:eb42b9c7d8a5d29e7c30a4f35f19f7e07fecacb83dee26aaf638093cbf2826b2') { throw 'P14.4 closure wrapper digest mismatch.' }
if ([string]$release.productization.artifactClosure.downloadedArtifactSha256 -ne 'eb42b9c7d8a5d29e7c30a4f35f19f7e07fecacb83dee26aaf638093cbf2826b2') { throw 'P14.4 downloaded wrapper digest mismatch.' }
if ([string]$release.productization.artifactClosure.payloadZipSha256 -ne 'e0ceb47c4b3be61c98f2d494b652fb546bd0d7624b7e5f452e66627414376d65') { throw 'P14.4 closure payload digest mismatch.' }
if ([int]$release.productization.artifactClosure.artifactFileCount -ne 3) { throw 'P14.4 closure artifact file count mismatch.' }
if (-not [bool]$release.productization.artifactClosure.retainedArtifactDownloadedAndVerified) { throw 'P14.4 retained artifact must have been downloaded and verified.' }
if ([bool]$release.productization.artifactClosure.hardwareExecution) { throw 'P14.4 must record zero hardware execution.' }
if ([bool]$release.productization.finalSoftwareRcAuditClosed) { throw 'P14.4 must not close P14.5.' }

Assert-Contains $workflow 'HP 8C40 P14.4 final retained artifact verification' 'P14.4 final pre-upload verification step missing.'
Assert-Contains $workflow 'uses: actions/upload-artifact@v4' 'P14.4 must use GitHub Actions artifact upload v4.'
Assert-Contains $workflow 'id: p14_artifact' 'P14.4 upload step must expose artifact outputs.'
Assert-Contains $workflow 'name: VictusFanControl-0.4.0-rc.1-win-x64-${{ github.sha }}' 'P14.4 artifact name must bind the source HEAD.'
Assert-Contains $workflow 'path: ${{ runner.temp }}/VictusFanControl-P14.4-artifact' 'P14.4 upload path mismatch.'
Assert-Contains $workflow 'if-no-files-found: error' 'P14.4 missing-file behavior must fail closed.'
Assert-Contains $workflow 'retention-days: 30' 'P14.4 retention setting missing.'
Assert-Contains $workflow 'compression-level: 0' 'P14.4 wrapper should avoid recompressing the deterministic RC ZIP.'
Assert-Contains $workflow 'overwrite: false' 'P14.4 must not silently replace an artifact.'
Assert-Contains $workflow 'artifact-digest' 'P14.4 workflow must surface the GitHub wrapper digest outside the RC payload.'

$verifyIndex = $workflow.IndexOf('HP 8C40 P14.4 final retained artifact verification',[StringComparison]::Ordinal)
$uploadIndex = $workflow.IndexOf('uses: actions/upload-artifact@v4',[StringComparison]::Ordinal)
$lastSelfTestIndex = $workflow.IndexOf('HP fan backend self-test',[StringComparison]::Ordinal)
if ($verifyIndex -lt 0 -or $uploadIndex -lt 0 -or $verifyIndex -ge $uploadIndex) { throw 'P14.4 retained artifact must be verified before upload.' }
if ($uploadIndex -le $lastSelfTestIndex) { throw 'P14.4 artifact must be uploaded only after the full test suite.' }

Assert-Contains $writer 'VictusFanControl.P14.4.CiArtifactAttestation' 'P14.4 external attestation marker missing.'
Assert-Contains $writer 'hardwareExecution = $false' 'P14.4 attestation must record no hardware execution.'
Assert-Contains $verifier 'must contain exactly three files' 'P14.4 retained stage must have a closed file set.'
Assert-NotContains $writer 'artifact-digest' 'GitHub wrapper digest must not be embedded before upload.'
Assert-NotContains $package 'P14-ARTIFACT-ATTESTATION.json' 'P14.4 external attestation must remain outside the RC ZIP.'
Assert-NotContains $writer 'SetFanLevel' 'P14.4 writer must not contain fan-control writes.'
Assert-NotContains $verifier 'SetFanLevel' 'P14.4 verifier must not contain fan-control writes.'
Assert-NotContains $writer 'sc.exe' 'P14.4 writer must not mutate services.'
Assert-NotContains $verifier 'sc.exe' 'P14.4 verifier must not mutate services.'

Write-Host 'HP 8C40 P14.4 retained artifact invariant: PASS' -ForegroundColor Green
