param(
    [ValidateRange(30,300)][int]$RecommendedSleepSeconds=60,
    [ValidateRange(180,600)][int]$FailsafeDelaySeconds=300,
    [ValidateRange(60,300)][int]$ResultTimeoutSeconds=180
)

$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER.
# This must precede Administrator, evidence creation, service mutation, PawnIO,
# named-pipe construction, GUI launch or any fan write.
$m9d=$profile.watchdogM9ProductionIntegration.m9d
$m9b=$profile.watchdogM9ProductionIntegration.m9b
$m9c=$profile.watchdogM9ProductionIntegration.m9c
if(-not [bool]$m9b.noWritePreflightPassed -or
   -not [bool]$m9c.physicalPassed -or
   -not [bool]$m9d.physicalAuthorization.authorized -or
   -not [bool]$m9d.physicalExecutionAuthorized -or
   -not [bool]$m9d.qualificationConstructionAuthorized){
    throw 'M9D PHYSICAL BLOCKED: M9B PASS + M9C PASS + explicit M9D authorization/controller/construction gates are required.'
}
if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
   [bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized -or
   [bool]$profile.control.enabledByDefault -or
   [bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled){
    throw 'M9D refuses a profile that already promoted watchdog/default/automatic production behavior.'
}

$expectedBranch=[string]$m9d.expectedPhysicalBranch
if([string]::IsNullOrWhiteSpace($expectedBranch)){
    throw 'M9D profile expectedPhysicalBranch is missing.'
}

$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$appLog=Join-Path $env:LOCALAPPDATA ("VictusFanControl\logs\events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$modulesDir=Join-Path $repoRoot 'modules'
$appExe=Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$token='8C40-M9D-PRODUCTION-LIFECYCLE30'
$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9d-production-lifecycle_{0}" -f $stamp)
$markerRoot=Join-Path $evidenceRoot 'markers'
$readyPath=Join-Path $markerRoot 'm9d-production-lifecycle.ready'
$preSleepPath=Join-Path $markerRoot 'm9d-production-lifecycle.presleep'
$resumePath=Join-Path $markerRoot 'm9d-production-lifecycle.resume-gate'
$reentryPath=Join-Path $markerRoot 'm9d-production-lifecycle.reentry'
$resultPath=Join-Path $markerRoot 'm9d-production-lifecycle.result'
$failsafeLog=Join-Path $evidenceRoot 'm9d-failsafe.log'
$summaryPath=Join-Path $evidenceRoot 'm9d-harness-summary.json'
$trackedHelper=Join-Path $PSScriptRoot 'm9d-tracked-child.ps1'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-m9d-service-failsafe-8c40.ps1'
$packager=Join-Path $PSScriptRoot 'package-m9d-evidence.ps1'

. $trackedHelper

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M9D physical lifecycle gate requires elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    $head=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    $upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($branch -cne $expectedBranch){throw "M9D requires branch '$expectedBranch'; observed '$branch'."}
    if($head -notmatch '^[0-9a-f]{40}$' -or $upstreamHead -notmatch '^[0-9a-f]{40}$' -or $head -cne $upstreamHead){
        throw "M9D requires local HEAD == upstream HEAD. local=$head upstream=$upstreamHead"
    }

    $statusText=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    $blocking=@($statusText -split "[\r\n]+" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
    })
    if($blocking.Count -gt 0){throw 'M9D requires committed source/config state; only untracked logs/ evidence is allowed.'}
    return [pscustomobject]@{Branch=$branch;Head=$head;Upstream=$upstream;UpstreamHead=$upstreamHead}
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
        throw 'M9D exact-target fingerprint mismatch.'
    }
    return [pscustomobject]@{Sku=$sku;Bios=$biosText;Board='HP 8C40 63.43'}
}

