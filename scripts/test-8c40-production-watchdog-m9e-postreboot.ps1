$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'
$m9eStateRoot=Join-Path $env:ProgramData 'VictusFanControl\M9E'
$armPath=Join-Path $m9eStateRoot 'm9e-reboot-arm.json'
$postPath=Join-Path $m9eStateRoot 'm9e-postreboot-result.json'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9e-postreboot_{0}" -f $stamp)
$resultPath=Join-Path $evidenceRoot 'm9e-postreboot-result.json'
$packageScript=Join-Path $PSScriptRoot 'package-m9e-evidence.ps1'
$expectedBranch='feature/victus-8c40-m9-preproduction'

# Stage2 is read-only with respect to service/fans, but it is only meaningful
# after the versioned M9E stage1 arm and earlier physical gates.
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9d.physicalPassed){
    throw 'M9E post-reboot verification blocked: prerequisite physical gates are not recorded.'
}
if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
   [bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized){
    throw 'M9E post-reboot verification must occur before final production promotion.'
}
if(-not (Test-Path -LiteralPath $armPath -PathType Leaf)){
    throw 'M9E post-reboot verification requires the preserved stage1 reboot-arm marker.'
}
if(Test-Path -LiteralPath $postPath){
    throw 'M9E refuses to overwrite preserved post-reboot evidence.'
}

$arm=Get-Content -LiteralPath $armPath -Raw | ConvertFrom-Json
if($arm.gate-cne 'M9E-STAGE1' -or $arm.result-cne 'PASS' -or [bool]$arm.fanWriteAttempted -or [bool]$arm.watchdogLeaseAttempted){
    throw 'M9E stage1 arm marker is invalid.'
}

$branch=(& git branch --show-current 2>&1 | Out-String).Trim()
$head=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
$upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
if($LASTEXITCODE-ne 0 -or $branch-cne $expectedBranch -or $head-cne [string]$arm.repositoryHead -or $head-cne $upstreamHead){
    throw "M9E post-reboot requires the exact armed branch/HEAD. branch=$branch head=$head armed=$($arm.repositoryHead) upstream=$upstreamHead"
}

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9E post-reboot refused while '$name' is running."}
}
if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9E post-reboot found retained watchdog journal evidence.'
}

$boot=(Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime()
$armedUtc=[DateTimeOffset]::Parse([string]$arm.armedUtc)
if($boot -le $armedUtc.UtcDateTime){
    throw "M9E requires an actual reboot after stage1 arm. boot=$($boot.ToString('O')) armed=$($armedUtc.UtcDateTime.ToString('O'))"
}

$svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
if($svc.StartMode-cne 'Auto' -or $svc.State-cne 'Running' -or [int]$svc.ProcessId-le 0 -or
   [string]$svc.StartName -notmatch 'LocalSystem|Local System'){
    throw "M9E post-reboot service state invalid: $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)/$($svc.StartName)"
}
foreach($required in @($serviceExe,'--service-name VictusFanControlWatchdogM4','--m4-8c40-lease-service')){
    if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase)-lt 0){
        throw "M9E post-reboot service path mismatch; missing '$required'."
    }
}

$pid=[int]$svc.ProcessId
$proc=[Diagnostics.Process]::GetProcessById($pid)
try{$startUtc=$proc.StartTime.ToUniversalTime();$startTicks=[long]$startUtc.Ticks}finally{$proc.Dispose()}
if($startUtc -lt $boot.AddSeconds(-5)){
    throw "M9E service PID predates the qualified boot unexpectedly. serviceStart=$($startUtc.ToString('O')) boot=$($boot.ToString('O'))"
}

