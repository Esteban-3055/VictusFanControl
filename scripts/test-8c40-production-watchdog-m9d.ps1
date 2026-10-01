param(
    [ValidateRange(90,300)]
    [int]$FailsafeDelaySeconds=120,

    [ValidateRange(30,120)]
    [int]$ControllerDeathRecoveryTimeoutSeconds=30,

    [ValidateRange(120,600)]
    [int]$LifecycleResultTimeoutSeconds=360
)

$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json

# HARD VERSIONED AUTHORIZATION BARRIER. This must remain before Administrator,
# CIM, service mutation, process launch, PawnIO/EC probing or evidence creation.
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed){
    throw 'M9D PHYSICAL BLOCKED: M9B read-only physical PASS is not recorded.'
}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed){
    throw 'M9D PHYSICAL BLOCKED: M9C physical PASS is not recorded.'
}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized -or
   -not [bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized){
    throw 'M9D PHYSICAL BLOCKED: explicit physical/controller/construction authorization is incomplete.'
}
if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
   [bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized -or
   [bool]$profile.control.enabledByDefault -or
   [bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled){
    throw 'M9D refuses if production watchdog/default/automatic policy has already been promoted.'
}

$expectedBranch='feature/victus-8c40-m9-final-prehardware'
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$stateDir=Join-Path $serviceRoot 'state'
$statusPath=Join-Path $stateDir 'm4-8c40.status.json'
$journalPath=Join-Path $stateDir 'lease.json'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'

$appExe=Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$failsafeScript=Join-Path $PSScriptRoot 'watchdog-m9d-service-failsafe-8c40.ps1'
$packagingScript=Join-Path $PSScriptRoot 'package-m9d-evidence.ps1'
$token='8C40-M9D-PRODUCTION-LIFECYCLE30'

$appMarkerRoot=Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyMarker=Join-Path $appMarkerRoot 'm9d-production-lifecycle.ready'
$preSleepMarker=Join-Path $appMarkerRoot 'm9d-production-lifecycle.presleep'
$resumeMarker=Join-Path $appMarkerRoot 'm9d-production-lifecycle.resume-gate'
$reentryMarker=Join-Path $appMarkerRoot 'm9d-production-lifecycle.reentry'
$resultMarker=Join-Path $appMarkerRoot 'm9d-production-lifecycle.result'

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9d-production-lifecycle_{0}" -f $stamp)
$summaryPath=Join-Path $evidenceRoot 'm9d-harness-summary.json'
$failsafeD1Log=Join-Path $evidenceRoot 'm9d-d1-failsafe.log'
$failsafeD2Log=Join-Path $evidenceRoot 'm9d-d2-failsafe.log'
$baselineFfPath=Join-Path $evidenceRoot 'm9d-baseline-ff.json'
$d1FinalFfPath=Join-Path $evidenceRoot 'm9d-d1-final-ff.json'
$d2BaselineFfPath=Join-Path $evidenceRoot 'm9d-d2-baseline-ff.json'
$d2FinalFfPath=Join-Path $evidenceRoot 'm9d-d2-final-ff.json'
$cleanupFfPath=Join-Path $evidenceRoot 'm9d-cleanup-ff.json'
$d1LogPath=Join-Path $evidenceRoot 'm9d-d1-watchdog-log-segment.txt'
$d2LogPath=Join-Path $evidenceRoot 'm9d-d2-watchdog-log-segment.txt'
$retainedLeasePath=Join-Path $evidenceRoot 'm9d-retained-lease-before-recovery.json'
$kernelEvidencePath=Join-Path $evidenceRoot 'm9d-d2-kernel-power.json'

$app=$null
$failsafe=$null
$pass=$false
$failure=$null
$head=$null
$watchdogPid=0
$watchdogStartTicks=0L
$serviceStarted=$false
$d1Pass=$false
$d2Pass=$false
$d1GuiPid=0
$d1GuiStartTicks=0L
$d2GuiPid=0
$d2GuiStartTicks=0L
$d1RecoverySeconds=$null
$d1FailsafeTakeover=$false
$d2FailsafeTakeover=$false
$d1LogBoundary=0
$d2LogBoundary=0
$finalJournalAbsent=$false
$finalFirmwareProofPass=$false
$finalServiceBaselinePass=$false
$packagePath=$null
$packageSha256=$null

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M9D must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryProvenance {
    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch){
        throw "M9D requires branch '$expectedBranch'; observed '$branch'."
    }

    $localHead=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $localHead -notmatch '^[0-9a-f]{40}$'){
        throw "M9D could not resolve a valid HEAD. Raw='$localHead'"
    }

    $upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)){
        throw 'M9D requires a configured tracked upstream.'
    }

    $upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $localHead -cne $upstreamHead){
        throw "M9D requires local HEAD == upstream HEAD. local=$localHead upstream=$upstreamHead"
    }

    $authorizationSourceHead=[string]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.sourceHead
    $parentHead=(& git rev-parse HEAD^ 2>&1 | Out-String).Trim()
    if($authorizationSourceHead -notmatch '^[0-9a-f]{40}(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'M9D could not inspect repository status.'}

    $blocking=@(
        $statusText -split "[\r\n]+" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
        }
    )

    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'M9D requires committed source/config state; only untracked logs/ evidence is allowed.'
    }

    return $localHead
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop
    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    if(([string]$board.Manufacturer).Trim() -cne 'HP' -or
       ([string]$board.Product).Trim() -cne '8C40' -or
       ([string]$board.Version).Trim() -cne '63.43' -or
       ([string]$system.Manufacturer).Trim() -cne 'HP' -or
       ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M9D exact-target fingerprint mismatch.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ProcessStartTicks([int]$ProcessId){
    $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try{return [long]$process.StartTime.ToUniversalTime().Ticks}
    finally{$process.Dispose()}
}

function Assert-ServiceBaseline {
    $svc=Get-ServiceState
    if(-not $svc){throw 'M9D requires the already-qualified M4 service to be installed; harness will not install it.'}
    if([string]$svc.State -cne 'Stopped' -or
       [string]$svc.StartMode -cne 'Manual' -or
       [int]$svc.ProcessId -ne 0){
        throw "M9D requires M4 Manual/Stopped/PID0; observed $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)."
    }
    if([string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw "M9D requires LocalSystem; observed '$($svc.StartName)'."
    }
    foreach($required in @(
        $serviceExe,
        '--service-name VictusFanControlWatchdogM4',
        '--m4-8c40-lease-service'
    )){
        if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){
            throw "M9D M4 service configuration mismatch; missing '$required' in '$($svc.PathName)'."
        }
    }
    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "M9D service executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "M9D service module missing: $serviceModule"}
}

function Get-PowerSnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $status=[System.Windows.Forms.SystemInformation]::PowerStatus
    $percent=$null
    if($status.BatteryLifePercent -ge 0){$percent=[math]::Round([double]$status.BatteryLifePercent*100,0)}
    [pscustomobject]@{PowerLineStatus=[string]$status.PowerLineStatus;BatteryPercent=$percent}
}

function Assert-PowerSane {
    $power=Get-PowerSnapshot
    if($power.PowerLineStatus -cne 'Online'){throw "M9D requires AC online; observed '$($power.PowerLineStatus)'."}
    if($null -eq $power.BatteryPercent -or [double]$power.BatteryPercent -lt 20){
        throw "M9D requires readable battery >=20%; observed '$($power.BatteryPercent)'."
    }
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9D setpoint probe failed. Raw: $output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $match=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M9D could not parse setpoint probe. Raw: $output"}
    [pscustomobject]@{
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        cpu=[int]$match.Groups[1].Value
        gpu=[int]$match.Groups[2].Value
        raw=[string]$line
    }
}

function Assert-StableFirmwareOwned([string]$Context,[string]$EvidencePath){
    $samples=@()
    $consecutive=0
    for($read=1;$read -le 6;$read++){
        $sample=Read-8C40Setpoint
        $samples+=@($sample)
        Write-Host ("{0} FF proof {1}/6: {2}" -f $Context,$read,$sample.raw)

        if($sample.cpu -eq 255 -and $sample.gpu -eq 255){
            $consecutive++
            if($consecutive -ge 2){
                [ordered]@{context=$Context;passed=$true;requiredConsecutive=2;maximumReads=6;samples=$samples} |
                    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
                return
            }
        } else {$consecutive=0}

        if($read -lt 6){Start-Sleep -Milliseconds 75}
    }

    [ordered]@{context=$Context;passed=$false;requiredConsecutive=2;maximumReads=6;samples=$samples} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
    throw "$Context requires two consecutive independent FF/FF observations within six reads."
}

function Wait-M4Ready([int]$ExpectedPid,[long]$ExpectedStartTicks){
    $deadline=(Get-Date).AddSeconds(20)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try {
                $status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if([int]$status.ProcessId -eq $ExpectedPid -and
                   $status.Ready -and -not $status.Blocked -and
                   [int]$status.SessionId -eq 0 -and
                   $status.AccountName -match 'SYSTEM$' -and
                   $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   (Get-ProcessStartTicks $ExpectedPid) -eq $ExpectedStartTicks){
                    return $status
                }
            } catch {}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9D timed out waiting for exact M4 Ready state.'
}

function Test-OwnedPhase($Phase){
    if($null -eq $Phase){return $false}
    if($Phase -is [string]){return ($Phase -ceq 'Owned' -or $Phase -ceq '2')}
    try{return ([int]$Phase -eq 2)}catch{return $false}
}

