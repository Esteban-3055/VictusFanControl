$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$expectedBranch='feature/victus-8c40-m8b-retry5'
$serviceName='VictusFanControlWatchdogM4'
$journalPath=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'
$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'

function Assert-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        throw 'M8 preflight must run from an elevated PowerShell.'
    }
}

function Assert-RepositoryHead {
    if(-not (Get-Command git -ErrorAction SilentlyContinue)){
        throw 'M8 preflight requires git to verify branch/HEAD provenance.'
    }

    $branch=(& git branch --show-current 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)){
        throw 'M8 preflight could not resolve the current git branch.'
    }

    $head=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$'){
        throw "M8 preflight could not resolve a valid git HEAD. Raw='$head'"
    }

    $statusText=(& git status --porcelain=v1 --untracked-files=all 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){
        throw 'M8 preflight could not inspect repository working-tree state.'
    }

    $statusLines=@(
        $statusText -split "[\r\n]+" |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    $preservedEvidence=@(
        $statusLines |
            Where-Object { $_.StartsWith('?? logs/',[StringComparison]::Ordinal) }
    )

    $blockingStatus=@(
        $statusLines |
            Where-Object { -not $_.StartsWith('?? logs/',[StringComparison]::Ordinal) }
    )

    if($preservedEvidence.Count -gt 0){
        Write-Host 'Preserved untracked historical evidence under logs/ is allowed and will not be deleted:' -ForegroundColor DarkYellow
        $preservedEvidence | ForEach-Object { Write-Host ("  {0}" -f $_) }
    }

    if($blockingStatus.Count -gt 0){
        $blockingStatus | ForEach-Object { Write-Host $_ }
        throw 'M8 preflight requires committed source/config state; only untracked historical evidence under logs/ is allowed.'
    }

    if($branch -cne $expectedBranch){
        throw "M8 preflight requires branch '$expectedBranch'; observed '$branch'."
    }

    $upstream=(& git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)){
        throw 'M8 preflight requires a configured upstream branch; run the versioned workflow from the tracked qualification branch.'
    }

    $upstreamHead=(& git rev-parse '@{u}' 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $upstreamHead -notmatch '^[0-9a-f]{40}$'){
        throw "M8 preflight could not resolve upstream HEAD for '$upstream'."
    }

    if($head -cne $upstreamHead){
        throw "M8 preflight requires local HEAD to match tracked upstream. local=$head upstream=$upstreamHead"
    }

    Write-Host ("Git branch : {0}" -f $branch)
    Write-Host ("Git HEAD   : {0}" -f $head)
    Write-Host ("Git upstream: {0} @ {1}" -f $upstream,$upstreamHead)

    return $head
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

    Write-Host ("Board : {0} / {1} / {2}" -f $boardManufacturer,$boardProduct,$boardVersion)
    Write-Host ("System: {0} / {1}" -f $systemManufacturer,$systemModel)
    Write-Host ("SKU   : {0}" -f $sku)
    Write-Host ("BIOS  : {0}" -f $biosText)

    if($boardManufacturer -cne 'HP' -or
       $boardProduct -cne '8C40' -or
       $boardVersion -cne '63.43' -or
       $systemManufacturer -cne 'HP' -or
       $systemModel -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
       $skuBase -cne '9D0R1LA' -or
       $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)'){
        throw 'M8 preflight exact-target fingerprint mismatch.'
    }
}

function Assert-ProfileBoundary {
    $profile=Get-Content $profilePath -Raw | ConvertFrom-Json

    if(-not [bool]$profile.lifecycle.modernStandbyM6PhysicalPassed){
        throw 'M8 preflight requires M6 physical PASS in the target profile.'
    }

    if(-not [bool]$profile.lifecycle.hibernationM7PhysicalPassed){
        throw 'M8 preflight requires M7 physical PASS in the target profile.'
    }

    if([bool]$profile.lifecycle.watchdogRecoveryValidated){
        throw 'M8 preflight refuses a profile that already sets WatchdogRecoveryValidated=true.'
    }

    if([bool]$profile.control.enabledByDefault){
        throw 'M8 preflight refuses automatic/adaptive control enabled by default.'
    }

    if([int]$profile.control.validatedMinimumLevel -ne 10 -or
       [int]$profile.control.validatedMaximumLevel -ne 50 -or
       [bool]$profile.control.supportsIndependentLevels){
        throw 'M8 preflight requires the unchanged equal-only 10..50 production envelope.'
    }

    if([string]$profile.loadThermalM8Qualification.targetProfileId -cne 'HP-8C40-9D0R1LA-F18'){
        throw 'M8 profile qualification target identity is missing or changed.'
    }

    if([string]$profile.loadThermalM8Qualification.noWritePreflightScript -cne 'scripts/test-8c40-load-thermal-m8-preflight.ps1'){
        throw 'M8 profile does not point to the versioned no-write preflight script.'
    }

    Write-Host ("Profile status: {0}" -f $profile.status)
    Write-Host 'Profile boundary: M6 PASS / M7 PASS / watchdog recovery false / automatic policy OFF / equal-only 10..50.'
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
    Write-Host ("Power : AC={0}, battery={1}%, chargeStatus={2}" -f
        $power.PowerLineStatus,
        $(if($null -eq $power.BatteryPercent){'unknown'}else{$power.BatteryPercent}),
        $power.BatteryChargeStatus)

    if($power.PowerLineStatus -cne 'Online'){
        throw "M8 preflight requires AC online; observed '$($power.PowerLineStatus)'."
    }

    if($null -eq $power.BatteryPercent){
        throw 'M8 preflight requires a readable battery percentage on this target.'
    }

    if([double]$power.BatteryPercent -lt 20){
        throw "M8 preflight requires at least 20% battery; observed $($power.BatteryPercent)%."
    }

    return $power
}

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){
        throw "HP 8C40 read-only setpoint probe failed. Raw output: $output"
    }

    $line=($output -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1)

    if(-not $line){
        throw "Could not parse HP 8C40 setpoint probe. Raw output: $output"
    }

    $match=[regex]::Match($line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $match.Success){
        throw "Could not parse HP 8C40 setpoint line: $line"
    }

    [pscustomobject]@{
        Cpu=[int]$match.Groups[1].Value
        Gpu=[int]$match.Groups[2].Value
        Raw=$line
    }
}

