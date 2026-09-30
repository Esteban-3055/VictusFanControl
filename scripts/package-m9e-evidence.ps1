param(
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [string]$RepoRoot=(Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference='Stop'
$resolved=(Resolve-Path -LiteralPath $EvidenceRoot -ErrorAction Stop).Path
$RepoRoot=[IO.Path]::GetFullPath($RepoRoot)
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$m9eRoot=Join-Path $env:ProgramData 'VictusFanControl\M9E'

function Copy-IfPresent([string]$Source,[string]$Name){
    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){return $false}
    $dest=Join-Path $resolved $Name
    if(Test-Path -LiteralPath $dest){throw "M9E packager refuses to overwrite '$dest'."}
    Copy-Item -LiteralPath $Source -Destination $dest
    return $true
}

$log=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
[void](Copy-IfPresent $log 'm9e-watchdog-full.log')
[void](Copy-IfPresent (Join-Path $serviceRoot 'state\m4-8c40.status.json') 'm9e-watchdog-status.json')
[void](Copy-IfPresent (Join-Path $serviceRoot 'state\lease.json') 'm9e-retained-lease.json')
[void](Copy-IfPresent (Join-Path $m9eRoot 'm9e-reboot-arm.json') 'm9e-programdata-reboot-arm.json')
[void](Copy-IfPresent (Join-Path $m9eRoot 'm9e-postreboot-result.json') 'm9e-programdata-postreboot.json')

$svc=$null
try{$svc=Get-CimInstance Win32_Service -Filter "Name='VictusFanControlWatchdogM4'" -ErrorAction SilentlyContinue}catch{}
[ordered]@{
    installed=($null-ne $svc)
    name='VictusFanControlWatchdogM4'
    state=$(if($svc){[string]$svc.State}else{'Absent'})
    startMode=$(if($svc){[string]$svc.StartMode}else{'Absent'})
    processId=$(if($svc){[int]$svc.ProcessId}else{0})
    startName=$(if($svc){[string]$svc.StartName}else{$null})
    pathName=$(if($svc){[string]$svc.PathName}else{$null})
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resolved 'm9e-service-snapshot.json') -Encoding UTF8

$qfailure=(& sc.exe qfailure VictusFanControlWatchdogM4 2>&1 | Out-String)
$qfailureExit=$LASTEXITCODE
$qflag=(& sc.exe qfailureflag VictusFanControlWatchdogM4 2>&1 | Out-String)
$qflagExit=$LASTEXITCODE
[ordered]@{qfailureExit=$qfailureExit;qfailure=$qfailure;qfailureFlagExit=$qflagExit;qfailureFlag=$qflag} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resolved 'm9e-scm-recovery.json') -Encoding UTF8

$head=(& git -C $RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if($LASTEXITCODE-ne 0 -or $head-notmatch '^[0-9a-f]{40}$'){throw "M9E packager could not resolve HEAD. Raw='$head'"}
$status=(& git -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
$head | Set-Content -LiteralPath (Join-Path $resolved 'm9e-head.txt') -Encoding ASCII
$status | Set-Content -LiteralPath (Join-Path $resolved 'm9e-git-status.txt') -Encoding UTF8

$external=@(
    [pscustomobject]@{role='profile';path=(Join-Path $RepoRoot 'profiles\HP-8C40.json')},
    [pscustomobject]@{role='m9-doc';path=(Join-Path $RepoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md')},
    [pscustomobject]@{role='service-policy-source';path=(Join-Path $RepoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogServicePolicy.cs')},
    [pscustomobject]@{role='stage1-source';path=(Join-Path $RepoRoot 'scripts\test-8c40-production-watchdog-m9e-arm.ps1')},
    [pscustomobject]@{role='postreboot-source';path=(Join-Path $RepoRoot 'scripts\test-8c40-production-watchdog-m9e-postreboot.ps1')},
    [pscustomobject]@{role='installed-watchdog-exe';path=(Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe')},
    [pscustomobject]@{role='installed-pawnio-module';path=(Join-Path $serviceRoot 'modules\LpcACPIEC.bin')}
)
$externalHashes=@()
foreach($x in $external){
    $present=Test-Path -LiteralPath $x.path -PathType Leaf
    $externalHashes+=@([ordered]@{
        role=$x.role;path=$x.path;present=$present;
        sha256=$(if($present){(Get-FileHash -LiteralPath $x.path -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null});
        length=$(if($present){(Get-Item -LiteralPath $x.path).Length}else{$null})
    })
}
$evidenceHashes=@()
Get-ChildItem -LiteralPath $resolved -File |
    Where-Object {$_.Name-cne 'm9e-package-manifest.json'} |
    Sort-Object Name |
    ForEach-Object {$evidenceHashes+=@([ordered]@{name=$_.Name;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();length=$_.Length})}

$manifest=Join-Path $resolved 'm9e-package-manifest.json'
$zip="$resolved.zip";$sha="$zip.sha256"
if((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath $sha)){throw 'M9E packager refuses to overwrite an existing ZIP/SHA sidecar.'}
[ordered]@{
    schemaVersion=1;gate='M9E-EVIDENCE-PACKAGE';packagedUtc=(Get-Date).ToUniversalTime().ToString('O');
    repositoryHead=$head;evidenceFiles=$evidenceHashes;externalFiles=$externalHashes;
    destructiveOperations=$false;zipPath=$zip
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest -Encoding UTF8
Compress-Archive -Path (Join-Path $resolved '*') -DestinationPath $zip -CompressionLevel Optimal
$zipHash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
("$zipHash  $([IO.Path]::GetFileName($zip))") | Set-Content -LiteralPath $sha -Encoding ASCII
[pscustomobject]@{ZipPath=$zip;ZipSha256=$zipHash;Sha256SidecarPath=$sha;ManifestPath=$manifest}