function Assert-OwnedJournal($Journal,[int]$ExpectedPid,[long]$ExpectedStartTicks){
    if([int]$Journal.SchemaVersion -ne 2 -or
       $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Test-OwnedPhase $Journal.Phase) -or
       [int]$Journal.Generation -ne 3 -or
       [int]$Journal.Controller.ProcessId -ne $ExpectedPid -or
       [long]$Journal.Controller.ProcessStartUtcTicks -ne $ExpectedStartTicks -or
       [int]$Journal.Owned.Cpu -ne 30 -or
       [int]$Journal.Owned.Gpu -ne 30){
        throw 'M9D durable journal is not schema-v2 generation-3 OWNED 30/30 bound to exact GUI PID+creation ticks.'
    }
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function ConvertTo-ProcessArgument([string]$Value){
    if($Value.Contains([char]34) -or $Value.Contains([char]13) -or $Value.Contains([char]10) -or $Value.EndsWith('\')){
        throw 'M9D child argument contains unsupported quote/newline/trailing backslash.'
    }
    [string]::Concat('"',$Value,'"')
}

function Start-M9DApp {
    if(-not (Test-Path -LiteralPath $appExe -PathType Leaf)){throw "M9D app executable missing: $appExe"}

    $args=@(
        '--8c40-m9d-production-lifecycle-test',
        '--8c40-m9d-test-token',$token,
        '--modules-dir',$modulesDir
    )
    $quoted=@($args | ForEach-Object {ConvertTo-ProcessArgument ([string]$_)})

    $start=New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName=$appExe
    $start.Arguments=[string]::Join(' ',$quoted)
    $start.WorkingDirectory=$repoRoot
    $start.UseShellExecute=$false

    $process=New-Object System.Diagnostics.Process
    $process.StartInfo=$start
    if(-not $process.Start()){
        $process.Dispose()
        throw 'M9D GUI process could not be started.'
    }
    return $process
}

function Read-PipeMarker([string]$Path){
    $text=(Get-Content -LiteralPath $Path -Raw).Trim()
    $parts=@($text -split '\|')
    if($parts.Count -lt 2){throw "M9D marker '$Path' is malformed."}

    $map=[ordered]@{kind=$parts[0];timestamp=$parts[1];raw=$text}
    for($i=2;$i -lt $parts.Count;$i++){
        $kv=$parts[$i] -split '=',2
        if($kv.Count -eq 2){$map[$kv[0]]=$kv[1]}
    }
    [pscustomobject]$map
}

function Wait-ReadyMarker([System.Diagnostics.Process]$Process,[int]$Seconds=60){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $readyMarker){
            return Read-PipeMarker $readyMarker
        }
        $Process.Refresh()
        if($Process.HasExited){
            throw "M9D GUI exited before READY. ExitCode=$($Process.ExitCode)."
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9D timed out waiting for full-GUI OWNED 30/30 READY marker.'
}

function Assert-ReadyMarker($Ready,[System.Diagnostics.Process]$Process,[long]$StartTicks,[int]$ExpectedWatchdogPid,[long]$ExpectedWatchdogTicks){
    if($Ready.kind -cne 'READY' -or
       $Ready.authority -cne 'Custom' -or
       [int]$Ready.cpu -ne 30 -or [int]$Ready.gpu -ne 30 -or
       $Ready.ack -cne 'backend-ec+tachs+watchdog-owned' -or
       $Ready.journal -cne 'Owned30' -or
       [int]$Ready.watchdogPid -ne $ExpectedWatchdogPid -or
       [long]$Ready.watchdogStartTicks -ne $ExpectedWatchdogTicks -or
       [int]$Ready.guiPid -ne $Process.Id -or
       [long]$Ready.guiStartTicks -ne $StartTicks -or
       $Ready.transitionMode -cne 'm9d-production-modern-standby'){
        throw 'M9D READY marker does not prove exact full-GUI production-path OWNED 30/30 identity.'
    }
}

function Preserve-Marker([string]$Source,[string]$DestinationName){
    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){return}
    $destination=Join-Path $evidenceRoot $DestinationName
    if(Test-Path -LiteralPath $destination){throw "M9D refuses to overwrite preserved marker '$destination'."}
    Move-Item -LiteralPath $Source -Destination $destination
}

function Assert-NoExistingM9DMarkers {
    $existing=@($readyMarker,$preSleepMarker,$resumeMarker,$reentryMarker,$resultMarker | Where-Object {Test-Path -LiteralPath $_})
    if($existing.Count -gt 0){
        $existing | ForEach-Object {Write-Host ("Preserved marker: {0}" -f $_) -ForegroundColor Yellow}
        throw 'M9D refuses to delete/overwrite existing lifecycle marker evidence. Preserve/review it before another physical run.'
    }
}

