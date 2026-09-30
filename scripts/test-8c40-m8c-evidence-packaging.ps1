$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$pack=Join-Path $PSScriptRoot 'package-latest-m8c-evidence.ps1'
$temp=Join-Path $env:TEMP ("vfc-m8c-pack-{0}" -f ([guid]::NewGuid().ToString('N')))
$evidence=Join-Path $temp 'm8c-thermal-preemption_2026-09-30_164000'
$fakeLog=Join-Path $temp 'watchdog-m4-8c40-2026-09-30.log'
$fakeStatus=Join-Path $temp 'm4-8c40.status.json'
$fakeJournal=Join-Path $temp 'lease.json'

try {
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    Set-Content -LiteralPath (Join-Path $evidence 'm8c-harness-summary.json') -Value '{"result":"FAIL_CLOSED","evidenceHead":"qualification-head-test"}' -Encoding ASCII
    Set-Content -LiteralPath $fakeLog -Value 'WATCHDOG TEST LOG' -Encoding ASCII
    Set-Content -LiteralPath $fakeStatus -Value '{"Ready":true}' -Encoding ASCII
    Set-Content -LiteralPath $fakeJournal -Value '{"SchemaVersion":2}' -Encoding ASCII

    $output=@(& $pack -EvidenceRoot $evidence -RepoRoot $repoRoot -WatchdogLogPath $fakeLog -WatchdogStatusPath $fakeStatus -WatchdogJournalPath $fakeJournal)
    $zip=($output | Select-Object -Last 1)

    foreach($required in @(
        'watchdog-m4-8c40-2026-09-30.log',
        'm8c-watchdog-status-final.json',
        'm8c-retained-lease-final.json',
        'm8c-service-final.json',
        'm8c-head.txt',
        'm8c-qualification-head.txt',
        'm8c-git-status.txt',
        'm8c-package-manifest.json'
    )){
        if(-not (Test-Path -LiteralPath (Join-Path $evidence $required) -PathType Leaf)){
            throw "M8C packaging self-test missing $required"
        }
    }

    if(-not (Test-Path -LiteralPath $zip -PathType Leaf)){
        throw "M8C packaging self-test did not create ZIP: $zip"
    }

    $manifest=Get-Content (Join-Path $evidence 'm8c-package-manifest.json') -Raw | ConvertFrom-Json
    if([string]$manifest.qualificationEvidenceHead -cne 'qualification-head-test'){
        throw 'M8C packaging self-test did not preserve the qualification evidence HEAD.'
    }

    if(-not [bool]$manifest.watchdogLogCopied -or
       -not [bool]$manifest.watchdogStatusCopied -or
       -not [bool]$manifest.retainedJournalCopied){
        throw 'M8C packaging self-test manifest did not record copied evidence.'
    }

    Write-Host 'M8C automatic evidence packaging self-test: PASS' -ForegroundColor Green
}
finally {
    if(Test-Path -LiteralPath $temp){
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
