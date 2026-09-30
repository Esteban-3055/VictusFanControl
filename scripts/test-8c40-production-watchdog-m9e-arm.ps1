param(
    [ValidateRange(3000,10000)]
    [int]$FirstRestartDelayMs=5000
)

$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json

# HARD M9E PHYSICAL/SERVICE-MUTATION BARRIER. Keep this before Administrator,
# CIM, EC, evidence creation or SCM mutation.
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed){
    throw 'M9E BLOCKED: M9B read-only physical PASS is not recorded.'
}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed){
    throw 'M9E BLOCKED: M9C physical PASS is not recorded.'
}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9d.physicalPassed){
    throw 'M9E BLOCKED: M9D physical PASS is not recorded.'
}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9e.stage1.physicalAuthorization.authorized -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9e.stage1.serviceConfigurationAuthorized){
    throw 'M9E BLOCKED: explicit service-lifecycle physical authorization is not recorded.'
}
if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
   [bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized -or
   [bool]$profile.watchdogM9ProductionIntegration.m9e.promotionAuthorized -or
   [bool]$profile.control.enabledByDefault -or
   [bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled){
    throw 'M9E refuses after any production/default/automatic promotion has already been enabled.'
}

$expectedBranch='feature/victus-8c40-m9-preproduction'
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$m9eStateRoot=Join-Path $env:ProgramData 'VictusFanControl\M9E'
$armPath=Join-Path $m9eStateRoot 'm9e-reboot-arm.json'
$postRebootPath=Join-Path $m9eStateRoot 'm9e-postreboot-result.json'
$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9e-service-arm_{0}" -f $stamp)
$resultPath=Join-Path $evidenceRoot 'm9e-stage1-result.json'
$packageScript=Join-Path $PSScriptRoot 'package-m9e-evidence.ps1'
$token='8C40-M9E-SERVICE-LIFECYCLE'

function Assert-Administrator {
    $id=[Security.Principal.WindowsIdentity]::GetCurrent()
    $p=New-Object Security.Principal.WindowsPrincipal($id)
    if(-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M9E stage1 must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    $head=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch -or $head -notmatch '^[0-9a-f]{40}$'){
        throw "M9E requires branch '$expectedBranch' with a valid HEAD; observed branch='$branch' head='$head'."
    }
    $upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    $upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream) -or $head -cne $upstreamHead){
        throw "M9E requires local HEAD == tracked upstream. local=$head upstream=$upstreamHead"
    }
    $status=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'M9E could not inspect git status.'}
    $blocking=@(
        $status -split "[\r\n]+" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
        }
    )
    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'M9E requires committed source/config; only untracked logs/ evidence is allowed.'
    }
    [pscustomobject]@{Branch=$branch;Head=$head;Upstream=$upstream;UpstreamHead=$upstreamHead}
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop
    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(([string]$bios.SMBIOSBIOSVersion).Trim(),([string]$bios.Version).Trim()) -join ' | '
    if(([string]$board.Manufacturer).Trim() -cne 'HP' -or
       ([string]$board.Product).Trim() -cne '8C40' -or
       ([string]$board.Version).Trim() -cne '63.43' -or
       ([string]$system.Manufacturer).Trim() -cne 'HP' -or
       ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M9E exact-target fingerprint mismatch.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Assert-QualificationBaseline {
    $svc=Get-ServiceState
    if(-not $svc){throw 'M9E requires the already-qualified M4 service; it will not install or replace it.'}
    if([string]$svc.State -cne 'Stopped' -or [string]$svc.StartMode -cne 'Manual' -or [int]$svc.ProcessId -ne 0){
        throw "M9E requires Manual/Stopped/PID0 baseline; observed $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)."
    }
    if([string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw "M9E requires LocalSystem; observed '$($svc.StartName)'."
    }
    foreach($required in @($serviceExe,'--service-name VictusFanControlWatchdogM4','--m4-8c40-lease-service')){
        if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase)-lt 0){
            throw "M9E service PathName mismatch; missing '$required'."
        }
    }
    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "M9E watchdog executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "M9E PawnIO module missing: $serviceModule"}
    return $svc
}

function Read-Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9E setpoint probe failed. Raw=$output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $match=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M9E could not parse setpoint. Raw=$output"}
    [pscustomobject]@{timestampUtc=(Get-Date).ToUniversalTime().ToString('O');cpu=[int]$match.Groups[1].Value;gpu=[int]$match.Groups[2].Value;raw=$line}
}

function Assert-StableFirmwareOwned([string]$Context){
    $samples=@();$consecutive=0
    for($i=1;$i -le 6;$i++){
        $x=Read-Setpoint;$samples+=@($x)
        if($x.cpu -eq 255 -and $x.gpu -eq 255){$consecutive++;if($consecutive-ge 2){return $samples}}else{$consecutive=0}
        if($i-lt 6){Start-Sleep -Milliseconds 75}
    }
    throw "$Context requires two consecutive FF/FF observations within six reads."
}