function Start-M9DFailsafe([string]$LogPath){
    $process=Start-Process powershell.exe -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass',
        '-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,
        '-LogPath',$LogPath
    ) -WindowStyle Hidden -PassThru

    $deadline=(Get-Date).AddSeconds(3)
    while((Get-Date) -lt $deadline){
        $process.Refresh()
        if($process.HasExited){throw 'M9D independent failsafe exited before ARMED proof.'}
        if(Test-Path -LiteralPath $LogPath){
            $text=Get-Content -LiteralPath $LogPath -Raw -ErrorAction SilentlyContinue
            if($text -match 'M9D FAILSAFE ARMED:'){return $process}
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'M9D independent failsafe did not publish ARMED proof before GUI launch.'
}

function Test-FailsafeTakeover([string]$LogPath){
    if(-not (Test-Path -LiteralPath $LogPath)){return $false}
    $text=Get-Content -LiteralPath $LogPath -Raw
    return ($text -match 'M9D FAILSAFE TAKEOVER:' -or
            $text -match 'M9D FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M9D FAILSAFE SERVICE-START:' -or
            $text -match 'M9D FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M9D FAILSAFE STARTED:' -or
            $text -match 'M9D FAILSAFE RECOVERED:')
}

function Stop-FailsafeIfSafe([System.Diagnostics.Process]$Process){
    if($null -eq $Process){return}
    if(Test-Path -LiteralPath $journalPath){
        throw 'M9D refuses to terminate the independent failsafe while durable ownership evidence remains.'
    }
    try {
        $Process.Refresh()
        if(-not $Process.HasExited){
            $Process.Kill()
            [void]$Process.WaitForExit(3000)
        }
    } finally {$Process.Dispose()}
}

function Kill-ExactGui([System.Diagnostics.Process]$Process,[long]$ExpectedStartTicks){
    $Process.Refresh()
    if($Process.HasExited){throw 'M9D D1 GUI exited before intentional controller-death injection.'}
    $actual=[long]$Process.StartTime.ToUniversalTime().Ticks
    if($actual -ne $ExpectedStartTicks){throw 'M9D D1 GUI PID creation time changed before force-kill.'}
    $Process.Kill()
    if(-not $Process.WaitForExit(5000)){throw 'M9D D1 GUI did not terminate within 5 s.'}
}

function Get-LogBoundary {
    if(Test-Path -LiteralPath $serviceLog){return @(Get-Content -LiteralPath $serviceLog).Count}
    return 0
}

function Get-LogSegment([int]$Boundary,[string]$Destination){
    $all=@()
    if(Test-Path -LiteralPath $serviceLog){$all=@(Get-Content -LiteralPath $serviceLog)}
    $segment=@($all | Select-Object -Skip $Boundary)
    $segment | Set-Content -LiteralPath $Destination -Encoding UTF8
    return $segment
}

function Assert-D1ServiceLog([object[]]$Segment,[int]$GuiPid){
    $tag="controller PID=$GuiPid"
    $prepare=@($Segment | Where-Object {$_ -match 'WATCHDOG PREPARE ACK' -and $_.Contains($tag)})
    $intent=@($Segment | Where-Object {$_ -match 'WATCHDOG WRITE_INTENT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $commit=@($Segment | Where-Object {$_ -match 'WATCHDOG COMMIT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $ownerLoss=@($Segment | Where-Object {$_ -match 'WATCHDOG OWNER LOSS:' -and $_ -match 'disposition=RestoredFirmware'})
    if($prepare.Count -ne 1 -or $intent.Count -ne 1 -or $commit.Count -ne 1 -or $ownerLoss.Count -lt 1){
        throw "M9D D1 causal log invalid: PREPARE=$($prepare.Count) INTENT=$($intent.Count) COMMIT=$($commit.Count) OWNER_LOSS_RESTORE=$($ownerLoss.Count)."
    }
}

function Assert-D2ServiceLog([object[]]$Segment,[int]$GuiPid){
    $tag="controller PID=$GuiPid"
    $prepare=@($Segment | Where-Object {$_ -match 'WATCHDOG PREPARE ACK' -and $_.Contains($tag)})
    $intent=@($Segment | Where-Object {$_ -match 'WATCHDOG WRITE_INTENT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $commit=@($Segment | Where-Object {$_ -match 'WATCHDOG COMMIT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $restore=@($Segment | Where-Object {$_ -match 'WATCHDOG RESTORE_BEGIN ACK' -and $_.Contains($tag)})
    $release=@($Segment | Where-Object {$_ -match 'WATCHDOG RELEASE ACK' -and $_.Contains($tag)})
    if($prepare.Count -ne 2 -or $intent.Count -ne 2 -or $commit.Count -ne 2 -or
       $restore.Count -ne 2 -or $release.Count -ne 2){
        throw "M9D D2 causal log invalid: PREPARE=$($prepare.Count) INTENT=$($intent.Count) COMMIT=$($commit.Count) RESTORE=$($restore.Count) RELEASE=$($release.Count)."
    }
}

function Get-KernelPowerRecordBoundary {
    $latest=Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power'} -MaxEvents 1 -ErrorAction SilentlyContinue
    if($null -eq $latest){return 0L}
    return [long]$latest.RecordId
}

function Wait-ModernStandbyKernelEvidence([long]$AfterRecordId,[int]$Seconds=45){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        $events=@(
            Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power'} -ErrorAction SilentlyContinue |
            Where-Object {$_.RecordId -gt $AfterRecordId -and ($_.Id -in 42,506,507,524)} |
            Sort-Object RecordId
        )

        if($events | Where-Object {$_.Id -eq 524}){
            throw 'M9D D2 disqualified by Kernel-Power 524 critical-battery evidence.'
        }
        if($events | Where-Object {$_.Id -eq 507 -and $_.Message -match '(?i)hibern'}){
            throw 'M9D D2 disqualified because Kernel-Power 507 reports hibernation instead of Modern Standby.'
        }

        $entry=$events | Where-Object {$_.Id -eq 506} | Select-Object -First 1
        $exit=$events | Where-Object {$_.Id -eq 507} | Select-Object -First 1
        if($entry -and $exit -and $entry.RecordId -lt $exit.RecordId){
            $result=[ordered]@{
                afterRecordId=$AfterRecordId
                entry506=[ordered]@{recordId=[long]$entry.RecordId;timeCreated=$entry.TimeCreated.ToUniversalTime().ToString('O');message=[string]$entry.Message}
                exit507=[ordered]@{recordId=[long]$exit.RecordId;timeCreated=$exit.TimeCreated.ToUniversalTime().ToString('O');message=[string]$exit.Message}
            }
            $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $kernelEvidencePath -Encoding UTF8
            return [pscustomobject]$result
        }

        Start-Sleep -Milliseconds 500
    }
    throw 'M9D D2 did not observe causal Kernel-Power 506 -> 507 Modern Standby evidence.'
}

function Assert-D2Markers([int]$GuiPid,[long]$GuiTicks){
    foreach($path in @($preSleepMarker,$resumeMarker,$reentryMarker,$resultMarker)){
        if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "M9D D2 required marker missing: $path"}
    }

    $pre=Read-PipeMarker $preSleepMarker
    $resume=Read-PipeMarker $resumeMarker
    $reentry=Read-PipeMarker $reentryMarker
    $result=Read-PipeMarker $resultMarker

    if($pre.kind -cne 'PASS' -or
       $pre.source -cne 'GUID_SESSION_DISPLAY_STATUS/Off' -or
       $pre.primaryDisplaySignal -cne 'True' -or
       $pre.wasCustom -cne 'True' -or
       $pre.backendAck -cne 'True' -or
       $pre.authority -cne 'Firmware' -or
       $pre.localFirmwareAck -cne 'True' -or
       $pre.watchdogRelease -cne 'True' -or
       $pre.journal -cne 'absent' -or
       $pre.ec -cne '255/255' -or
       $pre.pbtSuspendAlreadyObserved -cne 'True' -or
       [int]$pre.guiPid -ne $GuiPid){
        throw 'M9D D2 pre-sleep marker does not prove proactive display-Off firmware/watchdog closure.'
    }

    if($resume.kind -cne 'GATED' -or
       [int]$resume.acceptedUserResumes -ne 1 -or
       $resume.authority -cne 'Firmware' -or
       $resume.journal -cne 'absent' -or
       [int]$resume.guiPid -ne $GuiPid){
        throw 'M9D D2 resume-gate marker does not prove single display-On gated recovery.'
    }

    if($reentry.kind -cne 'REENTRY' -or
       $reentry.authority -cne 'Custom' -or
       [int]$reentry.cpu -ne 30 -or [int]$reentry.gpu -ne 30 -or
       $reentry.ack -cne 'backend-ec+tachs+watchdog-owned' -or
       [int]$reentry.watchdogPid -ne $watchdogPid -or
       [long]$reentry.watchdogStartTicks -ne $watchdogStartTicks -or
       [int]$reentry.guiPid -ne $GuiPid -or
       [long]$reentry.guiStartTicks -ne $GuiTicks){
        throw 'M9D D2 re-entry marker does not prove exact controlled post-resume OWNED 30/30.'
    }

    if($result.kind -cne 'PASS' -or
       $result.transitionMode -cne 'm9d-production-modern-standby' -or
       $result.primaryDisplayOff -cne 'True' -or
       $result.pbtSuspend -cne 'True' -or
       $result.displayOn -cne 'True' -or
       [int]$result.acceptedUserResumes -ne 1 -or
       [int]$result.guiPid -ne $GuiPid){
        throw 'M9D D2 final marker does not prove complete full-GUI Modern Standby closure.'
    }
}

function Wait-ResultMarker([System.Diagnostics.Process]$Process,[int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $resultMarker){return}
        $Process.Refresh()
        if($Process.HasExited -and -not (Test-Path -LiteralPath $resultMarker)){
            throw "M9D D2 GUI exited without final result marker. ExitCode=$($Process.ExitCode)."
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'M9D D2 timed out waiting for lifecycle result marker.'
}

function Write-HarnessSummary([string]$Result,[string]$Failure){
    [ordered]@{
        schemaVersion=1
        gate='M9D-HARNESS'
        result=$Result
        failure=$Failure
        evidenceHead=$head
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        watchdogPid=$watchdogPid
        watchdogStartUtcTicks=$watchdogStartTicks
        d1=[ordered]@{
            passed=$d1Pass
            guiPid=$d1GuiPid
            guiStartUtcTicks=$d1GuiStartTicks
            recoverySeconds=$d1RecoverySeconds
            failsafeTakeover=$d1FailsafeTakeover
        }
        d2=[ordered]@{
            passed=$d2Pass
            guiPid=$d2GuiPid
            guiStartUtcTicks=$d2GuiStartTicks
            failsafeTakeover=$d2FailsafeTakeover
        }
        finalJournalAbsent=$finalJournalAbsent
        finalFirmwareProofPass=$finalFirmwareProofPass
        finalServiceBaselinePass=$finalServiceBaselinePass
        productionConstructionAuthorized=$false
        watchdogRecoveryValidated=$false
        automaticPolicyEnabled=$false
        controlEnabledByDefault=$false
        packagePath=$packagePath
        packageSha256=$packageSha256
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator

foreach($name in @('OmenMon','OmenMon-Reborn')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9D refused while '$name' is running."}
}
if(Get-Process -Name 'VictusFanControl.App' -ErrorAction SilentlyContinue){
    throw 'M9D refused while VictusFanControl.App is already running.'
}

$head=Assert-RepositoryProvenance
Assert-ExactTarget
Assert-PowerSane
Assert-ServiceBaseline
Assert-NoExistingM9DMarkers

if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9D refuses retained watchdog journal evidence; it was not deleted.'
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 M9D FULL-GUI PRODUCTION RECOVERY/LIFECYCLE' -ForegroundColor Cyan
Write-Host 'WRITE-CAPABLE ONLY AFTER THE VERSIONED M9D AUTHORIZATION BARRIER.' -ForegroundColor Yellow
Write-Host 'Subcycle D1 force-kills the exact GUI; D2 requires user-initiated Modern Standby.' -ForegroundColor Yellow
Write-Host ''

try {
    Write-Host 'Step 1: same-HEAD build + M5-M9 deterministic/static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "M9D build failed with exit=$LASTEXITCODE."}

    foreach($script in @(
        'test-8c40-m9-production-watchdog-invariants.ps1',
        'test-8c40-m9b-readonly-preflight-invariants.ps1',
        'test-8c40-m9c-production-smoke-invariants.ps1',
        'test-8c40-m9c-harness-invariants.ps1',
        'test-8c40-m9d-production-lifecycle-invariants.ps1',
        'test-8c40-m9d-harness-invariants.ps1',
        'test-8c40-m9d-evidence-packaging.ps1'
    )){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE -ne 0){throw "M9D regression '$script' failed with exit=$LASTEXITCODE."}
    }

    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'M9D SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'M9D coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'M9D HP backend/gate self-test failed.'}

    Write-Host 'Step 2: stable firmware/service baseline...' -ForegroundColor Cyan
    Assert-StableFirmwareOwned 'M9D baseline' $baselineFfPath
    Assert-ServiceBaseline
    Assert-PowerSane

    $confirm=Read-Host "Type exactly $token to authorize this one versioned two-subcycle M9D physical gate"
    if($confirm -cne $token){throw 'M9D cancelled before service/failsafe/GUI active boundary.'}

    Write-Host 'Step 3: start exact LocalSystem M4 watchdog once for both subcycles...' -ForegroundColor Cyan
    Start-Service -Name $serviceName
    $serviceStarted=$true
    $svc=Get-ServiceState
    if(-not $svc -or $svc.State -ne 'Running' -or [int]$svc.ProcessId -le 0){
        throw 'M9D watchdog service did not reach Running.'
    }
    $watchdogPid=[int]$svc.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
    [void](Wait-M4Ready $watchdogPid $watchdogStartTicks)

    # D1 - full GUI production path + exact controller death.
    Write-Host 'Step 4 / D1: arm failsafe, launch full GUI path, prove OWNED 30/30...' -ForegroundColor Cyan
    $d1LogBoundary=Get-LogBoundary
    $failsafe=Start-M9DFailsafe $failsafeD1Log
    $app=Start-M9DApp
    $d1GuiPid=$app.Id
    $d1GuiStartTicks=[long]$app.StartTime.ToUniversalTime().Ticks
    $ready=Wait-ReadyMarker $app
    Assert-ReadyMarker $ready $app $d1GuiStartTicks $watchdogPid $watchdogStartTicks

    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9D D1 READY exists but OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $d1GuiPid $d1GuiStartTicks

    Preserve-Marker $readyMarker 'm9d-d1-ready.txt'

    if(Test-FailsafeTakeover $failsafeD1Log){
        $d1FailsafeTakeover=$true
        throw 'M9D D1 invalid: independent failsafe took over before controller-death injection.'
    }

    Write-Host ("D1 FORCE-KILL exact GUI PID={0} startTicks={1}. Parent issues no HP restore." -f $d1GuiPid,$d1GuiStartTicks) -ForegroundColor Yellow
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Kill-ExactGui $app $d1GuiStartTicks
    $app.Dispose()
    $app=$null

    if(-not (Wait-JournalGone $ControllerDeathRecoveryTimeoutSeconds)){
        throw "M9D D1 watchdog did not clear journal within $ControllerDeathRecoveryTimeoutSeconds s."
    }
    $watch.Stop()
    $d1RecoverySeconds=$watch.Elapsed.TotalSeconds

    Assert-StableFirmwareOwned 'M9D D1 final' $d1FinalFfPath

    $svc=Get-ServiceState
    if(-not $svc -or [int]$svc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'M9D D1 watchdog PID/creation identity changed during controller-death recovery.'
    }

    $d1Segment=Get-LogSegment $d1LogBoundary $d1LogPath
    Assert-D1ServiceLog $d1Segment $d1GuiPid

    if(Test-FailsafeTakeover $failsafeD1Log){
        $d1FailsafeTakeover=$true
        throw 'M9D D1 safe recovery required the delayed failsafe; integration result is invalid.'
    }
    Stop-FailsafeIfSafe $failsafe
    $failsafe=$null
    $d1Pass=$true

    # D2 - fresh full GUI production path + real user-initiated Modern Standby.
    Write-Host 'Step 5 / D2: fresh baseline and full-GUI lifecycle launch...' -ForegroundColor Cyan
    Assert-NoExistingM9DMarkers
    Assert-StableFirmwareOwned 'M9D D2 baseline' $d2BaselineFfPath
    [void](Wait-M4Ready $watchdogPid $watchdogStartTicks)
    $d2LogBoundary=Get-LogBoundary

    $failsafe=Start-M9DFailsafe $failsafeD2Log
    $app=Start-M9DApp
    $d2GuiPid=$app.Id
    $d2GuiStartTicks=[long]$app.StartTime.ToUniversalTime().Ticks
    $ready=Wait-ReadyMarker $app
    Assert-ReadyMarker $ready $app $d2GuiStartTicks $watchdogPid $watchdogStartTicks

    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9D D2 READY exists but OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $d2GuiPid $d2GuiStartTicks

    Preserve-Marker $readyMarker 'm9d-d2-ready.txt'

    if(Test-FailsafeTakeover $failsafeD2Log){
        $d2FailsafeTakeover=$true
        throw 'M9D D2 invalid: independent failsafe took over before sleep boundary.'
    }

    $kernelBoundary=Get-KernelPowerRecordBoundary
    Write-Host ''
    Write-Host 'D2 ACTIVE LIFECYCLE BOUNDARY.' -ForegroundColor Yellow
    Write-Host 'Type the confirmation below, then use Windows Start -> Power -> Sleep.' -ForegroundColor Yellow
    Write-Host 'Do not use shutdown /h, close the lid, kill GUI/watchdog or start a workload.' -ForegroundColor Yellow
    $sleepConfirm=Read-Host 'Type exactly M9D-SLEEP when ready to initiate Modern Standby manually'
    if($sleepConfirm -cne 'M9D-SLEEP'){throw 'M9D D2 cancelled before user-initiated sleep.'}

    Wait-ResultMarker $app $LifecycleResultTimeoutSeconds
    Assert-D2Markers $d2GuiPid $d2GuiStartTicks

    if(-not $app.WaitForExit(15000)){
        throw 'M9D D2 GUI wrote PASS result but did not exit within 15 s.'
    }
    $app.Refresh()
    if($app.ExitCode -ne 0){throw "M9D D2 GUI final ExitCode=$($app.ExitCode)."}
    $app.Dispose()
    $app=$null

    [void](Wait-ModernStandbyKernelEvidence $kernelBoundary)

    $svc=Get-ServiceState
    if(-not $svc -or [int]$svc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'M9D D2 watchdog PID/creation identity changed across Modern Standby.'
    }
    if(Test-Path -LiteralPath $journalPath){throw 'M9D D2 final lifecycle marker exists but durable journal remains.'}

    Assert-StableFirmwareOwned 'M9D D2 final' $d2FinalFfPath
    $d2Segment=Get-LogSegment $d2LogBoundary $d2LogPath
    Assert-D2ServiceLog $d2Segment $d2GuiPid

    if(Test-FailsafeTakeover $failsafeD2Log){
        $d2FailsafeTakeover=$true
        throw 'M9D D2 safe lifecycle closure required the delayed failsafe; integration result is invalid.'
    }
    Stop-FailsafeIfSafe $failsafe
    $failsafe=$null

    Preserve-Marker $preSleepMarker 'm9d-d2-presleep.txt'
    Preserve-Marker $resumeMarker 'm9d-d2-resume-gate.txt'
    Preserve-Marker $reentryMarker 'm9d-d2-reentry.txt'
    Preserve-Marker $resultMarker 'm9d-d2-result.txt'
    $d2Pass=$true

    Write-Host 'Step 6: final independent firmware/service closure...' -ForegroundColor Cyan
    if(Test-Path -LiteralPath $journalPath){throw 'M9D final closure found retained journal.'}
    $finalJournalAbsent=$true
    Assert-StableFirmwareOwned 'M9D final' $cleanupFfPath
    $finalFirmwareProofPass=$true

    Stop-Service -Name $serviceName -Force
    $serviceStarted=$false
    $svc=Get-ServiceState
    if(-not $svc -or $svc.State -ne 'Stopped' -or $svc.StartMode -ne 'Manual' -or
       [int]$svc.ProcessId -ne 0 -or [string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw 'M9D final M4 baseline is not Manual/Stopped/PID0/LocalSystem.'
    }
    $finalServiceBaselinePass=$true

    $pass=$d1Pass -and $d2Pass -and $finalJournalAbsent -and
          $finalFirmwareProofPass -and $finalServiceBaselinePass -and
          -not $d1FailsafeTakeover -and -not $d2FailsafeTakeover

    if(-not $pass){throw 'M9D internal final PASS predicate was not satisfied.'}
    Write-Host 'PASS: M9D D1 controller-death and D2 Modern Standby last-mile integration closed.' -ForegroundColor Green
}
catch {
    $failure=$_.Exception.Message
    Write-Host ("M9D FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally {
    if($app){
        try {
            $app.Refresh()
            if(-not $app.HasExited){
                $liveTicks=[long]$app.StartTime.ToUniversalTime().Ticks
                Write-Warning ("M9D cleanup is terminating exact GUI PID={0} ticks={1}; watchdog owns any retained lease recovery." -f $app.Id,$liveTicks)
                $app.Kill()
                try{[void]$app.WaitForExit(5000)}catch{}
            }
        } catch {}
        try{$app.Dispose()}catch{}
        $app=$null
    }

    foreach($pair in @(
        @($readyMarker,'m9d-unexpected-ready-final.txt'),
        @($preSleepMarker,'m9d-unexpected-presleep-final.txt'),
        @($resumeMarker,'m9d-unexpected-resume-final.txt'),
        @($reentryMarker,'m9d-unexpected-reentry-final.txt'),
        @($resultMarker,'m9d-unexpected-result-final.txt')
    )){
        try {
            if(Test-Path -LiteralPath $pair[0]){
                $dest=Join-Path $evidenceRoot $pair[1]
                if(-not (Test-Path -LiteralPath $dest)){
                    Copy-Item -LiteralPath $pair[0] -Destination $dest
                }
            }
        } catch {}
    }

    if(Test-Path -LiteralPath $journalPath){
        try {
            if(-not (Test-Path -LiteralPath $retainedLeasePath)){
                Copy-Item -LiteralPath $journalPath -Destination $retainedLeasePath
            }
        } catch {}

        $svc=Get-ServiceState
        if(-not $svc -or $svc.State -ne 'Running'){
            Write-Warning 'M9D cleanup: retained journal exists; starting qualified recovery service without deleting evidence.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            $serviceStarted=$true
        }
        [void](Wait-JournalGone 30)
    }

    $finalJournalAbsent=(-not (Test-Path -LiteralPath $journalPath))

    if($finalJournalAbsent){
        try {
            Assert-StableFirmwareOwned 'M9D cleanup' $cleanupFfPath
            $finalFirmwareProofPass=$true
        } catch {
            Write-Warning "M9D cleanup FF/FF proof failed: $($_.Exception.Message)"
            $finalFirmwareProofPass=$false
        }

        if($failsafe){
            try{Stop-FailsafeIfSafe $failsafe}catch{Write-Warning $_.Exception.Message}
            $failsafe=$null
        }

        try {
            $svc=Get-ServiceState
            if($svc -and $svc.State -ne 'Stopped'){
                Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            }
            $svc=Get-ServiceState
            $finalServiceBaselinePass=
                $svc -and $svc.State -eq 'Stopped' -and $svc.StartMode -eq 'Manual' -and
                [int]$svc.ProcessId -eq 0 -and [string]$svc.StartName -match 'LocalSystem|Local System'
        } catch {$finalServiceBaselinePass=$false}
    }
    else {
        $pass=$false
        Write-Host 'CRITICAL: M9D durable ownership evidence remains and was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Qualified watchdog/failsafe recovery is left available; do not run another write gate.' -ForegroundColor Red
    }

    if(Test-FailsafeTakeover $failsafeD1Log){$d1FailsafeTakeover=$true;$pass=$false}
    if(Test-FailsafeTakeover $failsafeD2Log){$d2FailsafeTakeover=$true;$pass=$false}

    $pass=$pass -and $d1Pass -and $d2Pass -and $finalJournalAbsent -and
          $finalFirmwareProofPass -and $finalServiceBaselinePass -and
          -not $d1FailsafeTakeover -and -not $d2FailsafeTakeover

    try {Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure}
    catch {
        $pass=$false
        $failure="M9D summary write failed: $($_.Exception.Message)"
    }

    try {
        $package=& $packagingScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot -WatchdogLogPath $serviceLog -WatchdogStatusPath $statusPath -WatchdogJournalPath $journalPath
        $packagePath=[string]$package.ZipPath
        $packageSha256=[string]$package.ZipSha256
        Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure
        Write-Host ("M9D evidence ZIP: {0}" -f $packagePath)
        Write-Host ("M9D ZIP SHA256 : {0}" -f $packageSha256)
    }
    catch {
        $pass=$false
        $packFailure="M9D evidence packaging failed: $($_.Exception.Message)"
        $failure=$(if([string]::IsNullOrWhiteSpace($failure)){$packFailure}else{"$failure | $packFailure"})
        try{Write-HarnessSummary 'FAIL_CLOSED' $failure}catch{}
    }
}

if(-not $pass){throw "M9D FAILED: $failure"}

Write-Host ''
Write-Host 'PASS: HP 8C40 M9D full-GUI production recovery/lifecycle qualification fully closed.' -ForegroundColor Green
Write-Host 'D1 proved exact GUI owner-death watchdog recovery; D2 proved display-aware Modern Standby release/resume/re-entry on the normal production construction surfaces. Production promotion still remains a separate commit.' -ForegroundColor Green
 -or
       $LASTEXITCODE -ne 0 -or
       $parentHead -cne $authorizationSourceHead){
        throw "M9D authorization must be a direct child of its same-CI source HEAD. source=$authorizationSourceHead parent=$parentHead current=$localHead"
    }

    $statusText=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'M9D could not inspect repository status.'}

    $blocking=@(
        $statusText -split "[\r\n]+" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)
        }
    )

    if($blocking.Count -gt 0){
        $blocking | ForEach-Object {Write-Host $_}
        throw 'M9D requires committed source/config state; only untracked logs/ evidence is allowed.'
    }

    return $localHead
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop
    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    if(([string]$board.Manufacturer).Trim() -cne 'HP' -or
       ([string]$board.Product).Trim() -cne '8C40' -or
       ([string]$board.Version).Trim() -cne '63.43' -or
       ([string]$system.Manufacturer).Trim() -cne 'HP' -or
       ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M9D exact-target fingerprint mismatch.'
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ProcessStartTicks([int]$ProcessId){
    $process=[System.Diagnostics.Process]::GetProcessById($ProcessId)
    try{return [long]$process.StartTime.ToUniversalTime().Ticks}
    finally{$process.Dispose()}
}

function Assert-ServiceBaseline {
    $svc=Get-ServiceState
    if(-not $svc){throw 'M9D requires the already-qualified M4 service to be installed; harness will not install it.'}
    if([string]$svc.State -cne 'Stopped' -or
       [string]$svc.StartMode -cne 'Manual' -or
       [int]$svc.ProcessId -ne 0){
        throw "M9D requires M4 Manual/Stopped/PID0; observed $($svc.StartMode)/$($svc.State)/PID$($svc.ProcessId)."
    }
    if([string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw "M9D requires LocalSystem; observed '$($svc.StartName)'."
    }
    foreach($required in @(
        $serviceExe,
        '--service-name VictusFanControlWatchdogM4',
        '--m4-8c40-lease-service'
    )){
        if(([string]$svc.PathName).IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){
            throw "M9D M4 service configuration mismatch; missing '$required' in '$($svc.PathName)'."
        }
    }
    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "M9D service executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "M9D service module missing: $serviceModule"}
}

function Get-PowerSnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $status=[System.Windows.Forms.SystemInformation]::PowerStatus
    $percent=$null
    if($status.BatteryLifePercent -ge 0){$percent=[math]::Round([double]$status.BatteryLifePercent*100,0)}
    [pscustomobject]@{PowerLineStatus=[string]$status.PowerLineStatus;BatteryPercent=$percent}
}

function Assert-PowerSane {
    $power=Get-PowerSnapshot
    if($power.PowerLineStatus -cne 'Online'){throw "M9D requires AC online; observed '$($power.PowerLineStatus)'."}
    if($null -eq $power.BatteryPercent -or [double]$power.BatteryPercent -lt 20){
        throw "M9D requires readable battery >=20%; observed '$($power.BatteryPercent)'."
    }
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9D setpoint probe failed. Raw: $output"}
    $line=($output -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    $match=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M9D could not parse setpoint probe. Raw: $output"}
    [pscustomobject]@{
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        cpu=[int]$match.Groups[1].Value
        gpu=[int]$match.Groups[2].Value
        raw=[string]$line
    }
}

function Assert-StableFirmwareOwned([string]$Context,[string]$EvidencePath){
    $samples=@()
    $consecutive=0
    for($read=1;$read -le 6;$read++){
        $sample=Read-8C40Setpoint
        $samples+=@($sample)
        Write-Host ("{0} FF proof {1}/6: {2}" -f $Context,$read,$sample.raw)

        if($sample.cpu -eq 255 -and $sample.gpu -eq 255){
            $consecutive++
            if($consecutive -ge 2){
                [ordered]@{context=$Context;passed=$true;requiredConsecutive=2;maximumReads=6;samples=$samples} |
                    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
                return
            }
        } else {$consecutive=0}

        if($read -lt 6){Start-Sleep -Milliseconds 75}
    }

    [ordered]@{context=$Context;passed=$false;requiredConsecutive=2;maximumReads=6;samples=$samples} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
    throw "$Context requires two consecutive independent FF/FF observations within six reads."
}

function Wait-M4Ready([int]$ExpectedPid,[long]$ExpectedStartTicks){
    $deadline=(Get-Date).AddSeconds(20)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $statusPath){
            try {
                $status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if([int]$status.ProcessId -eq $ExpectedPid -and
                   $status.Ready -and -not $status.Blocked -and
                   [int]$status.SessionId -eq 0 -and
                   $status.AccountName -match 'SYSTEM$' -and
                   $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                   (Get-ProcessStartTicks $ExpectedPid) -eq $ExpectedStartTicks){
                    return $status
                }
            } catch {}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9D timed out waiting for exact M4 Ready state.'
}

function Test-OwnedPhase($Phase){
    if($null -eq $Phase){return $false}
    if($Phase -is [string]){return ($Phase -ceq 'Owned' -or $Phase -ceq '2')}
    try{return ([int]$Phase -eq 2)}catch{return $false}
}

function Assert-OwnedJournal($Journal,[int]$ExpectedPid,[long]$ExpectedStartTicks){
    if([int]$Journal.SchemaVersion -ne 2 -or
       $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Test-OwnedPhase $Journal.Phase) -or
       [int]$Journal.Generation -ne 3 -or
       [int]$Journal.Controller.ProcessId -ne $ExpectedPid -or
       [long]$Journal.Controller.ProcessStartUtcTicks -ne $ExpectedStartTicks -or
       [int]$Journal.Owned.Cpu -ne 30 -or
       [int]$Journal.Owned.Gpu -ne 30){
        throw 'M9D durable journal is not schema-v2 generation-3 OWNED 30/30 bound to exact GUI PID+creation ticks.'
    }
}

function Wait-JournalGone([int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(-not (Test-Path -LiteralPath $journalPath)){return $true}
        Start-Sleep -Milliseconds 200
    }
    return (-not (Test-Path -LiteralPath $journalPath))
}

function ConvertTo-ProcessArgument([string]$Value){
    if($Value.Contains([char]34) -or $Value.Contains([char]13) -or $Value.Contains([char]10) -or $Value.EndsWith('\')){
        throw 'M9D child argument contains unsupported quote/newline/trailing backslash.'
    }
    [string]::Concat('"',$Value,'"')
}

function Start-M9DApp {
    if(-not (Test-Path -LiteralPath $appExe -PathType Leaf)){throw "M9D app executable missing: $appExe"}

    $args=@(
        '--8c40-m9d-production-lifecycle-test',
        '--8c40-m9d-test-token',$token,
        '--modules-dir',$modulesDir
    )
    $quoted=@($args | ForEach-Object {ConvertTo-ProcessArgument ([string]$_)})

    $start=New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName=$appExe
    $start.Arguments=[string]::Join(' ',$quoted)
    $start.WorkingDirectory=$repoRoot
    $start.UseShellExecute=$false

    $process=New-Object System.Diagnostics.Process
    $process.StartInfo=$start
    if(-not $process.Start()){
        $process.Dispose()
        throw 'M9D GUI process could not be started.'
    }
    return $process
}

function Read-PipeMarker([string]$Path){
    $text=(Get-Content -LiteralPath $Path -Raw).Trim()
    $parts=@($text -split '\|')
    if($parts.Count -lt 2){throw "M9D marker '$Path' is malformed."}

    $map=[ordered]@{kind=$parts[0];timestamp=$parts[1];raw=$text}
    for($i=2;$i -lt $parts.Count;$i++){
        $kv=$parts[$i] -split '=',2
        if($kv.Count -eq 2){$map[$kv[0]]=$kv[1]}
    }
    [pscustomobject]$map
}

function Wait-ReadyMarker([System.Diagnostics.Process]$Process,[int]$Seconds=60){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $readyMarker){
            return Read-PipeMarker $readyMarker
        }
        $Process.Refresh()
        if($Process.HasExited){
            throw "M9D GUI exited before READY. ExitCode=$($Process.ExitCode)."
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'M9D timed out waiting for full-GUI OWNED 30/30 READY marker.'
}

function Assert-ReadyMarker($Ready,[System.Diagnostics.Process]$Process,[long]$StartTicks,[int]$ExpectedWatchdogPid,[long]$ExpectedWatchdogTicks){
    if($Ready.kind -cne 'READY' -or
       $Ready.authority -cne 'Custom' -or
       [int]$Ready.cpu -ne 30 -or [int]$Ready.gpu -ne 30 -or
       $Ready.ack -cne 'backend-ec+tachs+watchdog-owned' -or
       $Ready.journal -cne 'Owned30' -or
       [int]$Ready.watchdogPid -ne $ExpectedWatchdogPid -or
       [long]$Ready.watchdogStartTicks -ne $ExpectedWatchdogTicks -or
       [int]$Ready.guiPid -ne $Process.Id -or
       [long]$Ready.guiStartTicks -ne $StartTicks -or
       $Ready.transitionMode -cne 'm9d-production-modern-standby'){
        throw 'M9D READY marker does not prove exact full-GUI production-path OWNED 30/30 identity.'
    }
}

function Preserve-Marker([string]$Source,[string]$DestinationName){
    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){return}
    $destination=Join-Path $evidenceRoot $DestinationName
    if(Test-Path -LiteralPath $destination){throw "M9D refuses to overwrite preserved marker '$destination'."}
    Move-Item -LiteralPath $Source -Destination $destination
}

function Assert-NoExistingM9DMarkers {
    $existing=@($readyMarker,$preSleepMarker,$resumeMarker,$reentryMarker,$resultMarker | Where-Object {Test-Path -LiteralPath $_})
    if($existing.Count -gt 0){
        $existing | ForEach-Object {Write-Host ("Preserved marker: {0}" -f $_) -ForegroundColor Yellow}
        throw 'M9D refuses to delete/overwrite existing lifecycle marker evidence. Preserve/review it before another physical run.'
    }
}

function Start-M9DFailsafe([string]$LogPath){
    $process=Start-Process powershell.exe -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass',
        '-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,
        '-LogPath',$LogPath
    ) -WindowStyle Hidden -PassThru

    $deadline=(Get-Date).AddSeconds(3)
    while((Get-Date) -lt $deadline){
        $process.Refresh()
        if($process.HasExited){throw 'M9D independent failsafe exited before ARMED proof.'}
        if(Test-Path -LiteralPath $LogPath){
            $text=Get-Content -LiteralPath $LogPath -Raw -ErrorAction SilentlyContinue
            if($text -match 'M9D FAILSAFE ARMED:'){return $process}
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'M9D independent failsafe did not publish ARMED proof before GUI launch.'
}

function Test-FailsafeTakeover([string]$LogPath){
    if(-not (Test-Path -LiteralPath $LogPath)){return $false}
    $text=Get-Content -LiteralPath $LogPath -Raw
    return ($text -match 'M9D FAILSAFE TAKEOVER:' -or
            $text -match 'M9D FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M9D FAILSAFE SERVICE-START:' -or
            $text -match 'M9D FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M9D FAILSAFE STARTED:' -or
            $text -match 'M9D FAILSAFE RECOVERED:')
}

function Stop-FailsafeIfSafe([System.Diagnostics.Process]$Process){
    if($null -eq $Process){return}
    if(Test-Path -LiteralPath $journalPath){
        throw 'M9D refuses to terminate the independent failsafe while durable ownership evidence remains.'
    }
    try {
        $Process.Refresh()
        if(-not $Process.HasExited){
            $Process.Kill()
            [void]$Process.WaitForExit(3000)
        }
    } finally {$Process.Dispose()}
}

function Kill-ExactGui([System.Diagnostics.Process]$Process,[long]$ExpectedStartTicks){
    $Process.Refresh()
    if($Process.HasExited){throw 'M9D D1 GUI exited before intentional controller-death injection.'}
    $actual=[long]$Process.StartTime.ToUniversalTime().Ticks
    if($actual -ne $ExpectedStartTicks){throw 'M9D D1 GUI PID creation time changed before force-kill.'}
    $Process.Kill()
    if(-not $Process.WaitForExit(5000)){throw 'M9D D1 GUI did not terminate within 5 s.'}
}

function Get-LogBoundary {
    if(Test-Path -LiteralPath $serviceLog){return @(Get-Content -LiteralPath $serviceLog).Count}
    return 0
}

function Get-LogSegment([int]$Boundary,[string]$Destination){
    $all=@()
    if(Test-Path -LiteralPath $serviceLog){$all=@(Get-Content -LiteralPath $serviceLog)}
    $segment=@($all | Select-Object -Skip $Boundary)
    $segment | Set-Content -LiteralPath $Destination -Encoding UTF8
    return $segment
}

function Assert-D1ServiceLog([object[]]$Segment,[int]$GuiPid){
    $tag="controller PID=$GuiPid"
    $prepare=@($Segment | Where-Object {$_ -match 'WATCHDOG PREPARE ACK' -and $_.Contains($tag)})
    $intent=@($Segment | Where-Object {$_ -match 'WATCHDOG WRITE_INTENT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $commit=@($Segment | Where-Object {$_ -match 'WATCHDOG COMMIT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $ownerLoss=@($Segment | Where-Object {$_ -match 'WATCHDOG OWNER LOSS:' -and $_ -match 'disposition=RestoredFirmware'})
    if($prepare.Count -ne 1 -or $intent.Count -ne 1 -or $commit.Count -ne 1 -or $ownerLoss.Count -lt 1){
        throw "M9D D1 causal log invalid: PREPARE=$($prepare.Count) INTENT=$($intent.Count) COMMIT=$($commit.Count) OWNER_LOSS_RESTORE=$($ownerLoss.Count)."
    }
}

function Assert-D2ServiceLog([object[]]$Segment,[int]$GuiPid){
    $tag="controller PID=$GuiPid"
    $prepare=@($Segment | Where-Object {$_ -match 'WATCHDOG PREPARE ACK' -and $_.Contains($tag)})
    $intent=@($Segment | Where-Object {$_ -match 'WATCHDOG WRITE_INTENT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $commit=@($Segment | Where-Object {$_ -match 'WATCHDOG COMMIT ACK' -and $_.Contains($tag) -and $_ -match 'target=30/30'})
    $restore=@($Segment | Where-Object {$_ -match 'WATCHDOG RESTORE_BEGIN ACK' -and $_.Contains($tag)})
    $release=@($Segment | Where-Object {$_ -match 'WATCHDOG RELEASE ACK' -and $_.Contains($tag)})
    if($prepare.Count -ne 2 -or $intent.Count -ne 2 -or $commit.Count -ne 2 -or
       $restore.Count -ne 2 -or $release.Count -ne 2){
        throw "M9D D2 causal log invalid: PREPARE=$($prepare.Count) INTENT=$($intent.Count) COMMIT=$($commit.Count) RESTORE=$($restore.Count) RELEASE=$($release.Count)."
    }
}

function Get-KernelPowerRecordBoundary {
    $latest=Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power'} -MaxEvents 1 -ErrorAction SilentlyContinue
    if($null -eq $latest){return 0L}
    return [long]$latest.RecordId
}

function Wait-ModernStandbyKernelEvidence([long]$AfterRecordId,[int]$Seconds=45){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        $events=@(
            Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power'} -ErrorAction SilentlyContinue |
            Where-Object {$_.RecordId -gt $AfterRecordId -and ($_.Id -in 42,506,507,524)} |
            Sort-Object RecordId
        )

        if($events | Where-Object {$_.Id -eq 524}){
            throw 'M9D D2 disqualified by Kernel-Power 524 critical-battery evidence.'
        }
        if($events | Where-Object {$_.Id -eq 507 -and $_.Message -match '(?i)hibern'}){
            throw 'M9D D2 disqualified because Kernel-Power 507 reports hibernation instead of Modern Standby.'
        }

        $entry=$events | Where-Object {$_.Id -eq 506} | Select-Object -First 1
        $exit=$events | Where-Object {$_.Id -eq 507} | Select-Object -First 1
        if($entry -and $exit -and $entry.RecordId -lt $exit.RecordId){
            $result=[ordered]@{
                afterRecordId=$AfterRecordId
                entry506=[ordered]@{recordId=[long]$entry.RecordId;timeCreated=$entry.TimeCreated.ToUniversalTime().ToString('O');message=[string]$entry.Message}
                exit507=[ordered]@{recordId=[long]$exit.RecordId;timeCreated=$exit.TimeCreated.ToUniversalTime().ToString('O');message=[string]$exit.Message}
            }
            $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $kernelEvidencePath -Encoding UTF8
            return [pscustomobject]$result
        }

        Start-Sleep -Milliseconds 500
    }
    throw 'M9D D2 did not observe causal Kernel-Power 506 -> 507 Modern Standby evidence.'
}

function Assert-D2Markers([int]$GuiPid,[long]$GuiTicks){
    foreach($path in @($preSleepMarker,$resumeMarker,$reentryMarker,$resultMarker)){
        if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "M9D D2 required marker missing: $path"}
    }

    $pre=Read-PipeMarker $preSleepMarker
    $resume=Read-PipeMarker $resumeMarker
    $reentry=Read-PipeMarker $reentryMarker
    $result=Read-PipeMarker $resultMarker

    if($pre.kind -cne 'PASS' -or
       $pre.source -cne 'GUID_SESSION_DISPLAY_STATUS/Off' -or
       $pre.primaryDisplaySignal -cne 'True' -or
       $pre.wasCustom -cne 'True' -or
       $pre.backendAck -cne 'True' -or
       $pre.authority -cne 'Firmware' -or
       $pre.localFirmwareAck -cne 'True' -or
       $pre.watchdogRelease -cne 'True' -or
       $pre.journal -cne 'absent' -or
       $pre.ec -cne '255/255' -or
       $pre.pbtSuspendAlreadyObserved -cne 'True' -or
       [int]$pre.guiPid -ne $GuiPid){
        throw 'M9D D2 pre-sleep marker does not prove proactive display-Off firmware/watchdog closure.'
    }

    if($resume.kind -cne 'GATED' -or
       [int]$resume.acceptedUserResumes -ne 1 -or
       $resume.authority -cne 'Firmware' -or
       $resume.journal -cne 'absent' -or
       [int]$resume.guiPid -ne $GuiPid){
        throw 'M9D D2 resume-gate marker does not prove single display-On gated recovery.'
    }

    if($reentry.kind -cne 'REENTRY' -or
       $reentry.authority -cne 'Custom' -or
       [int]$reentry.cpu -ne 30 -or [int]$reentry.gpu -ne 30 -or
       $reentry.ack -cne 'backend-ec+tachs+watchdog-owned' -or
       [int]$reentry.watchdogPid -ne $watchdogPid -or
       [long]$reentry.watchdogStartTicks -ne $watchdogStartTicks -or
       [int]$reentry.guiPid -ne $GuiPid -or
       [long]$reentry.guiStartTicks -ne $GuiTicks){
        throw 'M9D D2 re-entry marker does not prove exact controlled post-resume OWNED 30/30.'
    }

    if($result.kind -cne 'PASS' -or
       $result.transitionMode -cne 'm9d-production-modern-standby' -or
       $result.primaryDisplayOff -cne 'True' -or
       $result.pbtSuspend -cne 'True' -or
       $result.displayOn -cne 'True' -or
       [int]$result.acceptedUserResumes -ne 1 -or
       [int]$result.guiPid -ne $GuiPid){
        throw 'M9D D2 final marker does not prove complete full-GUI Modern Standby closure.'
    }
}

function Wait-ResultMarker([System.Diagnostics.Process]$Process,[int]$Seconds){
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date) -lt $deadline){
        if(Test-Path -LiteralPath $resultMarker){return}
        $Process.Refresh()
        if($Process.HasExited -and -not (Test-Path -LiteralPath $resultMarker)){
            throw "M9D D2 GUI exited without final result marker. ExitCode=$($Process.ExitCode)."
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'M9D D2 timed out waiting for lifecycle result marker.'
}

function Write-HarnessSummary([string]$Result,[string]$Failure){
    [ordered]@{
        schemaVersion=1
        gate='M9D-HARNESS'
        result=$Result
        failure=$Failure
        evidenceHead=$head
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        watchdogPid=$watchdogPid
        watchdogStartUtcTicks=$watchdogStartTicks
        d1=[ordered]@{
            passed=$d1Pass
            guiPid=$d1GuiPid
            guiStartUtcTicks=$d1GuiStartTicks
            recoverySeconds=$d1RecoverySeconds
            failsafeTakeover=$d1FailsafeTakeover
        }
        d2=[ordered]@{
            passed=$d2Pass
            guiPid=$d2GuiPid
            guiStartUtcTicks=$d2GuiStartTicks
            failsafeTakeover=$d2FailsafeTakeover
        }
        finalJournalAbsent=$finalJournalAbsent
        finalFirmwareProofPass=$finalFirmwareProofPass
        finalServiceBaselinePass=$finalServiceBaselinePass
        productionConstructionAuthorized=$false
        watchdogRecoveryValidated=$false
        automaticPolicyEnabled=$false
        controlEnabledByDefault=$false
        packagePath=$packagePath
        packageSha256=$packageSha256
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Assert-Administrator

foreach($name in @('OmenMon','OmenMon-Reborn')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "M9D refused while '$name' is running."}
}
if(Get-Process -Name 'VictusFanControl.App' -ErrorAction SilentlyContinue){
    throw 'M9D refused while VictusFanControl.App is already running.'
}

$head=Assert-RepositoryProvenance
Assert-ExactTarget
Assert-PowerSane
Assert-ServiceBaseline
Assert-NoExistingM9DMarkers

if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9D refuses retained watchdog journal evidence; it was not deleted.'
}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host 'VictusFanControl - HP 8C40 M9D FULL-GUI PRODUCTION RECOVERY/LIFECYCLE' -ForegroundColor Cyan
Write-Host 'WRITE-CAPABLE ONLY AFTER THE VERSIONED M9D AUTHORIZATION BARRIER.' -ForegroundColor Yellow
Write-Host 'Subcycle D1 force-kills the exact GUI; D2 requires user-initiated Modern Standby.' -ForegroundColor Yellow
Write-Host ''

try {
    Write-Host 'Step 1: same-HEAD build + M5-M9 deterministic/static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "M9D build failed with exit=$LASTEXITCODE."}

    foreach($script in @(
        'test-8c40-m9-production-watchdog-invariants.ps1',
        'test-8c40-m9b-readonly-preflight-invariants.ps1',
        'test-8c40-m9c-production-smoke-invariants.ps1',
        'test-8c40-m9c-harness-invariants.ps1',
        'test-8c40-m9d-production-lifecycle-invariants.ps1',
        'test-8c40-m9d-harness-invariants.ps1',
        'test-8c40-m9d-evidence-packaging.ps1'
    )){
        & (Join-Path $PSScriptRoot $script)
        if($LASTEXITCODE -ne 0){throw "M9D regression '$script' failed with exit=$LASTEXITCODE."}
    }

    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'M9D SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'M9D coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'M9D HP backend/gate self-test failed.'}

    Write-Host 'Step 2: stable firmware/service baseline...' -ForegroundColor Cyan
    Assert-StableFirmwareOwned 'M9D baseline' $baselineFfPath
    Assert-ServiceBaseline
    Assert-PowerSane

    $confirm=Read-Host "Type exactly $token to authorize this one versioned two-subcycle M9D physical gate"
    if($confirm -cne $token){throw 'M9D cancelled before service/failsafe/GUI active boundary.'}

    Write-Host 'Step 3: start exact LocalSystem M4 watchdog once for both subcycles...' -ForegroundColor Cyan
    Start-Service -Name $serviceName
    $serviceStarted=$true
    $svc=Get-ServiceState
    if(-not $svc -or $svc.State -ne 'Running' -or [int]$svc.ProcessId -le 0){
        throw 'M9D watchdog service did not reach Running.'
    }
    $watchdogPid=[int]$svc.ProcessId
    $watchdogStartTicks=Get-ProcessStartTicks $watchdogPid
    [void](Wait-M4Ready $watchdogPid $watchdogStartTicks)

    # D1 - full GUI production path + exact controller death.
    Write-Host 'Step 4 / D1: arm failsafe, launch full GUI path, prove OWNED 30/30...' -ForegroundColor Cyan
    $d1LogBoundary=Get-LogBoundary
    $failsafe=Start-M9DFailsafe $failsafeD1Log
    $app=Start-M9DApp
    $d1GuiPid=$app.Id
    $d1GuiStartTicks=[long]$app.StartTime.ToUniversalTime().Ticks
    $ready=Wait-ReadyMarker $app
    Assert-ReadyMarker $ready $app $d1GuiStartTicks $watchdogPid $watchdogStartTicks

    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9D D1 READY exists but OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $d1GuiPid $d1GuiStartTicks

    Preserve-Marker $readyMarker 'm9d-d1-ready.txt'

    if(Test-FailsafeTakeover $failsafeD1Log){
        $d1FailsafeTakeover=$true
        throw 'M9D D1 invalid: independent failsafe took over before controller-death injection.'
    }

    Write-Host ("D1 FORCE-KILL exact GUI PID={0} startTicks={1}. Parent issues no HP restore." -f $d1GuiPid,$d1GuiStartTicks) -ForegroundColor Yellow
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Kill-ExactGui $app $d1GuiStartTicks
    $app.Dispose()
    $app=$null

    if(-not (Wait-JournalGone $ControllerDeathRecoveryTimeoutSeconds)){
        throw "M9D D1 watchdog did not clear journal within $ControllerDeathRecoveryTimeoutSeconds s."
    }
    $watch.Stop()
    $d1RecoverySeconds=$watch.Elapsed.TotalSeconds

    Assert-StableFirmwareOwned 'M9D D1 final' $d1FinalFfPath

    $svc=Get-ServiceState
    if(-not $svc -or [int]$svc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'M9D D1 watchdog PID/creation identity changed during controller-death recovery.'
    }

    $d1Segment=Get-LogSegment $d1LogBoundary $d1LogPath
    Assert-D1ServiceLog $d1Segment $d1GuiPid

    if(Test-FailsafeTakeover $failsafeD1Log){
        $d1FailsafeTakeover=$true
        throw 'M9D D1 safe recovery required the delayed failsafe; integration result is invalid.'
    }
    Stop-FailsafeIfSafe $failsafe
    $failsafe=$null
    $d1Pass=$true

    # D2 - fresh full GUI production path + real user-initiated Modern Standby.
    Write-Host 'Step 5 / D2: fresh baseline and full-GUI lifecycle launch...' -ForegroundColor Cyan
    Assert-NoExistingM9DMarkers
    Assert-StableFirmwareOwned 'M9D D2 baseline' $d2BaselineFfPath
    [void](Wait-M4Ready $watchdogPid $watchdogStartTicks)
    $d2LogBoundary=Get-LogBoundary

    $failsafe=Start-M9DFailsafe $failsafeD2Log
    $app=Start-M9DApp
    $d2GuiPid=$app.Id
    $d2GuiStartTicks=[long]$app.StartTime.ToUniversalTime().Ticks
    $ready=Wait-ReadyMarker $app
    Assert-ReadyMarker $ready $app $d2GuiStartTicks $watchdogPid $watchdogStartTicks

    if(-not (Test-Path -LiteralPath $journalPath)){throw 'M9D D2 READY exists but OWNED journal is missing.'}
    $journal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal $journal $d2GuiPid $d2GuiStartTicks

    Preserve-Marker $readyMarker 'm9d-d2-ready.txt'

    if(Test-FailsafeTakeover $failsafeD2Log){
        $d2FailsafeTakeover=$true
        throw 'M9D D2 invalid: independent failsafe took over before sleep boundary.'
    }

    $kernelBoundary=Get-KernelPowerRecordBoundary
    Write-Host ''
    Write-Host 'D2 ACTIVE LIFECYCLE BOUNDARY.' -ForegroundColor Yellow
    Write-Host 'Type the confirmation below, then use Windows Start -> Power -> Sleep.' -ForegroundColor Yellow
    Write-Host 'Do not use shutdown /h, close the lid, kill GUI/watchdog or start a workload.' -ForegroundColor Yellow
    $sleepConfirm=Read-Host 'Type exactly M9D-SLEEP when ready to initiate Modern Standby manually'
    if($sleepConfirm -cne 'M9D-SLEEP'){throw 'M9D D2 cancelled before user-initiated sleep.'}

    Wait-ResultMarker $app $LifecycleResultTimeoutSeconds
    Assert-D2Markers $d2GuiPid $d2GuiStartTicks

    if(-not $app.WaitForExit(15000)){
        throw 'M9D D2 GUI wrote PASS result but did not exit within 15 s.'
    }
    $app.Refresh()
    if($app.ExitCode -ne 0){throw "M9D D2 GUI final ExitCode=$($app.ExitCode)."}
    $app.Dispose()
    $app=$null

    [void](Wait-ModernStandbyKernelEvidence $kernelBoundary)

    $svc=Get-ServiceState
    if(-not $svc -or [int]$svc.ProcessId -ne $watchdogPid -or
       (Get-ProcessStartTicks $watchdogPid) -ne $watchdogStartTicks){
        throw 'M9D D2 watchdog PID/creation identity changed across Modern Standby.'
    }
    if(Test-Path -LiteralPath $journalPath){throw 'M9D D2 final lifecycle marker exists but durable journal remains.'}

    Assert-StableFirmwareOwned 'M9D D2 final' $d2FinalFfPath
    $d2Segment=Get-LogSegment $d2LogBoundary $d2LogPath
    Assert-D2ServiceLog $d2Segment $d2GuiPid

    if(Test-FailsafeTakeover $failsafeD2Log){
        $d2FailsafeTakeover=$true
        throw 'M9D D2 safe lifecycle closure required the delayed failsafe; integration result is invalid.'
    }
    Stop-FailsafeIfSafe $failsafe
    $failsafe=$null

    Preserve-Marker $preSleepMarker 'm9d-d2-presleep.txt'
    Preserve-Marker $resumeMarker 'm9d-d2-resume-gate.txt'
    Preserve-Marker $reentryMarker 'm9d-d2-reentry.txt'
    Preserve-Marker $resultMarker 'm9d-d2-result.txt'
    $d2Pass=$true

    Write-Host 'Step 6: final independent firmware/service closure...' -ForegroundColor Cyan
    if(Test-Path -LiteralPath $journalPath){throw 'M9D final closure found retained journal.'}
    $finalJournalAbsent=$true
    Assert-StableFirmwareOwned 'M9D final' $cleanupFfPath
    $finalFirmwareProofPass=$true

    Stop-Service -Name $serviceName -Force
    $serviceStarted=$false
    $svc=Get-ServiceState
    if(-not $svc -or $svc.State -ne 'Stopped' -or $svc.StartMode -ne 'Manual' -or
       [int]$svc.ProcessId -ne 0 -or [string]$svc.StartName -notmatch 'LocalSystem|Local System'){
        throw 'M9D final M4 baseline is not Manual/Stopped/PID0/LocalSystem.'
    }
    $finalServiceBaselinePass=$true

    $pass=$d1Pass -and $d2Pass -and $finalJournalAbsent -and
          $finalFirmwareProofPass -and $finalServiceBaselinePass -and
          -not $d1FailsafeTakeover -and -not $d2FailsafeTakeover

    if(-not $pass){throw 'M9D internal final PASS predicate was not satisfied.'}
    Write-Host 'PASS: M9D D1 controller-death and D2 Modern Standby last-mile integration closed.' -ForegroundColor Green
}
catch {
    $failure=$_.Exception.Message
    Write-Host ("M9D FAIL_CLOSED: {0}" -f $failure) -ForegroundColor Red
}
finally {
    if($app){
        try {
            $app.Refresh()
            if(-not $app.HasExited){
                $liveTicks=[long]$app.StartTime.ToUniversalTime().Ticks
                Write-Warning ("M9D cleanup is terminating exact GUI PID={0} ticks={1}; watchdog owns any retained lease recovery." -f $app.Id,$liveTicks)
                $app.Kill()
                try{[void]$app.WaitForExit(5000)}catch{}
            }
        } catch {}
        try{$app.Dispose()}catch{}
        $app=$null
    }

    foreach($pair in @(
        @($readyMarker,'m9d-unexpected-ready-final.txt'),
        @($preSleepMarker,'m9d-unexpected-presleep-final.txt'),
        @($resumeMarker,'m9d-unexpected-resume-final.txt'),
        @($reentryMarker,'m9d-unexpected-reentry-final.txt'),
        @($resultMarker,'m9d-unexpected-result-final.txt')
    )){
        try {
            if(Test-Path -LiteralPath $pair[0]){
                $dest=Join-Path $evidenceRoot $pair[1]
                if(-not (Test-Path -LiteralPath $dest)){
                    Copy-Item -LiteralPath $pair[0] -Destination $dest
                }
            }
        } catch {}
    }

    if(Test-Path -LiteralPath $journalPath){
        try {
            if(-not (Test-Path -LiteralPath $retainedLeasePath)){
                Copy-Item -LiteralPath $journalPath -Destination $retainedLeasePath
            }
        } catch {}

        $svc=Get-ServiceState
        if(-not $svc -or $svc.State -ne 'Running'){
            Write-Warning 'M9D cleanup: retained journal exists; starting qualified recovery service without deleting evidence.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            $serviceStarted=$true
        }
        [void](Wait-JournalGone 30)
    }

    $finalJournalAbsent=(-not (Test-Path -LiteralPath $journalPath))

    if($finalJournalAbsent){
        try {
            Assert-StableFirmwareOwned 'M9D cleanup' $cleanupFfPath
            $finalFirmwareProofPass=$true
        } catch {
            Write-Warning "M9D cleanup FF/FF proof failed: $($_.Exception.Message)"
            $finalFirmwareProofPass=$false
        }

        if($failsafe){
            try{Stop-FailsafeIfSafe $failsafe}catch{Write-Warning $_.Exception.Message}
            $failsafe=$null
        }

        try {
            $svc=Get-ServiceState
            if($svc -and $svc.State -ne 'Stopped'){
                Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            }
            $svc=Get-ServiceState
            $finalServiceBaselinePass=
                $svc -and $svc.State -eq 'Stopped' -and $svc.StartMode -eq 'Manual' -and
                [int]$svc.ProcessId -eq 0 -and [string]$svc.StartName -match 'LocalSystem|Local System'
        } catch {$finalServiceBaselinePass=$false}
    }
    else {
        $pass=$false
        Write-Host 'CRITICAL: M9D durable ownership evidence remains and was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Qualified watchdog/failsafe recovery is left available; do not run another write gate.' -ForegroundColor Red
    }

    if(Test-FailsafeTakeover $failsafeD1Log){$d1FailsafeTakeover=$true;$pass=$false}
    if(Test-FailsafeTakeover $failsafeD2Log){$d2FailsafeTakeover=$true;$pass=$false}

    $pass=$pass -and $d1Pass -and $d2Pass -and $finalJournalAbsent -and
          $finalFirmwareProofPass -and $finalServiceBaselinePass -and
          -not $d1FailsafeTakeover -and -not $d2FailsafeTakeover

    try {Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure}
    catch {
        $pass=$false
        $failure="M9D summary write failed: $($_.Exception.Message)"
    }

    try {
        $package=& $packagingScript -EvidenceRoot $evidenceRoot -RepoRoot $repoRoot -WatchdogLogPath $serviceLog -WatchdogStatusPath $statusPath -WatchdogJournalPath $journalPath
        $packagePath=[string]$package.ZipPath
        $packageSha256=[string]$package.ZipSha256
        Write-HarnessSummary $(if($pass){'PASS'}else{'FAIL_CLOSED'}) $failure
        Write-Host ("M9D evidence ZIP: {0}" -f $packagePath)
        Write-Host ("M9D ZIP SHA256 : {0}" -f $packageSha256)
    }
    catch {
        $pass=$false
        $packFailure="M9D evidence packaging failed: $($_.Exception.Message)"
        $failure=$(if([string]::IsNullOrWhiteSpace($failure)){$packFailure}else{"$failure | $packFailure"})
        try{Write-HarnessSummary 'FAIL_CLOSED' $failure}catch{}
    }
}

if(-not $pass){throw "M9D FAILED: $failure"}

Write-Host ''
Write-Host 'PASS: HP 8C40 M9D full-GUI production recovery/lifecycle qualification fully closed.' -ForegroundColor Green
Write-Host 'D1 proved exact GUI owner-death watchdog recovery; D2 proved display-aware Modern Standby release/resume/re-entry on the normal production construction surfaces. Production promotion still remains a separate commit.' -ForegroundColor Green
