$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Value,[string]$Message) {
    if (-not $Value) { throw $Message }
}
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message }
}

$root = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$watchdogGate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$p14doc = Get-Content -LiteralPath (Join-Path $root 'docs\P14_RELEASE_CANDIDATE.md') -Raw

if ([int]$release.schemaVersion -ne 1) { throw 'P14.1 release contract schemaVersion must be 1.' }
if ([string]$release.milestone -ne 'P14.5') { throw 'P14.5 milestone mismatch.' }
$finalAuditClosed = [bool]$release.productization.finalSoftwareRcAuditClosed
if ($finalAuditClosed) {
    if ([string]$release.status -ne 'P14_5_FINAL_SOFTWARE_RC_AUDIT_CI_PASS_FORMALLY_CLOSED') { throw 'P14.5 closed status mismatch.' }
} else {
    if ([string]$release.status -ne 'P14_5_FINAL_SOFTWARE_RC_AUDIT_IMPLEMENTED_AWAITING_CI') { throw 'P14.5 implementation status mismatch.' }
}
if ([string]$release.targetProfileId -ne 'HP-8C40-9D0R1LA-F18') { throw 'P14.1 exact target mismatch.' }

if ([string]$release.p13Baseline.head -ne '7395a8c14afcaf352ad5e5be48f66c897f03fd2e') { throw 'P14.1 P13 baseline HEAD mismatch.' }
if ([int]$release.p13Baseline.ciRunNumber -ne 1072) { throw 'P14.1 P13 baseline CI number mismatch.' }
if ([long]$release.p13Baseline.ciRunId -ne 36899789506) { throw 'P14.1 P13 baseline CI ID mismatch.' }
if ([string]$release.p13Baseline.ciResult -ne 'SUCCESS') { throw 'P14.1 requires successful P13 baseline CI.' }

Assert-True ([bool]$release.safetyBoundary.softwareOnly) 'P14.1 must remain software-only.'
Assert-True (-not [bool]$release.safetyBoundary.controlEnabledByDefault) 'P14.1 contract must keep default control OFF.'
Assert-True (-not [bool]$release.safetyBoundary.automaticPolicyEnabled) 'P14.1 contract must keep automatic policy OFF.'
Assert-True (-not [bool]$release.safetyBoundary.manualExecutionAuthorized) 'P14.1 contract must keep Manual execution closed.'
Assert-True (-not [bool]$release.safetyBoundary.automaticExecutionAuthorized) 'P14.1 contract must keep Automatic execution closed.'

