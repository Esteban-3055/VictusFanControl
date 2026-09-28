param([ValidateRange(10,30)][int]$RecoveryTimeoutSeconds=15)
$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath=Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$serviceLog=Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$localRoot=Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath=Join-Path $localRoot 'm5d-8c40-write-armed.ready.json'
$cli=Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir=Join-Path $repoRoot 'modules'
$requiredToken='8C40-M5D-WRITE-ARMED-CRASH30'
$controller=$null
$servicePidBefore=0
$logBoundary=0
$readyReached=$false
$pass=$false
$failure=$null

function Assert-Administrator {
    $id=[Security.Principal.WindowsIdentity]::GetCurrent()
    $p=New-Object Security.Principal.WindowsPrincipal($id)
    if(-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'M5D requires elevated PowerShell.'}
}

function Read-Setpoint {
    $raw=(& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    $line=($raw -split "[\r\n]+" | Where-Object {$_ -match '^setpoint CPU='} | Select-Object -Last 1)
    if(-not $line){throw "Could not parse 8C40 setpoint probe: $raw"}
    $m=[regex]::Match($line,'^setpoint CPU=(\d+) GPU=(\d+)$')
    if(-not $m.Success){throw "Could not parse setpoint line: $line"}
    [pscustomobject]@{Cpu=[int]$m.Groups[1].Value;Gpu=[int]$m.Groups[2].Value;Raw=$line}
}

function Get-ServicePid {
    $svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if(-not $svc -or $svc.State -ne 'Running'){return 0}
    return [int]$svc.ProcessId
}

function Wait-Ready([int]$Seconds=20) {
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Get-Date)-lt $deadline){
        if(Test-Path $statusPath){
            try{
                $s=Get-Content $statusPath -Raw | ConvertFrom-Json
                if($s.Ready -and -not $s.Blocked -and [int]$s.SessionId -eq 0 -and
                   $s.AccountName -match 'SYSTEM$' -and
                   $s.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                   $s.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2'){return $s}
            }catch{}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Timed out waiting for exact-target M4 Ready state.'
}

function Wait-JournalGone([int]$Seconds) {
    $deadline=(Get-Date).AddSeconds($Seconds)
    while((Test-Path $journalPath)-and (Get-Date)-lt $deadline){Start-Sleep -Milliseconds 100}
    return (-not (Test-Path $journalPath))
}

function Is-WriteArmed($phase) {
    if($null -eq $phase){return $false}
    if($phase -is [string]){return ($phase -ceq 'WriteArmed' -or $phase -ceq '1')}
    try{return ([int]$phase -eq 1)}catch{return $false}
}

function Assert-WriteArmedJournal($j,[int]$pid,[long]$ticks) {
    if([int]$j.SchemaVersion -ne 2 -or
       $j.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       -not (Is-WriteArmed $j.Phase) -or
       [long]$j.Generation -ne 2 -or
       [int]$j.Controller.ProcessId -ne $pid -or
       [long]$j.Controller.ProcessStartUtcTicks -ne $ticks -or
       $null -ne $j.PreviousOwned -or
       [int]$j.Pending.Cpu -ne 30 -or [int]$j.Pending.Gpu -ne 30 -or
       $null -ne $j.Owned){
        throw 'Journal is not exact generation-2 WRITE_ARMED pending 30/30 for the exact controller.'
    }
}

function Diagnostics {
    Write-Host ''
    if(Test-Path $readyPath){Write-Host 'M5D READY:' -ForegroundColor Cyan;Get-Content $readyPath}
    if(Test-Path $statusPath){Write-Host 'M4 status:' -ForegroundColor Cyan;Get-Content $statusPath}
    if(Test-Path $journalPath){Write-Host 'M4 journal:' -ForegroundColor Yellow;Get-Content $journalPath}
    if(Test-Path $serviceLog){Write-Host 'M4 log:' -ForegroundColor Cyan;Get-Content $serviceLog | Select-Object -Last 120}
    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator
Write-Host 'VictusFanControl - HP 8C40 M5D WRITE_ARMED POST-WMI/PRE-COMMIT CRASH' -ForegroundColor Cyan
Write-Host 'Parent has no direct HP/WMI restore authority.' -ForegroundColor Yellow

foreach($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')){
    if(Get-Process -Name $name -ErrorAction SilentlyContinue){throw "Refusing M5D while '$name' is running."}
}

Write-Host 'Step 1: build + regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m4-8c40-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-c-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

Write-Host 'Step 2: clean firmware baseline...' -ForegroundColor Cyan
$baseline=Read-Setpoint
Write-Host $baseline.Raw
if($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255){throw "M5D requires FF/FF, observed $($baseline.Cpu)/$($baseline.Gpu)."}
if(Test-Path $journalPath){Get-Content $journalPath;throw 'M5D refuses an existing durable journal.'}

Write-Host 'Step 3: install/start isolated M4 watchdog...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')
Remove-Item $statusPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $localRoot | Out-Null
Remove-Item $readyPath -Force -ErrorAction SilentlyContinue
Start-Service $serviceName
$status=Wait-Ready
$servicePidBefore=Get-ServicePid
if($servicePidBefore -le 0){throw 'M5D could not resolve watchdog PID.'}
Write-Host "Service PID=$servicePidBefore; account=$($status.AccountName); target=$($status.TargetProfileId)"
$logBoundary=if(Test-Path $serviceLog){@(Get-Content $serviceLog).Count}else{0}

Write-Host ''
Write-Host 'ACTIVE M5D BOUNDARY: real 30/30 will be written, acknowledged, then held before Commit.' -ForegroundColor Yellow
$token=Read-Host "Type exactly $requiredToken to continue"
if($token -cne $requiredToken){
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    throw 'M5D cancelled before any fan write.'
}

try{
    Write-Host 'Step 4: launch pre-Commit controller...' -ForegroundColor Cyan
    $controller=Start-Process dotnet -ArgumentList @(
        $cli,'--8c40-m5d-write-armed-crash-controller','--8c40-m5d-token',$requiredToken,
        '--8c40-m5d-ready-path',$readyPath,'--modules-dir',$modulesDir
    ) -PassThru -NoNewWindow

    $deadline=(Get-Date).AddSeconds(35)
    while(-not (Test-Path $readyPath)){
        if($controller.HasExited){$controller.WaitForExit();$controller.Refresh();throw "Controller exited before READY: $($controller.ExitCode)"}
        if((Get-Date)-gt $deadline){throw 'Timed out waiting for M5D READY.'}
        Start-Sleep -Milliseconds 50
    }

    $readyReached=$true
    $ready=Get-Content $readyPath -Raw | ConvertFrom-Json
    $ticks=[long]$controller.StartTime.ToUniversalTime().Ticks
    Get-Content $readyPath

    if([int]$ready.SchemaVersion -ne 1 -or $ready.Gate -cne 'M5D' -or
       $ready.Stage -cne 'WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT' -or
       $ready.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
       [int]$ready.ProcessId -ne $controller.Id -or [long]$ready.ProcessStartUtcTicks -ne $ticks -or
       [int]$ready.CpuSetpoint -ne 30 -or [int]$ready.GpuSetpoint -ne 30 -or
       [int]$ready.MaxFan -ne 0 -or [int]$ready.FanSwitch -ne 0 -or
       [int]$ready.CpuRpm -le 0 -or [int]$ready.GpuRpm -le 0 -or
       $ready.Ack -cne 'real-wmi+ec+tachs;watchdog-commit-not-dispatched'){
        throw 'M5D READY marker is not the required post-WMI/pre-Commit boundary.'
    }

    if(-not (Test-Path $journalPath)){throw 'M5D READY exists but journal is missing.'}
    $j=Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-WriteArmedJournal $j $controller.Id $ticks
    Write-Host "Journal phase=$($j.Phase); generation=$($j.Generation); pending=$($j.Pending.Cpu)/$($j.Pending.Gpu)"

    if($controller.HasExited){throw 'Controller exited before kill boundary.'}
    if((Get-ServicePid)-ne $servicePidBefore){throw 'Watchdog PID changed before kill boundary.'}

    $ec=Read-Setpoint
    Write-Host "Independent pre-kill EC: $($ec.Raw)"
    if($ec.Cpu -ne 30 -or $ec.Gpu -ne 30){throw 'Real EC is not 30/30 at WRITE_ARMED pre-Commit boundary.'}

    $j2=Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-WriteArmedJournal $j2 $controller.Id $ticks
    if((Get-ServicePid)-ne $servicePidBefore){throw 'Watchdog PID changed at kill boundary.'}

    Write-Host "Step 5: FORCE-KILL exact controller PID $($controller.Id) before Commit..." -ForegroundColor Yellow
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Stop-Process -Id $controller.Id -Force
    try{[void]$controller.WaitForExit(5000)}catch{}
    if(-not $controller.HasExited){throw 'Controller did not terminate.'}

    Write-Host 'Step 6: wait for same-watchdog WRITE_ARMED owner-loss recovery...' -ForegroundColor Cyan
    if(-not (Wait-JournalGone $RecoveryTimeoutSeconds)){throw 'Watchdog did not clear WRITE_ARMED journal in time.'}
    $watch.Stop()
    Start-Sleep -Milliseconds 400
    $final=Read-Setpoint
    Write-Host "Independent final EC: $($final.Raw)"
    Write-Host ("Recovery elapsed: {0:N3} s" -f $watch.Elapsed.TotalSeconds)
    if($final.Cpu -ne 255 -or $final.Gpu -ne 255){throw 'Final EC is not FF/FF.'}
    if((Get-ServicePid)-ne $servicePidBefore){throw 'Watchdog PID changed during M5D recovery.'}

    $newLog=@()
    if(Test-Path $serviceLog){$newLog=@(@(Get-Content $serviceLog) | Select-Object -Skip $logBoundary)}
    $newLog | Select-Object -Last 80

    $prepare=$newLog | Where-Object {$_ -match ("WATCHDOG PREPARE ACK controller PID={0}" -f $controller.Id)}
    $intent=$newLog | Where-Object {$_ -match ("WATCHDOG WRITE_INTENT ACK controller PID={0}" -f $controller.Id) -and $_ -match 'target=30/30'}
    $commit=$newLog | Where-Object {$_ -match ("WATCHDOG COMMIT ACK controller PID={0}" -f $controller.Id)}
    $restore=$newLog | Where-Object {(($_ -match 'WATCHDOG OWNER LOSS:') -and ($_ -match 'RestoredFirmware')) -or (($_ -match 'M4 RECOVERY disposition=RestoredFirmware') -and ($_ -match 'controller'))}

    if(-not $prepare -or -not $intent){throw 'Fresh PREPARE/WRITE_INTENT evidence missing.'}
    if($commit){throw 'COMMIT appeared for killed controller; M5D boundary was not preserved.'}
    if(-not $restore){throw 'Causal RestoredFirmware owner-loss evidence missing.'}
    if(Test-Path $journalPath){throw 'Journal reappeared after recovery.'}

    $pass=$true
    Write-Host 'PASS: HP 8C40 M5D WRITE_ARMED post-WMI/pre-Commit crash recovery completed.' -ForegroundColor Green
}
catch{
    $failure=$_.Exception.Message
    Diagnostics
}
finally{
    if($controller -and -not $controller.HasExited){
        Write-Warning 'M5D cleanup: killing exact test controller so watchdog recovery owns cleanup.'
        Stop-Process -Id $controller.Id -Force -ErrorAction SilentlyContinue
        try{[void]$controller.WaitForExit(5000)}catch{}
    }

    if($readyReached -and (Test-Path $journalPath)){[void](Wait-JournalGone $RecoveryTimeoutSeconds)}

    if(Test-Path $journalPath){
        Write-Warning 'M5D cleanup: journal remains; requesting service-stop/start recovery without deleting evidence.'
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
        if(Test-Path $journalPath){
            Start-Service $serviceName -ErrorAction SilentlyContinue
            [void](Wait-JournalGone $RecoveryTimeoutSeconds)
        }
    }

    $safe=$false
    if(-not (Test-Path $journalPath)){
        try{$check=Read-Setpoint;Write-Host "Post-test EC check: $($check.Raw)";$safe=($check.Cpu -eq 255 -and $check.Gpu -eq 255)}catch{}
    }

    if($safe){
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    }elseif(Test-Path $journalPath){
        Write-Host 'CRITICAL: durable M5D evidence remains. Do not run another fan-write gate.' -ForegroundColor Red
        Get-Content $journalPath
    }
}

if(-not $pass){throw "M5D FAILED: $failure"}
