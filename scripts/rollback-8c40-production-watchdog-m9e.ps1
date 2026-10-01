param(
    [string]$Reason='operator-requested M9E rollback'
)

$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json

if([bool]$profile.lifecycle.watchdogRecoveryValidated -or
   [bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized){
    throw 'M9E rollback refuses after final production watchdog promotion.'
}

$serviceName='VictusFanControlWatchdogM4'
$serviceRoot=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$journalPath=Join-Path $serviceRoot 'state\lease.json'
$expectedExe=Join-Path $serviceRoot 'bin\VictusFanControl.Watchdog.exe'
$stamp=Get-Date -Format 'yyyy-MM-dd_HHmmss'
$evidenceRoot=Join-Path $repoRoot ("logs\m9e-rollback_{0}" -f $stamp)
$resultPath=Join-Path $evidenceRoot 'm9e-rollback-result.json'

$id=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($id)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'M9E rollback requires elevated PowerShell.'
}

$svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
if(([string]$svc.PathName).IndexOf($expectedExe,[StringComparison]::OrdinalIgnoreCase)-lt 0 -or
   ([string]$svc.PathName).IndexOf('--m4-8c40-lease-service',[StringComparison]::OrdinalIgnoreCase)-lt 0 -or
   [string]$svc.StartName-notmatch 'LocalSystem|Local System'){
    throw 'M9E rollback refuses because the installed service is not the qualified HP 8C40 M4 service.'
}

if(Test-Path -LiteralPath $journalPath){
    Get-Content -LiteralPath $journalPath
    throw 'M9E rollback refuses to stop/reconfigure the watchdog while durable ownership evidence exists.'
}

if(Test-Path -LiteralPath $evidenceRoot){
    throw "M9E rollback evidence path already exists: $evidenceRoot"
}
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null

$before=$svc
$completed=$false
$failure=$null

try{
    if([string]$svc.State-cne 'Stopped'){
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
    }

    & sc.exe config $serviceName start= demand | Out-Host
    if($LASTEXITCODE-ne 0){throw "sc config demand failed: $LASTEXITCODE"}

    & sc.exe failureflag $serviceName 0 | Out-Host
    if($LASTEXITCODE-ne 0){throw "sc failureflag 0 failed: $LASTEXITCODE"}

    # Microsoft sc.exe documents actions= "" as the no-failure-action policy.
    & sc.exe failure $serviceName reset= 0 actions= '""' | Out-Host
    if($LASTEXITCODE-ne 0){throw "sc failure clear failed: $LASTEXITCODE"}

    $after=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
    if($after.State-cne 'Stopped' -or
       $after.StartMode-cne 'Manual' -or
       [int]$after.ProcessId-ne 0){
        throw "Rollback did not restore Manual/Stopped/PID0: $($after.StartMode)/$($after.State)/PID$($after.ProcessId)"
    }

    if(Test-Path -LiteralPath $journalPath){
        throw 'Journal appeared during M9E rollback.'
    }

    $completed=$true
}
catch{
    $failure=$_.Exception.Message
    throw
}
finally{
    $final=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue

    [ordered]@{
        schemaVersion=1
        gate='M9E-ROLLBACK'
        result=$(if($completed){'PASS'}else{'FAIL_CLOSED'})
        reason=$Reason
        failure=$failure
        timestampUtc=(Get-Date).ToUniversalTime().ToString('O')
        serviceBefore=$before
        serviceFinal=$final
        journalPresentFinal=(Test-Path -LiteralPath $journalPath)
        evidenceDeleted=$false
        fanWriteAttempted=$false
        watchdogLeaseAttempted=$false
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}

Write-Host 'PASS: M9E service policy rolled back to the qualified Manual/Stopped baseline. Evidence was retained.' -ForegroundColor Green
