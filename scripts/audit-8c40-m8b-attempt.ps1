param(
    [string]$EvidenceRoot = '',
    [string]$ServiceLogPath = ''
)

$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot

if([string]::IsNullOrWhiteSpace($EvidenceRoot)){
    $EvidenceRoot=Join-Path $repoRoot 'logs\m8b-watchdog-load_2026-09-30_032326'
}

if([string]::IsNullOrWhiteSpace($ServiceLogPath)){
    $ServiceLogPath=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\logs\watchdog-m4-8c40-2026-09-30.log'
}

$readyPath=Join-Path $EvidenceRoot 'm8b-ready.json'
$resultPath=Join-Path $EvidenceRoot 'm8b-result.json'
$summaryPath=Join-Path $EvidenceRoot 'm8b-harness-summary.json'
$failsafePath=Join-Path $EvidenceRoot 'm8b-failsafe.log'

function Require-File([string]$Path,[string]$Label){
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf)){
        throw ("M8B retrospective audit incomplete: missing {0}: {1}" -f $Label,$Path)
    }
}

function Require([bool]$Condition,[string]$Message){
    if(-not $Condition){throw ("M8B retrospective audit failed: " + $Message)}
}

function Read-Json([string]$Path){
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Find-EventIndices([string[]]$Lines,[int]$ControllerPid){
    $tag="controller PID=$ControllerPid"
    $result=[ordered]@{
        PREPARE=@()
        WRITE_INTENT=@()
        COMMIT=@()
        RESTORE_BEGIN=@()
        RELEASE=@()
    }

    for($i=0;$i -lt $Lines.Count;$i++){
        $line=[string]$Lines[$i]
        if(-not $line.Contains($tag)){continue}

        if($line -match 'WATCHDOG PREPARE ACK'){$result.PREPARE+=@($i)}
        if($line -match 'WATCHDOG WRITE_INTENT ACK' -and $line -match 'target=50/50'){$result.WRITE_INTENT+=@($i)}
        if($line -match 'WATCHDOG COMMIT ACK' -and $line -match 'target=50/50'){$result.COMMIT+=@($i)}
        if($line -match 'WATCHDOG RESTORE_BEGIN ACK'){$result.RESTORE_BEGIN+=@($i)}
        if($line -match 'WATCHDOG RELEASE ACK'){$result.RELEASE+=@($i)}
    }

    return $result
}

Write-Host 'VictusFanControl - M8B ATTEMPT-4 RETROSPECTIVE EVIDENCE AUDIT' -ForegroundColor Cyan
Write-Host 'READ-ONLY: this script does not start/stop services, delete journals or access fan-control hardware.' -ForegroundColor Yellow
Write-Host ''

Require-File $readyPath 'READY evidence'
Require-File $resultPath 'controller result evidence'
Require-File $summaryPath 'parent harness summary'

$ready=Read-Json $readyPath
$result=Read-Json $resultPath
$summary=Read-Json $summaryPath

Require ([int]$ready.schemaVersion -eq 1) 'READY schemaVersion is not 1.'
Require ([string]$ready.gate -ceq 'M8B') 'READY gate is not M8B.'
Require ([string]$ready.targetProfileId -ceq 'HP-8C40-9D0R1LA-F18') 'READY target mismatch.'
Require ([string]$result.targetProfileId -ceq 'HP-8C40-9D0R1LA-F18') 'result target mismatch.'

$controllerPid=[int]$ready.processId
$controllerTicks=[long]$ready.processStartUtcTicks

Require ($controllerPid -gt 0 -and $controllerTicks -gt 0) 'READY controller identity is incomplete.'
Require ([int]$result.controller.processId -eq $controllerPid) 'READY/result controller PID mismatch.'
Require ([long]$result.controller.processStartUtcTicks -eq $controllerTicks) 'READY/result controller creation-time mismatch.'
Require ([int]$summary.controllerPid -eq $controllerPid) 'READY/summary controller PID mismatch.'
Require ([long]$summary.controllerStartUtcTicks -eq $controllerTicks) 'READY/summary controller creation-time mismatch.'
Require ([int]$summary.watchdogPid -gt 0 -and [long]$summary.watchdogStartUtcTicks -gt 0) 'summary watchdog identity is incomplete.'
Require ([int]$summary.journalGeneration -gt 0) 'summary journal generation is missing.'

Require ([int]$ready.cpuSetpoint -eq 50 -and [int]$ready.gpuSetpoint -eq 50) 'READY does not prove EC 50/50.'
Require ([int]$ready.cpuRpm -gt 0 -and [int]$ready.gpuRpm -gt 0) 'READY does not prove dual-tach feedback.'
Require ([int]$ready.maxFan -eq 0 -and [int]$ready.fanSwitch -eq 0) 'READY guards are not 00/00.'
Require ([int]$ready.applyCalls -eq 1) 'READY applyCalls is not exactly 1.'

Require ([string]$result.result -ceq 'PASS') 'controller result is not PASS.'
Require ([int]$result.applyCalls -eq 1) 'controller result applyCalls is not exactly 1.'
Require ([bool]$result.normalRestoreCompleted) 'controller did not record normalRestoreCompleted=true.'
Require ([bool]$result.finalFirmwareOwned) 'controller did not record finalFirmwareOwned=true.'
Require ([int]$result.window.representativeSamples -eq 30) 'controller result does not contain 30/30 representative supervision samples.'
Require ([int]$result.window.maximumConsecutiveRepresentative -eq 30) 'controller result does not contain max consecutive 30.'

Require ([bool]$summary.finalJournalAbsent) 'parent summary does not prove final journal absence.'
Require ([bool]$summary.finalFirmwareProofPass) 'parent summary does not prove independent final FF/FF.'
Require ([bool]$summary.finalServiceBaselinePass) 'parent summary does not prove M4 Manual/stopped closure.'
Require ([bool]$summary.finalClosurePass) 'parent summary finalClosurePass is false.'
Require (-not [bool]$summary.failsafeTakeover) 'parent summary reports failsafe takeover.'

Write-Host ("Controller identity: PID={0} startTicks={1}" -f $controllerPid,$controllerTicks) -ForegroundColor Green
Write-Host ("Watchdog identity : PID={0} startTicks={1}" -f [int]$summary.watchdogPid,[long]$summary.watchdogStartUtcTicks) -ForegroundColor Green
Write-Host ("Evidence HEAD      : {0}" -f [string]$summary.evidenceHead) -ForegroundColor Green
Write-Host ("Journal generation : {0}" -f [int]$summary.journalGeneration) -ForegroundColor Green

Require-File $ServiceLogPath 'dated M4 watchdog service log'
$serviceLines=@(Get-Content -LiteralPath $ServiceLogPath)
$events=Find-EventIndices -Lines $serviceLines -ControllerPid $controllerPid

foreach($name in @('PREPARE','WRITE_INTENT','COMMIT','RESTORE_BEGIN','RELEASE')){
    $count=@($events[$name]).Count
    Require ($count -eq 1) ("watchdog causal event {0} count is {1}, expected exactly 1 for controller PID {2}." -f $name,$count,$controllerPid)
}

Require (
    $events.PREPARE[0] -lt $events.WRITE_INTENT[0] -and
    $events.WRITE_INTENT[0] -lt $events.COMMIT[0] -and
    $events.COMMIT[0] -lt $events.RESTORE_BEGIN[0] -and
    $events.RESTORE_BEGIN[0] -lt $events.RELEASE[0]
) 'watchdog causal ordering is not PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE.'

Write-Host 'Watchdog causal chain: PASS' -ForegroundColor Green

if(-not (Test-Path -LiteralPath $failsafePath -PathType Leaf)){
    Write-Host ''
    Write-Host ("INCOMPLETE: independent failsafe log is not present at {0}" -f $failsafePath) -ForegroundColor Yellow
    Write-Host 'The summary says failsafeTakeover=false, but this audit deliberately does not promote that field alone to an independent no-takeover proof.' -ForegroundColor Yellow
    exit 4
}

$failsafeText=Get-Content -LiteralPath $failsafePath -Raw

Require ($failsafeText -notmatch 'M8B FAILSAFE TAKEOVER:') 'independent failsafe log records TAKEOVER.'
Require ($failsafeText -notmatch 'M8B FAILSAFE CONTROLLER-KILL:') 'independent failsafe log records controller kill.'
Require ($failsafeText -notmatch 'M8B FAILSAFE SERVICE-START:') 'independent failsafe log records recovery service start.'
Require ($failsafeText -notmatch 'M8B FAILSAFE SERVICE-RESTART:') 'independent failsafe log records recovery service restart.'
Require ($failsafeText -notmatch 'M8B FAILSAFE ERROR:') 'independent failsafe log records an error.'

Write-Host 'Independent failsafe no-takeover evidence: PASS' -ForegroundColor Green
Write-Host ''
Write-Host 'RETROSPECTIVE AUDIT PASS: controller evidence, parent closure, M4 causal log and independent failsafe evidence are mutually consistent.' -ForegroundColor Green
Write-Host 'This script is evidence-only; changing M8B physicalPassed remains a separate reviewed repository decision.' -ForegroundColor Yellow
exit 0
