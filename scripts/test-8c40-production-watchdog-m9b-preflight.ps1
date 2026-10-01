$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$expectedBranch='feature/victus-8c40-m9-canonical-prehardware'
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$serviceExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$serviceModule=Join-Path $serviceRoot 'modules\LpcACPIEC.bin'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9b-production-watchdog-preflight_{0}" -f $stamp)
$resultPath=Join-Path $evidenceRoot 'm9b-preflight-result.json'
$telemetryPath=Join-Path $evidenceRoot 'telemetry-output.txt'
$packagingScript=Join-Path $PSScriptRoot 'package-m9b-evidence.ps1'
$packagePath=$null
$packageSha256=$null

$profile=Get-Content $profilePath -Raw | ConvertFrom-Json

# READ-ONLY AUTHORIZATION BARRIER. Keep this before Administrator/CIM/EC work.
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyExecutionAuthorized){
    throw 'M9B READ-ONLY BLOCKED: profile readOnlyExecutionAuthorized=false. Wait for same-HEAD preparation CI and an explicit closure commit.'
}

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M9B read-only preflight must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryHead {
    if(-not (Get-Command git -ErrorAction SilentlyContinue)){
        throw 'M9B preflight requires git to verify branch/HEAD provenance.'
    }

    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    $head=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){
        throw "M9B could not resolve a valid git HEAD. Raw='$head'"
    }

    $statusText=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw 'M9B could not inspect working-tree state.'}

    $statusLines=@($statusText -split "[\r\n]+" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $preservedEvidence=@($statusLines | Where-Object { $_.StartsWith('?? logs/',[StringComparison]::Ordinal) })
    $blockingStatus=@($statusLines | Where-Object { -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal) })

    if($preservedEvidence.Count -gt 0){
        Write-Host 'Preserved untracked evidence under logs/ is allowed and will not be deleted:' -ForegroundColor DarkYellow
        $preservedEvidence | ForEach-Object { Write-Host ("  {0}" -f $_) }
    }

    if($blockingStatus.Count -gt 0){
        $blockingStatus | ForEach-Object { Write-Host $_ }
        throw 'M9B requires committed source/config state; only untracked evidence below logs/ is allowed.'
    }

    if($branch -cne $expectedBranch){
        throw "M9B requires branch '$expectedBranch'; observed '$branch'."
    }

    $upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)){
        throw 'M9B requires a configured upstream branch.'
    }

    $upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $upstreamHead -notmatch '^[0-9a-f]{40}$'){
        throw "M9B could not resolve upstream HEAD for '$upstream'."
    }

    if($head -cne $upstreamHead){
        throw "M9B requires local HEAD to match tracked upstream. local=$head upstream=$upstreamHead"
    }

    [pscustomobject]@{Branch=$branch;Head=$head;Upstream=$upstream;UpstreamHead=$upstreamHead}
}

function Assert-Exact8C40Target {
    $board=Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios=Get-CimInstance Win32_BIOS -ErrorAction Stop

    $boardManufacturer=([string]$board.Manufacturer).Trim()
    $boardProduct=([string]$board.Product).Trim()
    $boardVersion=([string]$board.Version).Trim()
    $systemManufacturer=([string]$system.Manufacturer).Trim()
    $systemModel=([string]$system.Model).Trim()
    $sku=([string]$system.SystemSKUNumber).Trim()
    $skuBase=($sku -split '#',2)[0].Trim()
    $biosText=@(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    if($boardManufacturer -cne 'HP' -or
       $boardProduct -cne '8C40' -or
       $boardVersion -cne '63.43' -or
       $systemManufacturer -cne 'HP' -or
       $systemModel -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M9B exact-target fingerprint mismatch.'
    }

    [pscustomobject]@{
        BoardManufacturer=$boardManufacturer
        BoardProduct=$boardProduct
        BoardVersion=$boardVersion
        SystemManufacturer=$systemManufacturer
        SystemModel=$systemModel
        Sku=$sku
        Bios=$biosText
    }
}

function Assert-ProfileBoundary {
    $current=Get-Content $profilePath -Raw | ConvertFrom-Json

    if(-not [bool]$current.loadThermalM8Qualification.m8c.physicalPassed){
        throw 'M9B requires recorded M8C physical PASS.'
    }
    if([bool]$current.loadThermalM8Qualification.m8c.physicalExecutionAuthorized){
        throw 'M9B requires M8C physical execution to remain re-blocked.'
    }
    if(-not [bool]$current.lifecycle.watchdogM9CodeCiPassed){
        throw 'M9B requires M9A CODE/CI PASS.'
    }
    if([bool]$current.lifecycle.watchdogRecoveryValidated){
        throw 'M9B refuses WatchdogRecoveryValidated=true before M9 promotion.'
    }
    if([bool]$current.control.enabledByDefault -or
       [bool]$current.loadThermalM8Qualification.automaticPolicyEnabled){
        throw 'M9B requires default and automatic/adaptive control OFF.'
    }
    if([bool]$current.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized){
        throw 'M9B requires production watchdog construction to remain blocked.'
    }
    if(-not [bool]$current.watchdogM9ProductionIntegration.m9b.readOnlyExecutionAuthorized){
        throw 'M9B read-only execution authorization is not recorded.'
    }
    if([bool]$current.watchdogM9ProductionIntegration.m9b.physicalWriteAuthorized){
        throw 'M9B must never authorize a write-capable physical run.'
    }

    return $current
}

function Get-PowerSnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $status=[System.Windows.Forms.SystemInformation]::PowerStatus
    $percent=$null
    if($status.BatteryLifePercent -ge 0){
        $percent=[math]::Round([double]$status.BatteryLifePercent*100,0)
    }

    [pscustomobject]@{
        PowerLineStatus=[string]$status.PowerLineStatus
        BatteryPercent=$percent
        BatteryChargeStatus=[string]$status.BatteryChargeStatus
    }
}

