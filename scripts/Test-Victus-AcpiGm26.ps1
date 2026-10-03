# Bounded HP 8C40/F.18 investigation. No PawnIO, RPM requests or fan setters.
[CmdletBinding()]
param([string]$OutputRoot='', [switch]$SelfTest, [switch]$Child)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ScenarioAcpiEventFilter.ps1')
function Assert-Gm26Response([int]$Code,[byte[]]$Data) {
    if($Code -ne 0){throw ('HP return code '+$Code)}
    if($Data.Length -ne 4){throw ('Expected four bytes; received '+$Data.Length)}
    if($Data[0] -gt 1 -or $Data[1] -ne 0 -or $Data[2] -ne 0 -or $Data[3] -ne 0){throw 'Response outside the AML GM26 contract.'}
}
function Assert-Gm26Isolation {
    if(@(Get-Process -Name 'VictusFanControl*','OmenMon','HWiNFO*','RW*' -ErrorAction SilentlyContinue).Count){throw 'Close VictusFanControl and other hardware polling tools first.'}
    if(@(Get-CimInstance Win32_Service -Filter "Name LIKE 'VictusFanControl%'" -OperationTimeoutSec 5 | Where-Object {$_.State -ne 'Stopped'}).Count){throw 'VictusFanControl services must be stopped.'}
    $base=Join-Path $env:ProgramData 'VictusFanControl'
    if(Test-Path $base){
        foreach($directory in @(Get-ChildItem $base -Directory)){
            if(Test-Path (Join-Path $directory.FullName 'state\lease.json')){throw 'Pending fan lease: return to Firmware and close the app; preserve the lease file.'}
        }
    }
}
function Assert-Gm26Hardware {
    $board=Get-CimInstance Win32_BaseBoard -OperationTimeoutSec 5
    $system=Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 5
    $bios=Get-CimInstance Win32_BIOS -OperationTimeoutSec 5
    if($board.Manufacturer -ne 'HP' -or $board.Product -ne '8C40' -or $board.Version -ne '63.43' -or
        $system.Manufacturer -ne 'HP' -or $system.Model -ne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
        $system.SystemSKUNumber -notmatch '^9D0R1LA(?:#.*)?$' -or $bios.SMBIOSBIOSVersion -ne 'F.18'){
        throw 'This probe requires HP 8C40 / 63.43 / 9D0R1LA / Victus 15-fa1xxx / F.18.'
    }
    return [pscustomobject]@{Board=$board.Product;Version=$board.Version;Model=$system.Model;Sku=$system.SystemSKUNumber;Bios=$bios.SMBIOSBIOSVersion}
}
if($SelfTest){
    Assert-Gm26Response 0 ([byte[]](0,0,0,0))
    Assert-Gm26Response 0 ([byte[]](1,0,0,0))
    foreach($case in @(@{Code=5;Data=[byte[]](0,0,0,0)},@{Code=0;Data=[byte[]](0)},@{Code=0;Data=[byte[]](2,0,0,0)},@{Code=0;Data=[byte[]](1,1,0,0)})){
        $rejected=$false
        try{Assert-Gm26Response $case.Code $case.Data}catch{$rejected=$true}
        if(-not $rejected){throw 'Malformed response accepted.'}
    }
    Write-Host 'GM26 response fixtures passed; no hardware access.'
    return
}
if($env:OS -ne 'Windows_NT'){throw 'Windows is required.'}
if($Child){
    # The parent bounds this whole process. WMI Timeout alone is not a hard deadline.
    try{
        Assert-Gm26Isolation
        $identity=Assert-Gm26Hardware
        $identity | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'hardware.json') -Encoding UTF8
        Add-Type -AssemblyName System.Management
        $scope=New-Object System.Management.ManagementScope '\\.\root\wmi'
        $scope.Connect()
        $query=New-Object System.Management.ObjectQuery 'SELECT * FROM hpqBIntM'
        $enumeration=New-Object System.Management.EnumerationOptions
        $enumeration.Timeout=[TimeSpan]::FromSeconds(5)
        $searcher=New-Object System.Management.ManagementObjectSearcher($scope,$query,$enumeration)
        $results=$searcher.Get();$target=$null
        foreach($candidate in $results){
            if($candidate['InstanceName'] -eq 'ACPI\PNP0C14\0_0'){$target=$candidate;break}
            $candidate.Dispose()
        }
        if(-not $target){throw 'Qualified HP WMI instance unavailable.'}
        $class=New-Object System.Management.ManagementClass($scope,(New-Object System.Management.ManagementPath 'hpqBDataIn'),$null)
        for($sample=1;$sample -le 3;$sample++){
            if(Test-Path (Join-Path $OutputRoot 'stop.signal')){throw 'Parent requested stop.'}
            Assert-Gm26Isolation
            $utc=[DateTimeOffset]::UtcNow
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $data=$class.CreateInstance()
            $data['Sign']=[byte[]](0x53,0x45,0x43,0x55)
            $data['Command']=[uint32]0x20008
            $data['CommandType']=[uint32]0x26
            $data['Size']=[uint32]0
            $data['hpqBData']=[byte[]]@()
            $inputData=$target.GetMethodParameters('hpqBIOSInt4')
            $inputData['InData']=$data
            $invoke=New-Object System.Management.InvokeMethodOptions
            $invoke.Timeout=[TimeSpan]::FromSeconds(5)
            [pscustomobject]@{phase='begin';sample=$sample;utc=$utc.ToString('o')} | ConvertTo-Json -Compress | Add-Content (Join-Path $OutputRoot 'calls.jsonl') -Encoding UTF8
            $result=$target.InvokeMethod('hpqBIOSInt4',$inputData,$invoke)
            $watch.Stop()
            $outData=$result['OutData']
            if(-not $outData){throw 'No OutData.'}
            if($null -eq $outData['rwReturnCode']){throw 'Missing rwReturnCode.'}
            $code=[int]$outData['rwReturnCode'];$bytes=[byte[]]$outData['Data']
            [pscustomobject]@{phase='response';sample=$sample;utc=[DateTimeOffset]::UtcNow.ToString('o');duration_ms=$watch.Elapsed.TotalMilliseconds;return_code=$code;bytes_hex=([BitConverter]::ToString($bytes))} | ConvertTo-Json -Compress | Add-Content (Join-Path $OutputRoot 'calls.jsonl') -Encoding UTF8
            Assert-Gm26Response $code $bytes
            [pscustomobject]@{sample=$sample;utc=$utc.ToString('o');duration_ms=$watch.Elapsed.TotalMilliseconds;return_code=$code;bytes_hex=([BitConverter]::ToString($bytes));fffs=$bytes[0];zero_is_ambiguous=($bytes[0] -eq 0)} |
                Export-Csv (Join-Path $OutputRoot 'gm26.csv') -NoTypeInformation -Append -Encoding UTF8
            $outData.Dispose();$result.Dispose();$inputData.Dispose();$data.Dispose()
            if($sample -lt 3){
                # Minimum five seconds after completion, with an interruptible wait.
                for($tick=0;$tick -lt 50;$tick++){
                    if(Test-Path (Join-Path $OutputRoot 'stop.signal')){throw 'Parent requested stop.'}
                    Start-Sleep -Milliseconds 100
                }
            }
        }
        $class.Dispose();$target.Dispose();$results.Dispose();$searcher.Dispose()
        exit 0
    }catch{
        $_ | Out-String | Set-Content (Join-Path $OutputRoot 'child-error.txt') -Encoding UTF8
        exit 1
    }
}
Assert-Gm26Isolation
if(-not $OutputRoot){$OutputRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'diagnostics'}
$folder=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('ACPI-GM26_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item $folder -ItemType Directory -Force | Out-Null
$cursor=0
$started=[DateTimeOffset]::UtcNow
$summary=[ordered]@{Mode='GM26-only';StartedUtc=$started.ToString('o');InitialSystemRecordId=$cursor;RequestedSamples=3;Command='0x20008';CommandType='0x26';PayloadSize=0;OutputSize=4;DirectEcAccess=$false;FanSetters=$false;Healthy=$false;Reason='incomplete';Samples=0;ChildExitCode=$null;AcpiEvents=@()}
$child=New-Object Diagnostics.Process
$childStarted=$false
try{
    $initial=@(Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction SilentlyContinue -ErrorVariable cursorErrors)
    if(@($cursorErrors | Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count){throw 'Initial System event observation failed.'}
    if($initial.Count){$cursor=[long]$initial[0].RecordId}
    $summary.InitialSystemRecordId=$cursor
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $info.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Child -OutputRoot "'+$folder+'"'
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $child.StartInfo=$info
    if(-not $child.Start()){throw 'Child did not start.'}
    $childStarted=$true
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(45)
    $nextReport=[DateTimeOffset]::MinValue
    Write-Host 'GM26: tres lecturas, separadas por al menos 5 s; Firmware conserva el control.'
    do{
        $events=@(Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery $cursor -IncludeUnexpectedData) -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
        if(@($eventErrors | Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count){throw 'System event observation failed.'}
        $fresh=@(Select-ScenarioANewEvents $events $cursor $started -IncludeUnexpectedData)
        if($fresh.Count){
            $summary.AcpiEvents=@($fresh | ForEach-Object {[pscustomobject]@{Id=$_.Id;RecordId=$_.RecordId;Xml=$_.ToXml()}})
            throw 'Nuevo evento ACPI 13/15; prueba detenida.'
        }
        if([DateTimeOffset]::UtcNow -ge $nextReport){
            $rows=@(if(Test-Path (Join-Path $folder 'gm26.csv')){Import-Csv (Join-Path $folder 'gm26.csv')})
            Write-Host ('GM26: '+$rows.Count+'/3 muestras; UTC '+[DateTimeOffset]::UtcNow.ToString('o'))
            $nextReport=[DateTimeOffset]::UtcNow.AddSeconds(5)
        }
        if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Deadline 45 s: child termination does not guarantee cancellation of an in-flight firmware call.'}
        if(-not $child.HasExited){Start-Sleep -Milliseconds 200}
    }while(-not $child.HasExited)
    $child.WaitForExit();$summary.ChildExitCode=$child.ExitCode
    Start-Sleep -Seconds 2
    $events=@(Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery $cursor -IncludeUnexpectedData) -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
    if(@($eventErrors | Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count){throw 'Final System event observation failed.'}
    $fresh=@(Select-ScenarioANewEvents $events $cursor $started -IncludeUnexpectedData)
    if($fresh.Count){
        $summary.AcpiEvents=@($fresh | ForEach-Object {[pscustomobject]@{Id=$_.Id;RecordId=$_.RecordId;Xml=$_.ToXml()}})
        throw 'Nuevo evento ACPI 13/15 al finalizar; captura rechazada.'
    }
    $rows=@(if(Test-Path (Join-Path $folder 'gm26.csv')){Import-Csv (Join-Path $folder 'gm26.csv')})
    $summary.Samples=$rows.Count
    if($child.ExitCode -ne 0 -or $rows.Count -ne 3){throw 'Probe incomplete; inspect child-error.txt.'}
    $summary.Healthy=$true;$summary.Reason='Three responses conform to AML; no ACPI 13/15 observed in this short window. Zero remains ambiguous; no production qualification.'
}catch{
    $summary.Reason=$_.Exception.Message
    Write-Warning $summary.Reason
}finally{
    Set-Content (Join-Path $folder 'stop.signal') 'stop'
    if($childStarted -and -not $child.HasExited){
        if(-not $child.WaitForExit(2000)){$child.Kill();$child.WaitForExit()}
    }
    if($childStarted){$summary.ChildExitCode=$child.ExitCode}
    $child.Dispose()
    $summary.Samples=@(if(Test-Path (Join-Path $folder 'gm26.csv')){Import-Csv (Join-Path $folder 'gm26.csv')}).Count
    $summary.EndedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $folder 'summary.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath ($folder+'.zip')
    Write-Host ('Evidencia: '+$folder+'.zip')
}
if(-not $summary.Healthy){throw 'GM26 investigation failed; evidence preserved.'}
