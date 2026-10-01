param(
    [ValidateRange(30,300)]
    [int]$RecommendedSleepSeconds=60,

    [ValidateRange(180,600)]
    [int]$FailsafeDelaySeconds=300,

    [ValidateRange(120,900)]
    [int]$ResultTimeoutSeconds=300
)

$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$expectedBranch='feature/victus-8c40-m9d-production-lifecycle'
$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER. Nothing below may create evidence,
# query hardware, start the service or launch the App before this passes.
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized){
    throw 'M9D PHYSICAL BLOCKED: requires recorded M9B read-only PASS, recorded M9C physical PASS and an explicit same-HEAD M9D authorization commit.'
}

$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$appExe=Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9d-production-lifecycle_{0}" -f $stamp)
$markerRoot=Join-Path $evidenceRoot 'app-markers'
$summaryPath=Join-Path $evidenceRoot 'm9d-harness-summary.json'
$failsafeLog=Join-Path $evidenceRoot 'm9d-failsafe.log'
$appLogSnapshot=Join-Path $evidenceRoot 'm9d-app-events-tail.log'
$watchdogLogSnapshot=Join-Path $evidenceRoot 'm9d-watchdog-tail.log'

$readyPath=Join-Path $markerRoot 'ready.marker'
$preSleepPath=Join-Path $markerRoot 'presleep.marker'
$resumeGatePath=Join-Path $markerRoot 'resume-gate.marker'
$reentryPath=Join-Path $markerRoot 'reentry.marker'
$resultPath=Join-Path $markerRoot 'result.marker'

$token='8C40-M9D-LIFECYCLE30'
$appLog=Join-Path $env:LOCALAPPDATA ("VictusFanControl\logs\events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

. (Join-Path $PSScriptRoot 'm9d-tracked-child.ps1')

$app=$null
$failsafe=$null
$serviceStartedByHarness=$false
$passed=$false
$failure=$null
$repoEvidence=$null
$targetEvidence=$null
$serviceBefore=$null
$serviceRunning=$null
$serviceFinal=$null
$watchdogPid=0
$watchdogStartTicks=0L
$appPid=0
$appStartTicks=0L
$readyText=$null
$preSleepText=$null
$resumeGateText=$null
$reentryText=$null
$resultText=$null
$kernelEvidence=$null
$finalFirmwareProof=$null
$failsafeTakeover=$false

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M9D physical lifecycle gate must run from elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    $head=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){throw 'M9D could not resolve git HEAD.'}
    if($branch -cne $expectedBranch){throw "M9D requires branch '$expectedBranch'; observed '$branch'."}

    $upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)){throw 'M9D requires a tracked upstream.'}
    $upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($head -cne $upstreamHead){throw "M9D requires local HEAD=upstream. local=$head upstream=$upstreamHead"}

    $statusText=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'M9D could not inspect git status.'}
    $blocking=@(
        $statusText -split "[\r\n]+" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
        }
    )
    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'M9D requires committed source/config; only untracked logs/ evidence is allowed.'
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
        throw 'M9D exact-target fingerprint mismatch.'
    }

    [pscustomobject]@{Board='HP 8C40';Version='63.43';System=[string]$system.Model;Sku=$sku;Bios=$biosText}
}

function Get-ProcessStartTicks([int]$ProcessId){
    $p=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try{return [long]$p.StartTime.ToUniversalTime().Ticks}finally{$p.Dispose()}
}

function Get-M4ServiceSnapshot {
    $svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if(-not $svc){return [pscustomobject]@{Installed=$false;State='Absent';StartMode='Absent';StartName='';ProcessId=0;PathName=''}}
    [pscustomobject]@{
        Installed=$true;State=[string]$svc.State;StartMode=[string]$svc.StartMode;
        StartName=[string]$svc.StartName;ProcessId=[int]$svc.ProcessId;PathName=[string]$svc.PathName
    }
}

function Assert-ServiceBaseline([object]$svc){
    if(-not $svc.Installed){throw 'M9D requires previously qualified VictusFanControlWatchdogM4 installed.'}
    if($svc.State -cne 'Stopped' -or $svc.StartMode -cne 'Manual' -or $svc.ProcessId -ne 0){
        throw "M9D requires M4 Manual/Stopped/PID0; observed $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)."
    }
    if($svc.StartName -notmatch '(^|\\)LocalSystem$' -and $svc.StartName -cne 'LocalSystem'){
        throw "M9D requires LocalSystem; observed '$($svc.StartName)'."
    }
    foreach($required in @($serviceExe,'--service-name VictusFanControlWatchdogM4','--m4-8c40-lease-service')){
        if($svc.PathName.IndexOf($required,[StringComparison]::OrdinalIgnoreCase)-lt 0){
            throw "M9D service configuration mismatch: missing '$required'."
        }
    }
    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "M9D service executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "M9D service PawnIO module missing: $serviceModule"}
}