$deadline=(Get-Date).AddSeconds(20);$status=$null
while((Get-Date)-lt $deadline){
    if(Test-Path -LiteralPath $statusPath){
        try{
            $candidate=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            if([int]$candidate.ProcessId-eq $pid -and $candidate.Ready -and -not $candidate.Blocked -and
               [int]$candidate.SessionId-eq 0 -and $candidate.AccountName-match 'SYSTEM$' -and
               $candidate.TargetProfileId-ceq 'HP-8C40-9D0R1LA-F18' -and
               $candidate.PipeName-ceq 'VictusFanControl.Watchdog.M4.8C40.v2'){
                $status=$candidate;break
            }
        }catch{}
    }
    Start-Sleep -Milliseconds 100
}
if($null-eq $status){throw 'M9E post-reboot watchdog did not reach exact Ready state.'}

$qfailure=(& sc.exe qfailure $serviceName 2>&1 | Out-String)
if($LASTEXITCODE-ne 0 -or $qfailure-notmatch '(?s)5000\s*ms.*5000\s*ms.*10000\s*ms' -or $qfailure-notmatch '86400'){
    throw 'M9E post-reboot SCM recovery policy drifted from 5000/5000/10000 reset=86400.'
}
$qflag=(& sc.exe qfailureflag $serviceName 2>&1 | Out-String)
if($LASTEXITCODE-ne 0 -or $qflag-notmatch '(?i)TRUE|1'){
    throw 'M9E post-reboot failure-actions-on-non-crash flag is not enabled.'
}

function Read-Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE-ne 0){throw "M9E post-reboot setpoint probe failed. Raw=$output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $mx=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $mx.Success){throw "M9E post-reboot could not parse setpoint. Raw=$output"}
    [pscustomobject]@{timestampUtc=(Get-Date).ToUniversalTime().ToString('O');cpu=[int]$mx.Groups[1].Value;gpu=[int]$mx.Groups[2].Value;raw=$line}
}
$samples=@();$consecutive=0
for($i=1;$i-le 6;$i++){
    $x=Read-Setpoint;$samples+=@($x)
    if($x.cpu-eq 255 -and $x.gpu-eq 255){$consecutive++;if($consecutive-ge 2){break}}else{$consecutive=0}
    if($i-lt 6){Start-Sleep -Milliseconds 75}
}
if($consecutive-lt 2){throw 'M9E post-reboot requires two consecutive FF/FF samples.'}
if(Test-Path -LiteralPath $journalPath){throw 'M9E post-reboot journal appeared during read-only verification.'}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
$result=[ordered]@{
    schemaVersion=1
    gate='M9E-POSTREBOOT'
    result='PASS'
    repositoryHead=$head
    stage1ArmSha256=(Get-FileHash -LiteralPath $armPath -Algorithm SHA256).Hash.ToLowerInvariant()
    armedUtc=[string]$arm.armedUtc
    bootUtc=$boot.ToString('O')
    servicePid=$pid
    serviceStartUtc=$startUtc.ToString('O')
    serviceStartUtcTicks=$startTicks
    serviceStartMode=[string]$svc.StartMode
    serviceAccount=[string]$svc.StartName
    recoveryDisposition=[string]$status.RecoveryDisposition
    journalAbsent=$true
    firmwareProof=$samples
    watchdogExeSha256=(Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
    pawnIoModuleSha256=(Get-FileHash -LiteralPath $serviceModule -Algorithm SHA256).Hash.ToLowerInvariant()
    qfailure=$qfailure
    qfailureFlag=$qflag
    serviceMutationAttempted=$false
    fanWriteAttempted=$false
    watchdogLeaseAttempted=$false
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $postPath -Encoding UTF8
Copy-Item -LiteralPath $postPath -Destination $resultPath

$package=& $packageScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot
Write-Host ''
Write-Host 'PASS: M9E post-reboot Automatic/Running watchdog lifecycle verified.' -ForegroundColor Green
Write-Host ("Evidence ZIP: {0}" -f $package.ZipPath) -ForegroundColor Green
Write-Host ("ZIP SHA256 : {0}" -f $package.ZipSha256) -ForegroundColor Green
Write-Host 'No fan write, watchdog lease or service mutation occurred in post-reboot verification.' -ForegroundColor Green
