$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$pack=Join-Path $PSScriptRoot 'package-m9e-evidence.ps1'
$temp=Join-Path $env:TEMP ("vfc-m9e-pack-{0}" -f ([guid]::NewGuid().ToString('N')))
$evidence=Join-Path $temp 'm9e-evidence'
try{
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    Set-Content -LiteralPath (Join-Path $evidence 'm9e-test-result.json') -Value '{"gate":"M9E","result":"FAIL_CLOSED"}' -Encoding ASCII
    $package=& $pack -EvidenceRoot $evidence -RepoRoot $repoRoot
    foreach($name in @('m9e-service-snapshot.json','m9e-scm-recovery.json','m9e-head.txt','m9e-git-status.txt','m9e-package-manifest.json')){
        if(-not (Test-Path -LiteralPath (Join-Path $evidence $name) -PathType Leaf)){throw "M9E packaging self-test missing $name"}
    }
    if(-not (Test-Path -LiteralPath $package.ZipPath -PathType Leaf)){throw 'M9E packaging ZIP missing.'}
    if(-not (Test-Path -LiteralPath $package.Sha256SidecarPath -PathType Leaf)){throw 'M9E packaging SHA sidecar missing.'}
    $actual=(Get-FileHash -LiteralPath $package.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual-cne [string]$package.ZipSha256){throw 'M9E packaging ZIP SHA mismatch.'}
    $manifest=Get-Content -LiteralPath $package.ManifestPath -Raw | ConvertFrom-Json
    if($manifest.gate-cne 'M9E-EVIDENCE-PACKAGE' -or [bool]$manifest.destructiveOperations){throw 'M9E manifest contract mismatch.'}
    if(-not (Test-Path -LiteralPath (Join-Path $evidence 'm9e-test-result.json'))){throw 'M9E packager deleted source evidence.'}
    Write-Host 'HP 8C40 M9E evidence packaging self-test: PASS' -ForegroundColor Green
}
finally{
    if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue}
}