function Get-PowerSnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $s=[System.Windows.Forms.SystemInformation]::PowerStatus
    $pct=$null;if($s.BatteryLifePercent-ge 0){$pct=[math]::Round([double]$s.BatteryLifePercent*100,0)}
    [pscustomobject]@{PowerLineStatus=[string]$s.PowerLineStatus;BatteryPercent=$pct}
}

function Assert-PowerSane {
    $x=Get-PowerSnapshot
    if($x.PowerLineStatus-cne 'Online' -or $null-eq $x.BatteryPercent -or [double]$x.BatteryPercent-lt 20){
        throw "M9E requires AC online and battery >=20%; observed AC=$($x.PowerLineStatus) battery=$($x.BatteryPercent)."
    }
    return $x
}

function Get-ServiceConfigEvidence {
    $qc=(& sc.exe qc $serviceName 2>&1 | Out-String)
    if($LASTEXITCODE-ne 0){throw 'M9E sc qc failed.'}
    $qfailure=(& sc.exe qfailure $serviceName 2>&1 | Out-String)
    if($LASTEXITCODE-ne 0){throw 'M9E sc qfailure failed.'}
    $qfailureFlag=(& sc.exe qfailureflag $serviceName 2>&1 | Out-String)
    if($LASTEXITCODE-ne 0){throw 'M9E sc qfailureflag failed.'}
    [pscustomobject]@{qc=$qc;qfailure=$qfailure;qfailureFlag=$qfailureFlag}
}

function Assert-ProductionPolicy([object]$Service,[object]$Config){
    if([string]$Service.StartMode -cne 'Auto'){throw "M9E production policy requires StartMode Auto; observed '$($Service.StartMode)'."}
    if([string]$Service.State -cne 'Running' -or [int]$Service.ProcessId -le 0){throw 'M9E production policy requires watchdog Running with PID > 0.'}
    if($Config.qfailure -notmatch ("(?s){0}\s*ms.*5000\s*ms.*10000\s*ms" -f $FirstRestartDelayMs) -or
       $Config.qfailure -notmatch '86400'){
        throw 'M9E SCM recovery policy does not contain reset=86400 and ordered 5000/5000/10000 ms restarts.'
    }
    if($Config.qfailureFlag -notmatch '(?i)TRUE|1'){
        throw 'M9E SCM failure-actions-on-non-crash-failures flag is not enabled.'
    }
}

function Wait-Ready([int]$Pid,[long]$StartTicks){
    $deadline=(Get-Date).AddSeconds(20)
    while((Get-Date)-lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try {
                $s=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                $p=[Diagnostics.Process]::GetProcessById($Pid)
                try{$ticks=[long]$p.StartTime.ToUniversalTime().Ticks}finally{$p.Dispose()}
                if([int]$s.ProcessId-eq $Pid -and $ticks-eq $StartTicks -and
                   $s.Ready -and -not $s.Blocked -and [int]$s.SessionId-eq 0 -and
                   $s.AccountName-match 'SYSTEM$' -and
                   $s.TargetProfileId-ceq 'HP-8C40-9D0R1LA-F18' -and
                   $s.PipeName-ceq 'VictusFanControl.Watchdog.M4.8C40.v2'){
                    return $s
                }
            }catch{}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9E timed out waiting for exact watchdog Ready state.'
}

Assert-Administrator
$repo=Assert-RepositoryProvenance
Assert-ExactTarget
$power=Assert-PowerSane

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9E refused while '$name' is running."}
}
if(Test-Path -LiteralPath $armPath -or Test-Path -LiteralPath $postRebootPath){
    throw 'M9E refuses to overwrite preserved M9E reboot evidence in ProgramData.'
}
if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9E refuses retained watchdog journal evidence.'
}
$before=Assert-QualificationBaseline
$beforeConfig=Get-ServiceConfigEvidence
$baselineFf=Assert-StableFirmwareOwned 'M9E stage1 baseline'

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 M9E PRODUCTION WATCHDOG SERVICE LIFECYCLE ARM' -ForegroundColor Cyan
Write-Host 'This gate changes SCM lifecycle only. It issues NO fan target and acquires NO watchdog lease.' -ForegroundColor Yellow

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if($LASTEXITCODE-ne 0){throw "M9E build failed: $LASTEXITCODE"}
& (Join-Path $PSScriptRoot 'test-8c40-m9e-service-lifecycle-invariants.ps1')
if($LASTEXITCODE-ne 0){throw 'M9E invariant failed.'}

$confirm=Read-Host "Type exactly $token to apply the candidate production SCM lifecycle policy"
if($confirm-cne $token){throw 'M9E cancelled before SCM mutation.'}

