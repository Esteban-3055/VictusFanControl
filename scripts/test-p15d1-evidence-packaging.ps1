$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $PSScriptRoot 'package-p15d1-evidence.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P15D1-PackageTest-'+[Guid]::NewGuid().ToString('N'))
$evidence=Join-Path $tempRoot 'logs\p15d1-tray-exit_test'
try{
 New-Item -ItemType Directory -Force -Path $evidence | Out-Null
 '{"gate":"P15D1-HARNESS","result":"PASS"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d1-harness-summary.json') -Encoding UTF8
 '{"gate":"P15D1-GUI","result":"WINDOW_HIDDEN_TO_TRAY"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d1-window-hidden.json') -Encoding UTF8
 '{"gate":"P15D1-GUI","result":"TRAY_EXIT_REQUESTED"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d1-tray-exit-requested.json') -Encoding UTF8
 '{"gate":"P15D1-GUI","result":"PASS_TRAY_EXIT_STRONG_RESTORE"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d1-shutdown-result.json') -Encoding UTF8
 '{"cpu":30,"gpu":30}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d1-owned-setpoint-hidden.json') -Encoding UTF8
 '{"passed":true}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d1-final-ff.json') -Encoding UTF8
 $p=& $helper -EvidenceRoot $evidence -RepositoryRoot $repoRoot
 if(-not (Test-Path -LiteralPath $p.ZipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $p.Sha256SidecarPath -PathType Leaf)){throw 'P15D1 package self-test output missing.'}
 $actual=(Get-FileHash -LiteralPath $p.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -cne [string]$p.ZipSha256){throw 'P15D1 package self-test ZIP SHA mismatch.'}
 $m=Get-Content -LiteralPath $p.ManifestPath -Raw | ConvertFrom-Json
 if([string]$m.gate -cne 'P15D1' -or [bool]$m.gitCleanUsed -or -not [bool]$m.sourceEvidencePreserved){throw 'P15D1 package manifest safety contract mismatch.'}
 $roles=@($m.files | ForEach-Object {[string]$_.role})
 foreach($role in @('parent-owned-proof','parent-hidden-owned-proof','program-source','user-gate-source','production-adapter-source','coordinator-source','failsafe-source')){if($role -notin $roles){throw "P15D1 package manifest missing critical role '$role'."}}
 if(-not (Test-Path -LiteralPath (Join-Path $evidence 'p15d1-harness-summary.json'))){throw 'P15D1 packager deleted source evidence.'}
 Write-Host 'HP 8C40 P15D1 evidence packaging self-test: PASS' -ForegroundColor Green
}finally{
 Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
