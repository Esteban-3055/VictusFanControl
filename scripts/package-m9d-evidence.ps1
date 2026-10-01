param(
    [Parameter(Mandatory=$true)]
    [string]$EvidenceRoot,

    [string]$RepoRoot=(Split-Path -Parent $PSScriptRoot),

    [string]$WatchdogLogPath,
    [string]$WatchdogStatusPath,
    [string]$WatchdogJournalPath
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

function Copy-IfPresent([string]$Source,[string]$DestinationName){
    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){return $false}
    Copy-Item -LiteralPath $Source -Destination (Join-Path $resolvedEvidence $DestinationName)
    return $true
}

$watchdogLogCopied=Copy-IfPresent $WatchdogLogPath 'm9d-watchdog-full.log'
$statusCopied=Copy-IfPresent $WatchdogStatusPath 'm9d-watchdog-status-final.json'
$journalCopied=Copy-IfPresent $WatchdogJournalPath 'm9d-retained-lease-final.json'

$serviceSnapshot=[ordered]@{installed=$false;name='VictusFanControlWatchdogM4'}
try {
    $svc=Get-CimInstance Win32_Service -Filter "Name='VictusFanControlWatchdogM4'" -ErrorAction SilentlyContinue
    if($svc){
        $serviceSnapshot=[ordered]@{
            installed=$true
            name=[string]$svc.Name
            state=[string]$svc.State
            startMode=[string]$svc.StartMode
            processId=[int]$svc.ProcessId
            startName=[string]$svc.StartName
            pathName=[string]$svc.PathName
        }
    }
}
catch {
    $serviceSnapshot=[ordered]@{
        installed=$false
        name='VictusFanControlWatchdogM4'
        error=$_.Exception.Message
    }
}
$serviceSnapshot | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm9d-service-final.json') -Encoding UTF8

$head=(& git -C $RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){
    throw "M9D packaging could not resolve repository HEAD. Raw='$head'"
}
$status=(& git -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
if($LASTEXITCODE -ne 0){throw 'M9D packaging could not capture repository status.'}

$head | Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm9d-head.txt') -Encoding ASCII
$status | Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm9d-git-status.txt') -Encoding UTF8

$external=@(
    [pscustomobject]@{Role='profile';Path=(Join-Path $RepoRoot 'profiles\HP-8C40.json')},
    [pscustomobject]@{Role='m9-doc';Path=(Join-Path $RepoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md')},
    [pscustomobject]@{Role='app-program-source';Path=(Join-Path $RepoRoot 'src\VictusFanControl.App\Program.cs')},
    [pscustomobject]@{Role='mainform-source';Path=(Join-Path $RepoRoot 'src\VictusFanControl.App\MainForm.cs')},
    [pscustomobject]@{Role='m9d-contract-source';Path=(Join-Path $RepoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualification.cs')},
    [pscustomobject]@{Role='production-gate-source';Path=(Join-Path $RepoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs')},
    [pscustomobject]@{Role='harness-source';Path=(Join-Path $RepoRoot 'scripts\test-8c40-production-watchdog-m9d.ps1')},
    [pscustomobject]@{Role='failsafe-source';Path=(Join-Path $RepoRoot 'scripts\watchdog-m9d-service-failsafe-8c40.ps1')},
    [pscustomobject]@{Role='tracked-child-source';Path=(Join-Path $RepoRoot 'scripts\m9d-tracked-child.ps1')},
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

$evidenceHashes=@()
Get-ChildItem -LiteralPath $resolvedEvidence -File |
    Where-Object {$_.Name -cne 'm9d-package-manifest.json'} |
    Sort-Object Name |
    ForEach-Object {
        $evidenceHashes+=@([ordered]@{
            name=$_.Name
            sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            length=$_.Length
        })
    }

$manifestPath=Join-Path $resolvedEvidence 'm9d-package-manifest.json'
$zipPath="$resolvedEvidence.zip"
$shaPath="$zipPath.sha256"

if(Test-Path -LiteralPath $zipPath){
    throw "M9D packaging refuses to overwrite existing archive: $zipPath"
}
if(Test-Path -LiteralPath $shaPath){
    throw "M9D packaging refuses to overwrite existing SHA sidecar: $shaPath"
}

[ordered]@{
    schemaVersion=1
    gate='M9D-EVIDENCE-PACKAGE'
    packagedUtc=(Get-Date).ToUniversalTime().ToString('O')
    evidenceRoot=$resolvedEvidence
    repositoryHead=$head
    watchdogLogCopied=$watchdogLogCopied
    watchdogStatusCopied=$statusCopied
    retainedJournalCopied=$journalCopied
    evidenceFiles=$evidenceHashes
    externalFiles=$externalHashes
    destructiveOperations=$false
    zipPath=$zipPath
} | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $manifestPath -Encoding UTF8

Compress-Archive -Path (Join-Path $resolvedEvidence '*') -DestinationPath $zipPath -CompressionLevel Optimal
if(-not (Test-Path -LiteralPath $zipPath -PathType Leaf)){
    throw 'M9D packaging did not create the evidence ZIP.'
}

$zipSha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
("$zipSha256  $([IO.Path]::GetFileName($zipPath))") |
    Set-Content -LiteralPath $shaPath -Encoding ASCII

[pscustomobject]@{
    ZipPath=$zipPath
    ZipSha256=$zipSha256
    Sha256SidecarPath=$shaPath
    ManifestPath=$manifestPath
}
