param(
    [Parameter(Mandatory=$true)]
    [string]$ArmPath
)

$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$ArmPath=[IO.Path]::GetFullPath($ArmPath)

$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9e-postreboot_{0}" -f $stamp)
$resultPath=Join-Path $evidenceRoot 'm9e-postreboot-result.json'
$packageScript=Join-Path $PSScriptRoot 'package-m9e-evidence.ps1'
$expectedBranch='feature/victus-8c40-m9-preproduction'

# Read-only post-reboot gate. No service/fan/lease mutation is allowed here.
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9d.physicalPassed){
    throw 'M9E post-reboot verification blocked: prerequisite physical gates are not recorded.'
}
if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
   [bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized){
    throw 'M9E post-reboot verification must occur before final production promotion.'
}
if(-not (Test-Path -LiteralPath $ArmPath -PathType Leaf)){
    throw "M9E post-reboot verification requires preserved stage1 arm evidence: $ArmPath"
}

$arm=Get-Content -LiteralPath $ArmPath -Raw | ConvertFrom-Json
if([int]$arm.schemaVersion-ne 2 -or
   $arm.gate-cne 'M9E-STAGE1' -or
   $arm.result-cne 'PASS' -or
   [bool]$arm.fanWriteAttempted -or
   [bool]$arm.watchdogLeaseAttempted){
    throw 'M9E stage1 arm evidence is invalid.'
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop
    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(([string]$bios.SMBIOSBIOSVersion).Trim(),([string]$bios.Version).Trim()) -join ' | '

    if(([string]$board.Manufacturer).Trim()-cne 'HP' -or
       ([string]$board.Product).Trim()-cne '8C40' -or
       ([string]$board.Version).Trim()-cne '63.43' -or
       ([string]$system.Manufacturer).Trim()-cne 'HP' -or
       ([string]$system.Model).Trim()-cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase-cne '9D0R1LA' -or
       $biosText-notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M9E post-reboot exact-target fingerprint mismatch.'
    }
}

function Get-DelayedAutoStart {
    $key="HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $value=(Get-ItemProperty -LiteralPath $key -Name DelayedAutostart -ErrorAction SilentlyContinue).DelayedAutostart
    if($null-eq $value){return 0}
    return [int]$value
}

$branch=(& git branch --show-current 2>&1 | Out-String).Trim()
$head=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
$upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
$upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
if($LASTEXITCODE-ne 0 -or
   $branch-cne $expectedBranch -or
   $head-cne [string]$arm.repositoryHead -or
   $head-cne $upstreamHead){
    throw "M9E post-reboot requires exact armed branch/HEAD. branch=$branch head=$head armed=$($arm.repositoryHead) upstream=$upstreamHead"
}

$statusText=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
if($LASTEXITCODE-ne 0){throw 'M9E post-reboot could not inspect git status.'}
$blocking=@(
    $statusText -split "[\r\n]+" |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
    }
)
if($blocking.Count-gt 0){
    $blocking | ForEach-Object {Write-Host $_}
    throw 'M9E post-reboot requires committed source/config; only untracked logs/ evidence is allowed.'
}

Assert-ExactTarget

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){
        throw "M9E post-reboot refused while '$name' is running."
    }
}

if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9E post-reboot found retained watchdog journal evidence.'
}

if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){
    throw "M9E post-reboot service executable missing: $serviceExe"
}
if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){
    throw "M9E post-reboot PawnIO module missing: $serviceModule"
}

