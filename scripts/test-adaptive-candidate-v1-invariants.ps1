$ErrorActionPreference = 'Stop'

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message }
}

$root = Split-Path -Parent $PSScriptRoot
$source = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$selfTest = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1SelfTest.cs') -Raw
$candidate = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.adaptive-candidate-v1.shadow.json') -Raw | ConvertFrom-Json
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json

Assert-Contains $source 'HP-8C40-ADAPTIVE-CANDIDATE-V1' 'P12 candidate ID is missing.'
Assert-Contains $source 'PhysicallyValidated = false' 'P12 candidate must remain physically unvalidated.'
Assert-Contains $source 'AuthorizedForProduction = false' 'P12 candidate must remain unauthorized for production.'
Assert-Contains $selfTest 'TestThermalCeilings' 'P12 thermal ceiling self-test is missing.'
Assert-Contains $selfTest 'TestUpwardSlew' 'P12 upward slew self-test is missing.'

if ([string]$candidate.targetProfileId -ne 'HP-8C40-9D0R1LA-F18') { throw 'P12 candidate config target mismatch.' }
if ([string]$candidate.purpose -ne 'shadow-only') { throw 'P12 candidate config must remain shadow-only.' }
if ([bool]$candidate.authorizedForProduction) { throw 'P12 candidate config must remain unauthorized for production.' }
if ([int]$candidate.minimumLevel -ne 10 -or [int]$candidate.maximumLevel -ne 50) { throw 'P12 candidate envelope must remain exactly 10..50.' }
if ([int]$candidate.maximumUpStepPerSample -ne 4) { throw 'P12 candidate upward slew changed unexpectedly.' }
if ([int]$candidate.maximumDownStepPerSample -ne 1) { throw 'P12 candidate downward slew changed unexpectedly.' }
if ([int]$candidate.decreaseConfirmationSamples -ne 5) { throw 'P12 candidate decrease confirmation changed unexpectedly.' }

$prep = $profile.control.adaptivePolicyPreparation
if (-not [bool]$prep.candidateCurveDefined) { throw 'Profile must record the P12 candidate curve.' }
if ([string]$prep.candidateCurveId -ne 'HP-8C40-ADAPTIVE-CANDIDATE-V1') { throw 'Profile candidate curve ID mismatch.' }
if ([bool]$prep.candidateCurveValidated) { throw 'P12 candidate must not be marked physically validated.' }
if ([bool]$prep.candidateCurveAuthorizedForProduction) { throw 'P12 candidate must not be production-authorized.' }
if ([bool]$prep.productionCurveDefined) { throw 'P12 must not claim a final production curve is defined.' }
if ([bool]$prep.productionCurveValidated) { throw 'P12 must not claim a production curve is validated.' }
if ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) { throw 'P12 must keep automatic policy OFF.' }

Write-Host 'HP 8C40 P12 adaptive candidate curve invariant: PASS'
