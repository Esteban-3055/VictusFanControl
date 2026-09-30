$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$pack=Join-Path $PSScriptRoot 'package-m9c-evidence.ps1'
$temp=Join-Path $env:TEMP ("vfc-m9c-pack-{0}" -f ([guid]::NewGuid().ToString('N')))
$evidence=Join-Path $temp 'm9c-production-smoke_2026-09-30_220000'
$fakeLog=Join-Path $temp 'watchdog-m4.log'
$fakeStatus=Join-Path $temp 'm4-status.json'
$fakeJournal=Join-Path $temp 'lease.json'

try {
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    Set-Content -LiteralPath (Join-Path $evidence 'm9c-harness-summary.json') -Value '{"gate":"M9C-HARNESS","result":"FAIL_CLOSED"}' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $evidence 'm9c-result.json') -Value '{"gate":"M9C","result":"FAIL_CLOSED"}' -Encoding ASCII
    Set-Content -LiteralPath $fakeLog -Value 'WATCHDOG TEST LOG' -Encoding ASCII
    Set-Content -LiteralPath $fakeStatus -Value '{"Ready":true}' -Encoding ASCII
    Set-Content -LiteralPath $fakeJournal -Value '{"SchemaVersion":2}' -Encoding ASCII

    $package=& $pack -EvidenceRoot $evidence -RepoRoot $repoRoot -WatchdogLogPath $fakeLog -WatchdogStatusPath $fakeStatus -WatchdogJournalPath $fakeJournal

    foreach($required in @(
        'm9c-watchdog-full.log',
        'm9c-watchdog-status-final.json',
        'm9c-retained-lease-final.json',
        'm9c-service-final.json',
        'm9c-head.txt',
        'm9c-git-status.txt',
        'm9c-package-manifest.json'
    )){
        if(-not (Test-Path -LiteralPath (Join-Path $evidence $required) -PathType Leaf)){
            throw "M9C packaging self-test missing $required"
        }
    }

    if(-not (Test-Path -LiteralPath $package.ZipPath -PathType Leaf)){throw 'M9C packaging self-test ZIP missing.'}
    if(-not (Test-Path -LiteralPath $package.Sha256SidecarPath -PathType Leaf)){throw 'M9C packaging self-test SHA sidecar missing.'}

    $actual=(Get-FileHash -LiteralPath $package.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -cne [string]$package.ZipSha256){throw 'M9C packaging self-test ZIP SHA mismatch.'}

    $manifest=Get-Content -LiteralPath $package.ManifestPath -Raw | ConvertFrom-Json
    if($manifest.gate -cne 'M9C-EVIDENCE-PACKAGE' -or
       [int]$manifest.schemaVersion -ne 1 -or
       [bool]$manifest.destructiveOperations){
        throw 'M9C packaging self-test manifest contract mismatch.'
    }

    if(-not (Test-Path -LiteralPath (Join-Path $evidence 'm9c-result.json'))){
        throw 'M9C packager deleted source evidence.'
    }

    Write-Host 'HP 8C40 M9C evidence packaging self-test: PASS' -ForegroundColor Green
}
finally {
    if(Test-Path -LiteralPath $temp){
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