$pass=$false;$failure=$null;$package=$null
try {
    & sc.exe failure $serviceName reset= 86400 actions= "restart/$FirstRestartDelayMs/restart/5000/restart/10000" | Out-Host
    if($LASTEXITCODE-ne 0){throw "M9E sc failure failed: $LASTEXITCODE"}
    & sc.exe failureflag $serviceName 1 | Out-Host
    if($LASTEXITCODE-ne 0){throw "M9E sc failureflag failed: $LASTEXITCODE"}
    & sc.exe config $serviceName start= auto | Out-Host
    if($LASTEXITCODE-ne 0){throw "M9E sc config start=auto failed: $LASTEXITCODE"}

    if(Test-Path -LiteralPath $journalPath){throw 'M9E journal appeared before service start; service will not be started by this harness.'}

    Start-Service -Name $serviceName
    $svc=Get-ServiceState
    if(-not $svc -or $svc.State-cne 'Running' -or [int]$svc.ProcessId-le 0){throw 'M9E service did not reach Running.'}
    $pid=[int]$svc.ProcessId
    $proc=[Diagnostics.Process]::GetProcessById($pid)
    try{$startTicks=[long]$proc.StartTime.ToUniversalTime().Ticks}finally{$proc.Dispose()}
    $ready=Wait-Ready $pid $startTicks

    $afterConfig=Get-ServiceConfigEvidence
    $svc=Get-ServiceState
    Assert-ProductionPolicy $svc $afterConfig
    if(Test-Path -LiteralPath $journalPath){throw 'M9E service startup unexpectedly left/created a durable journal.'}
    $afterFf=Assert-StableFirmwareOwned 'M9E stage1 post-service-start'

    New-Item -ItemType Directory -Force -Path $m9eStateRoot | Out-Null
    $boot=(Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
    $arm=[ordered]@{
        schemaVersion=1
        gate='M9E-STAGE1'
        result='PASS'
        repositoryHead=$repo.Head
        armedUtc=(Get-Date).ToUniversalTime().ToString('O')
        bootBeforeArmUtc=$boot.ToUniversalTime().ToString('O')
        serviceName=$serviceName
        servicePid=$pid
        serviceStartUtcTicks=$startTicks
        serviceStartMode=[string]$svc.StartMode
        serviceAccount=[string]$svc.StartName
        servicePath=[string]$svc.PathName
        watchdogExeSha256=(Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
        pawnIoModuleSha256=(Get-FileHash -LiteralPath $serviceModule -Algorithm SHA256).Hash.ToLowerInvariant()
        profileSha256=(Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        failureResetSeconds=86400
        restartDelaysMs=@($FirstRestartDelayMs,5000,10000)
        failureActionsOnNonCrashFailures=$true
        delayedAutoStart=$false
        journalAbsent=(-not (Test-Path -LiteralPath $journalPath))
        baselineFf=$baselineFf
        postStartFf=$afterFf
        serviceConfigBefore=$beforeConfig
        serviceConfigAfter=$afterConfig
        fanWriteAttempted=$false
        watchdogLeaseAttempted=$false
        rebootDispatched=$false
    }
    $arm | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $armPath -Encoding UTF8
    Copy-Item -LiteralPath $armPath -Destination (Join-Path $evidenceRoot 'm9e-reboot-arm.json')
    $arm | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    $pass=$true
}
catch {
    $failure=$_.Exception.Message
    [ordered]@{
        schemaVersion=1;gate='M9E-STAGE1';result='FAIL_CLOSED';failure=$failure;
        repositoryHead=$repo.Head;timestampUtc=(Get-Date).ToUniversalTime().ToString('O');
        service=(Get-ServiceState);journalPresent=(Test-Path -LiteralPath $journalPath);
        fanWriteAttempted=$false;watchdogLeaseAttempted=$false;rebootDispatched=$false
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
finally {
    try {
        $package=& $packageScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot
        Write-Host ("M9E evidence ZIP: {0}" -f $package.ZipPath)
        Write-Host ("M9E ZIP SHA256 : {0}" -f $package.ZipSha256)
    } catch {
        $pass=$false
        $failure=$(if($failure){"$failure | packaging: $($_.Exception.Message)"}else{"packaging: $($_.Exception.Message)"})
    }
}

if(-not $pass){
    Write-Host 'M9E FAIL_CLOSED. Do not reboot for qualification and do not run a later write gate until this state is reviewed.' -ForegroundColor Red
    throw "M9E stage1 failed: $failure"
}

Write-Host ''
Write-Host 'PASS: M9E stage1 candidate production SCM policy is armed.' -ForegroundColor Green
Write-Host 'Service intentionally remains Automatic/Running for the later reboot qualification. No fan write or watchdog lease occurred.' -ForegroundColor Green
Write-Host 'Do not reboot for M9E qualification until this PASS evidence is reviewed/authorized by the versioned workflow.' -ForegroundColor Yellow
