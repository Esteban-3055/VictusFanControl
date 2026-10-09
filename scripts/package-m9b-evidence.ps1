param(
    [Parameter(Mandatory=$true)]
    [string]$EvidenceRoot,

    [Parameter(Mandatory=$true)]
    [string]$RepositoryRoot
)

$ErrorActionPreference='Stop'

$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
$RepositoryRoot=[IO.Path]::GetFullPath($RepositoryRoot)

if(-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)){
    throw "M9B evidence directory does not exist: $EvidenceRoot"
}
if(-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)){
    throw "M9B repository root does not exist: $RepositoryRoot"
}

$resultPath=Join-Path $EvidenceRoot 'm9b-preflight-result.json'
if(-not (Test-Path -LiteralPath $resultPath -PathType Leaf)){
    throw "M9B result evidence is missing: $resultPath"
}

$gitHeadPath=Join-Path $EvidenceRoot 'git-head.txt'
$gitStatusPath=Join-Path $EvidenceRoot 'git-status.txt'
$manifestPath=Join-Path $EvidenceRoot 'm9b-package-manifest.json'

$head=(& git -C $RepositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){
    throw "M9B packaging could not resolve repository HEAD. Raw='$head'"
}
$status=(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
if($LASTEXITCODE -ne 0){throw 'M9B packaging could not capture repository status.'}

$head | Set-Content -LiteralPath $gitHeadPath -Encoding ASCII
$status | Set-Content -LiteralPath $gitStatusPath -Encoding UTF8

$profilePath=Join-Path $RepositoryRoot 'profiles\HP-8C40.json'
$docPath=Join-Path $RepositoryRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md'
$preflightPath=Join-Path $RepositoryRoot 'scripts\test-8c40-production-watchdog-m9b-preflight.ps1'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'

$files=@(
    [pscustomobject]@{Role='result';Path=$resultPath;Required=$true},
    [pscustomobject]@{Role='telemetry';Path=(Join-Path $EvidenceRoot 'telemetry-output.txt');Required=$false},
    [pscustomobject]@{Role='git-head';Path=$gitHeadPath;Required=$true},
    [pscustomobject]@{Role='git-status';Path=$gitStatusPath;Required=$true},
    [pscustomobject]@{Role='profile';Path=$profilePath;Required=$true},
    [pscustomobject]@{Role='m9-doc';Path=$docPath;Required=$true},
    [pscustomobject]@{Role='preflight-script';Path=$preflightPath;Required=$true},
    [pscustomobject]@{Role='installed-watchdog-exe';Path=$serviceExe;Required=$false},
    [pscustomobject]@{Role='installed-pawnio-module';Path=$serviceModule;Required=$false}
)

$hashes=@()
foreach($entry in $files){
    $exists=Test-Path -LiteralPath $entry.Path -PathType Leaf
    if($entry.Required -and -not $exists){
        throw "M9B packaging required file missing for role '$($entry.Role)': $($entry.Path)"
    }

    $hashes+=@([ordered]@{
        role=$entry.Role
        path=$entry.Path
        present=$exists
        sha256=$(if($exists){(Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null})
        length=$(if($exists){(Get-Item -LiteralPath $entry.Path).Length}else{$null})
    })
}

$manifest=[ordered]@{
    schemaVersion=1
    gate='M9B'
    packagedUtc=(Get-Date).ToUniversalTime().ToString('O')
    repositoryHead=$head
    evidenceRoot=$EvidenceRoot
    files=$hashes
    destructiveOperations=$false
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$zipPath="$EvidenceRoot.zip"
if(Test-Path -LiteralPath $zipPath){
    throw "M9B packaging refuses to overwrite an existing archive: $zipPath"
}

Compress-Archive -Path (Join-Path $EvidenceRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
if(-not (Test-Path -LiteralPath $zipPath -PathType Leaf)){
    throw 'M9B packaging did not create the evidence ZIP.'
}

$zipSha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath="$zipPath.sha256"
("$zipSha256  $([IO.Path]::GetFileName($zipPath))") |
    Set-Content -LiteralPath $shaPath -Encoding ASCII

[pscustomobject]@{
    ZipPath=$zipPath
    ZipSha256=$zipSha256
    Sha256SidecarPath=$shaPath
    ManifestPath=$manifestPath
}
