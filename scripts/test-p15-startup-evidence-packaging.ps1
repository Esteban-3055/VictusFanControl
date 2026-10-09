$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $PSScriptRoot 'package-p15-startup-evidence.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P15A-PackageTest-'+[Guid]::NewGuid().ToString('N'))
$evidence=Join-Path $tempRoot 'logs\p15a-startup-no-write_test'
try{
 New-Item -ItemType Directory -Force -Path $evidence | Out-Null
 '{"gate":"P15A","result":"PASS"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15a-startup-result.json') -Encoding UTF8
 'startup mode=Firmware; gates CLOSED' | Set-Content -LiteralPath (Join-Path $evidence 'app-session.log') -Encoding UTF8
 '[]' | Set-Content -LiteralPath (Join-Path $evidence 'setpoint-samples.json') -Encoding UTF8
 '{}' | Set-Content -LiteralPath (Join-Path $evidence 'service-snapshots.json') -Encoding UTF8
 '{}' | Set-Content -LiteralPath (Join-Path $evidence 'rc-artifact-identity.json') -Encoding UTF8
 $p=& $helper -EvidenceRoot $evidence -RepositoryRoot $repoRoot
 if(-not (Test-Path -LiteralPath $p.ZipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $p.Sha256SidecarPath -PathType Leaf)){throw 'P15A package self-test output missing.'}
 $actual=(Get-FileHash -LiteralPath $p.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -cne [string]$p.ZipSha256){throw 'P15A package self-test ZIP SHA mismatch.'}
 $m=Get-Content -LiteralPath $p.ManifestPath -Raw | ConvertFrom-Json
 if([string]$m.gate -cne 'P15A' -or [bool]$m.gitCleanUsed -or -not [bool]$m.sourceEvidencePreserved){throw 'P15A package manifest safety contract mismatch.'}
 if(-not (Test-Path -LiteralPath (Join-Path $evidence 'p15a-startup-result.json'))){throw 'P15A packager deleted source evidence.'}
 Write-Host 'HP 8C40 P15A startup evidence packaging self-test: PASS' -ForegroundColor Green
}finally{
 Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