function Get-ServiceSnapshot {
    $svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if(-not $svc){return [pscustomobject]@{Installed=$false;State='Absent';StartMode='Absent';StartName='Absent';ProcessId=0;PathName=''}}
    [pscustomobject]@{
        Installed=$true;State=[string]$svc.State;StartMode=[string]$svc.StartMode;
        StartName=[string]$svc.StartName;ProcessId=[int]$svc.ProcessId;PathName=[string]$svc.PathName
    }
}

function Assert-ServiceBaseline([object]$svc){
    if(-not $svc.Installed){throw 'M9D requires installed VictusFanControlWatchdogM4.'}
    if($svc.State -cne 'Stopped' -or $svc.StartMode -cne 'Manual' -or $svc.ProcessId -ne 0){
        throw "M9D requires M4 Manual/Stopped/PID0; observed $($svc.StartMode)/$($svc.State)/$($svc.ProcessId)."
    }
    if($svc.StartName -notmatch 'SYSTEM$'){throw "M9D requires LocalSystem; observed '$($svc.StartName)'."}
    if($svc.PathName -notmatch [regex]::Escape('--m4-8c40-lease-service')){throw 'M9D service is not configured for the qualified M4 lease mode.'}
}

function Get-ServiceStartTicks([int]$ProcessId){
    $proc=[Diagnostics.Process]::GetProcessById($ProcessId)
    try{return [long]$proc.StartTime.ToUniversalTime().Ticks}finally{$proc.Dispose()}
}

function Wait-ServiceReady([int]$ProcessId,[long]$Ticks,[int]$Seconds=20){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date)-lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try {
                $status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                $svc=Get-ServiceSnapshot
                if($status.Ready -and -not $status.Blocked -and
                   [int]$status.ProcessId -eq $ProcessId -and $svc.State -ceq 'Running' -and [int]$svc.ProcessId -eq $ProcessId -and
                   [int]$status.SessionId -eq 0 -and $status.AccountName -match 'SYSTEM$' -and
                   $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   (Get-ServiceStartTicks $ProcessId) -eq $Ticks){return $status}
            } catch {}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9D timed out waiting for exact watchdog Ready identity.'
}

function Read-Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9D read-only setpoint probe failed: $output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $m=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $m.Success){throw "M9D could not parse setpoint: $line"}
    [pscustomobject]@{Cpu=[int]$m.Groups[1].Value;Gpu=[int]$m.Groups[2].Value;Raw=$line;TimestampUtc=(Get-Date).ToUniversalTime().ToString('O')}
}

function Wait-StableFF([string]$Label,[int]$MaxReads=8){
    $reads=@();$consecutive=0
    for($i=1;$i -le $MaxReads;$i++){
        $r=Read-Setpoint;$reads+=@($r)
        Write-Host ("{0} {1}/{2}: {3}" -f $Label,$i,$MaxReads,$r.Raw)
        if($r.Cpu -eq 255 -and $r.Gpu -eq 255){$consecutive++}else{$consecutive=0}
        if($consecutive -ge 2){return [pscustomobject]@{Passed=$true;Reads=$reads}}
        Start-Sleep -Milliseconds 75
    }
    throw "M9D failed stable two-consecutive FF/FF proof at '$Label'."
}

function Wait-File([string]$Path,[int]$Seconds,[Diagnostics.Process]$Process){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date)-lt $deadline -and -not (Test-Path -LiteralPath $Path)){
        if($Process.HasExited){return $false}
        Start-Sleep -Milliseconds 100
    }
    return (Test-Path -LiteralPath $Path)
}

function Assert-OwnedJournal([int]$ProcessId,[long]$Ticks){
    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9D READY exists but durable journal is absent.'}
    $j=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    $phase=([string]$j.Phase -ceq 'Owned') -or ([string]$j.Phase -ceq '2') -or ([int]$j.Phase -eq 2)
    if([int]$j.SchemaVersion -ne 2 -or $j.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not $phase -or [long]$j.Generation -ne 3 -or
       [int]$j.Controller.ProcessId -ne $ProcessId -or [long]$j.Controller.ProcessStartUtcTicks -ne $Ticks -or
       [int]$j.Owned.Cpu -ne 30 -or [int]$j.Owned.Gpu -ne 30){
        throw 'M9D journal is not exact schema-v2 generation-3 OWNED 30/30 bound to the exact GUI identity.'
    }
    return $j
}

