param(
    [Parameter(Mandatory=$true)][string]$RcArtifactZipPath
)

$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$contractPath=Join-Path $repoRoot 'release\p15-target-checkpoint.json'
$contract=Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json

# P15A AUTHORIZATION BARRIER. Keep this before Administrator/CIM/EC/process work.
if(-not [bool]$contract.startupNoWrite.executionAuthorized){
    throw 'P15A STARTUP/NO-WRITE BLOCKED: executionAuthorized=false. Close preparation CI first, then use a separate explicit authorization commit.'
}
if([bool]$contract.manual30.executionAuthorized -or [bool]$contract.automatic.executionAuthorized){
    throw 'P15A refuses overlapping Manual/Automatic authorization.'
}

$expectedBranch=[string]$contract.startupNoWrite.expectedBranch
$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$p14Path=Join-Path $repoRoot 'release\p14-software-rc.json'
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$appLogPath=Join-Path $env:LOCALAPPDATA ('VictusFanControl\logs\events-{0}.log' -f (Get-Date -Format 'yyyy-MM-dd'))
$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ('logs\p15a-startup-no-write_{0}' -f $stamp)
$resultPath=Join-Path $evidenceRoot 'p15a-startup-result.json'
$sessionLogPath=Join-Path $evidenceRoot 'app-session.log'
$setpointPath=Join-Path $evidenceRoot 'setpoint-samples.json'
$servicePath=Join-Path $evidenceRoot 'service-snapshots.json'
$artifactPath=Join-Path $evidenceRoot 'rc-artifact-identity.json'
$packager=Join-Path $PSScriptRoot 'package-p15-startup-evidence.ps1'
$payloadResolver=Join-Path $PSScriptRoot 'expand-p15-rc-payload.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P15A-'+[Guid]::NewGuid().ToString('N'))
$outerStage=Join-Path $tempRoot 'outer'
$payloadRoot=Join-Path $tempRoot 'payload'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$operatorConfirmationToken='P15A-OBSERVED'
$operatorConfirmationMaxAttempts=3

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'P15A must run from elevated PowerShell.'}
}

