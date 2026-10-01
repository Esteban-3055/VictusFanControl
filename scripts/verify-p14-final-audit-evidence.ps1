param(
 [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
 [Parameter(Mandatory=$true)][string]$RcStageDirectory,
 [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSourceHead,
 [Parameter(Mandatory=$true)][string]$ExpectedRepository,
 [Parameter(Mandatory=$true)][string]$ExpectedRef,
 [Parameter(Mandatory=$true)][long]$ExpectedRunId,
 [Parameter(Mandatory=$true)][int]$ExpectedRunNumber,
 [Parameter(Mandatory=$true)][long]$ExpectedRcArtifactId,
 [Parameter(Mandatory=$true)][string]$ExpectedRcArtifactName,
 [Parameter(Mandatory=$true)][ValidatePattern('^sha256:[0-9a-fA-F]{64}$')][string]$ExpectedRcArtifactDigest
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dir=[IO.Path]::GetFullPath($EvidenceDirectory)
$stage=[IO.Path]::GetFullPath($RcStageDirectory)
$release=Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$files=@(Get-ChildItem -LiteralPath $dir -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'P14-FINAL-SOFTWARE-RC-AUDIT.json') { throw 'P14.5 evidence must contain exactly one audit JSON.' }
$r=Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json
if ([int]$r.schemaVersion -ne 1 -or [string]$r.kind -ne 'VictusFanControl.P14.5.FinalSoftwareRcAudit' -or [string]$r.result -ne 'PASS') { throw 'P14.5 report identity/result mismatch.' }
if ([string]$r.version -ne '0.4.0-rc.1' -or [string]$r.targetProfileId -ne 'HP-8C40-9D0R1LA-F18' -or [string]$r.sourceHead -ne $ExpectedSourceHead.ToLowerInvariant()) { throw 'P14.5 report version/target/source mismatch.' }
if ([string]$r.github.repository -ne $ExpectedRepository -or [string]$r.github.ref -ne $ExpectedRef -or [long]$r.github.runId -ne $ExpectedRunId -or [int]$r.github.runNumber -ne $ExpectedRunNumber) { throw 'P14.5 report GitHub identity mismatch.' }
if ([long]$r.auditedRcArtifact.id -ne $ExpectedRcArtifactId -or [string]$r.auditedRcArtifact.name -ne $ExpectedRcArtifactName -or [string]$r.auditedRcArtifact.digest -ne $ExpectedRcArtifactDigest.ToLowerInvariant()) { throw 'P14.5 audited RC artifact identity mismatch.' }
$zipName=[string]$release.productization.packageName;$zip=Join-Path $stage $zipName;$sha=$zip+'.sha256';$att=Join-Path $stage 'P14-ARTIFACT-ATTESTATION.json'
if ([string]$r.auditedRcPayload.zipFileName -ne $zipName -or [string]$r.auditedRcPayload.zipSha256 -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant() -or [string]$r.auditedRcPayload.sha256FileSha256 -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $sha).Hash.ToLowerInvariant() -or [string]$r.auditedRcPayload.p14ArtifactAttestationSha256 -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $att).Hash.ToLowerInvariant() -or [int]$r.auditedRcPayload.stagedFileCount -ne 3) { throw 'P14.5 audited RC payload identity mismatch.' }
foreach($n in @('exactTargetContract','centralizedVersion','deterministicPublish','deterministicPackage','packageManifestVerification','retainedArtifactIdentity','manualGateClosed','automaticGateClosed','candidatePhysicallyUnvalidated','candidateProductionUnauthorized','m9cQualificationGateClosed','m9dQualificationGateClosed','controlDisabledByDefault','automaticPolicyDisabled','p15NotExecuted')){if(-not [bool]$r.auditChecks.$n){throw "P14.5 audit check failed: $n"}}
if (-not [bool]$r.safetyBoundary.softwareOnly -or [bool]$r.safetyBoundary.hardwareExecution -or [bool]$r.safetyBoundary.controlEnabledByDefault -or [bool]$r.safetyBoundary.automaticPolicyEnabled -or [bool]$r.safetyBoundary.manualExecutionAuthorized -or [bool]$r.safetyBoundary.automaticExecutionAuthorized) { throw 'P14.5 report safety boundary mismatch.' }
Write-Host 'HP 8C40 P14.5 final audit evidence verification: PASS' -ForegroundColor Green
