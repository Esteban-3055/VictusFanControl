$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $PSScriptRoot 'package-p15c-evidence.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P15C-PackageTest-'+[Guid]::NewGuid().ToString('N'))
$evidence=Join-Path $tempRoot 'logs\p15c-gui-manual_test'
try{
 New-Item -ItemType Directory -Force -Path $evidence | Out-Null
 '{"gate":"P15C-HARNESS","result":"PASS"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15c-harness-summary.json') -Encoding UTF8
 '{"gate":"P15C-GUI","result":"PASS_GUI_MANUAL30_STRONG_RESTORE"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15c-gui-result.json') -Encoding UTF8
 '{"cpu":30,"gpu":30}' | Set-Content -LiteralPath (Join-Path $evidence 'p15c-owned-setpoint.json') -Encoding UTF8
 '{"passed":true}' | Set-Content -LiteralPath (Join-Path $evidence 'p15c-final-ff.json') -Encoding UTF8
 $p=& $helper -EvidenceRoot $evidence -RepositoryRoot $repoRoot
 if(-not (Test-Path -LiteralPath $p.ZipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $p.Sha256SidecarPath -PathType Leaf)){throw 'P15C package self-test output missing.'}
 $actual=(Get-FileHash -LiteralPath $p.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -cne [string]$p.ZipSha256){throw 'P15C package self-test ZIP SHA mismatch.'}
 $m=Get-Content -LiteralPath $p.ManifestPath -Raw | ConvertFrom-Json
 if([string]$m.gate -cne 'P15C' -or [bool]$m.gitCleanUsed -or -not [bool]$m.sourceEvidencePreserved){throw 'P15C package manifest safety contract mismatch.'}
 if(-not (Test-Path -LiteralPath (Join-Path $evidence 'p15c-harness-summary.json'))){throw 'P15C packager deleted source evidence.'}
 Write-Host 'HP 8C40 P15C evidence packaging self-test: PASS' -ForegroundColor Green
}finally{
 Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