function Assert-RepositoryHead {
    if(-not (Get-Command git -ErrorAction SilentlyContinue)){throw 'P15A requires git.'}
    $branch=(& git -C $repoRoot branch --show-current 2>&1 | Out-String).Trim()
    $head=(& git -C $repoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    $upstream=(& git -C $repoRoot rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    $upstreamHead=(& git -C $repoRoot rev-parse '@{u}' 2>&1 | Out-String).Trim()
    $status=(& git -C $repoRoot status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($branch -cne $expectedBranch){throw "P15A requires branch '$expectedBranch'; observed '$branch'."}
    if($head -notmatch '^[0-9a-f]{40}$' -or $upstreamHead -notmatch '^[0-9a-f]{40}$' -or $head -cne $upstreamHead){throw "P15A requires local HEAD == upstream HEAD. local=$head upstream=$upstreamHead"}
    $lines=@($status -split '[\r\n]+' | Where-Object {-not [string]::IsNullOrWhiteSpace($_)})
    $blocking=@($lines | Where-Object {-not $_.StartsWith('?? logs/',[StringComparison]::Ordinal)})
    if($blocking.Count -gt 0){$blocking | ForEach-Object {Write-Host $_};throw 'P15A requires committed source/config state; only untracked logs/ evidence is allowed.'}
    [pscustomobject]@{Branch=$branch;Head=$head;Upstream=$upstream;UpstreamHead=$upstreamHead}
}

function Assert-ExactTarget {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop
    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(([string]$bios.SMBIOSBIOSVersion).Trim(),([string]$bios.Version).Trim()) -join ' | '
    if(([string]$board.Manufacturer).Trim() -cne 'HP' -or ([string]$board.Product).Trim() -cne '8C40' -or ([string]$board.Version).Trim() -cne '63.43' -or ([string]$system.Manufacturer).Trim() -cne 'HP' -or ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or $skuBase -cne '9D0R1LA' -or $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){throw 'P15A exact-target fingerprint mismatch.'}
    [pscustomobject]@{BoardManufacturer=([string]$board.Manufacturer).Trim();BoardProduct=([string]$board.Product).Trim();BoardVersion=([string]$board.Version).Trim();SystemModel=([string]$system.Model).Trim();Sku=$sku;Bios=$biosText}
}

function Get-ServiceSnapshot {
    $svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if(-not $svc){return [pscustomobject]@{Installed=$false;State='Absent';StartMode='Absent';StartName='Absent';ProcessId=0;PathName='';ProcessStartUtcTicks=0}}
    $ticks=0
    if([int]$svc.ProcessId -gt 0){
        try{$p=Get-Process -Id ([int]$svc.ProcessId) -ErrorAction Stop;$ticks=$p.StartTime.ToUniversalTime().Ticks}catch{}
    }
    [pscustomobject]@{Installed=$true;State=[string]$svc.State;StartMode=[string]$svc.StartMode;StartName=[string]$svc.StartName;ProcessId=[int]$svc.ProcessId;PathName=[string]$svc.PathName;ProcessStartUtcTicks=[long]$ticks}
}

function Assert-ServiceCommon([object]$s) {
    if(-not $s.Installed){throw 'P15A requires the already-qualified VictusFanControlWatchdogM4 service.'}
    if($s.StartMode -cne 'Manual'){throw "P15A requires watchdog StartMode Manual; observed '$($s.StartMode)'."}
    if($s.StartName -notmatch '(^|\\)LocalSystem$' -and $s.StartName -cne 'LocalSystem'){throw "P15A requires LocalSystem service account; observed '$($s.StartName)'."}
    foreach($required in @($serviceExe,'--service-name VictusFanControlWatchdogM4','--m4-8c40-lease-service','--modules-dir','--result-path','--log-dir')){
        if($s.PathName.IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){throw "P15A watchdog service command line mismatch; missing '$required' in '$($s.PathName)'."}
    }
}

function Get-ServiceIntegrity {
    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "P15A watchdog executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "P15A watchdog PawnIO module missing: $serviceModule"}
    [pscustomobject]@{
        WatchdogExeSha256=(Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
        PawnIoModuleSha256=(Get-FileHash -LiteralPath $serviceModule -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Assert-ServiceIntegrity([object]$i) {
    if($i.WatchdogExeSha256 -cne ([string]$contract.startupNoWrite.qualifiedInstalledWatchdogExeSha256).ToLowerInvariant()){throw "P15A installed watchdog executable hash mismatch: $($i.WatchdogExeSha256)."}
    if($i.PawnIoModuleSha256 -cne ([string]$contract.startupNoWrite.qualifiedInstalledPawnIoModuleSha256).ToLowerInvariant()){throw "P15A installed watchdog PawnIO module hash mismatch: $($i.PawnIoModuleSha256)."}
}

function Assert-ServiceBaseline([object]$s) {
    Assert-ServiceCommon $s
    if($s.State -cne 'Stopped' -or $s.ProcessId -ne 0){throw "P15A initial watchdog baseline must be Manual/Stopped/PID0; observed $($s.State)/$($s.StartMode)/PID$($s.ProcessId)."}
}

function Assert-ServiceRuntime([object]$s) {
    Assert-ServiceCommon $s
    if($s.State -cne 'Running' -or $s.ProcessId -le 0 -or $s.ProcessStartUtcTicks -le 0){throw "P15A runtime watchdog must be Manual/Running with live PID; observed $($s.State)/$($s.StartMode)/PID$($s.ProcessId)."}
}

$script:setpointEvidence=@()
$script:journalEvidenceDetected=$false

function Read-Setpoint {
    for($attempt=1;$attempt -le 10;$attempt++){
        $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $script:modulesDir 2>&1 | Out-String)
        if($LASTEXITCODE -eq 0){
            $line=($output -split '[\r\n]+' | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
            $m=[regex]::Match([string]$line,'^setpoint CPU=(\d+) GPU=(\d+)$')
            if($m.Success){
                $sample=[pscustomobject]@{TimestampUtc=(Get-Date).ToUniversalTime().ToString('O');Cpu=[int]$m.Groups[1].Value;Gpu=[int]$m.Groups[2].Value;Attempt=$attempt}
                $script:setpointEvidence+=@($sample)
                return $sample
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw 'P15A could not obtain a read-only EC setpoint sample after bounded retries.'
}

function Assert-NoJournal([string]$phase) {
    if(Test-Path -LiteralPath $journalPath){
        $script:journalEvidenceDetected=$true
        throw "P15A watchdog journal evidence detected during $phase."
    }
}

function Get-StrictFirmwareProof([int]$required,[string]$phase) {
    $samples=@()
    for($i=1;$i -le $required;$i++){
        $s=Read-Setpoint
        $samples+=@($s)
        if($s.Cpu -ne 255 -or $s.Gpu -ne 255){throw "P15A observed non-firmware setpoint during $phase sample $i/${required}: $($s.Cpu)/$($s.Gpu)."}
        Assert-NoJournal $phase
        if($i -lt $required){Start-Sleep -Milliseconds 125}
    }
    [pscustomobject]@{Passed=$true;Phase=$phase;RequiredSamples=$required;Samples=$samples}
}

function Get-NewLogLines([int]$skip) {
    if(-not (Test-Path -LiteralPath $appLogPath -PathType Leaf)){return @()}
    @(Get-Content -LiteralPath $appLogPath | Select-Object -Skip $skip)
}

Assert-Administrator
$repoEvidence=Assert-RepositoryHead
$targetEvidence=Assert-ExactTarget
$p14=Get-Content -LiteralPath $p14Path -Raw | ConvertFrom-Json
$profile=Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
if(-not [bool]$p14.productization.finalSoftwareRcAuditClosed -or [string]$p14.status -cne 'P14_5_FINAL_SOFTWARE_RC_AUDIT_CI_PASS_FORMALLY_CLOSED'){throw 'P15A requires formally closed P14.5.'}
if([bool]$profile.control.enabledByDefault -or [bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled){throw 'P15A requires default and automatic control OFF.'}
if([bool]$profile.releaseCandidateP14.manualExecutionAuthorized -or [bool]$profile.releaseCandidateP14.automaticExecutionAuthorized){throw 'P15A requires Manual and Automatic user-control gates closed.'}

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "P15A refused while process '$name' is running."}}
if(-not (Test-Path -LiteralPath $RcArtifactZipPath -PathType Leaf)){throw "P15A RC artifact wrapper missing: $RcArtifactZipPath"}
$outerHash=(Get-FileHash -LiteralPath $RcArtifactZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if($outerHash -cne ([string]$contract.p14Baseline.auditedRcArtifactSha256).ToLowerInvariant()){throw "P15A requires exact audited P14.5 artifact wrapper; observed SHA-256 $outerHash."}

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
New-Item -ItemType Directory -Force -Path $outerStage | Out-Null
New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null
Expand-Archive -LiteralPath $RcArtifactZipPath -DestinationPath $outerStage
$artifactName=[string]$contract.p14Baseline.auditedRcArtifactName
& (Join-Path $PSScriptRoot 'verify-p14-artifact-stage.ps1') -ArtifactDirectory $outerStage -ExpectedSourceHead ([string]$contract.p14Baseline.auditedRcSourceHead) -ExpectedRepository 'Esteban-3055/VictusFanControl' -ExpectedRef 'refs/heads/feature/victus-8c40-p10-p14-software-rc' -ExpectedRunId ([long]$contract.p14Baseline.auditedRcCiRunId) -ExpectedRunNumber ([int]$contract.p14Baseline.auditedRcCiRunNumber) -ExpectedArtifactName $artifactName
$innerZip=Join-Path $outerStage 'VictusFanControl-0.4.0-rc.1-win-x64.zip'
$innerHash=(Get-FileHash -LiteralPath $innerZip -Algorithm SHA256).Hash.ToLowerInvariant()
if($innerHash -cne ([string]$contract.p14Baseline.auditedRcPayloadZipSha256).ToLowerInvariant()){throw 'P15A inner audited RC ZIP SHA-256 mismatch.'}
$payload=& $payloadResolver -InnerZipPath $innerZip -DestinationPath $payloadRoot
$appExe=[string]$payload.AppExe
$script:modulesDir=[string]$payload.ModulesDirectory
if(-not (Test-Path -LiteralPath $appExe -PathType Leaf)){throw 'P15A audited RC GUI executable missing after validated package-root resolution.'}

$serviceBefore=Get-ServiceSnapshot
Assert-ServiceBaseline $serviceBefore
$serviceIntegrityBefore=Get-ServiceIntegrity
Assert-ServiceIntegrity $serviceIntegrityBefore
Assert-NoJournal 'pre-start baseline'

dotnet build (Join-Path $repoRoot 'src\VictusFanControl\VictusFanControl.csproj') -c Release -warnaserror
if($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cli -PathType Leaf)){throw 'P15A read-only probe build failed.'}

$preProof=Get-StrictFirmwareProof ([int]$contract.startupNoWrite.requiredFirmwareSamplesBefore) 'pre-start firmware baseline'
$logSkip=0
if(Test-Path -LiteralPath $appLogPath -PathType Leaf){$logSkip=@(Get-Content -LiteralPath $appLogPath).Count}

$passed=$false;$failure=$null;$app=$null;$serviceDuring=$null;$serviceAfter=$null;$serviceIntegrityDuring=$null;$serviceIntegrityAfter=$null;$duringSamples=@();$postProof=$null;$sessionLines=@();$packagePath=$null;$packageSha256=$null
try {
    $args=@('--modules-dir',('"{0}"' -f $script:modulesDir))
    $app=Start-Process -FilePath $appExe -ArgumentList $args -WorkingDirectory (Split-Path -Parent $appExe) -PassThru
    $deadline=(Get-Date).AddSeconds(75)
    $healthy=$false
    while((Get-Date) -lt $deadline){
        if($app.HasExited){throw "P15A GUI exited before Healthy startup proof; exitCode=$($app.ExitCode)."}
        $sessionLines=Get-NewLogLines $logSkip
        $text=$sessionLines -join [Environment]::NewLine
        if($text.IndexOf('P13 UI: startup mode=Firmware; manualGate=False; automaticGate=False.',[StringComparison]::Ordinal) -ge 0 -and $text.IndexOf('Recovery completed; telemetry is healthy after 3 complete snapshots.',[StringComparison]::Ordinal) -ge 0){$healthy=$true;break}
        Start-Sleep -Milliseconds 500
    }
    if(-not $healthy){throw 'P15A did not observe Firmware/CLOSED gates plus Healthy telemetry within 75 s.'}
    $serviceDuring=Get-ServiceSnapshot;Assert-ServiceRuntime $serviceDuring
    $serviceIntegrityDuring=Get-ServiceIntegrity;Assert-ServiceIntegrity $serviceIntegrityDuring
    Assert-NoJournal 'ordinary GUI startup'
    for($i=1;$i -le [int]$contract.startupNoWrite.requiredFirmwareSamplesDuring;$i++){
        $s=Read-Setpoint;$duringSamples+=@($s)
        if($s.Cpu -ne 255 -or $s.Gpu -ne 255){throw "P15A observed non-firmware setpoint during GUI runtime: $($s.Cpu)/$($s.Gpu)."}
        Assert-NoJournal 'GUI runtime FF/FF sampling'
        Start-Sleep -Milliseconds 400
    }
    Write-Host ''
    Write-Host 'P15A normal GUI startup is Healthy with Firmware mode and CLOSED Manual/Automatic gates.' -ForegroundColor Green
    Write-Host 'Do not select Manual or Automatic. Inspect the UI, then use the tray icon -> Exit.' -ForegroundColor Yellow
    $operatorConfirmed=$false
    for($confirmationAttempt=1;$confirmationAttempt -le $operatorConfirmationMaxAttempts;$confirmationAttempt++){
        $app.Refresh()
        if($app.HasExited){throw "P15A GUI exited before operator confirmation; exitCode=$($app.ExitCode)."}
        $confirm=Read-Host ("Type {0} when you have inspected the normal UI and are ready to exit it from the tray (attempt {1}/{2})" -f $operatorConfirmationToken,$confirmationAttempt,$operatorConfirmationMaxAttempts)
        if($confirm -ceq $operatorConfirmationToken){$operatorConfirmed=$true;break}
        if($confirmationAttempt -lt $operatorConfirmationMaxAttempts){
            Write-Warning ("P15A confirmation did not match the exact token. GUI remains running; retry {0}/{1}." -f ($confirmationAttempt+1),$operatorConfirmationMaxAttempts)
        }
    }
    if(-not $operatorConfirmed){throw "P15A operator observation was not confirmed after $operatorConfirmationMaxAttempts attempts."}
    Write-Host 'Now choose Exit from the VictusFanControl tray menu. The harness will wait up to 120 seconds.' -ForegroundColor Cyan
    $exitDeadline=(Get-Date).AddSeconds(120)
    while(-not $app.HasExited -and (Get-Date) -lt $exitDeadline){Start-Sleep -Milliseconds 500;$app.Refresh()}
    if(-not $app.HasExited){throw 'P15A GUI did not exit normally through the tray within 120 s.'}
    if($app.ExitCode -ne 0){throw "P15A GUI normal exit code was $($app.ExitCode)."}
    $postProof=Get-StrictFirmwareProof ([int]$contract.startupNoWrite.requiredFirmwareSamplesAfter) 'post-exit firmware baseline'
    Assert-NoJournal 'post-exit baseline'
    $serviceAfter=Get-ServiceSnapshot;Assert-ServiceRuntime $serviceAfter
    $serviceIntegrityAfter=Get-ServiceIntegrity;Assert-ServiceIntegrity $serviceIntegrityAfter
    if($serviceDuring.ProcessId -ne $serviceAfter.ProcessId -or $serviceDuring.ProcessStartUtcTicks -ne $serviceAfter.ProcessStartUtcTicks){throw 'P15A watchdog process identity changed during ordinary startup/exit.'}
    $sessionLines=Get-NewLogLines $logSkip
    $sessionText=$sessionLines -join [Environment]::NewLine
    foreach($marker in @('Starting GUI. Modules=','Fan backend:','HP 8C40 production watchdog-backed backend selected through the explicit M9 promotion gate; automatic policy remains OFF.','P13 UI: startup mode=Firmware; manualGate=False; automaticGate=False.','Automatic fan policy is OFF.','Recovery completed; telemetry is healthy after 3 complete snapshots.','GUI exited.')){if($sessionText.IndexOf($marker,[StringComparison]::Ordinal) -lt 0){throw "P15A app log missing marker: $marker"}}
    foreach($forbidden in @('P13 mode request Manual','P13 mode request Automatic','P13 manual request')){if($sessionText.IndexOf($forbidden,[StringComparison]::Ordinal) -ge 0){throw "P15A observed forbidden user-control request in app log: $forbidden"}}
    $passed=$true
} catch {
    $failure=$_.Exception.Message
} finally {
    $sessionLines=Get-NewLogLines $logSkip
    $sessionLines | Set-Content -LiteralPath $sessionLogPath -Encoding UTF8
    @($script:setpointEvidence) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $setpointPath -Encoding UTF8
    [ordered]@{before=$serviceBefore;during=$serviceDuring;after=$serviceAfter;integrityBefore=$serviceIntegrityBefore;integrityDuring=$serviceIntegrityDuring;integrityAfter=$serviceIntegrityAfter;journalEvidenceDetected=$script:journalEvidenceDetected;journalPresentAtEnd=(Test-Path -LiteralPath $journalPath)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $servicePath -Encoding UTF8
    [ordered]@{wrapperPath=[IO.Path]::GetFullPath($RcArtifactZipPath);wrapperSha256=$outerHash;artifactName=$artifactName;auditedSourceHead=[string]$contract.p14Baseline.auditedRcSourceHead;innerZipSha256=$innerHash} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
    $result=[ordered]@{schemaVersion=1;gate='P15A';result=$(if($passed){'PASS'}else{'FAIL_CLOSED'});failure=$failure;timestampUtc=(Get-Date).ToUniversalTime().ToString('O');repository=$repoEvidence;target=$targetEvidence;rcWrapperSha256=$outerHash;rcPayloadZipSha256=$innerHash;startupMode='Firmware';manualExecutionAuthorized=$false;automaticExecutionAuthorized=$false;controlEnabledByDefault=$false;automaticPolicyEnabled=$false;firmwareProofBefore=$preProof;firmwareSetpointDuringPassed=(@($duringSamples).Count -eq [int]$contract.startupNoWrite.requiredFirmwareSamplesDuring -and @($duringSamples | Where-Object {$_.Cpu -ne 255 -or $_.Gpu -ne 255}).Count -eq 0);firmwareProofAfter=$postProof;nonFirmwareSetpointEvidenceDetected=(@($script:setpointEvidence | Where-Object {$_.Cpu -ne 255 -or $_.Gpu -ne 255}).Count -gt 0);watchdogJournalEvidenceDetected=$script:journalEvidenceDetected;watchdogJournalPresentAtEnd=(Test-Path -LiteralPath $journalPath);serviceBefore=$serviceBefore;serviceDuring=$serviceDuring;serviceAfter=$serviceAfter;serviceIntegrityBefore=$serviceIntegrityBefore;serviceIntegrityDuring=$serviceIntegrityDuring;serviceIntegrityAfter=$serviceIntegrityAfter;watchdogLeaseOwnershipEvidenceDetected=$script:journalEvidenceDetected;powerTransitionAttempted=$false;manualOrAutomaticRequestObserved=$false;packagePath=$packagePath;packageSha256=$packageSha256}
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    try{
        $package=& $packager -EvidenceRoot $evidenceRoot -RepositoryRoot $repoRoot
        $packagePath=[string]$package.ZipPath;$packageSha256=[string]$package.ZipSha256
        $result.packagePath=$packagePath;$result.packageSha256=$packageSha256
        $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    }catch{
        if($passed){$passed=$false;$failure='P15A evidence packaging failed: '+$_.Exception.Message;$result.result='FAIL_CLOSED';$result.failure=$failure;$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8}
    }
}

if($passed){Write-Host "P15A STARTUP/NO-WRITE PASS. Evidence ZIP: $packagePath" -ForegroundColor Green;Write-Host "SHA-256: $packageSha256";exit 0}
Write-Error ("P15A FAIL_CLOSED: {0}. Evidence preserved at {1}" -f $failure,$evidenceRoot)
exit 1