function Wait-ServiceReady([int]$ExpectedPid,[long]$ExpectedStartTicks,[int]$Seconds=20){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date)-lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try{
                $status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                $svc=Get-M4ServiceSnapshot
                if($status.Ready -and -not $status.Blocked -and
                   [int]$status.ProcessId -eq $ExpectedPid -and
                   $svc.State -ceq 'Running' -and [int]$svc.ProcessId -eq $ExpectedPid -and
                   [int]$status.SessionId -eq 0 -and $status.AccountName -match 'SYSTEM$' -and
                   $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   (Get-ProcessStartTicks $ExpectedPid) -eq $ExpectedStartTicks){
                    return $status
                }
            }catch{}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9D timed out waiting for exact watchdog Ready state.'
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9D read-only setpoint probe failed: $output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $match=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M9D could not parse setpoint: $line"}
    [pscustomobject]@{TimestampUtc=(Get-Date).ToUniversalTime().ToString('O');Cpu=[int]$match.Groups[1].Value;Gpu=[int]$match.Groups[2].Value;Raw=$line}
}

function Read-StableFirmwareOwned([int]$MaxReads=6){
    $samples=@();$consecutive=0
    for($i=1;$i-le $MaxReads;$i++){
        $x=Read-8C40Setpoint;$samples+=@($x)
        if($x.Cpu-eq 255 -and $x.Gpu-eq 255){$consecutive++}else{$consecutive=0}
        if($consecutive-ge 2){return [pscustomobject]@{Passed=$true;Samples=$samples}}
        if($i-lt $MaxReads){Start-Sleep -Milliseconds 75}
    }
    return [pscustomobject]@{Passed=$false;Samples=$samples}
}

function Wait-File([string]$Path,[int]$Seconds,[System.Diagnostics.Process]$Process=$null){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date)-lt $deadline){
        if(Test-Path -LiteralPath $Path){return $true}
        if($Process -and $Process.HasExited){return $false}
        Start-Sleep -Milliseconds 100
    }
    return (Test-Path -LiteralPath $Path)
}

function Read-OwnedJournal([int]$ControllerPid,[long]$ControllerStartTicks){
    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9D READY exists but durable journal is missing.'}
    $j=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    $ownedPhase=([string]$j.Phase -ceq 'Owned') -or ([string]$j.Phase -ceq '2') -or ([int]$j.Phase -eq 2)
    if([int]$j.SchemaVersion-ne 2 -or $j.TargetProfileId-cne 'HP-8C40-9D0R1LA-F18' -or
       -not $ownedPhase -or [long]$j.Generation-ne 3 -or
       [int]$j.Controller.ProcessId-ne $ControllerPid -or
       [long]$j.Controller.ProcessStartUtcTicks-ne $ControllerStartTicks -or
       [int]$j.Owned.Cpu-ne 30 -or [int]$j.Owned.Gpu-ne 30){
        throw 'M9D journal is not exact schema-v2 generation-3 OWNED 30/30 bound to the exact GUI PID+creation ticks.'
    }
    return $j
}