function Assert-AcBatterySane {
    $power=Get-PowerSnapshot
    if($power.PowerLineStatus -cne 'Online'){throw "M9B requires AC online; observed '$($power.PowerLineStatus)'."}
    if($null -eq $power.BatteryPercent){throw 'M9B requires readable battery percentage.'}
    if([double]$power.BatteryPercent -lt 20){throw "M9B requires at least 20% battery; observed $($power.BatteryPercent)%."}
    return $power
}

function Get-M4ServiceSnapshot {
    $svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if(-not $svc){
        return [pscustomobject]@{Installed=$false;State='Absent';StartMode='Absent';StartName='Absent';ProcessId=0;PathName=''}
    }

    [pscustomobject]@{
        Installed=$true
        State=[string]$svc.State
        StartMode=[string]$svc.StartMode
        StartName=[string]$svc.StartName
        ProcessId=[int]$svc.ProcessId
        PathName=[string]$svc.PathName
    }
}

function Assert-M4ServiceBaseline([object]$snapshot) {
    if(-not $snapshot.Installed){throw 'M9B requires the physically qualified VictusFanControlWatchdogM4 service to be installed.'}
    if($snapshot.State -cne 'Stopped'){throw "M9B requires M4 Stopped; observed '$($snapshot.State)'."}
    if($snapshot.StartMode -cne 'Manual'){throw "M9B requires M4 Manual; observed '$($snapshot.StartMode)'."}
    if($snapshot.ProcessId -ne 0){throw "M9B requires service PID 0 while stopped; observed $($snapshot.ProcessId)."}
    if($snapshot.StartName -notmatch '(^|\\)LocalSystem$' -and $snapshot.StartName -cne 'LocalSystem'){
        throw "M9B requires LocalSystem service account; observed '$($snapshot.StartName)'."
    }

    foreach($required in @(
        $serviceExe,
        '--service-name VictusFanControlWatchdogM4',
        '--m4-8c40-lease-service',
        '--modules-dir',
        '--result-path',
        '--log-dir'
    )){
        if($snapshot.PathName.IndexOf($required,[StringComparison]::OrdinalIgnoreCase) -lt 0){
            throw "M9B service binary/configuration mismatch; missing '$required' in PathName='$($snapshot.PathName)'."
        }
    }

    if(-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)){throw "M9B service executable missing: $serviceExe"}
    if(-not (Test-Path -LiteralPath $serviceModule -PathType Leaf)){throw "M9B service PawnIO module missing: $serviceModule"}
}

function Assert-ServiceUnchanged([object]$before) {
    $after=Get-M4ServiceSnapshot
    foreach($property in @('Installed','State','StartMode','StartName','ProcessId','PathName')){
        if($before.$property -cne $after.$property){
            throw "M9B mutated M4 service property '$property': before='$($before.$property)' after='$($after.$property)'."
        }
    }
    return $after
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){throw "M9B read-only setpoint probe failed. Raw output: $output"}

    $line=($output -split "[\r\n]+" | Where-Object { $_ -match '^setpoint CPU=' } | Select-Object -Last 1)
    if(-not $line){throw "M9B could not parse setpoint probe. Raw output: $output"}
    $match=[regex]::Match($line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){throw "M9B could not parse setpoints from '$line'."}

    [pscustomobject]@{
        TimestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        Cpu=[int]$match.Groups[1].Value
        Gpu=[int]$match.Groups[2].Value
        Raw=$line
    }
}