function Assert-StableFirmwareBaseline {
    $consecutive=0
    $last=$null

    for($sample=1;$sample -le 8;$sample++){
        $last=Read-8C40Setpoint
        Write-Host ("EC sample {0}/8: {1}" -f $sample,$last.Raw)

        if($last.Cpu -eq 255 -and $last.Gpu -eq 255){
            $consecutive++
            if($consecutive -ge 2){
                return
            }
        }else{
            $consecutive=0
        }

        Start-Sleep -Milliseconds 75
    }

    throw "M8 preflight could not prove two consecutive independent FF/FF samples. Last=$($last.Raw)"
}

function Get-M4ServiceSnapshot {
    $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if(-not $svc){
        return [pscustomobject]@{
            Installed=$false
            Status='Absent'
            StartType='Absent'
        }
    }

    [pscustomobject]@{
        Installed=$true
        Status=[string]$svc.Status
        StartType=[string]$svc.StartType
    }
}

function Assert-M4ServiceBaseline([object]$snapshot) {
    if(-not $snapshot.Installed){
        Write-Host 'M4 service: not currently installed (acceptable for no-write preflight).'
        return
    }

    Write-Host ("M4 service: StartType={0}, Status={1}" -f $snapshot.StartType,$snapshot.Status)

    if($snapshot.Status -cne 'Stopped'){
        throw 'M8 no-write preflight requires the M4 qualification service stopped; it will not stop it automatically.'
    }

    if($snapshot.StartType -cne 'Manual'){
        throw "M8 no-write preflight requires M4 Manual startup; observed $($snapshot.StartType)."
    }
}

function Assert-ServiceUnchanged([object]$before) {
    $after=Get-M4ServiceSnapshot

    if($before.Installed -ne $after.Installed -or
       $before.Status -cne $after.Status -or
       $before.StartType -cne $after.StartType){
        throw ("M8 no-write preflight mutated M4 service state. before={0}/{1}/{2} after={3}/{4}/{5}" -f
            $before.Installed,$before.StartType,$before.Status,
            $after.Installed,$after.StartType,$after.Status)
    }
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M8 LOAD/THERMAL NO-WRITE PREFLIGHT' -ForegroundColor Cyan
Write-Host ''
Write-Host 'No fan write, firmware restore, watchdog lease, service mutation, sleep transition or deliberate stress load is performed.' -ForegroundColor Yellow
Write-Host ''

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){
        throw "M8 preflight refused while '$name' is running."
    }
}

Write-Host 'Step 1: repository provenance + exact target/profile boundary...' -ForegroundColor Cyan
$head=Assert-RepositoryHead
Assert-Exact8C40Target
Assert-ProfileBoundary

Write-Host ''
Write-Host 'Step 2: AC/battery + durable/service baseline without mutation...' -ForegroundColor Cyan
$powerBefore=Assert-AcBatterySane

if(Test-Path $journalPath){
    Get-Content $journalPath
    throw 'M8 preflight refuses retained watchdog journal evidence.'
}

$serviceBefore=Get-M4ServiceSnapshot
Assert-M4ServiceBaseline $serviceBefore

Write-Host ''
Write-Host 'Step 3: build + M5-M8 static/synthetic regressions...' -ForegroundColor Cyan

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

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
    'test-8c40-load-thermal-m8-preflight-invariants.ps1'
)){
    & (Join-Path $PSScriptRoot $invariant)
    if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
}

dotnet run --project .\src\VictusFanControl.ModernStandbyProbe -c Release --no-build -- --self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m4-8c40-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-c-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --bios-contract-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

Write-Host ''
Write-Host 'Step 4: exact-target read-only telemetry + production SafetyGate readiness...' -ForegroundColor Cyan
& dotnet $cli --8c40-m8-preflight-probe --modules-dir $modulesDir
if($LASTEXITCODE -ne 0){
    throw "M8 read-only telemetry/SafetyGate probe failed with exit code $LASTEXITCODE."
}

Write-Host ''
Write-Host 'Step 5: independent stable firmware-owned EC baseline...' -ForegroundColor Cyan
Assert-StableFirmwareBaseline

Write-Host ''
Write-Host 'Step 6: prove preflight caused no durable/service/power boundary change...' -ForegroundColor Cyan

if(Test-Path $journalPath){
    throw 'M8 no-write preflight unexpectedly created a watchdog journal.'
}

Assert-ServiceUnchanged $serviceBefore
$powerAfter=Assert-AcBatterySane

$headAfter=(& git rev-parse HEAD 2>&1 | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $headAfter -cne $head){
    throw "M8 preflight repository HEAD changed during execution. before=$head after=$headAfter"
}

Write-Host ''
Write-Host 'PASS: HP 8C40 M8 no-write load/thermal preflight completed.' -ForegroundColor Green
Write-Host ("Evidence HEAD: {0}" -f $head) -ForegroundColor Green
Write-Host 'No fan write, firmware restore, watchdog lease, service mutation, fault injection, sleep transition or deliberate stress load was performed.' -ForegroundColor Green
