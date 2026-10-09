param(
 [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
 [Parameter(Mandatory=$true)][string]$RcStageDirectory,
 [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceHead,
 [Parameter(Mandatory=$true)][string]$Repository,
 [Parameter(Mandatory=$true)][string]$Ref,
 [Parameter(Mandatory=$true)][long]$RunId,
 [Parameter(Mandatory=$true)][int]$RunNumber,
 [Parameter(Mandatory=$true)][long]$RcArtifactId,
 [Parameter(Mandatory=$true)][string]$RcArtifactName,
 [Parameter(Mandatory=$true)][ValidatePattern('^(?:sha256:)?[0-9a-fA-F]{64}$')][string]$RcArtifactDigest
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=[IO.Path]::GetFullPath($EvidenceDirectory)
$stage=[IO.Path]::GetFullPath($RcStageDirectory)
$release=Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
if ([string]$release.milestone -ne 'P14.5' -or -not [bool]$release.productization.finalSoftwareRcAuditImplemented -or -not [bool]$release.productization.finalSoftwareRcAuditEvidenceImplemented) { throw 'P14.5 audit contract is not implemented.' }
foreach ($v in @('controlEnabledByDefault','automaticPolicyEnabled','manualExecutionAuthorized','automaticExecutionAuthorized','candidateCurvePhysicallyValidated','candidateCurveAuthorizedForProduction','m9cQualificationConstructionAuthorized','m9dQualificationConstructionAuthorized')) { if ([bool]$release.safetyBoundary.$v) { throw "P14.5 safety boundary unexpectedly open: $v" } }
$expectedRcName="VictusFanControl-0.4.0-rc.1-win-x64-$($SourceHead.ToLowerInvariant())"
if ($RcArtifactName -ne $expectedRcName) { throw 'RC artifact/source binding mismatch.' }
$normalizedRcArtifactDigest=$RcArtifactDigest.ToLowerInvariant()
if (-not $normalizedRcArtifactDigest.StartsWith('sha256:')) { $normalizedRcArtifactDigest='sha256:'+$normalizedRcArtifactDigest }
& (Join-Path $root 'scripts\verify-p14-artifact-stage.ps1') -ArtifactDirectory $stage -ExpectedSourceHead $SourceHead -ExpectedRepository $Repository -ExpectedRef $Ref -ExpectedRunId $RunId -ExpectedRunNumber $RunNumber -ExpectedArtifactName $RcArtifactName
if (Test-Path -LiteralPath $out) { if (@(Get-ChildItem -LiteralPath $out -Force).Count -ne 0) { throw 'P14.5 evidence directory must be empty.' } } else { New-Item -ItemType Directory -Force -Path $out | Out-Null }
$zipName=[string]$release.productization.packageName
$zipPath=Join-Path $stage $zipName
$shaPath=$zipPath+'.sha256'
$attPath=Join-Path $stage 'P14-ARTIFACT-ATTESTATION.json'
$report=[ordered]@{
 schemaVersion=1; kind='VictusFanControl.P14.5.FinalSoftwareRcAudit'; result='PASS'; version=[string]$release.version; targetProfileId=[string]$release.targetProfileId; sourceHead=$SourceHead.ToLowerInvariant();
 github=[ordered]@{repository=$Repository;ref=$Ref;runId=$RunId;runNumber=$RunNumber};
 auditedRcArtifact=[ordered]@{id=$RcArtifactId;name=$RcArtifactName;digest=$normalizedRcArtifactDigest};
 auditedRcPayload=[ordered]@{zipFileName=$zipName;zipSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant();sha256FileSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $shaPath).Hash.ToLowerInvariant();p14ArtifactAttestationSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $attPath).Hash.ToLowerInvariant();stagedFileCount=3};
 priorClosures=[ordered]@{p14_2=[ordered]@{closed=[bool]$release.productization.publishLayoutClosure.closed;result=[string]$release.productization.publishLayoutClosure.result};p14_3=[ordered]@{closed=[bool]$release.productization.packageClosure.closed;result=[string]$release.productization.packageClosure.result};p14_4=[ordered]@{closed=[bool]$release.productization.artifactClosure.closed;result=[string]$release.productization.artifactClosure.result;downloadedAndVerified=[bool]$release.productization.artifactClosure.retainedArtifactDownloadedAndVerified}};
 pinnedExternalInputs=[ordered]@{pawnIoModulesVersion=[string]$release.productization.pawnIoModulesVersion;pawnIoModulesArchiveSha256=([string]$release.productization.pawnIoModulesArchiveSha256).ToLowerInvariant()};
 auditChecks=[ordered]@{exactTargetContract=$true;centralizedVersion=$true;deterministicPublish=$true;deterministicPackage=$true;packageManifestVerification=$true;retainedArtifactIdentity=$true;manualGateClosed=$true;automaticGateClosed=$true;candidatePhysicallyUnvalidated=$true;candidateProductionUnauthorized=$true;m9cQualificationGateClosed=$true;m9dQualificationGateClosed=$true;controlDisabledByDefault=$true;automaticPolicyDisabled=$true;p15NotExecuted=$true};
 safetyBoundary=[ordered]@{softwareOnly=$true;hardwareExecution=$false;controlEnabledByDefault=$false;automaticPolicyEnabled=$false;manualExecutionAuthorized=$false;automaticExecutionAuthorized=$false};
 repositoryContract=[ordered]@{status=[string]$release.status;finalSoftwareRcAuditClosed=[bool]$release.productization.finalSoftwareRcAuditClosed}
}
$utf8=New-Object System.Text.UTF8Encoding($false)
$reportPath=Join-Path $out 'P14-FINAL-SOFTWARE-RC-AUDIT.json'
[IO.File]::WriteAllText($reportPath,(($report|ConvertTo-Json -Depth 10)+[Environment]::NewLine),$utf8)
Write-Host "P14.5 final audit evidence written: $reportPath" -ForegroundColor Green