function Start-M9DFailsafe {
    $exe=(Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
    $args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'watchdog-m9d-service-failsafe-8c40.ps1'),'-DelaySeconds',[string]$FailsafeDelaySeconds,'-LogPath',$failsafeLog)
    $p=Start-M9DTrackedChild -Executable $exe -Arguments $args -WorkingDirectory $repoRoot

    $deadline=(Get-Date).AddSeconds(10)
    while((Get-Date)-lt $deadline){
        if($p.HasExited){throw 'M9D failsafe exited before publishing ARMED.'}
        if(Test-Path -LiteralPath $failsafeLog){
            $text=Get-Content -LiteralPath $failsafeLog -Raw
            if($text -match 'M9D FAILSAFE ARMED:'){return $p}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9D failsafe did not publish durable ARMED evidence.'
}

function Test-FailsafeTakeover {
    if(-not (Test-Path -LiteralPath $failsafeLog)){return $false}
    $text=Get-Content -LiteralPath $failsafeLog -Raw
    return ($text -match 'M9D FAILSAFE TAKEOVER:' -or
            $text -match 'M9D FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M9D FAILSAFE SERVICE-START:' -or
            $text -match 'M9D FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M9D FAILSAFE STARTED:' -or
            $text -match 'M9D FAILSAFE RECOVERED:')
}

function Get-KernelPowerBoundary {
    $e=Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power'} -MaxEvents 1 -ErrorAction SilentlyContinue
    if($null-eq $e){return 0L}
    return [long]$e.RecordId
}

function Read-ModernStandbyEvidence([long]$AfterRecordId,[datetime]$StartTime){
    $events=@(
        Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power';StartTime=$StartTime} -ErrorAction SilentlyContinue |
        Where-Object {$_.RecordId-gt $AfterRecordId -and ($_.Id-eq 506 -or $_.Id-eq 507 -or $_.Id-eq 524)} |
        Sort-Object TimeCreated
    )
    $bad=$events | Where-Object {$_.Id-eq 524 -or ($_.Id-eq 507 -and $_.Message -match '(?i)hibern')} | Select-Object -First 1
    if($bad){throw "M9D transition disqualified by Kernel-Power event $($bad.Id): $($bad.Message)"}
    $sleep=$events | Where-Object {$_.Id-eq 506} | Select-Object -First 1
    $resume=$events | Where-Object {$_.Id-eq 507 -and $sleep -and $_.TimeCreated-ge $sleep.TimeCreated} | Select-Object -Last 1
    if(-not $sleep -or -not $resume){throw 'M9D could not prove a Kernel-Power Modern Standby 506 -> 507 sequence.'}
    [pscustomobject]@{
        SleepRecordId=[long]$sleep.RecordId;SleepTime=[datetime]$sleep.TimeCreated;
        ResumeRecordId=[long]$resume.RecordId;ResumeTime=[datetime]$resume.TimeCreated
    }
}

function Copy-TailIfPresent([string]$Source,[string]$Destination,[int]$Lines=300){
    if(Test-Path -LiteralPath $Source -PathType Leaf){
        Get-Content -LiteralPath $Source | Select-Object -Last $Lines | Set-Content -LiteralPath $Destination -Encoding UTF8
    }
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M9D PRODUCTION LIFECYCLE PHYSICAL QUALIFICATION' -ForegroundColor Cyan
Write-Host 'One initial 30/30 and one controlled post-resume 30/30 are issued by the existing display-aware lifecycle engine.' -ForegroundColor Yellow
Write-Host 'Sleep is manual only; the harness does not dispatch a power transition.' -ForegroundColor Yellow

try {
    $repoEvidence=Assert-RepositoryProvenance
    $targetEvidence=Assert-ExactTarget

    foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
        if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9D refused while process '$name' is running."}
    }

    if(Test-Path -LiteralPath $evidenceRoot){
        throw "M9D refuses existing evidence directory: $evidenceRoot"
    }
    New-Item -ItemType Directory -Path $markerRoot -Force | Out-Null

    if(Test-Path -LiteralPath $journalPath){
        Copy-Item -LiteralPath $journalPath -Destination (Join-Path $evidenceRoot 'm9d-retained-lease-preflight.json')
        throw 'M9D refuses retained watchdog journal; evidence copied, not deleted.'
    }

    $serviceBefore=Get-M4ServiceSnapshot
    Assert-ServiceBaseline $serviceBefore

    $initialFirmware=Read-StableFirmwareOwned
    if(-not $initialFirmware.Passed){throw 'M9D requires stable two-consecutive FF/FF before service start.'}

    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE-ne 0){throw "M9D build failed with exit code $LASTEXITCODE."}

    foreach($script in @(
        'test-8c40-m9-production-watchdog-invariants.ps1',
        'test-8c40-m9c-production-smoke-invariants.ps1',
        'test-8c40-m9c-harness-invariants.ps1',
        'test-8c40-m9d-production-lifecycle-invariants.ps1',
        'test-8c40-m9d-harness-invariants.ps1'
    )){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE-ne 0){throw "M9D invariant '$script' failed."}
    }

    Start-Service -Name $serviceName
    $serviceStartedByHarness=$true
    $svc=Get-M4ServiceSnapshot
    if($svc.State-cne 'Running' -or $svc.ProcessId-le 0){throw 'M9D M4 service did not reach Running.'}
    $watchdogPid=[int]$svc.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
    $null=Wait-ServiceReady $watchdogPid $watchdogStartTicks

    $failsafe=Start-M9DFailsafe

    $kernelBoundary=Get-KernelPowerBoundary
    $transitionStart=Get-Date

    $app=Start-M9DTrackedChild -Executable $appExe -Arguments @(
        '--8c40-m9d-production-lifecycle-test',
        '--8c40-m9d-test-token',$token,
        '--8c40-m9d-marker-root',$markerRoot,
        '--modules-dir',$modulesDir
    ) -WorkingDirectory $repoRoot

    $appPid=$app.Id
    $appStartTicks=Get-ProcessStartTicks $appPid

    if(-not (Wait-File $readyPath 90 $app)){throw 'M9D App exited/timed out before READY.'}
    $readyText=Get-Content -LiteralPath $readyPath -Raw

    if($readyText -notmatch '^READY\|' -or
       $readyText -notmatch 'transitionMode=m9d-production-modern-standby' -or
       $readyText -notmatch ("guiPid={0}" -f $appPid) -or
       $readyText -notmatch ("guiStartTicks={0}" -f $appStartTicks)){
        throw "M9D READY marker identity/path mismatch: $readyText"
    }

    $null=Read-OwnedJournal $appPid $appStartTicks
    $null=Wait-ServiceReady $watchdogPid $watchdogStartTicks

    Write-Host ''
    Write-Host 'M9D READY: production factory/public backend is OWNED 30/30 with exact PID+creation-ticks binding.' -ForegroundColor Green
    Write-Host ("Recommended sleep duration: about {0} seconds." -f $RecommendedSleepSeconds)
    $confirm=Read-Host 'Type exactly M9D-SLEEP, then use Windows Start -> Power -> Sleep. Wake normally afterward'
    if($confirm -cne 'M9D-SLEEP'){throw 'M9D cancelled before Modern Standby transition.'}

    if(-not (Wait-File $preSleepPath 90 $app)){throw 'M9D did not publish pre-sleep release evidence.'}
    $preSleepText=Get-Content -LiteralPath $preSleepPath -Raw
    if($preSleepText -notmatch '^PASS\|' -or
       $preSleepText -notmatch 'primaryDisplaySignal=True' -or
       $preSleepText -notmatch 'localFirmwareAck=True' -or
       $preSleepText -notmatch 'watchdogRelease=True' -or
       $preSleepText -notmatch 'journal=absent'){
        throw "M9D pre-sleep marker failed contract: $preSleepText"
    }

    if(-not (Wait-File $resumeGatePath $ResultTimeoutSeconds $app)){throw 'M9D did not publish display-On resume gate evidence.'}
    $resumeGateText=Get-Content -LiteralPath $resumeGatePath -Raw
    if($resumeGateText -notmatch '^GATED\|' -or
       $resumeGateText -notmatch 'acceptedUserResumes=1' -or
       $resumeGateText -notmatch 'journal=absent'){
        throw "M9D resume gate marker failed contract: $resumeGateText"
    }

    if(-not (Wait-File $reentryPath 60 $app)){throw 'M9D did not publish controlled re-entry evidence.'}
    $reentryText=Get-Content -LiteralPath $reentryPath -Raw
    if($reentryText -notmatch '^REENTRY\|' -or
       $reentryText -notmatch 'cpu=30\|gpu=30' -or
       $reentryText -notmatch ("guiPid={0}" -f $appPid) -or
       $reentryText -notmatch ("guiStartTicks={0}" -f $appStartTicks)){
        throw "M9D re-entry marker failed contract: $reentryText"
    }

    if(-not (Wait-File $resultPath 60 $app)){throw 'M9D did not publish terminal result evidence.'}
    $resultText=Get-Content -LiteralPath $resultPath -Raw

    $exitCode=Wait-M9DTrackedChildExitCode -Process $app -Seconds 30
    if($exitCode-ne 0 -or $resultText -notmatch '^PASS\|' -or
       $resultText -notmatch 'transitionMode=m9d-production-modern-standby' -or
       $resultText -notmatch 'primaryDisplayOff=True' -or
       $resultText -notmatch 'pbtSuspend=True' -or
       $resultText -notmatch 'displayOn=True' -or
       $resultText -notmatch 'acceptedUserResumes=1'){
        throw "M9D App terminal result failed. Exit=$exitCode Result=$resultText"
    }

    $null=Wait-ServiceReady $watchdogPid $watchdogStartTicks
    if(Test-Path -LiteralPath $journalPath){
        Copy-Item -LiteralPath $journalPath -Destination (Join-Path $evidenceRoot 'm9d-retained-lease-before-recovery.json')
        throw 'M9D normal completion retained a durable lease; evidence copied and run fails closed.'
    }

    $kernelEvidence=Read-ModernStandbyEvidence $kernelBoundary $transitionStart
    $finalFirmwareProof=Read-StableFirmwareOwned
    if(-not $finalFirmwareProof.Passed){throw 'M9D final firmware ownership is not stable two-consecutive FF/FF.'}

    $failsafeTakeover=Test-FailsafeTakeover
    if($failsafeTakeover){throw 'M9D independent delayed failsafe took control; normal-path PASS is invalid.'}

    $passed=$true
}
catch {
    $failure=$_.Exception.Message
    Write-Host ("M9D FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally {
    Copy-TailIfPresent $appLog $appLogSnapshot
    Copy-TailIfPresent $serviceLog $watchdogLogSnapshot

    if($app -and -not $app.HasExited){
        try {$app.Kill();$app.WaitForExit(5000)}catch{}
    }

    if(Test-Path -LiteralPath $journalPath){
        $dest=Join-Path $evidenceRoot 'm9d-retained-lease-final.json'
        if(-not (Test-Path -LiteralPath $dest)){Copy-Item -LiteralPath $journalPath -Destination $dest}
    }

    if($failsafe -and -not $failsafe.HasExited){
        try {$failsafe.Kill();$failsafe.WaitForExit(5000)}catch{}
    }

    # Restore the original Manual/Stopped baseline only when no durable lease
    # remains. Never stop/reinstall/delete around retained ownership evidence.
    if($serviceStartedByHarness -and -not (Test-Path -LiteralPath $journalPath)){
        try {
            $svc=Get-M4ServiceSnapshot
            if($svc.State-cne 'Stopped'){
                Stop-Service -Name $serviceName -Force
                (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
            }
        } catch {
            if($null-eq $failure){$failure="M9D service baseline restore failed: $($_.Exception.Message)"}
            $passed=$false
        }
    }

    $serviceFinal=Get-M4ServiceSnapshot
    if($passed){
        try {Assert-ServiceBaseline $serviceFinal}
        catch {$failure=$_.Exception.Message;$passed=$false}
    }

    if(-not (Test-Path -LiteralPath $evidenceRoot)){New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null}

    [ordered]@{
        schemaVersion=1;gate='M9D-HARNESS';result=$(if($passed){'PASS'}else{'FAIL_CLOSED'});failure=$failure;
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O');repository=$repoEvidence;target=$targetEvidence;
        watchdogPid=$watchdogPid;watchdogStartUtcTicks=$watchdogStartTicks;appPid=$appPid;appStartUtcTicks=$appStartTicks;
        readyMarker=$readyText;preSleepMarker=$preSleepText;resumeGateMarker=$resumeGateText;reentryMarker=$reentryText;resultMarker=$resultText;
        kernelPower=$kernelEvidence;finalFirmwareProof=$finalFirmwareProof;failsafeTakeover=$failsafeTakeover;
        journalPresentFinal=(Test-Path -LiteralPath $journalPath);serviceBefore=$serviceBefore;serviceFinal=$serviceFinal;
        watchdogRecoveryValidated=$false;productionConstructionAuthorized=$false;automaticPolicyEnabled=$false;controlEnabledByDefault=$false;
        evidenceDeleted=$false
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    try {
        $package=& (Join-Path $PSScriptRoot 'package-m9d-evidence.ps1') -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot
        Write-Host ("M9D evidence ZIP: {0}" -f $package.ZipPath)
        Write-Host ("M9D ZIP SHA256: {0}" -f $package.ZipSha256)
    } catch {
        Write-Warning ("M9D evidence packaging failed: {0}" -f $_.Exception.Message)
        if($passed){$passed=$false;$failure="Evidence packaging failed: $($_.Exception.Message)"}
    }

    if($app){$app.Dispose()}
    if($failsafe){$failsafe.Dispose()}
}

if(-not $passed){throw "M9D PHYSICAL FAIL_CLOSED: $failure"}

Write-Host ''
Write-Host 'PASS: HP 8C40 M9D production lifecycle qualification completed.' -ForegroundColor Green
Write-Host 'Normal production factory/public backend route survived display-aware Modern Standby lifecycle; final Firmware ownership and service baseline verified.' -ForegroundColor Green
