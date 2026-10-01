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
if ([string]$release.milestone -ne 'P14.1') { throw 'P14.1 milestone mismatch.' }
if ([string]$release.status -ne 'P14_1_SOFTWARE_READINESS_BASELINE') { throw 'P14.1 status mismatch.' }
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

Assert-True (-not [bool]$release.productization.centralizedRcVersionImplemented) 'P14.1 must not claim P14.2 version centralization.'
Assert-True (-not [bool]$release.productization.deterministicPublishImplemented) 'P14.1 must not claim deterministic publish.'
Assert-True (-not [bool]$release.productization.deterministicPackageImplemented) 'P14.1 must not claim deterministic packaging.'
Assert-True (-not [bool]$release.productization.sha256ManifestImplemented) 'P14.1 must not claim SHA-256 manifest completion.'
Assert-True (-not [bool]$release.productization.ciArtifactUploadImplemented) 'P14.1 must not claim CI artifact upload.'
Assert-True (-not [bool]$release.productization.finalSoftwareRcAuditClosed) 'P14.1 must not claim final P14 closure.'

Assert-Contains $p14doc 'P14.1 — software readiness baseline' 'P14.1 documentation boundary missing.'

Write-Host 'HP 8C40 P14.1 software readiness baseline invariant: PASS'
