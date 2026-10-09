$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $PSScriptRoot 'package-p15d2-evidence.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P15D2-PackageTest-'+[Guid]::NewGuid().ToString('N'))
$evidence=Join-Path $tempRoot 'logs\p15d2-variable-manual_test'
try{
 New-Item -ItemType Directory -Force -Path $evidence | Out-Null
 '{"gate":"P15D2-HARNESS","result":"PASS"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-harness-summary.json') -Encoding UTF8
 '{"gate":"P15D2-GUI","result":"READY"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-gui-ready.json') -Encoding UTF8
 '{"gate":"P15D2-GUI","result":"MANUAL_APPLIED","equalFanLevel":30}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-step1-apply30.json') -Encoding UTF8
 'P15D2-PARENT-30-VERIFIED|test|guiPid=1|guiStartTicks=2' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-parent-30-verified.txt') -Encoding ASCII
 '{"gate":"P15D2-GUI","result":"MANUAL_APPLIED","equalFanLevel":40}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-step2-apply40.json') -Encoding UTF8
 '{"gate":"P15D2-GUI","result":"HOLD_NO_RETRANSMIT","equalFanLevel":40}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-step3-hold40.json') -Encoding UTF8
 '{"gate":"P15D2-GUI","result":"MANUAL_APPLIED","equalFanLevel":30}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-step4-return30.json') -Encoding UTF8
 '{"gate":"P15D2-GUI","result":"FIRMWARE_RESTORED"}' | Set-Content -LiteralPath (Join-Path $evidence 'p15d2-firmware-restored.json') -Encoding UTF8
 $p=& $helper -EvidenceRoot $evidence -RepositoryRoot $repoRoot
 if(-not (Test-Path -LiteralPath $p.ZipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $p.Sha256SidecarPath -PathType Leaf)){throw 'P15D2 package self-test output missing.'}
 $actual=(Get-FileHash -LiteralPath $p.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -cne [string]$p.ZipSha256){throw 'P15D2 package self-test ZIP SHA mismatch.'}
 $m=Get-Content -LiteralPath $p.ManifestPath -Raw | ConvertFrom-Json
 if([string]$m.gate -cne 'P15D2' -or [bool]$m.gitCleanUsed -or -not [bool]$m.sourceEvidencePreserved){throw 'P15D2 package manifest safety contract mismatch.'}
 $roles=@($m.files | ForEach-Object {[string]$_.role})
 foreach($role in @('parent-30-proof','parent-40-proof','parent-hold40-proof','parent-return30-proof','program-source','user-gate-source','production-adapter-source','coordinator-source','failsafe-source')){
   if($role -notin $roles){throw "P15D2 package manifest missing critical role '$role'."}
 }
 if(-not (Test-Path -LiteralPath (Join-Path $evidence 'p15d2-harness-summary.json'))){throw 'P15D2 packager deleted source evidence.'}
 Write-Host 'HP 8C40 P15D2 evidence packaging self-test: PASS' -ForegroundColor Green
}finally{
 Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