$exeHash=(Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
$moduleHash=(Get-FileHash -LiteralPath $serviceModule -Algorithm SHA256).Hash.ToLowerInvariant()
$profileHash=(Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash.ToLowerInvariant()

if($exeHash-cne [string]$arm.watchdogExeSha256 -or
   $moduleHash-cne [string]$arm.pawnIoModuleSha256 -or
   $profileHash-cne [string]$arm.profileSha256){
    throw 'M9E post-reboot source/installed-binary hash continuity failed against stage1 arm evidence.'
}

$boot=(Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime()
$armedUtc=[DateTimeOffset]::Parse([string]$arm.armedUtc)
if($boot-le $armedUtc.UtcDateTime){
    throw "M9E requires an actual reboot after stage1 arm. boot=$($boot.ToString('O')) armed=$($armedUtc.UtcDateTime.ToString('O'))"
}

$svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
$delayedAutoStart=Get-DelayedAutoStart
if($svc.StartMode-cne 'Auto' -or
   $svc.State-cne 'Running' -or
   [int]$svc.ProcessId-le 0 -or
   [string]$svc.StartName-notmatch 'LocalSystem|Local System' -or
   $delayedAutoStart-ne 0){
    throw "M9E post-reboot service state invalid: $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)/$($svc.StartName)/delayed=$delayedAutoStart"
}

foreach($required in @($serviceExe,'--service-name VictusFanControlWatchdogM4','--m4-8c40-lease-service')){
    if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase)-lt 0){
        throw "M9E post-reboot service path mismatch; missing '$required'."
    }
}

$pid=[int]$svc.ProcessId
$proc=[Diagnostics.Process]::GetProcessById($pid)
try {
    $startUtc=$proc.StartTime.ToUniversalTime()
    $startTicks=[long]$startUtc.Ticks
}
finally {
    $proc.Dispose()
}

if($startUtc-lt $boot.AddSeconds(-5)){
    throw "M9E service PID predates qualified boot. serviceStart=$($startUtc.ToString('O')) boot=$($boot.ToString('O'))"
}

$deadline=(Get-Date).AddSeconds(20)
$watchdogStatus=$null
while((Get-Date)-lt $deadline){
    if(Test-Path -LiteralPath $statusPath){
        try{
            $candidate=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            if([int]$candidate.ProcessId-eq $pid -and
               $candidate.Ready -and
               -not $candidate.Blocked -and
               [int]$candidate.SessionId-eq 0 -and
               $candidate.AccountName-match 'SYSTEM$' -and
               $candidate.TargetProfileId-ceq 'HP-8C40-9D0R1LA-F18' -and
               $candidate.PipeName-ceq 'VictusFanControl.Watchdog.M4.8C40.v2'){
                $watchdogStatus=$candidate
                break
            }
        }catch{}
    }
    Start-Sleep -Milliseconds 100
}
if($null-eq $watchdogStatus){
    throw 'M9E post-reboot watchdog did not reach exact Ready state.'
}

$qfailure=(& sc.exe qfailure $serviceName 2>&1 | Out-String)
if($LASTEXITCODE-ne 0 -or
   $qfailure-notmatch '(?s)5000\s*ms.*5000\s*ms.*10000\s*ms' -or
   $qfailure-notmatch '86400'){
    throw 'M9E post-reboot SCM recovery policy drifted from 5000/5000/10000 reset=86400.'
}

$qflag=(& sc.exe qfailureflag $serviceName 2>&1 | Out-String)
if($LASTEXITCODE-ne 0 -or $qflag-notmatch '(?i)TRUE|1'){
    throw 'M9E post-reboot failure-actions-on-non-crash flag is not enabled.'
}

function Read-Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE-ne 0){
        throw "M9E post-reboot setpoint probe failed. Raw=$output"
    }
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $mx=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $mx.Success){
        throw "M9E post-reboot could not parse setpoint. Raw=$output"
    }
    [pscustomobject]@{
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        cpu=[int]$mx.Groups[1].Value
        gpu=[int]$mx.Groups[2].Value
        raw=$line
    }
}

$samples=@()
$consecutive=0
for($i=1;$i-le 6;$i++){
    $x=Read-Setpoint
    $samples+=@($x)
    if($x.cpu-eq 255 -and $x.gpu-eq 255){
        $consecutive++
        if($consecutive-ge 2){break}
    }else{
        $consecutive=0
    }
    if($i-lt 6){Start-Sleep -Milliseconds 75}
}
if($consecutive-lt 2){
    throw 'M9E post-reboot requires two consecutive FF/FF samples.'
}
if(Test-Path -LiteralPath $journalPath){
    throw 'M9E post-reboot journal appeared during read-only verification.'
}

if(Test-Path -LiteralPath $evidenceRoot){
    throw "M9E refuses existing post-reboot evidence directory: $evidenceRoot"
}
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null

$result=[ordered]@{
    schemaVersion=2
    gate='M9E-POSTREBOOT'
    result='PASS'
    repositoryHead=$head
    stage1ArmPath=$ArmPath
    stage1ArmSha256=(Get-FileHash -LiteralPath $ArmPath -Algorithm SHA256).Hash.ToLowerInvariant()
    armedUtc=[string]$arm.armedUtc
    bootUtc=$boot.ToString('O')
    servicePid=$pid
    serviceStartUtc=$startUtc.ToString('O')
    serviceStartUtcTicks=$startTicks
    serviceStartMode=[string]$svc.StartMode
    serviceAccount=[string]$svc.StartName
    delayedAutoStart=$delayedAutoStart
    recoveryDisposition=[string]$watchdogStatus.RecoveryDisposition
    journalAbsent=$true
    firmwareProof=$samples
    watchdogExeSha256=$exeHash
    pawnIoModuleSha256=$moduleHash
    profileSha256=$profileHash
    qfailure=$qfailure
    qfailureFlag=$qflag
    serviceMutationAttempted=$false
    fanWriteAttempted=$false
    watchdogLeaseAttempted=$false
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
$package=& $packageScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot

Write-Host ''
Write-Host 'PASS: M9E post-reboot Automatic/Running watchdog lifecycle verified.' -ForegroundColor Green
Write-Host ("Evidence ZIP: {0}" -f $package.ZipPath) -ForegroundColor Green
Write-Host ("ZIP SHA256 : {0}" -f $package.ZipSha256) -ForegroundColor Green
Write-Host 'No fan write, watchdog lease or service mutation occurred in post-reboot verification.' -ForegroundColor Green
