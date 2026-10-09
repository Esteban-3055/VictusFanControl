param(
    [Parameter(Mandatory=$true)]
    [string]$EvidenceRoot,

    [string]$RepoRoot=(Split-Path -Parent $PSScriptRoot),

    [string]$WatchdogLogPath,
    [string]$WatchdogStatusPath,
    [string]$WatchdogJournalPath,
    [string]$AppLogPath
)

$ErrorActionPreference='Stop'
$resolvedEvidence=(Resolve-Path -LiteralPath $EvidenceRoot -ErrorAction Stop).Path
$RepoRoot=[IO.Path]::GetFullPath($RepoRoot)
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'

if([string]::IsNullOrWhiteSpace($WatchdogLogPath)){
    $WatchdogLogPath=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
}
if([string]::IsNullOrWhiteSpace($WatchdogStatusPath)){
    $WatchdogStatusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
}
if([string]::IsNullOrWhiteSpace($WatchdogJournalPath)){
    $WatchdogJournalPath=Join-Path $serviceRoot 'state\lease.json'
}
if([string]::IsNullOrWhiteSpace($AppLogPath)){
    $AppLogPath=Join-Path $env:LOCALAPPDATA ("VictusFanControl\logs\events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
}

function Copy-IfPresent([string]$Source,[string]$DestinationName){
    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){return $false}
    $destination=Join-Path $resolvedEvidence $DestinationName
    if(Test-Path -LiteralPath $destination){throw "M9D packager refuses to overwrite evidence file '$destination'."}
    Copy-Item -LiteralPath $Source -Destination $destination
    return $true
}

$watchdogLogCopied=Copy-IfPresent $WatchdogLogPath 'm9d-watchdog-full.log'
$statusCopied=Copy-IfPresent $WatchdogStatusPath 'm9d-watchdog-status-final.json'
$journalCopied=Copy-IfPresent $WatchdogJournalPath 'm9d-retained-lease-final.json'
$appLogCopied=Copy-IfPresent $AppLogPath 'm9d-app-full.log'

$svc=Get-CimInstance Win32_Service -Filter "Name='VictusFanControlWatchdogM4'" -ErrorAction SilentlyContinue
$serviceSnapshot=[ordered]@{
    installed=($null -ne $svc)
    name='VictusFanControlWatchdogM4'
    state=$(if($svc){[string]$svc.State}else{'Absent'})
    startMode=$(if($svc){[string]$svc.StartMode}else{'Absent'})
    processId=$(if($svc){[int]$svc.ProcessId}else{0})
    startName=$(if($svc){[string]$svc.StartName}else{$null})
    pathName=$(if($svc){[string]$svc.PathName}else{$null})
}
$serviceSnapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm9d-service-final.json') -Encoding UTF8

$head=(& git -C $RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){throw "M9D packaging could not resolve HEAD. Raw='$head'"}
$status=(& git -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
if($LASTEXITCODE -ne 0){throw 'M9D packaging could not capture git status.'}
$head | Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm9d-head.txt') -Encoding ASCII
$status | Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm9d-git-status.txt') -Encoding UTF8

$external=@(
    [pscustomobject]@{Role='profile';Path=(Join-Path $RepoRoot 'profiles\HP-8C40.json')},
    [pscustomobject]@{Role='m9-doc';Path=(Join-Path $RepoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md')},
    [pscustomobject]@{Role='production-gate';Path=(Join-Path $RepoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs')},
    [pscustomobject]@{Role='m9d-metadata';Path=(Join-Path $RepoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualificationTest.cs')},
    [pscustomobject]@{Role='app-program';Path=(Join-Path $RepoRoot 'src\VictusFanControl.App\Program.cs')},
    [pscustomobject]@{Role='main-form';Path=(Join-Path $RepoRoot 'src\VictusFanControl.App\MainForm.cs')},
    [pscustomobject]@{Role='harness';Path=(Join-Path $RepoRoot 'scripts\test-8c40-production-watchdog-m9d.ps1')},
    [pscustomobject]@{Role='failsafe';Path=(Join-Path $RepoRoot 'scripts\watchdog-m9d-service-failsafe-8c40.ps1')},
    [pscustomobject]@{Role='tracked-child';Path=(Join-Path $RepoRoot 'scripts\m9d-tracked-child.ps1')},
    [pscustomobject]@{Role='installed-watchdog-exe';Path=(Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe')},
    [pscustomobject]@{Role='installed-pawnio-module';Path=(Join-Path $serviceRoot 'modules\LpcACPIEC.bin')}
)

$externalHashes=@()
foreach($entry in $external){
    $present=Test-Path -LiteralPath $entry.Path -PathType Leaf
    $externalHashes+=@([ordered]@{
        role=$entry.Role
        path=$entry.Path
        present=$present
        sha256=$(if($present){(Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null})
        length=$(if($present){(Get-Item -LiteralPath $entry.Path).Length}else{$null})
    })
}

$manifestPath=Join-Path $resolvedEvidence 'm9d-package-manifest.json'
$zipPath="$resolvedEvidence.zip"
$shaPath="$zipPath.sha256"
if(Test-Path -LiteralPath $manifestPath){throw "M9D packaging refuses existing manifest: $manifestPath"}
if(Test-Path -LiteralPath $zipPath){throw "M9D packaging refuses existing ZIP: $zipPath"}
if(Test-Path -LiteralPath $shaPath){throw "M9D packaging refuses existing SHA sidecar: $shaPath"}

$evidenceHashes=@()
Get-ChildItem -LiteralPath $resolvedEvidence -File | Sort-Object Name | ForEach-Object {
    $evidenceHashes+=@([ordered]@{
        name=$_.Name
        sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        length=$_.Length
    })
}

[ordered]@{
    schemaVersion=1
    gate='M9D-EVIDENCE-PACKAGE'
    packagedUtc=(Get-Date).ToUniversalTime().ToString('O')
    repositoryHead=$head
    evidenceRoot=$resolvedEvidence
    watchdogLogCopied=$watchdogLogCopied
    watchdogStatusCopied=$statusCopied
    retainedJournalCopied=$journalCopied
    appLogCopied=$appLogCopied
    evidenceFiles=$evidenceHashes
    externalFiles=$externalHashes
    destructiveOperations=$false
    zipPath=$zipPath
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Compress-Archive -Path (Join-Path $resolvedEvidence '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipSha=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
("$zipSha  $([IO.Path]::GetFileName($zipPath))") | Set-Content -LiteralPath $shaPath -Encoding ASCII

[pscustomobject]@{ZipPath=$zipPath;ZipSha256=$zipSha;Sha256SidecarPath=$shaPath;ManifestPath=$manifestPath}
