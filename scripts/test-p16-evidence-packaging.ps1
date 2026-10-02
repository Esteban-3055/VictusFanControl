$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $PSScriptRoot 'package-p16-evidence.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P16-PackageTest-'+[Guid]::NewGuid().ToString('N'))
$evidence=Join-Path $tempRoot 'logs\p16-normal-manual_test'
try{
 New-Item -ItemType Directory -Force -Path $evidence | Out-Null
 '{"gate":"P16-HARNESS","result":"PASS","attemptFenceClaimed":true}' | Set-Content -LiteralPath (Join-Path $evidence 'p16-harness-summary.json') -Encoding UTF8
 '{"gate":"P16","oneShot":true,"authorizationHead":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}' | Set-Content -LiteralPath (Join-Path $evidence 'p16-attempt-fence.json') -Encoding UTF8
 'synthetic normal-app log segment' | Set-Content -LiteralPath (Join-Path $evidence 'p16-app-log-segment.txt') -Encoding UTF8
 $p=& $helper -EvidenceRoot $evidence -RepositoryRoot $repoRoot
 if(-not (Test-Path -LiteralPath $p.ZipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $p.Sha256SidecarPath -PathType Leaf)){throw 'P16 package self-test output missing.'}
 $actual=(Get-FileHash -LiteralPath $p.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -cne [string]$p.ZipSha256){throw 'P16 package self-test ZIP SHA mismatch.'}
 $m=Get-Content -LiteralPath $p.ManifestPath -Raw | ConvertFrom-Json
 if([string]$m.gate -cne 'P16' -or [bool]$m.gitCleanUsed -or -not [bool]$m.sourceEvidencePreserved){throw 'P16 package manifest safety contract mismatch.'}
 $roles=@($m.files | ForEach-Object {[string]$_.role})
 foreach($role in @('attempt-fence','p15-contract','p16-contract','hardening-helper-source','hardening-helper-selftest','qualification-gate-source','main-form-source','program-source','user-gate-source','production-adapter-source','coordinator-source','reused-qualified-failsafe-source')){
   if($role -notin $roles){throw "P16 package manifest missing critical role '$role'."}
 }
 if(-not (Test-Path -LiteralPath (Join-Path $evidence 'p16-harness-summary.json'))){throw 'P16 packager deleted source evidence.'}
 Write-Host 'HP 8C40 P16 evidence packaging self-test: PASS' -ForegroundColor Green
}finally{
 Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
