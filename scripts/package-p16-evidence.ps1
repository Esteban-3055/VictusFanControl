param(
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [Parameter(Mandatory=$true)][string]$RepositoryRoot
)
$ErrorActionPreference='Stop'
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
$RepositoryRoot=[IO.Path]::GetFullPath($RepositoryRoot)
if(-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)){throw "P16 evidence directory missing: $EvidenceRoot"}
$summaryPath=Join-Path $EvidenceRoot 'p16-harness-summary.json'
if(-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)){throw 'P16 harness summary is missing.'}
$summary=Get-Content -LiteralPath $summaryPath -Raw|ConvertFrom-Json
$attemptFenceRequired=[bool]$summary.attemptFenceClaimed
$head=(& git -C $RepositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){throw 'P16 packager could not resolve repository HEAD.'}
$status=(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
$headPath=Join-Path $EvidenceRoot 'git-head.txt'
$statusPath=Join-Path $EvidenceRoot 'git-status.txt'
$head | Set-Content -LiteralPath $headPath -Encoding ASCII
$status | Set-Content -LiteralPath $statusPath -Encoding UTF8
$manifestPath=Join-Path $EvidenceRoot 'p16-package-manifest.json'
$files=@(
 [pscustomobject]@{role='harness-summary';path=$summaryPath;required=$true},
 [pscustomobject]@{role='authorization-ci';path=(Join-Path $EvidenceRoot 'p16-authorization-ci.json');required=$attemptFenceRequired},
 [pscustomobject]@{role='app-log-segment';path=(Join-Path $EvidenceRoot 'p16-app-log-segment.txt');required=$false},
 [pscustomobject]@{role='baseline-ff';path=(Join-Path $EvidenceRoot 'p16-baseline-ff.json');required=$false},
 [pscustomobject]@{role='setpoint-30-initial';path=(Join-Path $EvidenceRoot 'p16-setpoint-30-initial.json');required=$false},
 [pscustomobject]@{role='journal-30-initial';path=(Join-Path $EvidenceRoot 'p16-journal-30-initial.json');required=$false},
 [pscustomobject]@{role='setpoint-40';path=(Join-Path $EvidenceRoot 'p16-setpoint-40.json');required=$false},
 [pscustomobject]@{role='journal-40';path=(Join-Path $EvidenceRoot 'p16-journal-40.json');required=$false},
 [pscustomobject]@{role='setpoint-30-return';path=(Join-Path $EvidenceRoot 'p16-setpoint-30-return.json');required=$false},
 [pscustomobject]@{role='journal-30-return';path=(Join-Path $EvidenceRoot 'p16-journal-30-return.json');required=$false},
 [pscustomobject]@{role='firmware-ff';path=(Join-Path $EvidenceRoot 'p16-firmware-ff.json');required=$false},
 [pscustomobject]@{role='post-exit-ff';path=(Join-Path $EvidenceRoot 'p16-post-exit-ff.json');required=$false},
 [pscustomobject]@{role='cleanup-ff';path=(Join-Path $EvidenceRoot 'p16-cleanup-ff.json');required=$false},
 [pscustomobject]@{role='service-snapshots';path=(Join-Path $EvidenceRoot 'p16-service-snapshots.json');required=$false},
 [pscustomobject]@{role='watchdog-log-segment';path=(Join-Path $EvidenceRoot 'p16-watchdog-log-segment.txt');required=$false},
 [pscustomobject]@{role='failsafe-log';path=(Join-Path $EvidenceRoot 'p16-failsafe.log');required=$false},
 [pscustomobject]@{role='attempt-fence';path=(Join-Path $EvidenceRoot 'p16-attempt-fence.json');required=$attemptFenceRequired},
 [pscustomobject]@{role='git-head';path=$headPath;required=$true},
 [pscustomobject]@{role='git-status';path=$statusPath;required=$true},
 [pscustomobject]@{role='p15-contract';path=(Join-Path $RepositoryRoot 'release\p15-target-checkpoint.json');required=$true},
 [pscustomobject]@{role='p16-contract';path=(Join-Path $RepositoryRoot 'release\p16-target-checkpoint.json');required=$true},
 [pscustomobject]@{role='profile';path=(Join-Path $RepositoryRoot 'profiles\HP-8C40.json');required=$true},
 [pscustomobject]@{role='p16-doc';path=(Join-Path $RepositoryRoot 'docs\P16_NORMAL_MANUAL.md');required=$true},
 [pscustomobject]@{role='harness';path=(Join-Path $RepositoryRoot 'scripts\test-p16-normal-manual.ps1');required=$true},
 [pscustomobject]@{role='hardening-helper-source';path=(Join-Path $RepositoryRoot 'scripts\p16-hardening-helpers.ps1');required=$true},
 [pscustomobject]@{role='hardening-helper-selftest';path=(Join-Path $RepositoryRoot 'scripts\test-p16-hardening-helpers.ps1');required=$true},
 [pscustomobject]@{role='qualification-gate-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\P16NormalManualQualification.cs');required=$true},
 [pscustomobject]@{role='p13-surface-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\P13FanControlSurface.cs');required=$true},
 [pscustomobject]@{role='main-form-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\MainForm.cs');required=$true},
 [pscustomobject]@{role='program-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\Program.cs');required=$true},
 [pscustomobject]@{role='user-gate-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs');required=$true},
 [pscustomobject]@{role='p16-session-latch-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Control\Adaptive\Hp8C40P16QualificationSession.cs');required=$true},
 [pscustomobject]@{role='production-adapter-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionController.cs');required=$true},
 [pscustomobject]@{role='coordinator-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Control\FanControlCoordinator.cs');required=$true},
 [pscustomobject]@{role='hp8c40-backend-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs');required=$true},
 [pscustomobject]@{role='hp8c40-backend-selftest';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackendSelfTest.cs');required=$true},
 [pscustomobject]@{role='hp8c40-probe-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40EcControlStateProbe.cs');required=$true},
 [pscustomobject]@{role='hardware-telemetry-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Telemetry\HardwareTelemetryReader.cs');required=$true},
 [pscustomobject]@{role='wmi-fan-telemetry-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Telemetry\HpWmiFanTelemetryReader.cs');required=$true},
 [pscustomobject]@{role='wmi-fan-control-checkpoint';path=(Join-Path $RepositoryRoot 'release\fan-wmi-control-checkpoint.json');required=$true},
 [pscustomobject]@{role='wmi-fan-proof-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Telemetry\HpWmiFanProofReader.cs');required=$true},
 [pscustomobject]@{role='wmi-fan-proof-selftest';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Telemetry\HpWmiFanProofReaderSelfTest.cs');required=$true},
 [pscustomobject]@{role='wmi-fan-telemetry-selftest';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Telemetry\HpWmiFanTelemetryReaderSelfTest.cs');required=$true},
 [pscustomobject]@{role='telemetry-snapshot-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Telemetry\TelemetrySnapshot.cs');required=$true},
 [pscustomobject]@{role='telemetry-worker-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\TelemetryWorker.cs');required=$true},
 [pscustomobject]@{role='ec-reader-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Hardware\PawnIo\AcpiEcReader.cs');required=$true},
 [pscustomobject]@{role='setpoint-stabilizer-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Hardware\PawnIo\FanSetpointSnapshotStabilizer.cs');required=$true},
 [pscustomobject]@{role='reused-qualified-failsafe-source';path=(Join-Path $RepositoryRoot 'scripts\watchdog-p15d2-service-failsafe-8c40.ps1');required=$true}
)
$entries=@()
foreach($f in $files){
 $present=Test-Path -LiteralPath $f.path -PathType Leaf
 if($f.required -and -not $present){throw "P16 required evidence/source missing: $($f.role) $($f.path)"}
 $entries+=@([ordered]@{
   role=$f.role
   path=$f.path
   present=$present
   sha256=$(if($present){(Get-FileHash -LiteralPath $f.path -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null})
   length=$(if($present){(Get-Item -LiteralPath $f.path).Length}else{$null})
 })
}
$manifest=[ordered]@{
 schemaVersion=1
 gate='P16'
 repositoryHead=$head
 packagedUtc=(Get-Date).ToUniversalTime().ToString('O')
 files=$entries
 sourceEvidencePreserved=$true
 gitCleanUsed=$false
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$zipPath="$EvidenceRoot.zip"
if(Test-Path -LiteralPath $zipPath){throw "P16 packager refuses overwrite: $zipPath"}
Compress-Archive -Path (Join-Path $EvidenceRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipSha=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath="$zipPath.sha256"
("$zipSha  $([IO.Path]::GetFileName($zipPath))") | Set-Content -LiteralPath $shaPath -Encoding ASCII
[pscustomobject]@{ZipPath=$zipPath;ZipSha256=$zipSha;Sha256SidecarPath=$shaPath;ManifestPath=$manifestPath}
