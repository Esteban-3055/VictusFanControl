$ErrorActionPreference = 'Stop'
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message } }
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message } }
$root = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$watchdogGate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$layout = Get-Content -LiteralPath (Join-Path $root 'release\p14-publish-layout.json') -Raw | ConvertFrom-Json
$external = Get-Content -LiteralPath (Join-Path $root 'release\p14-external-inputs.json') -Raw | ConvertFrom-Json
$readme = Get-Content -LiteralPath (Join-Path $root 'release\README-RC.txt') -Raw
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\build.yml') -Raw
$writer = Get-Content -LiteralPath (Join-Path $root 'scripts\write-p14-final-audit-attestation.ps1') -Raw
$verifier = Get-Content -LiteralPath (Join-Path $root 'scripts\verify-p14-final-audit-evidence.ps1') -Raw
if ([string]$release.milestone -ne 'P14.5') { throw 'P14.5 milestone mismatch.' }
$closed = [bool]$release.productization.finalSoftwareRcAuditClosed
if ($closed) { if ([string]$release.status -ne 'P14_5_FINAL_SOFTWARE_RC_AUDIT_CI_PASS_FORMALLY_CLOSED') { throw 'P14.5 closed status mismatch.' } } else { if ([string]$release.status -ne 'P14_5_FINAL_SOFTWARE_RC_AUDIT_IMPLEMENTED_AWAITING_CI') { throw 'P14.5 implementation status mismatch.' } }
if ([string]$release.version -ne '0.4.0-rc.1' -or [string]$release.targetProfileId -ne 'HP-8C40-9D0R1LA-F18') { throw 'P14.5 version/target mismatch.' }
Assert-True ([bool]$release.safetyBoundary.softwareOnly) 'P14.5 must remain software-only.'
foreach ($v in @('controlEnabledByDefault','automaticPolicyEnabled','manualExecutionAuthorized','automaticExecutionAuthorized','candidateCurvePhysicallyValidated','candidateCurveAuthorizedForProduction','m9cQualificationConstructionAuthorized','m9dQualificationConstructionAuthorized')) { if ([bool]$release.safetyBoundary.$v) { throw "P14.5 safety boundary unexpectedly open: $v" } }
Assert-True (-not [bool]$profile.control.enabledByDefault) 'Profile default control must remain OFF.'
Assert-True (-not [bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Profile automatic policy must remain OFF.'
. (Join-Path $PSScriptRoot 'p16c-promotion-boundary.ps1')
Assert-CurrentManualAuthorizationBoundary $gate
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'Automatic execution gate must remain false.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'Candidate must remain unvalidated.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'Candidate must remain unauthorized.'
Assert-Contains $watchdogGate 'M9CPhysicalQualificationConstructionAuthorized = false' 'M9C must remain closed.'
Assert-Contains $watchdogGate 'M9DPhysicalQualificationConstructionAuthorized = false' 'M9D must remain closed.'
foreach ($c in @($release.productization.publishLayoutClosure,$release.productization.packageClosure,$release.productization.artifactClosure)) { if (-not [bool]$c.closed -or [string]$c.result -ne 'PASS' -or [bool]$c.hardwareExecution) { throw 'P14.5 requires prior software closures PASS/no-hardware.' } }
Assert-True ([bool]$release.productization.artifactClosure.retainedArtifactDownloadedAndVerified) 'P14.5 requires verified retained P14.4 artifact.'
Assert-True ([bool]$release.productization.finalSoftwareRcAuditImplemented) 'P14.5 audit implementation missing.'
Assert-True ([bool]$release.productization.finalSoftwareRcAuditEvidenceImplemented) 'P14.5 audit evidence implementation missing.'
if ([int]$release.productization.finalSoftwareRcAuditRetentionDays -ne 30) { throw 'P14.5 retention mismatch.' }
if ($closed) {
    Assert-True ([bool]$release.productization.finalSoftwareRcAuditCiValidated) 'Closed audit requires CI validation.'
    Assert-True ([bool]$release.productization.finalSoftwareRcAuditClosure.closed) 'Closure object missing.'
    if ([string]$release.productization.finalSoftwareRcAuditClosure.result -ne 'PASS' -or [bool]$release.productization.finalSoftwareRcAuditClosure.hardwareExecution) { throw 'P14.5 closure evidence invalid.' }
    if ([string]$release.productization.finalSoftwareRcAuditClosure.auditedSourceHead -ne 'eebcdd5e833256466c1ae023c35f7cef8d40d6ec') { throw 'P14.5 audited source HEAD mismatch.' }
    if ([int]$release.productization.finalSoftwareRcAuditClosure.sourceCiRunNumber -ne 1084) { throw 'P14.5 source CI run number mismatch.' }
    if ([long]$release.productization.finalSoftwareRcAuditClosure.sourceCiRunId -ne 36911964126) { throw 'P14.5 source CI run ID mismatch.' }
    if ([long]$release.productization.finalSoftwareRcAuditClosure.rcArtifactId -ne 11187157320) { throw 'P14.5 RC artifact ID mismatch.' }
    if ([string]$release.productization.finalSoftwareRcAuditClosure.rcArtifactDigest -ne 'sha256:588058a8b57c0ca1bb41649288682e0981be845ab747654472d3d10c88d572b5') { throw 'P14.5 RC artifact digest mismatch.' }
    if ([string]$release.productization.finalSoftwareRcAuditClosure.rcPayloadZipSha256 -ne '704983caa20abb21c3520ffbd165ad69f9843139b6b2fa47d9e9e2448a32ef68') { throw 'P14.5 RC payload ZIP digest mismatch.' }
    if ([int]$release.productization.finalSoftwareRcAuditClosure.rcPayloadManifestFilesVerified -ne 61) { throw 'P14.5 RC manifest verification count mismatch.' }
    if ([long]$release.productization.finalSoftwareRcAuditClosure.auditArtifactId -ne 11186817552) { throw 'P14.5 audit artifact ID mismatch.' }
    if ([string]$release.productization.finalSoftwareRcAuditClosure.auditArtifactDigest -ne 'sha256:e0b50389462cd2a3777a0e0deadddad365d8b5f4ce0eae3ee7442da7f2975975') { throw 'P14.5 audit artifact digest mismatch.' }
    if ([string]$release.productization.finalSoftwareRcAuditClosure.auditJsonSha256 -ne 'c3497104ffcb3dbb8de13c5c06b1cc746a84ad3ebf732f425c008ca735b7141a') { throw 'P14.5 audit JSON digest mismatch.' }
    if ([string]$release.productization.finalSoftwareRcAuditClosure.canonicalM9HeadRechecked -ne '9b49a57661b683a9ef67be510047d4ca364292f0') { throw 'P14.5 canonical M9 HEAD mismatch.' }
    if ([int]$release.productization.finalSoftwareRcAuditClosure.canonicalM9CiRunNumber -ne 1053 -or [long]$release.productization.finalSoftwareRcAuditClosure.canonicalM9CiRunId -ne 36856124313 -or [string]$release.productization.finalSoftwareRcAuditClosure.canonicalM9CiResult -ne 'SUCCESS') { throw 'P14.5 canonical M9 CI identity mismatch.' }
    Assert-True (-not [bool]$release.productization.finalSoftwareRcAuditClosure.p15Executed) 'P14.5 closure must record P15 not executed.'
} else {
    Assert-True (-not [bool]$release.productization.finalSoftwareRcAuditCiValidated) 'Implementation cannot pre-claim CI validation.'
}
Assert-Contains $props '<Version>0.4.0-rc.1</Version>' 'Centralized version drifted.'
if ([string]$layout.version -ne '0.4.0-rc.1' -or [string]$layout.runtimeIdentifier -ne 'win-x64' -or [bool]$layout.selfContained -or [bool]$layout.publishSingleFile -or [bool]$layout.debugSymbolsIncluded -or [bool]$layout.readyToRun) { throw 'Publish layout drifted.' }
if ([string]$external.pawnIoModules.version -ne '0.2.11' -or [string]$external.pawnIoModules.sha256 -ne '43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4') { throw 'Pinned PawnIO input drifted.' }
Assert-Contains $readme 'THIS IS A PRE-P15 SOFTWARE RC.' 'README pre-P15 boundary missing.'
Assert-Contains $workflow 'HP 8C40 P14.5 write final audit evidence' 'P14.5 writer CI step missing.'
Assert-Contains $workflow 'HP 8C40 P14.5 upload final audit evidence' 'P14.5 upload CI step missing.'
Assert-Contains $workflow 'P14_RC_ARTIFACT_DIGEST: ${{ steps.p14_artifact.outputs.artifact-digest }}' 'P14.5 must bind current RC artifact digest.'
Assert-NotContains $writer 'SetFanLevel' 'P14.5 writer must not contain fan writes.'
Assert-NotContains $verifier 'SetFanLevel' 'P14.5 verifier must not contain fan writes.'
Assert-NotContains $writer 'sc.exe' 'P14.5 writer must not mutate services.'
Assert-NotContains $verifier 'sc.exe' 'P14.5 verifier must not mutate services.'
Write-Host 'HP 8C40 P14.5 final software RC audit invariant: PASS' -ForegroundColor Green