function Get-KernelBoundary {
    $e=Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power'} -MaxEvents 1 -ErrorAction SilentlyContinue
    if($null -eq $e){return 0L}
    return [long]$e.RecordId
}

function Wait-ModernStandbyEvidence([datetime]$Start,[datetime]$DisplayOn,[long]$After,[int]$Seconds=30){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date)-lt $deadline){
        $events=@(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power';StartTime=$Start} -ErrorAction SilentlyContinue |
            Where-Object {$_.RecordId -gt $After -and ($_.Id -eq 506 -or $_.Id -eq 507 -or $_.Id -eq 524)} | Sort-Object TimeCreated)
        if($events | Where-Object {$_.Id -eq 524}){throw 'M9D power window contains Kernel-Power 524 critical-battery event.'}
        $sleep=$events | Where-Object {$_.Id -eq 506} | Select-Object -First 1
        $resume=$events | Where-Object {$_.Id -eq 507 -and $sleep -and $_.TimeCreated -ge $sleep.TimeCreated -and $_.TimeCreated -le $DisplayOn.AddSeconds(3)} | Select-Object -Last 1
        if($sleep -and $resume){return [pscustomobject]@{Sleep=$sleep;Resume=$resume;Events=$events}}
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Parse-MarkerTimestamp([string]$Text){
    $parts=$Text -split '\|'
    if($parts.Length -lt 2){throw "M9D marker has no timestamp: $Text"}
    [DateTimeOffset]::Parse($parts[1],[Globalization.CultureInfo]::InvariantCulture)
}

function Test-FailsafeTakeover {
    if(-not (Test-Path -LiteralPath $failsafeLog)){return $false}
    $t=Get-Content -LiteralPath $failsafeLog -Raw
    return $t -match 'M9D FAILSAFE (TAKEOVER|CONTROLLER-KILL|SERVICE-START|SERVICE-RESTART|RECOVERED):'
}

Assert-Administrator
$repoEvidence=Assert-RepositoryProvenance
$targetEvidence=Assert-ExactTarget
$baselineService=Get-ServiceSnapshot
Assert-ServiceBaseline $baselineService
if(Test-Path -LiteralPath $journalPath){throw 'M9D refuses retained durable journal evidence.'}
$baselineFf=Wait-StableFF 'M9D baseline'

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9D refused while '$name' is running."}
}

Write-Host 'M9D: build + M5-M9 regressions before active boundary...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
foreach($script in @(
    'test-watchdog-m5d-write-armed-invariants.ps1',
    'test-watchdog-m5e-write-armed-double-death-invariants.ps1',
    'test-8c40-modern-standby-m6-invariants.ps1',
    'test-8c40-load-thermal-m8-invariants.ps1',
    'test-8c40-m9-production-watchdog-invariants.ps1',
    'test-8c40-m9c-production-smoke-invariants.ps1',
    'test-8c40-m9d-production-lifecycle-invariants.ps1',
    'test-8c40-m9d-harness-invariants.ps1'
)){
    & (Join-Path $PSScriptRoot $script)
    if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
}