function Assert-StableFirmwareBaseline {
    $samples=@()
    $consecutive=0
    for($i=1;$i -le 8;$i++){
        $sample=Read-8C40Setpoint
        $samples+=@($sample)

        if($sample.Cpu -eq 255 -and $sample.Gpu -eq 255){
            $consecutive++
            if($consecutive -ge 2){
                return [pscustomobject]@{Passed=$true;Samples=$samples}
            }
        } else {
            $consecutive=0
        }

        Start-Sleep -Milliseconds 75
    }

    throw 'M9B could not prove two consecutive independent FF/FF samples.'
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M9B PRODUCTION WATCHDOG READ-ONLY PREFLIGHT' -ForegroundColor Cyan
Write-Host 'NO fan write / firmware restore / watchdog lease / service mutation / power transition.' -ForegroundColor Yellow
Write-Host ''

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){
        throw "M9B refused while process '$name' is running."
    }
}

$passed=$false
$failure=$null
$repoEvidence=$null
$targetEvidence=$null
$profileEvidence=$null
$powerBefore=$null
$powerAfter=$null
$serviceBefore=$null
$serviceAfter=$null
$ffProof=$null
$telemetryOutput=$null
$terminalFailure=$null
$serviceExeSha256=$null
$serviceModuleSha256=$null
$profileSha256=$null

