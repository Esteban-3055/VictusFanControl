$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName='VictusFanControlWatchdogM4'
$journalPath=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'

function Read-8C40Setpoint {
    $output=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)

    if($LASTEXITCODE -ne 0){
        throw "M8A final read-only setpoint probe failed. Raw output: $output"
    }

    $line=($output -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1)

    if(-not $line){
        throw "M8A could not parse final HP 8C40 setpoint probe. Raw output: $output"
    }

    $match=[regex]::Match($line,'^setpoint CPU=(\d+) GPU=(\d+)$')

    if(-not $match.Success){
        throw "M8A could not parse final setpoint line: $line"
    }

    [pscustomobject]@{
        Cpu=[int]$match.Groups[1].Value
        Gpu=[int]$match.Groups[2].Value
        Raw=$line
    }
}

function Assert-FinalStableFirmwareOwnership {
    $first=Read-8C40Setpoint
    Write-Host ("Independent final EC 1/2: {0}" -f $first.Raw)

    Start-Sleep -Milliseconds 75

    $second=Read-8C40Setpoint
    Write-Host ("Independent final EC 2/2: {0}" -f $second.Raw)

    if($first.Cpu -ne 255 -or
       $first.Gpu -ne 255 -or
       $second.Cpu -ne 255 -or
       $second.Gpu -ne 255){
        throw 'M8A final independent ownership proof requires FF/FF twice consecutively.'
    }
}

function Assert-M4ServiceBaseline {
    $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if(-not $svc){
        Write-Host 'M4 service: not installed (acceptable for M8A no-write admission).'
        return
    }

    Write-Host ("M4 service final: StartType={0}, Status={1}" -f
        $svc.StartType,$svc.Status)

    if($svc.StartType -ne 'Manual' -or
       $svc.Status -ne 'Stopped'){
        throw 'M8A requires the M4 qualification service to remain Manual/stopped.'
    }
}

Write-Host 'VictusFanControl - HP 8C40 M8A REPRESENTATIVE-LOAD ADMISSION (NO-WRITE)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'M8A observes a real gaming/3D workload. It does not write a fan level, restore firmware or acquire a watchdog lease.' -ForegroundColor Yellow
Write-Host ''

Write-Host 'Step 1: rerun the versioned M8 no-write preflight on the exact current HEAD...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8-preflight.ps1')
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

Write-Host ''
Write-Host 'Step 2: M8A harness invariant...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8a-invariants.ps1')
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

Write-Host ''
Write-Host 'M8A fixed representative-load criteria:' -ForegroundColor Cyan
Write-Host '  60 samples at 1 second.'
Write-Host '  At least 45/60 simultaneous representative samples.'
Write-Host '  At least 10 consecutive representative samples.'
Write-Host '  GPU: load >=35% AND power >=20 W.'
Write-Host '  CPU: load >=5% OR package power >=15 W.'
Write-Host '  Every sample: complete/fresh SafetyGate-ready telemetry, AC/battery sane.'
Write-Host '  Firmware ownership remains FF/FF; guards remain 00/00.'
Write-Host '  Immediate fail-closed at effective CPU >=90 C or GPU >=82 C.'
Write-Host ''
Write-Host 'Start a normal game or 3D workload now and reach active gameplay/rendering.' -ForegroundColor Yellow
Write-Host 'Do not start a synthetic stress test and do not change fan-control software.' -ForegroundColor Yellow

$confirmation=Read-Host 'When the workload is active, type M8A and press Enter'

if($confirmation -cne 'M8A'){
    throw 'M8A cancelled: exact confirmation token M8A was not entered.'
}

if(Test-Path $journalPath){
    Get-Content $journalPath
    throw 'M8A refuses to start while a retained watchdog journal exists.'
}

$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m8a-representative-load_{0}" -f $stamp)
$resultPath=Join-Path $evidenceRoot 'm8a-result.json'

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

Write-Host ''
Write-Host 'Step 3: observe the 60-second representative-load window...' -ForegroundColor Cyan

& dotnet $cli --8c40-m8a-representative-load --8c40-m8a-result-path $resultPath --modules-dir $modulesDir
$qualificationExit=$LASTEXITCODE

if(-not (Test-Path $resultPath)){
    throw "M8A did not produce its durable result file: $resultPath"
}

$result=Get-Content $resultPath -Raw | ConvertFrom-Json

if($qualificationExit -ne 0 -or
   [string]$result.result -cne 'PASS'){
    Write-Host ("M8A durable evidence: {0}" -f $resultPath) -ForegroundColor Yellow

    throw ("M8A representative-load admission did not PASS. exit={0} result={1} reason={2}" -f
        $qualificationExit,$result.result,$result.failureReason)
}

Write-Host ''
Write-Host 'Step 4: independent no-mutation closure proof...' -ForegroundColor Cyan

if(Test-Path $journalPath){
    Get-Content $journalPath
    throw 'M8A unexpectedly created or retained a watchdog journal.'
}

Assert-M4ServiceBaseline
Assert-FinalStableFirmwareOwnership

Write-Host ''
Write-Host 'PASS: HP 8C40 M8A representative-load admission completed NO-WRITE.' -ForegroundColor Green
Write-Host ("Evidence: {0}" -f $resultPath) -ForegroundColor Green
Write-Host 'M8B remains blocked until this physical M8A result is reviewed and recorded.' -ForegroundColor Green