Assert-True (-not [bool]$profile.control.enabledByDefault) 'Profile default control must remain OFF.'
Assert-True (-not [bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Profile automatic policy must remain OFF.'
Assert-True ([bool]$profile.control.adaptivePolicyPreparation.p13SoftwareComplete) 'P14.1 requires P13 software completion.'
Assert-True ([bool]$profile.control.adaptivePolicyPreparation.p13SoftwareClosure.closed) 'P14.1 requires formal P13 closure.'
if ([string]$profile.control.adaptivePolicyPreparation.p13SoftwareClosure.result -ne 'PASS') { throw 'P14.1 requires P13 PASS.' }

Assert-Contains $gate 'ManualExecutionAuthorized = false' 'Manual execution gate must remain closed.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'Automatic execution gate must remain closed.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'Candidate V1 must remain physically unvalidated.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'Candidate V1 must remain unauthorized for production.'
Assert-Contains $watchdogGate 'M9CPhysicalQualificationConstructionAuthorized = false' 'M9C qualification gate must remain closed.'
Assert-Contains $watchdogGate 'M9DPhysicalQualificationConstructionAuthorized = false' 'M9D qualification gate must remain closed.'

Assert-True ([bool]$release.productization.centralizedRcVersionImplemented) 'P14.2 must centralize the RC version.'
Assert-True ([bool]$release.productization.deterministicPublishImplemented) 'P14.2 must implement the fixed publish layout.'
Assert-True ([bool]$release.productization.publishLayoutCiValidated) 'P14.2 publish layout must be CI validated after formal closure.'
if (-not [bool]$release.productization.publishLayoutClosure.closed) { throw 'P14.2 publish-layout closure must be recorded closed.' }
if ([string]$release.productization.publishLayoutClosure.result -ne 'PASS') { throw 'P14.2 publish-layout closure result must be PASS.' }
if ([string]$release.productization.publishLayoutClosure.sourceHead -ne '7bc405b4103014e52cf606a6064c71d748123ad7') { throw 'P14.2 closure source HEAD mismatch.' }
if ([int]$release.productization.publishLayoutClosure.sourceCiRunNumber -ne 1076) { throw 'P14.2 closure CI number mismatch.' }
if ([long]$release.productization.publishLayoutClosure.sourceCiRunId -ne 36901860532) { throw 'P14.2 closure CI ID mismatch.' }
if ([string]$release.productization.publishLayoutClosure.sourceCiResult -ne 'SUCCESS') { throw 'P14.2 closure CI result mismatch.' }
Assert-True (-not [bool]$release.productization.publishLayoutClosure.hardwareExecution) 'P14.2 closure must record no hardware execution.'
Assert-True ([bool]$release.productization.deterministicPackageImplemented) 'P14.3 deterministic packaging must be implemented.'
Assert-True ([bool]$release.productization.sha256ManifestImplemented) 'P14.3 SHA-256 manifest must be implemented.'
Assert-True ([bool]$release.productization.packageCiValidated) 'P14.3 package must be CI validated after formal closure.'
if (-not [bool]$release.productization.packageClosure.closed) { throw 'P14.3 package closure must be recorded closed.' }
if ([string]$release.productization.packageClosure.result -ne 'PASS') { throw 'P14.3 package closure result must be PASS.' }
if ([string]$release.productization.packageClosure.sourceHead -ne '9ca006d98b369654ff1fd6ffc3fa7cb1c8745250') { throw 'P14.3 closure source HEAD mismatch.' }
if ([int]$release.productization.packageClosure.sourceCiRunNumber -ne 1078) { throw 'P14.3 closure CI number mismatch.' }
if ([long]$release.productization.packageClosure.sourceCiRunId -ne 36903426730) { throw 'P14.3 closure CI ID mismatch.' }
if ([string]$release.productization.packageClosure.zipSha256 -ne '603ac7b2ca6816fe00002598410983ba031529ec168a70c5ec59041e5a71756c') { throw 'P14.3 closure ZIP SHA-256 mismatch.' }
Assert-True (-not [bool]$release.productization.packageClosure.hardwareExecution) 'P14.3 closure must record no hardware execution.'
Assert-True ([bool]$release.productization.ciArtifactUploadImplemented) 'P14.4 CI artifact upload must be implemented.'
Assert-True ([bool]$release.productization.ciArtifactUploadCiValidated) 'P14.4 retained artifact must be CI validated.'
Assert-True ([bool]$release.productization.externalArtifactAttestationImplemented) 'P14.4 external attestation must be implemented.'
if ([int]$release.productization.ciArtifactRetentionDays -ne 30) { throw 'P14.4 retention mismatch.' }
if (-not [bool]$release.productization.artifactClosure.closed) { throw 'P14.4 artifact closure must be closed.' }
if ([string]$release.productization.artifactClosure.result -ne 'PASS') { throw 'P14.4 artifact closure result must be PASS.' }
if ([string]$release.productization.artifactClosure.implementationHead -ne '1c7ee997b5bbac02eb89e1c7d3f91e7130828c1d') { throw 'P14.4 implementation HEAD mismatch.' }
if ([int]$release.productization.artifactClosure.sourceCiRunNumber -ne 1080) { throw 'P14.4 CI run number mismatch.' }
if ([long]$release.productization.artifactClosure.sourceCiRunId -ne 36907848705) { throw 'P14.4 CI run ID mismatch.' }
if ([long]$release.productization.artifactClosure.artifactId -ne 11186116708) { throw 'P14.4 retained artifact ID mismatch.' }
if ([string]$release.productization.artifactClosure.githubArtifactDigest -ne 'sha256:eb42b9c7d8a5d29e7c30a4f35f19f7e07fecacb83dee26aaf638093cbf2826b2') { throw 'P14.4 GitHub artifact digest mismatch.' }
if ([string]$release.productization.artifactClosure.payloadZipSha256 -ne 'e0ceb47c4b3be61c98f2d494b652fb546bd0d7624b7e5f452e66627414376d65') { throw 'P14.4 retained payload ZIP digest mismatch.' }
Assert-True ([bool]$release.productization.artifactClosure.packageManifestVerified) 'P14.4 package manifest verification missing.'
Assert-True ([bool]$release.productization.artifactClosure.sha256FileVerified) 'P14.4 adjacent SHA-256 verification missing.'
Assert-True ([bool]$release.productization.artifactClosure.externalAttestationVerified) 'P14.4 external attestation verification missing.'
Assert-True ([bool]$release.productization.artifactClosure.retainedArtifactDownloadedAndVerified) 'P14.4 retained artifact download verification missing.'
Assert-True (-not [bool]$release.productization.artifactClosure.hardwareExecution) 'P14.4 must record no hardware execution.'
Assert-True ([bool]$release.productization.finalSoftwareRcAuditImplemented) 'P14.5 final software RC audit must be implemented.'
Assert-True ([bool]$release.productization.finalSoftwareRcAuditEvidenceImplemented) 'P14.5 final audit evidence must be implemented.'
if ([int]$release.productization.finalSoftwareRcAuditRetentionDays -ne 30) { throw 'P14.5 audit retention mismatch.' }
if ($finalAuditClosed) {
    Assert-True ([bool]$release.productization.finalSoftwareRcAuditCiValidated) 'P14.5 closed state requires CI validation.'
    if (-not [bool]$release.productization.finalSoftwareRcAuditClosure.closed) { throw 'P14.5 final audit closure must be recorded closed.' }
    if ([string]$release.productization.finalSoftwareRcAuditClosure.result -ne 'PASS') { throw 'P14.5 final audit closure result must be PASS.' }
    Assert-True (-not [bool]$release.productization.finalSoftwareRcAuditClosure.hardwareExecution) 'P14.5 final audit closure must record no hardware execution.'
} else {
    Assert-True (-not [bool]$release.productization.finalSoftwareRcAuditCiValidated) 'P14.5 implementation must not pre-claim CI validation.'
}

Assert-Contains $p14doc 'P14.1 — software readiness baseline' 'P14.1 documentation boundary missing.'

Write-Host 'HP 8C40 P14 software readiness invariant: PASS'
