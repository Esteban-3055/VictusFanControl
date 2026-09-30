param(
    [string]$EvidenceRoot,
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$WatchdogLogPath,
    [string]$WatchdogStatusPath,
    [string]$WatchdogJournalPath
)

$ErrorActionPreference='Stop'

$logsRoot=Join-Path $RepoRoot 'logs'

if([string]::IsNullOrWhiteSpace($EvidenceRoot)){
    $latest=Get-ChildItem -LiteralPath $logsRoot -Directory -Filter 'm8c-thermal-preemption_*' -ErrorAction Stop |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if($null -eq $latest){
        throw "No M8C evidence directory was found under $logsRoot."
    }

    $EvidenceRoot=$latest.FullName
}

$resolvedEvidence=(Resolve-Path -LiteralPath $EvidenceRoot -ErrorAction Stop).Path
$leaf=Split-Path -Leaf $resolvedEvidence
$dayMatch=[regex]::Match($leaf,'^m8c-thermal-preemption_(\d{4}-\d{2}-\d{2})_\d{6}$')
$day=if($dayMatch.Success){$dayMatch.Groups[1].Value}else{Get-Date -Format 'yyyy-MM-dd'}

$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
if([string]::IsNullOrWhiteSpace($WatchdogLogPath)){
    $WatchdogLogPath=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f $day)
}
if([string]::IsNullOrWhiteSpace($WatchdogStatusPath)){
    $WatchdogStatusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
}
if([string]::IsNullOrWhiteSpace($WatchdogJournalPath)){
    $WatchdogJournalPath=Join-Path $serviceRoot 'state\lease.json'
}

function Copy-EvidenceIfPresent {
    param(
        [string]$Source,
        [string]$DestinationName
    )

    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){
        return $false
    }

    Copy-Item -LiteralPath $Source -Destination (Join-Path $resolvedEvidence $DestinationName) -Force
    return $true
}

$watchdogLogName=Split-Path -Leaf $WatchdogLogPath
$watchdogLogCopied=Copy-EvidenceIfPresent -Source $WatchdogLogPath -DestinationName $watchdogLogName
$statusCopied=Copy-EvidenceIfPresent -Source $WatchdogStatusPath -DestinationName 'm8c-watchdog-status-final.json'
$journalCopied=Copy-EvidenceIfPresent -Source $WatchdogJournalPath -DestinationName 'm8c-retained-lease-final.json'

$serviceSnapshot=$null
try {
    $svc=Get-CimInstance Win32_Service -Filter "Name='VictusFanControlWatchdogM4'" -ErrorAction SilentlyContinue
    if($svc){
        $serviceSnapshot=[ordered]@{
            name=[string]$svc.Name
            state=[string]$svc.State
            startMode=[string]$svc.StartMode
            processId=[int]$svc.ProcessId
            startName=[string]$svc.StartName
            pathName=[string]$svc.PathName
        }
    }
}
catch {
    $serviceSnapshot=[ordered]@{ error=$_.Exception.Message }
}

$serviceSnapshot | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm8c-service-final.json') -Encoding UTF8

$head='unavailable'
try {
    $head=(& git -C $RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
}
catch {}

$gitStatus='unavailable'
try {
    $gitStatus=(& git -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String).TrimEnd()
}
catch {}

Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm8c-head.txt') -Value $head -Encoding ASCII
Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm8c-git-status.txt') -Value $gitStatus -Encoding UTF8

$zipPath=("{0}.zip" -f $resolvedEvidence)

$manifest=[ordered]@{
    schemaVersion=1
    gate='M8C-EVIDENCE-PACKAGE'
    packagedUtc=(Get-Date).ToUniversalTime().ToString('O')
    evidenceRoot=$resolvedEvidence
    sourceHead=$head
    watchdogLogSource=$WatchdogLogPath
    watchdogLogCopied=$watchdogLogCopied
    watchdogStatusSource=$WatchdogStatusPath
    watchdogStatusCopied=$statusCopied
    retainedJournalSource=$WatchdogJournalPath
    retainedJournalCopied=$journalCopied
    serviceSnapshotPresent=$null -ne $serviceSnapshot
    zipPath=$zipPath
}

$manifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $resolvedEvidence 'm8c-package-manifest.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $resolvedEvidence '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

Write-Host ("M8C evidence directory: {0}" -f $resolvedEvidence) -ForegroundColor Green
Write-Host ("M8C evidence ZIP      : {0}" -f $zipPath) -ForegroundColor Green
Write-Output $zipPath
