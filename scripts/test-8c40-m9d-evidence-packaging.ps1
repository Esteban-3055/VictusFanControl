$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$pack=Join-Path $PSScriptRoot 'package-m9d-evidence.ps1'
$temp=Join-Path $env:TEMP ("vfc-m9d-pack-{0}" -f ([guid]::NewGuid().ToString('N')))
$evidence=Join-Path $temp 'm9d-production-lifecycle_2026-10-01_000000'
$fakeLog=Join-Path $temp 'watchdog.log'
$fakeStatus=Join-Path $temp 'status.json'
$fakeJournal=Join-Path $temp 'lease.json'
$fakeApp=Join-Path $temp 'app.log'

try {
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $evidence 'markers') | Out-Null
    Set-Content (Join-Path $evidence 'm9d-harness-summary.json') '{"gate":"M9D-HARNESS","result":"FAIL_CLOSED"}' -Encoding ASCII
    Set-Content (Join-Path $evidence 'markers\m9d-production-lifecycle.result') 'FAIL|synthetic' -Encoding ASCII
    Set-Content $fakeLog 'WATCHDOG TEST LOG' -Encoding ASCII
    Set-Content $fakeStatus '{"Ready":true}' -Encoding ASCII
    Set-Content $fakeJournal '{"SchemaVersion":2}' -Encoding ASCII
    Set-Content $fakeApp 'APP TEST LOG' -Encoding ASCII

    $package=& $pack -EvidenceRoot $evidence -RepoRoot $repoRoot -WatchdogLogPath $fakeLog -WatchdogStatusPath $fakeStatus -WatchdogJournalPath $fakeJournal -AppLogPath $fakeApp

    foreach($required in @('m9d-watchdog-full.log','m9d-watchdog-status-final.json','m9d-retained-lease-final.json','m9d-app-full.log','m9d-service-final.json','m9d-head.txt','m9d-git-status.txt','m9d-package-manifest.json')){
        if(-not (Test-Path -LiteralPath (Join-Path $evidence $required) -PathType Leaf)){throw "M9D packaging self-test missing $required"}
    }
    if(-not (Test-Path -LiteralPath $package.ZipPath)){throw 'M9D packaging ZIP missing.'}
    if(-not (Test-Path -LiteralPath $package.Sha256SidecarPath)){throw 'M9D packaging SHA sidecar missing.'}
    $actual=(Get-FileHash -LiteralPath $package.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -cne [string]$package.ZipSha256){throw 'M9D packaging SHA mismatch.'}
    $manifest=Get-Content $package.ManifestPath -Raw | ConvertFrom-Json
    if($manifest.gate -cne 'M9D-EVIDENCE-PACKAGE' -or [bool]$manifest.destructiveOperations){throw 'M9D packaging manifest contract mismatch.'}
    if(-not (Test-Path -LiteralPath (Join-Path $evidence 'markers\m9d-production-lifecycle.result'))){throw 'M9D packager deleted source evidence.'}
    Write-Host 'HP 8C40 M9D evidence packaging self-test: PASS' -ForegroundColor Green
}
finally {
    if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue}
}