try {
    Write-Host 'Step 1: repository + target + profile boundary...' -ForegroundColor Cyan
    $repoEvidence=Assert-RepositoryHead
    $targetEvidence=Assert-Exact8C40Target
    $profileEvidence=Assert-ProfileBoundary

    New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

    Write-Host 'Step 2: AC/battery + installed M4 service/journal baseline...' -ForegroundColor Cyan
    $powerBefore=Assert-AcBatterySane
    if(Test-Path -LiteralPath $journalPath){
        Get-Content -LiteralPath $journalPath
        throw 'M9B refuses retained watchdog journal evidence.'
    }
    $serviceBefore=Get-M4ServiceSnapshot
    Assert-M4ServiceBaseline $serviceBefore

    $serviceExeSha256=(Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $serviceModuleSha256=(Get-FileHash -LiteralPath $serviceModule -Algorithm SHA256).Hash.ToLowerInvariant()
    $profileSha256=(Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Host 'Step 3: build + M5-M9 deterministic/static regressions...' -ForegroundColor Cyan
    dotnet build .\VictusFanControl.sln -c Release -warnaserror
    if($LASTEXITCODE -ne 0){throw "M9B build failed with exit code $LASTEXITCODE."}

    foreach($invariant in @(
        'test-watchdog-m5a-8c40-invariants.ps1',
        'test-watchdog-m5b-8c40-invariants.ps1',
        'test-watchdog-m5c-preflight-invariants.ps1',
        'test-watchdog-m5c-double-death-invariants.ps1',
        'test-watchdog-m5d-write-armed-invariants.ps1',
        'test-watchdog-m5e-write-armed-double-death-invariants.ps1',
        'test-8c40-modern-standby-m6-invariants.ps1',
        'test-8c40-modern-standby-m6-harness-invariants.ps1',
        'test-8c40-hibernation-m7-invariants.ps1',
        'test-8c40-hibernation-m7-harness-invariants.ps1',
        'test-8c40-load-thermal-m8-invariants.ps1',
        'test-8c40-m8-production-race-audit-invariants.ps1',
        'test-8c40-m9-production-watchdog-invariants.ps1',
        'test-8c40-m9b-readonly-preflight-invariants.ps1'
    )){
        & (Join-Path $PSScriptRoot $invariant)
        if($LASTEXITCODE -ne 0){throw "M9B invariant '$invariant' failed with exit code $LASTEXITCODE."}
    }

    dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m4-8c40-self-test
    if($LASTEXITCODE -ne 0){throw 'M9B M4 watchdog self-test failed.'}
    dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-c-self-test
    if($LASTEXITCODE -ne 0){throw 'M9B Gate C self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
    if($LASTEXITCODE -ne 0){throw 'M9B SafetyGate self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
    if($LASTEXITCODE -ne 0){throw 'M9B coordinator self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --bios-contract-self-test
    if($LASTEXITCODE -ne 0){throw 'M9B BIOS contract self-test failed.'}
    dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
    if($LASTEXITCODE -ne 0){throw 'M9B HP backend/M9 gate self-test failed.'}

    Write-Host 'Step 4: read-only telemetry/SafetyGate readiness...' -ForegroundColor Cyan
    $telemetryOutput=(& dotnet $cli --8c40-m8-preflight-probe --modules-dir $modulesDir 2>&1 | Out-String)
    $telemetryOutput | Set-Content -LiteralPath $telemetryPath -Encoding UTF8
    Write-Host $telemetryOutput.TrimEnd()
    if($LASTEXITCODE -ne 0){throw "M9B read-only telemetry probe failed with exit code $LASTEXITCODE."}
    if($telemetryOutput.IndexOf('M8_PREFLIGHT_TELEMETRY_PASS',[StringComparison]::Ordinal) -lt 0){
        throw 'M9B telemetry probe did not emit the required read-only PASS marker.'
    }

    Write-Host 'Step 5: independent stable FF/FF firmware-owned proof...' -ForegroundColor Cyan
    $ffProof=Assert-StableFirmwareBaseline

    Write-Host 'Step 6: prove no journal/service/repository/power mutation...' -ForegroundColor Cyan
    if(Test-Path -LiteralPath $journalPath){throw 'M9B unexpectedly created a watchdog journal.'}
    $serviceAfter=Assert-ServiceUnchanged $serviceBefore
    $powerAfter=Assert-AcBatterySane

    $headAfter=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $headAfter -cne $repoEvidence.Head){
        throw "M9B repository HEAD changed during execution. before=$($repoEvidence.Head) after=$headAfter"
    }

    $passed=$true
}
catch {
    $failure=$_.Exception.Message
    $terminalFailure=$_.Exception
}
finally {
    if(Test-Path -LiteralPath $evidenceRoot){
        $resultObject=[ordered]@{
            schemaVersion=1
            gate='M9B'
            result=$(if($passed){'PASS'}else{'FAIL_CLOSED'})
            failure=$failure
            timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
            repository=$repoEvidence
            target=$targetEvidence
            powerBefore=$powerBefore
            powerAfter=$powerAfter
            serviceBefore=$serviceBefore
            serviceAfter=$serviceAfter
            serviceExeSha256=$serviceExeSha256
            serviceModuleSha256=$serviceModuleSha256
            profileSha256=$profileSha256
            journalPresent=(Test-Path -LiteralPath $journalPath)
            firmwareProof=$ffProof
            telemetryEvidencePath=$telemetryPath
            watchdogRecoveryValidated=$false
            productionConstructionAuthorized=$false
            automaticPolicyEnabled=$false
            controlEnabledByDefault=$false
            fanWriteAttempted=$false
            firmwareRestoreAttempted=$false
            watchdogLeaseAttempted=$false
            serviceMutationAttempted=$false
            packagePath=$packagePath
            packageSha256=$packageSha256
        }

        $resultObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8

        try {
            $package=& $packagingScript -EvidenceRoot $evidenceRoot -RepositoryRoot $repoRoot
            $packagePath=[string]$package.ZipPath
            $packageSha256=[string]$package.ZipSha256

            $resultObject.packagePath=$packagePath
            $resultObject.packageSha256=$packageSha256
            $resultObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8
        }
        catch {
            $packagingMessage="M9B evidence packaging failed: $($_.Exception.Message)"
            $passed=$false
            if([string]::IsNullOrWhiteSpace($failure)){
                $failure=$packagingMessage
                $terminalFailure=$_.Exception
            } else {
                $failure="$failure | $packagingMessage"
            }

            $resultObject.result='FAIL_CLOSED'
            $resultObject.failure=$failure
            $resultObject.packagePath=$null
            $resultObject.packageSha256=$null
            $resultObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8
        }
    }
}

if(-not $passed){
    throw $(if([string]::IsNullOrWhiteSpace($failure)){'M9B FAIL_CLOSED.'}else{$failure})
}

Write-Host ''
Write-Host 'PASS: HP 8C40 M9B production-watchdog read-only preflight completed.' -ForegroundColor Green
Write-Host ("Evidence result: {0}" -f $resultPath) -ForegroundColor Green
Write-Host ("Evidence ZIP   : {0}" -f $packagePath) -ForegroundColor Green
Write-Host ("ZIP SHA256     : {0}" -f $packageSha256) -ForegroundColor Green
Write-Host 'No fan write, firmware restore, watchdog lease, service mutation or power transition was performed.' -ForegroundColor Green
