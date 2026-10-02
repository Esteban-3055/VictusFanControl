param(
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [Parameter(Mandatory=$true)][string]$RepositoryRoot
)
$ErrorActionPreference='Stop'
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
$RepositoryRoot=[IO.Path]::GetFullPath($RepositoryRoot)
if(-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)){throw "P15D2 evidence directory missing: $EvidenceRoot"}
$summaryPath=Join-Path $EvidenceRoot 'p15d2-harness-summary.json'
if(-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)){throw 'P15D2 harness summary is missing.'}
$head=(& git -C $RepositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){throw 'P15D2 packager could not resolve repository HEAD.'}
$status=(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
$headPath=Join-Path $EvidenceRoot 'git-head.txt'
$statusPath=Join-Path $EvidenceRoot 'git-status.txt'
$head | Set-Content -LiteralPath $headPath -Encoding ASCII
$status | Set-Content -LiteralPath $statusPath -Encoding UTF8
$manifestPath=Join-Path $EvidenceRoot 'p15d2-package-manifest.json'
$files=@(
 [pscustomobject]@{role='harness-summary';path=$summaryPath;required=$true},
 [pscustomobject]@{role='gui-ready';path=(Join-Path $EvidenceRoot 'p15d2-gui-ready.json');required=$false},
 [pscustomobject]@{role='step1-apply30';path=(Join-Path $EvidenceRoot 'p15d2-step1-apply30.json');required=$false},
 [pscustomobject]@{role='parent-30-proof';path=(Join-Path $EvidenceRoot 'p15d2-parent-30-verified.txt');required=$false},
 [pscustomobject]@{role='step2-apply40';path=(Join-Path $EvidenceRoot 'p15d2-step2-apply40.json');required=$false},
 [pscustomobject]@{role='parent-40-proof';path=(Join-Path $EvidenceRoot 'p15d2-parent-40-verified.txt');required=$false},
 [pscustomobject]@{role='step3-hold40';path=(Join-Path $EvidenceRoot 'p15d2-step3-hold40.json');required=$false},
 [pscustomobject]@{role='parent-hold40-proof';path=(Join-Path $EvidenceRoot 'p15d2-parent-hold40-verified.txt');required=$false},
 [pscustomobject]@{role='step4-return30';path=(Join-Path $EvidenceRoot 'p15d2-step4-return30.json');required=$false},
 [pscustomobject]@{role='parent-return30-proof';path=(Join-Path $EvidenceRoot 'p15d2-parent-return30-verified.txt');required=$false},
 [pscustomobject]@{role='firmware-restored';path=(Join-Path $EvidenceRoot 'p15d2-firmware-restored.json');required=$false},
 [pscustomobject]@{role='gui-events';path=(Join-Path $EvidenceRoot 'p15d2-gui-events.jsonl');required=$false},
 [pscustomobject]@{role='app-log';path=(Join-Path $EvidenceRoot 'p15d2-app.log');required=$false},
 [pscustomobject]@{role='baseline-ff';path=(Join-Path $EvidenceRoot 'p15d2-baseline-ff.json');required=$false},
 [pscustomobject]@{role='setpoint-30-initial';path=(Join-Path $EvidenceRoot 'p15d2-setpoint-30-initial.json');required=$false},
 [pscustomobject]@{role='journal-30-initial';path=(Join-Path $EvidenceRoot 'p15d2-journal-30-initial.json');required=$false},
 [pscustomobject]@{role='setpoint-40';path=(Join-Path $EvidenceRoot 'p15d2-setpoint-40.json');required=$false},
 [pscustomobject]@{role='journal-40';path=(Join-Path $EvidenceRoot 'p15d2-journal-40.json');required=$false},
 [pscustomobject]@{role='setpoint-40-hold';path=(Join-Path $EvidenceRoot 'p15d2-setpoint-40-hold.json');required=$false},
 [pscustomobject]@{role='journal-40-hold';path=(Join-Path $EvidenceRoot 'p15d2-journal-40-hold.json');required=$false},
 [pscustomobject]@{role='setpoint-30-return';path=(Join-Path $EvidenceRoot 'p15d2-setpoint-30-return.json');required=$false},
 [pscustomobject]@{role='journal-30-return';path=(Join-Path $EvidenceRoot 'p15d2-journal-30-return.json');required=$false},
 [pscustomobject]@{role='final-ff';path=(Join-Path $EvidenceRoot 'p15d2-final-ff.json');required=$false},
 [pscustomobject]@{role='cleanup-ff';path=(Join-Path $EvidenceRoot 'p15d2-cleanup-ff.json');required=$false},
 [pscustomobject]@{role='service-snapshots';path=(Join-Path $EvidenceRoot 'p15d2-service-snapshots.json');required=$false},
 [pscustomobject]@{role='watchdog-log-segment';path=(Join-Path $EvidenceRoot 'p15d2-watchdog-log-segment.txt');required=$false},
 [pscustomobject]@{role='failsafe-log';path=(Join-Path $EvidenceRoot 'p15d2-failsafe.log');required=$false},
 [pscustomobject]@{role='git-head';path=$headPath;required=$true},
 [pscustomobject]@{role='git-status';path=$statusPath;required=$true},
 [pscustomobject]@{role='p15-contract';path=(Join-Path $RepositoryRoot 'release\p15-target-checkpoint.json');required=$true},
 [pscustomobject]@{role='profile';path=(Join-Path $RepositoryRoot 'profiles\HP-8C40.json');required=$true},
 [pscustomobject]@{role='p15-doc';path=(Join-Path $RepositoryRoot 'docs\P15_TARGET_CHECKPOINT.md');required=$true},
 [pscustomobject]@{role='harness';path=(Join-Path $RepositoryRoot 'scripts\test-p15d2-variable-manual.ps1');required=$true},
 [pscustomobject]@{role='gui-qualification-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\P15D2VariableManualQualification.cs');required=$true},
 [pscustomobject]@{role='p13-surface-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\P13FanControlSurface.cs');required=$true},
 [pscustomobject]@{role='main-form-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\MainForm.cs');required=$true},
 [pscustomobject]@{role='program-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl.App\Program.cs');required=$true},
 [pscustomobject]@{role='user-gate-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs');required=$true},
 [pscustomobject]@{role='production-adapter-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionController.cs');required=$true},
 [pscustomobject]@{role='coordinator-source';path=(Join-Path $RepositoryRoot 'src\VictusFanControl\Control\FanControlCoordinator.cs');required=$true},
 [pscustomobject]@{role='failsafe-source';path=(Join-Path $RepositoryRoot 'scripts\watchdog-p15d2-service-failsafe-8c40.ps1');required=$true}
)
$entries=@()
foreach($f in $files){
 $present=Test-Path -LiteralPath $f.path -PathType Leaf
 if($f.required -and -not $present){throw "P15D2 required evidence/source missing: $($f.role) $($f.path)"}
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
 gate='P15D2'
 repositoryHead=$head
 packagedUtc=(Get-Date).ToUniversalTime().ToString('O')
 files=$entries
 sourceEvidencePreserved=$true
 gitCleanUsed=$false
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$zipPath="$EvidenceRoot.zip"
if(Test-Path -LiteralPath $zipPath){throw "P15D2 packager refuses overwrite: $zipPath"}
Compress-Archive -Path (Join-Path $EvidenceRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipSha=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath="$zipPath.sha256"
("$zipSha  $([IO.Path]::GetFileName($zipPath))") | Set-Content -LiteralPath $shaPath -Encoding ASCII
[pscustomobject]@{
 ZipPath=$zipPath
 ZipSha256=$zipSha
 Sha256SidecarPath=$shaPath
 ManifestPath=$manifestPath
}
