# One bounded, read-only session for existing HP 8C40/F.18 ACPI routes.
[CmdletBinding()]
param([string]$OutputRoot='', [switch]$SelfTest, [switch]$Child, [switch]$FanStatus)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'AcpiProbeCommon.ps1')
. (Join-Path $PSScriptRoot 'AcpiCoverageContract.ps1')
if($SelfTest){Test-AcpiCoverageContract;return}
if($env:OS -ne 'Windows_NT'){throw 'Windows is required.'}
if($Child){
    try{
        Assert-Gm26Isolation
        $identity=Assert-Gm26Hardware
        $identity | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'hardware.json') -Encoding UTF8
        [ordered]@{FanStatusGm11=$(if($FanStatus){'pending-raw-only'}else{'not-requested'});Fffs='pending';Rpm='pending';CpuGpuSetpoints='not-exposed-by-reviewed-getters';CompleteEcGuard='not-exposed-by-GM26';FanSwitchF4='not-exposed-by-reviewed-getters';Gm27Transition='not-executed';FirmwareRestore='not-exercised-no-setters';ProductionReady=$false} | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'coverage.json') -Encoding UTF8
        Add-Type -AssemblyName System.Management
        $scope=[System.Management.ManagementScope]::new('\\.\root\wmi')
        $scope.Connect()
        $enumeration=[System.Management.EnumerationOptions]::new()
        $enumeration.Timeout=[TimeSpan]::FromSeconds(5)
        $searcher=[System.Management.ManagementObjectSearcher]::new($scope,[System.Management.ObjectQuery]::new('SELECT * FROM hpqBIntM'),$enumeration)
        $results=$searcher.Get();$target=$null
        foreach($candidate in $results){
            if($candidate['InstanceName'] -eq 'ACPI\PNP0C14\0_0'){$target=$candidate;break}
            $candidate.Dispose()
        }
        if(-not $target){throw 'Qualified HP WMI instance unavailable.'}
        $dataClass=[System.Management.ManagementClass]::new($scope,[System.Management.ManagementPath]::new('hpqBDataIn'),$null)
        $read={param($kind,$round)
            if(Test-Path (Join-Path $OutputRoot 'stop.signal')){throw 'Parent requested stop.'}
            Assert-Gm26Isolation
            $request=Get-AcpiCoverageRequest $kind
            $data=$null;$inputData=$null;$result=$null;$outData=$null
            try{
                $data=$dataClass.CreateInstance()
                $data['Sign']=[byte[]](0x53,0x45,0x43,0x55)
                $data['Command']=$request.Command;$data['CommandType']=$request.Type
                $data['Size']=[uint32]$request.Payload.Length;$data['hpqBData']=$request.Payload
                $method='hpqBIOSInt'+$request.Output
                $inputData=$target.GetMethodParameters($method);$inputData['InData']=$data
                $invoke=[System.Management.InvokeMethodOptions]::new();$invoke.Timeout=[TimeSpan]::FromSeconds(5)
                [pscustomobject]@{phase='begin';kind=$kind;round=$round;utc=[DateTimeOffset]::UtcNow.ToString('o');command=$request.Command;type=$request.Type;payload_hex=[BitConverter]::ToString($request.Payload);output=$request.Output} | ConvertTo-Json -Compress | Add-Content (Join-Path $OutputRoot 'calls.jsonl') -Encoding UTF8
                $watch=[Diagnostics.Stopwatch]::StartNew()
                $result=$target.InvokeMethod($method,$inputData,$invoke)
                $watch.Stop();$outData=$result['OutData']
                if($null -eq $outData -or $null -eq $outData['rwReturnCode'] -or $null -eq $outData['Data']){throw 'Incomplete HP response.'}
                $response=[pscustomobject]@{Code=[int]$outData['rwReturnCode'];Data=[byte[]]$outData['Data'];NativeDurationMs=$watch.Elapsed.TotalMilliseconds}
                [pscustomobject]@{phase='response';kind=$kind;round=$round;utc=[DateTimeOffset]::UtcNow.ToString('o');return_code=$response.Code;bytes_hex=[BitConverter]::ToString($response.Data);native_duration_ms=$response.NativeDurationMs} | ConvertTo-Json -Compress | Add-Content (Join-Path $OutputRoot 'calls.jsonl') -Encoding UTF8
                # Wait between native calls; no retries or parallel hardware operations.
                for($tick=0;$tick -lt 10;$tick++){
                    if(Test-Path (Join-Path $OutputRoot 'stop.signal')){throw 'Parent requested stop.'}
                    Start-Sleep -Milliseconds 100
                }
                return $response
            }finally{foreach($item in @($outData,$result,$inputData,$data)){if($null -ne $item){$item.Dispose()}}}
        }
        $record={param($row) $row | Export-Csv (Join-Path $OutputRoot 'rounds.csv') -NoTypeInformation -Append -Encoding UTF8}
        $wait={for($tick=0;$tick -lt 50;$tick++){if(Test-Path (Join-Path $OutputRoot 'stop.signal')){throw 'Parent requested stop.'};Start-Sleep -Milliseconds 100}}
        Invoke-AcpiCoverageSequence $read $record $wait ([bool]$FanStatus)
        $coverage=Get-Content (Join-Path $OutputRoot 'coverage.json') -Raw | ConvertFrom-Json
        $coverage.Fffs='conditional-read-branch-supported-by-AML-and-bracketed-ECOK-codes; no-transition-tested'
        if($FanStatus){$coverage.FanStatusGm11='three-raw-responses-per-selector; semantics-and-freshness-unqualified'}
        $coverage.Rpm='three-valid-dual-fan-responses; nominal-100-rpm-resolution'
        $coverage | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'coverage.json') -Encoding UTF8
        $dataClass.Dispose();$target.Dispose();$results.Dispose();$searcher.Dispose()
        exit 0
    }catch{
        $_ | Out-String | Set-Content (Join-Path $OutputRoot 'child-error.txt') -Encoding UTF8
        exit 1
    }
}
Assert-Gm26Isolation
if(-not $OutputRoot){$OutputRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'diagnostics'}
$folder=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('ACPI-Coverage_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item $folder -ItemType Directory -Force | Out-Null
$cursor=0
$started=[DateTimeOffset]::UtcNow
$summary=[ordered]@{Mode='ACPI-read-only-coverage';HarnessVersion='2';FanStatusRequested=[bool]$FanStatus;StartedUtc=$started.ToString('o');InitialSystemRecordId=$cursor;RequestedSamples=3;MaximumRequests=$(if($FanStatus){19}else{13});ProductionReady=$false;DirectEcAccess=$false;FanSetters=$false;Healthy=$false;Reason='incomplete';Samples=0;ChildExitCode=$null;AcpiEvents=@()}
$probeProcess=New-Object Diagnostics.Process
$childStarted=$false
try{
    $initial=@(Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction SilentlyContinue -ErrorVariable cursorErrors)
    if(@($cursorErrors | Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count){throw 'Initial System event observation failed.'}
    if($initial.Count){$cursor=[long]$initial[0].RecordId}
    $summary.InitialSystemRecordId=$cursor
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $info.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Child -OutputRoot "'+$folder+'"'
    if($FanStatus){$info.Arguments+=' -FanStatus'}
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $probeProcess.StartInfo=$info
    if(-not $probeProcess.Start()){throw 'Child did not start.'}
    $childStarted=$true
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(120)
    $nextReport=[DateTimeOffset]::MinValue
    Write-Host 'ACPI: control GBIF + tres rondas ECOK/FFFS/RPM; sin setters ni PawnIO.'
    if($FanStatus){Write-Host 'GM11: dos selectores por ronda; cuatro bytes crudos, sin interpretar consignas ni guardas.'}
    do{
        $events=@(Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery $cursor -IncludeUnexpectedData) -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
        if(@($eventErrors | Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count){throw 'System event observation failed.'}
        $fresh=@(Select-ScenarioANewEvents $events $cursor $started -IncludeUnexpectedData)
        if($fresh.Count){
            $summary.AcpiEvents=@($fresh | ForEach-Object {[pscustomobject]@{Id=$_.Id;RecordId=$_.RecordId;Xml=$_.ToXml()}})
            throw 'Nuevo evento ACPI 13/15; prueba detenida.'
        }
        if([DateTimeOffset]::UtcNow -ge $nextReport){
            $rows=@(if(Test-Path (Join-Path $folder 'rounds.csv')){Import-Csv (Join-Path $folder 'rounds.csv')})
            Write-Host ('ACPI: '+$rows.Count+'/3 rondas; UTC '+[DateTimeOffset]::UtcNow.ToString('o'))
            $nextReport=[DateTimeOffset]::UtcNow.AddSeconds(5)
        }
        if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Deadline 120 s: child termination does not guarantee cancellation of an in-flight firmware call.'}
        if(-not $probeProcess.HasExited){Start-Sleep -Milliseconds 200}
    }while(-not $probeProcess.HasExited)
    $probeProcess.WaitForExit();$summary.ChildExitCode=$probeProcess.ExitCode
    Start-Sleep -Seconds 2
    $events=@(Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery $cursor -IncludeUnexpectedData) -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
    if(@($eventErrors | Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count){throw 'Final System event observation failed.'}
    $fresh=@(Select-ScenarioANewEvents $events $cursor $started -IncludeUnexpectedData)
    if($fresh.Count){
        $summary.AcpiEvents=@($fresh | ForEach-Object {[pscustomobject]@{Id=$_.Id;RecordId=$_.RecordId;Xml=$_.ToXml()}})
        throw 'Nuevo evento ACPI 13/15 al finalizar; captura rechazada.'
    }
    $rows=@(if(Test-Path (Join-Path $folder 'rounds.csv')){Import-Csv (Join-Path $folder 'rounds.csv')})
    $summary.Samples=$rows.Count
    if($probeProcess.ExitCode -ne 0 -or $rows.Count -ne 3){throw 'Probe incomplete; inspect child-error.txt.'}
    $summary.Healthy=$true;$summary.Reason='Three rounds conform to supplied AML contracts; no ACPI 13/15 observed. ECOK inference is conditional on provider code fidelity; no complete guard coverage or production qualification.'
}catch{
    $summary.Reason=$_.Exception.Message
    Write-Warning $summary.Reason
}finally{
    Set-Content (Join-Path $folder 'stop.signal') 'stop'
    if($childStarted -and -not $probeProcess.HasExited){
        if(-not $probeProcess.WaitForExit(2000)){$probeProcess.Kill();$probeProcess.WaitForExit()}
    }
    if($childStarted){$summary.ChildExitCode=$probeProcess.ExitCode}
    $probeProcess.Dispose()
    $summary.Samples=@(if(Test-Path (Join-Path $folder 'rounds.csv')){Import-Csv (Join-Path $folder 'rounds.csv')}).Count
    if(Test-Path (Join-Path $folder 'coverage.json')){$summary.Coverage=Get-Content (Join-Path $folder 'coverage.json') -Raw | ConvertFrom-Json}
    $summary.EndedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $folder 'summary.json') -Encoding UTF8
    Get-ChildItem $folder -File | ForEach-Object {[pscustomobject]@{file=$_.Name;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}} | ConvertTo-Json | Set-Content (Join-Path $folder 'manifest.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath ($folder+'.zip')
    (Get-FileHash ($folder+'.zip') -Algorithm SHA256).Hash | Set-Content ($folder+'.zip.sha256') -Encoding ASCII
    Write-Host ('Evidencia: '+$folder+'.zip')
}
if(-not $summary.Healthy){throw 'ACPI coverage investigation failed; evidence preserved.'}