$pass=$false;$failure=$null;$app=$null;$failsafe=$null;$servicePid=0;$serviceTicks=0L;$package=$null
try {
    New-Item -ItemType Directory -Path $markerRoot -Force | Out-Null

    # The parent deliberately leaves M4 Manual/Stopped here. M9D must prove
    # that the actual elevated GUI production route starts and validates the
    # already-installed service itself.
    $failsafe=Start-Process powershell.exe -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',$failsafeScript,
        '-DelaySeconds',[string]$FailsafeDelaySeconds,'-LogPath',$failsafeLog
    ) -WindowStyle Hidden -PassThru

    Start-Sleep -Milliseconds 300
    $failsafe.Refresh()
    if($failsafe.HasExited){throw 'M9D independent failsafe exited before GUI launch.'}

    Write-Host 'ACTIVE M9D LIFECYCLE BOUNDARY' -ForegroundColor Yellow
    Write-Host 'This will issue real equal-only 30/30, release before Modern Standby, then perform one controlled 30/30 re-entry after validated resume.' -ForegroundColor Yellow
    $confirm=Read-Host "Type exactly $token to continue"
    if($confirm -cne $token){throw 'M9D cancelled before GUI/fan write.'}

    $app=Start-M9DTrackedChild -Executable $appExe -WorkingDirectory $repoRoot -Arguments @(
        '--8c40-m9d-production-lifecycle-test',
        '--8c40-m9d-test-token',$token,
        '--8c40-m9d-marker-root',$markerRoot,
        '--modules-dir',$modulesDir
    )

    $serviceDeadline=(Get-Date).AddSeconds(20)
    while($servicePid -le 0 -and (Get-Date)-lt $serviceDeadline){
        if($app.HasExited){
            $app.WaitForExit();$app.Refresh()
            throw "M9D GUI exited before bootstrapping watchdog service. ExitCode=$($app.ExitCode)"
        }

        $svc=Get-ServiceSnapshot
        if($svc.State -ceq 'Running' -and [int]$svc.ProcessId -gt 0){
            $servicePid=[int]$svc.ProcessId
        } else {
            Start-Sleep -Milliseconds 100
        }
    }

    if($servicePid -le 0){
        throw 'M9D GUI did not bootstrap the existing watchdog service to Running.'
    }

    $serviceTicks=Get-ServiceStartTicks $servicePid
    [void](Wait-ServiceReady $servicePid $serviceTicks)

    if(-not (Wait-File $readyPath 45 $app)){
        if($app.HasExited){$app.WaitForExit();$app.Refresh();throw "M9D GUI exited before READY. ExitCode=$($app.ExitCode)"}
        throw 'M9D timed out waiting for READY marker.'
    }

    $guiTicks=[long]$app.StartTime.ToUniversalTime().Ticks
    $ready=Get-Content -LiteralPath $readyPath -Raw
    if($ready -notmatch '^READY\|' -or
       $ready -notmatch 'authority=Custom' -or
       $ready -notmatch 'cpu=30\|gpu=30' -or
       $ready -notmatch 'journal=Owned30' -or
       $ready -notmatch 'transitionMode=m9d-production-modern-standby' -or
       $ready -notmatch 'constructionRoute=production-factory-public-backend' -or
       $ready -notmatch 'serviceBootstrap=gui-ensure-ready' -or
       $ready -notmatch 'bootstrapStarted=True' -or
       $ready -notmatch ("bootstrapWatchdogPid={0}" -f $servicePid) -or
       $ready -notmatch ("bootstrapWatchdogStartTicks={0}" -f $serviceTicks) -or
       $ready -notmatch ("watchdogPid={0}" -f $servicePid) -or
       $ready -notmatch ("watchdogStartTicks={0}" -f $serviceTicks) -or
       $ready -notmatch ("guiPid={0}" -f $app.Id) -or
       $ready -notmatch ("guiStartTicks={0}" -f $guiTicks)){
        throw 'M9D READY marker does not prove exact production route/ownership/process identities.'
    }
    $journal=Assert-OwnedJournal $app.Id $guiTicks
    [void](Wait-ServiceReady $servicePid $serviceTicks)

    $eventStart=(Get-Date).AddSeconds(-2)
    $eventBoundary=Get-KernelBoundary

    Write-Host 'Perform the real Modern Standby transition now: Start -> Power -> Sleep.' -ForegroundColor Yellow
    Write-Host "Keep the notebook asleep about $RecommendedSleepSeconds seconds, then wake normally." -ForegroundColor Yellow
    [void](Read-Host 'Press Enter, then immediately choose Sleep from Windows')

    if(-not (Wait-File $resultPath $ResultTimeoutSeconds $app)){
        if($app.HasExited){$app.WaitForExit();$app.Refresh();throw "M9D GUI exited without result. ExitCode=$($app.ExitCode)"}
        throw 'M9D timed out waiting for lifecycle result after resume.'
    }

    foreach($path in @($preSleepPath,$resumePath,$reentryPath,$resultPath)){
        if(-not (Test-Path -LiteralPath $path)){throw "M9D required marker missing: $path"}
    }

    $pre=Get-Content -LiteralPath $preSleepPath -Raw
    $resume=Get-Content -LiteralPath $resumePath -Raw
    $reentry=Get-Content -LiteralPath $reentryPath -Raw
    $result=Get-Content -LiteralPath $resultPath -Raw

    if($pre -notmatch '^PASS\|' -or
       $pre -notmatch 'source=GUID_SESSION_DISPLAY_STATUS/Off' -or
       $pre -notmatch 'restoreTrigger=registered-WM_POWERBROADCAST/PBT_APMSUSPEND' -or
       $pre -notmatch 'authority=Firmware' -or
       $pre -notmatch 'watchdogRelease=True' -or
       $pre -notmatch 'journal=absent' -or
       $pre -notmatch 'ec=255/255' -or
       $pre -notmatch ("watchdogPid={0}" -f $servicePid) -or
       $pre -notmatch ("watchdogStartTicks={0}" -f $serviceTicks)){
        throw 'M9D pre-sleep marker does not prove proactive firmware/watchdog handoff.'
    }

    if($resume -notmatch '^GATED\|' -or
       $resume -notmatch 'source=GUID_SESSION_DISPLAY_STATUS/On' -or
       $resume -notmatch 'acceptedUserResumes=1' -or
       $resume -notmatch 'authority=Firmware' -or
       $resume -notmatch 'journal=absent'){
        throw 'M9D resume gate marker is incomplete.'
    }

    if($reentry -notmatch '^REENTRY\|' -or
       $reentry -notmatch 'authority=Custom' -or
       $reentry -notmatch 'cpu=30\|gpu=30' -or
       $reentry -notmatch ("guiPid={0}" -f $app.Id) -or
       $reentry -notmatch ("guiStartTicks={0}" -f $guiTicks)){
        throw 'M9D re-entry marker is incomplete.'
    }

    if($result -notmatch '^PASS\|' -or
       $result -notmatch 'transitionMode=m9d-production-modern-standby' -or
       $result -notmatch 'constructionRoute=production-factory-public-backend' -or
       $result -notmatch 'primaryDisplayOff=True' -or
       $result -notmatch 'pbtSuspend=True' -or
       $result -notmatch 'displayOn=True' -or
       $result -notmatch 'acceptedUserResumes=1'){
        throw 'M9D final GUI result does not prove the intended production lifecycle path.'
    }

    $displayOn=(Parse-MarkerTimestamp $resume).LocalDateTime
    $power=Wait-ModernStandbyEvidence $eventStart $displayOn $eventBoundary
    if($null -eq $power){throw 'M9D app passed but Kernel-Power 506 -> 507 Modern Standby evidence is missing.'}
    $standbySeconds=($power.Resume.TimeCreated-$power.Sleep.TimeCreated).TotalSeconds
    if($standbySeconds -lt 15){throw "M9D Modern Standby window too short: $standbySeconds s."}

    $exitCode=Wait-M9DTrackedChildExitCode $app 30
    if($exitCode -ne 0){throw "M9D GUI result marker passed but process ExitCode=$exitCode."}

    if(Test-Path -LiteralPath $journalPath){throw 'M9D final GUI PASS left durable journal present.'}
    $finalFf=Wait-StableFF 'M9D final'
    if(Test-FailsafeTakeover){throw 'M9D independent failsafe intervened; normal lifecycle path is not proven.'}

    Stop-Service -Name $serviceName -Force
    $stopDeadline=(Get-Date).AddSeconds(15)
    do {$svcFinal=Get-ServiceSnapshot;if($svcFinal.State -ceq 'Stopped'){break};Start-Sleep -Milliseconds 100} while((Get-Date)-lt $stopDeadline)
    Assert-ServiceBaseline $svcFinal
    if(Test-Path -LiteralPath $journalPath){throw 'M9D service stop left retained journal evidence.'}

    $pass=$true
}
catch {
    $failure=$_.Exception.Message
}
finally {
    if(-not $pass -and $app -and -not $app.HasExited){
        # Exact controller kill is a failure recovery action; retained journal is
        # preserved for the already-qualified watchdog to recover.
        try {
            $controllerPid=$app.Id;$ticks=[long]$app.StartTime.ToUniversalTime().Ticks
            $current=[Diagnostics.Process]::GetProcessById($controllerPid)
            try {
                if([long]$current.StartTime.ToUniversalTime().Ticks -eq $ticks){$current.Kill();[void]$current.WaitForExit(5000)}
            } finally {$current.Dispose()}
        } catch {}
    }

    if(Test-Path -LiteralPath $journalPath){
        Copy-Item -LiteralPath $journalPath -Destination (Join-Path $evidenceRoot 'm9d-retained-lease-before-recovery.json') -ErrorAction SilentlyContinue
        $svc=Get-ServiceSnapshot
        if($svc.Installed -and $svc.State -ne 'Running'){Start-Service -Name $serviceName -ErrorAction SilentlyContinue}
        $deadline=(Get-Date).AddSeconds(30)
        while((Test-Path -LiteralPath $journalPath) -and (Get-Date)-lt $deadline){Start-Sleep -Milliseconds 200}
    }

    $firmwareSafe=$false
    if(-not (Test-Path -LiteralPath $journalPath)){
        try {$cleanupFf=Wait-StableFF 'M9D cleanup';$firmwareSafe=$true}catch{}
    }

    if($firmwareSafe -and $failsafe){
        $failsafe.Refresh()
        if(-not $failsafe.HasExited){Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue;try{[void]$failsafe.WaitForExit(3000)}catch{}}
    }

    if($firmwareSafe){
        try {
            $svc=Get-ServiceSnapshot
            if($svc.Installed -and $svc.State -ne 'Stopped'){Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue}
        } catch {}
    }

    if(Test-Path -LiteralPath $evidenceRoot){
        [ordered]@{
            schemaVersion=1;gate='M9D-HARNESS';result=$(if($pass){'PASS'}else{'FAIL_CLOSED'});failure=$failure;
            repository=$repoEvidence;target=$targetEvidence;watchdogPid=$servicePid;watchdogStartUtcTicks=$serviceTicks;
            guiPid=$(if($app){$app.Id}else{0});failsafePid=$(if($failsafe){$failsafe.Id}else{0});
            failsafeTakeover=(Test-FailsafeTakeover);journalPresent=(Test-Path -LiteralPath $journalPath);
            finalService=Get-ServiceSnapshot;watchdogRecoveryValidated=$false;productionConstructionAuthorized=$false;
            automaticPolicyEnabled=$false;controlEnabledByDefault=$false
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

        try {$package=& $packager -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot -WatchdogLogPath $serviceLog -WatchdogStatusPath $statusPath -WatchdogJournalPath $journalPath -AppLogPath $appLog}
        catch {if($pass){$pass=$false;$failure="M9D evidence packaging failed: $($_.Exception.Message)"}}
    }
}

if(-not $pass){throw "M9D FAILED_CLOSED: $failure"}
Write-Host 'PASS: HP 8C40 M9D production-path Modern Standby lifecycle qualification completed.' -ForegroundColor Green
Write-Host ("Evidence ZIP: {0}" -f $package.ZipPath) -ForegroundColor Green
Write-Host ("ZIP SHA256 : {0}" -f $package.ZipSha256) -ForegroundColor Green
