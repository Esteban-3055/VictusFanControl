# Windows PowerShell 5.1 / PowerShell 7. Read-only isolation; no fan writes.
[CmdletBinding()]
param(
    [ValidateRange(1,60)][int]$CaptureMinutes=30,
    [string]$OutputRoot='',
    [switch]$SkipAcpiTrace,
    [switch]$ScenarioB,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ScenarioAcpiEventFilter.ps1')
$script:WmiOnlyState=@{Child=$null;ExitCode=$null;ExitCaptured=$false;OutTask=$null;ErrTask=$null;OutStream=$null;ErrStream=$null;ScenarioB=[bool]$ScenarioB;InitialSystemRecordId=$null;Acpi13=@();StopReason='duration-or-Q';Fault=$null;CaptureRoot=$null;Evidence=$null;WasRunning=$false;Paused=$false;ValidIsolation=$false;NextReport=[DateTimeOffset]::MinValue;Root=$root;Resumed=$false}

function Assert-WmiOnlyFacts($Facts,[int]$AllowedCliPid=0,[switch]$AllowRunningM4) {
    # Presence alone blocks: never parse/delete a retained or malformed lease.
    if(@($Facts.Journals).Count -gt 0){throw ('Hay evidencia de lease pendiente: '+($Facts.Journals -join ', ')+'. Vuelve a Firmware y cierra la app; no borres esos archivos.')}
    foreach($service in $Facts.Services){
        if($service.State -ne 'Stopped' -and -not($AllowRunningM4 -and $service.Name -eq 'VictusFanControlWatchdogM4' -and $service.State -eq 'Running')){
            throw ('Aislamiento rechazado: servicio activo '+$service.Name+' ('+$service.State+').')
        }
    }
    $allowedWatchdogPids=@($Facts.Services | Where-Object {$AllowRunningM4 -and $_.Name -eq 'VictusFanControlWatchdogM4' -and $_.State -eq 'Running'} | ForEach-Object {[int]$_.ProcessId})
    foreach($process in $Facts.Processes){
        if($process.Id -eq $AllowedCliPid -and $AllowedCliPid -gt 0){continue}
        if($AllowRunningM4 -and $process.Id -in $allowedWatchdogPids -and $process.Name -eq 'VictusFanControl.Watchdog'){continue}
        throw ('Aislamiento rechazado: proceso '+$process.Name+' PID='+$process.Id+'. Vuelve a Firmware y cierra la GUI/otros CLI antes de continuar.')
    }
}
function Assert-WmiOnlyM4Path([string]$CommandLine,[string]$ExpectedResult) {
    $match=[regex]::Match($CommandLine,'--result-path\s+(?:"([^"]+)"|(\S+))')
    if(-not $match.Success){throw 'No se pudo verificar la ruta de estado del watchdog M4 instalado.'}
    $actual=$match.Groups[1].Value;if(-not $actual){$actual=$match.Groups[2].Value}
    if(-not [string]::Equals([IO.Path]::GetFullPath($actual),[IO.Path]::GetFullPath($ExpectedResult),[StringComparison]::OrdinalIgnoreCase)){
        throw 'El watchdog M4 usa una ruta de estado personalizada; esta prueba no puede verificar su lease con seguridad.'
    }
}
function Get-WmiOnlyFacts {
    $base=Join-Path $env:ProgramData 'VictusFanControl'
    $journals=@()
    if(Test-Path -LiteralPath $base -ErrorAction Stop){
        foreach($directory in @(Get-ChildItem -LiteralPath $base -Directory -ErrorAction Stop)){
            $journal=Join-Path $directory.FullName 'state\lease.json'
            if(Test-Path -LiteralPath $journal -ErrorAction Stop){$journals+=@($journal)}
        }
    }
    $services=@(Get-CimInstance Win32_Service -Filter "Name LIKE 'VictusFanControl%'" | Select-Object Name,State,ProcessId,PathName)
    $m4=@($services | Where-Object {$_.Name -eq 'VictusFanControlWatchdogM4'})
    if($m4.Count -gt 0){Assert-WmiOnlyM4Path $m4[0].PathName (Join-Path $base 'WatchdogM4\state\m4-8c40.status.json')}
    $processes=@(Get-Process -Name 'VictusFanControl*' -ErrorAction SilentlyContinue | Select-Object @{Name='Name';Expression={$_.ProcessName}},Id)
    return [pscustomobject]@{Journals=$journals;Services=$services;Processes=$processes}
}
function Write-WmiOnlyJson([string]$Path,$Value) {
    [IO.File]::WriteAllText($Path,(ConvertTo-Json -InputObject $Value -Depth 8),(New-Object Text.UTF8Encoding($false)))
}
function Start-WmiOnlyChild($state,[string]$File,[string]$Arguments,[string]$WorkingDirectory) {
    # Own the Process instance that starts the child: Start-Process -PassThru
    # yielded a null ExitCode on the user's PS5.1 captures. Keep its handle.
    $child=New-Object Diagnostics.Process
    try{
        $info=New-Object Diagnostics.ProcessStartInfo
        $info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$WorkingDirectory
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true
        $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
        $state.OutStream=[IO.File]::Open((Join-Path $state.Evidence 'cli-stdout.txt'),[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        $state.ErrStream=[IO.File]::Open((Join-Path $state.Evidence 'cli-stderr.txt'),[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        $child.StartInfo=$info
        if(-not $child.Start()){throw 'No se pudo iniciar el CLI.'}
        $state.Child=$child
        $state.OutTask=$child.StandardOutput.BaseStream.CopyToAsync($state.OutStream)
        $state.ErrTask=$child.StandardError.BaseStream.CopyToAsync($state.ErrStream)
    }catch{
        if(-not $state.Child){$child.Dispose();if($state.OutStream){$state.OutStream.Dispose()};if($state.ErrStream){$state.ErrStream.Dispose()}}
        throw
    }
}
function Stop-WmiOnlyChild($state) {
    if(-not $state.Child -or $state.ExitCaptured){return}
    [IO.File]::WriteAllText((Join-Path $state.Evidence 'stop.signal'),'stop',(New-Object Text.UTF8Encoding($false)))
    if(-not $state.Child.WaitForExit(15000)){
        $state.Fault='El CLI no termino con la senal de cierre; captura incompleta.'
        # Only our own strictly read-only child may be terminated.
        $state.Child.Kill();[void]$state.Child.WaitForExit(5000)
    }
    if(-not $state.Child.HasExited){throw 'No se confirmo la salida del CLI; codigo de salida desconocido.'}
    $state.ExitCode=$state.Child.ExitCode
    if($null -eq $state.ExitCode){throw 'Codigo de salida desconocido; no se puede validar la captura.'}
    foreach($name in @('Out','Err')){
        $task=$state[$name+'Task'];$stream=$state[$name+'Stream']
        try{
            if($task -and -not $task.Wait(5000)){throw ('Salida '+$name+' no se completo.')}
            if($stream){$stream.Flush()}
        }catch{if(-not $state.Fault){$state.Fault=$_.Exception.Message}}finally{if($stream){$stream.Dispose();$state[$name+'Stream']=$null}}
    }
    $state.ExitCaptured=$true
    if($state.ExitCode -ne 0 -and -not $state.Fault){$state.Fault='El CLI termino con codigo '+$state.ExitCode+'. Revisa wmi-only/cli-stderr.txt.'}
}
function Resume-WmiOnlyWatchdog($state) {
    if(-not $state.Paused -or $state.Resumed){return}
    Write-Host '4/4 Volver a iniciar el watchdog M4 previamente activo. No se reinstala ni cambia su inicio.'
    Start-Service -Name 'VictusFanControlWatchdogM4'
    (Get-Service -Name 'VictusFanControlWatchdogM4').WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    $state.Resumed=$true
}
function Test-WmiOnlyTrace([string]$Path,[int]$ExpectedPid) {
    $rows=@(Get-Content -LiteralPath $Path | ForEach-Object {$_ | ConvertFrom-Json})
    if($rows.Count -eq 0 -or $rows[0].kind -ne 'session' -or $rows[0].pid -ne $ExpectedPid){throw 'Identidad del log de aislamiento ausente o incorrecta.'}
    $begins=@{};$ends=@{}
    foreach($row in $rows){
        if($row.Stage -like 'ec.*' -or $row.Stage -like 'isolation.*.denied' -or $row.kind -in @('capture-limit-reached','records-dropped')){throw 'El log contiene intentos EC, operaciones rechazadas o evidencia truncada.'}
        if($row.Stage -eq 'wmi.send.begin' -and $row.Detail -ne 'command=0x20008;type=0x2D;output=128'){throw 'El log contiene una llamada HP distinta de la lectura RPM permitida.'}
        if($row.Stage -eq 'wmi.send.begin'){
            $key=[string]$row.Operation
            if(-not $key -or $begins.ContainsKey($key)){throw 'Identidad WMI ausente o repetida.'}
            $begins[$key]=$true
        }
        if($row.Stage -eq 'wmi.send.end'){$key=[string]$row.Operation;$ends[$key]=1+[int]$ends[$key]}
    }
    foreach($key in $begins.Keys){if($ends[$key] -ne 1){throw 'Una consulta WMI no tiene cierre nativo registrado.'}}
    foreach($key in $ends.Keys){if(-not $begins.ContainsKey($key)){throw 'Un cierre WMI no tiene inicio registrado.'}}
    foreach($stage in @('isolation.enabled','isolation.ready','isolation.finished','wmi.fan-levels')){
        if(@($rows | Where-Object {$_.Stage -eq $stage}).Count -eq 0){throw ('Falta evidencia '+$stage+'.')}
    }
    $finish=@($rows | Where-Object {$_.Stage -eq 'isolation.finished'})[-1]
    if($finish.Detail -ne 'deniedEc=0;deniedWmi=0'){throw 'La frontera de aislamiento rechazo una operacion.'}
}

if($SelfTest){
    $nativeRoot=Join-Path ([IO.Path]::GetTempPath()) ('vfc-wmi-native-'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($nativeRoot)|Out-Null
    try{
        foreach($code in @(0,37)){
            $native=@{Child=$null;Evidence=$nativeRoot;ExitCaptured=$false;ExitCode=$null;Fault=$null;OutTask=$null;ErrTask=$null;OutStream=$null;ErrStream=$null}
            try{
                $shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                Start-WmiOnlyChild $native $shell ('-NoProfile -NonInteractive -Command "[Console]::Out.WriteLine(''fixture-out'');[Console]::Error.WriteLine(''fixture-err'');exit '+$code+'"') $nativeRoot
                Stop-WmiOnlyChild $native
                if($native.ExitCode -ne $code -or -not $native.ExitCaptured){throw 'Codigo nativo de salida incorrecto.'}
                if(($code -eq 0 -and $native.Fault) -or ($code -ne 0 -and -not $native.Fault)){throw 'Clasificacion de salida incorrecta.'}
                Stop-WmiOnlyChild $native
                if((Get-Content (Join-Path $nativeRoot 'cli-stdout.txt') -Raw) -notmatch 'fixture-out' -or (Get-Content (Join-Path $nativeRoot 'cli-stderr.txt') -Raw) -notmatch 'fixture-err'){throw 'Streams no vaciados antes de empaquetar.'}
            }finally{if($native.Child){$native.Child.Dispose()}}
        }
    }finally{Remove-Item -LiteralPath $nativeRoot -Recurse -Force}
    $empty=[pscustomobject]@{Journals=@();Services=@();Processes=@()}
    Assert-WmiOnlyFacts $empty
    $m4=[pscustomobject]@{Name='VictusFanControlWatchdogM4';State='Running';ProcessId=7}
    $allowed=[pscustomobject]@{Journals=@();Services=@($m4);Processes=@([pscustomobject]@{Name='VictusFanControl.Watchdog';Id=7})}
    Assert-WmiOnlyFacts $allowed -AllowRunningM4
    $rejections=0
    foreach($facts in @(
        [pscustomobject]@{Journals=@('retained-or-empty-lease.json');Services=@();Processes=@()},
        [pscustomobject]@{Journals=@();Services=@($m4);Processes=@()},
        [pscustomobject]@{Journals=@();Services=@();Processes=@([pscustomobject]@{Name='VictusFanControl.App';Id=8})},
        [pscustomobject]@{Journals=@();Services=@();Processes=@([pscustomobject]@{Name='VictusFanControl.Watchdog';Id=7})},
        [pscustomobject]@{Journals=@();Services=@([pscustomobject]@{Name='VictusFanControlWatchdogM2';State='Running';ProcessId=9});Processes=@()}
    )){
        $rejected=$false;try{Assert-WmiOnlyFacts $facts}catch{$rejected=$true}
        if(-not $rejected){throw 'Una condicion de aislamiento peligrosa fue aceptada.'};$rejections++
    }
    $own=[pscustomobject]@{Journals=@();Services=@();Processes=@([pscustomobject]@{Name='VictusFanControl';Id=42})}
    Assert-WmiOnlyFacts $own -AllowedCliPid 42
    $expected=Join-Path ([IO.Path]::GetTempPath()) 'vfc state\m4.status.json'
    Assert-WmiOnlyM4Path ('watchdog.exe --result-path "'+$expected+'"') $expected
    $rejected=$false;try{Assert-WmiOnlyM4Path 'watchdog.exe --result-path unexpected.json' $expected}catch{$rejected=$true}
    if(-not $rejected){throw 'Una ruta personalizada del journal fue aceptada.'}
    $temp=Join-Path ([IO.Path]::GetTempPath()) ('vfc-wmi-only-fixture-'+[Guid]::NewGuid().ToString('N')+'.log')
    try{
        $rows=@([pscustomobject]@{kind='session';pid=42})
        $rows+=@([pscustomobject]@{Stage='wmi.send.begin';Operation=1;Detail='command=0x20008;type=0x2D;output=128'},[pscustomobject]@{Stage='wmi.send.end';Operation=1})
        foreach($s in @('isolation.enabled','isolation.ready','wmi.fan-levels','isolation.finished')){$rows+=@([pscustomobject]@{Stage=$s;Detail=$(if($s -eq 'isolation.finished'){'deniedEc=0;deniedWmi=0'}else{''})})}
        [IO.File]::WriteAllLines($temp,@($rows | ForEach-Object {ConvertTo-Json -Compress -InputObject $_}),(New-Object Text.UTF8Encoding($false)))
        Test-WmiOnlyTrace $temp 42
        foreach($bad in @([pscustomobject]@{Stage='ec.read.begin'},[pscustomobject]@{Stage='isolation.wmi-request.denied'},[pscustomobject]@{kind='records-dropped'},[pscustomobject]@{Stage='wmi.send.begin';Detail='command=0x20008;type=0x2E;output=0'},[pscustomobject]@{Stage='wmi.send.begin';Operation=2;Detail='command=0x20008;type=0x2D;output=128'})){
            [IO.File]::WriteAllLines($temp,@(($rows+@($bad)) | ForEach-Object {ConvertTo-Json -Compress -InputObject $_}),(New-Object Text.UTF8Encoding($false)))
            $rejected=$false;try{Test-WmiOnlyTrace $temp 42}catch{$rejected=$true};if(-not $rejected){throw 'Evidencia de aislamiento contaminada fue aceptada.'}
        }
    }finally{Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}
    Write-Host ('PASS: WMI-only clean/dirty lease, GUI/CLI/service isolation, custom state paths and contaminated chronology; rejected cases='+$rejections)
    return
}

if($env:OS -ne 'Windows_NT'){throw 'Esta prueba requiere Windows.'}
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Abre PowerShell como administrador.'}
if(-not ('VictusWmiOnly.PowerSnapshot' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace VictusWmiOnly {
    [StructLayout(LayoutKind.Sequential)] public struct PowerStatus {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    public static class PowerSnapshot {
        [DllImport("kernel32.dll", SetLastError=true)]
        public static extern bool GetSystemPowerStatus(out PowerStatus status);
    }
}
'@
}
Assert-WmiOnlyFacts (Get-WmiOnlyFacts) -AllowRunningM4
Push-Location $root
try{
    Write-Host '1/4 Compilar Release y verificar nuevamente que no haya control pendiente.'
    & dotnet build .\VictusFanControl.sln -c Release
    if($LASTEXITCODE -ne 0){throw 'La compilacion fallo.'}
    Assert-WmiOnlyFacts (Get-WmiOnlyFacts) -AllowRunningM4
    $script:WmiOnlyState.Cli=Join-Path $root 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.exe'
    $script:WmiOnlyState.Modules=Join-Path $root 'modules'
    if(-not(Test-Path -LiteralPath $script:WmiOnlyState.Cli)){throw 'CLI compilado no encontrado.'}
    $service=Get-Service -Name 'VictusFanControlWatchdogM4' -ErrorAction SilentlyContinue
    $script:WmiOnlyState.WasRunning=($service -and $service.Status -eq 'Running')
    if($script:WmiOnlyState.WasRunning){
        Write-Host '2/4 Detener temporalmente el watchdog M4 SIN lease. Se volvera a iniciar al finalizar.'
        $script:WmiOnlyState.Paused=$true
        Stop-Service -Name $service.Name
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
    }else{Write-Host '2/4 Watchdog ya detenido o no instalado; su configuracion se conserva.'}
    Assert-WmiOnlyFacts (Get-WmiOnlyFacts)
    Write-Host '3/4 Preparar ETW y evidencias; luego abrir SOLO el CLI de lectura.'
    Write-Host 'Al aparecer WMI-ONLY ACTIVO, usa el equipo normalmente. Los ventiladores quedan bajo firmware.'
    Write-Host 'No abras la GUI ni otros arneses VFC durante esta comparacion. Q termina y genera el ZIP.'

    $start={param($captureRoot,$state)
        $state.CaptureRoot=$captureRoot;$state.Evidence=Join-Path $captureRoot 'wmi-only'
        [IO.Directory]::CreateDirectory($state.Evidence)|Out-Null
        try{
            Assert-WmiOnlyFacts (Get-WmiOnlyFacts)
            if($state.ScenarioB){$state.InitialSystemRecordId=[long](Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop).RecordId}
            $state.StartedUtc=[DateTimeOffset]::UtcNow.ToString('o')
            $cliArgs=@('--wmi-only-investigation','--modules-dir',('"'+$state.Modules+'"'),'--interval-ms','1000',
                '--output',('"'+(Join-Path $state.Evidence 'telemetry.csv')+'"'),
                '--stop-file',('"'+(Join-Path $state.Evidence 'stop.signal')+'"'),
                '--ready-file',('"'+(Join-Path $state.Evidence 'ready.json')+'"'))
            $previous=$env:VFC_EC_WMI_DIAGNOSTICS
            try{
                $env:VFC_EC_WMI_DIAGNOSTICS='1'
                Start-WmiOnlyChild $state $state.Cli ($cliArgs -join ' ') $state.Root
            }finally{$env:VFC_EC_WMI_DIAGNOSTICS=$previous}
            $timer=[Diagnostics.Stopwatch]::StartNew()
            while(-not(Test-Path -LiteralPath (Join-Path $state.Evidence 'ready.json'))){
                if($state.Child.HasExited){throw 'El CLI de aislamiento no pudo iniciar; revisa cli-stderr.txt.'}
                if($timer.Elapsed.TotalSeconds -gt 30){throw 'El CLI no confirmo el inicio dentro de 30 segundos.'}
                Start-Sleep -Milliseconds 100
            }
            $ready=Get-Content -LiteralPath (Join-Path $state.Evidence 'ready.json') -Raw | ConvertFrom-Json
            if($ready.Pid -ne $state.Child.Id -or -not $ready.DirectEcProhibited -or -not $ready.HpWritesProhibited -or $ready.Mode -ne 'wmi-only-no-direct-ec'){throw 'Frontera de aislamiento no confirmada.'}
            Write-Host ('ESCENARIO B / WMI-ONLY ACTIVO: PID='+$state.Child.Id+'. Comienza ahora la comparacion; no usa Manual ni lectura EC directa.') -ForegroundColor Green
        }catch{$state.Fault=$_.Exception.Message;throw}
    }
    $guard={param($captureRoot,$state)
        try{
            if(-not $state.Child -or $state.Child.HasExited){throw 'El CLI termino antes del cierre solicitado.'}
            $facts=Get-WmiOnlyFacts;Assert-WmiOnlyFacts $facts -AllowedCliPid $state.Child.Id
            $power=New-Object VictusWmiOnly.PowerStatus
            $powerAvailable=[VictusWmiOnly.PowerSnapshot]::GetSystemPowerStatus([ref]$power)
            $row=[pscustomobject]@{Utc=[DateTimeOffset]::UtcNow.ToString('o');CliPid=$state.Child.Id;WatchdogStates=@($facts.Services | Select-Object Name,State,ProcessId);RelevantProcesses=$facts.Processes;JournalPresent=($facts.Journals.Count -gt 0);WindowsPowerAvailable=$powerAvailable;WindowsPower=$(if($powerAvailable){$power}else{$null})}
            [IO.File]::AppendAllText((Join-Path $state.Evidence 'isolation-timeline.ndjson'),(ConvertTo-Json -Compress -InputObject $row -Depth 6)+[Environment]::NewLine,(New-Object Text.UTF8Encoding($false)))
            if($state.ScenarioB){
                $latest=Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop
                if([long]$latest.RecordId -lt $state.InitialSystemRecordId){throw 'System se reinicio durante B; comparacion invalidada.'}
                $raw=@()
                try{$raw=@(Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery $state.InitialSystemRecordId) -MaxEvents 20 -ErrorAction Stop)}catch{if($_.FullyQualifiedErrorId -notmatch '^NoMatchingEventsFound'){throw}}
                $events=@(Select-ScenarioANewEvents $raw $state.InitialSystemRecordId ([DateTimeOffset]::Parse($state.StartedUtc)))
                if($events.Count){
                    $state.Acpi13=@($events | Select-Object @{Name='TimeCreatedUtc';Expression={([DateTimeOffset]::Parse(([xml]$_.ToXml()).Event.System.TimeCreated.SystemTime)).ToUniversalTime().ToString('o')}},RecordId,Id,ProviderName,Message)
                    Write-WmiOnlyJson (Join-Path $state.Evidence 'acpi13-detected.json') $state.Acpi13
                    $state.StopReason='new-acpi13'
                    throw 'ACPI 13 nuevo durante B: terminar y preservar cronologia.'
                }
            }
            if([DateTimeOffset]::UtcNow -ge $state.NextReport){
                $state.NextReport=[DateTimeOffset]::UtcNow.AddSeconds(30)
                Write-Host ('WMI-ONLY activo: '+(Get-Content -LiteralPath (Join-Path $state.Evidence 'cli-stdout.txt') -Tail 1 -ErrorAction SilentlyContinue))
            }
        }catch{if($state.StopReason -ne 'new-acpi13'){$state.Fault=$_.Exception.Message};throw}
    }
    $finish={param($captureRoot,$state)
        if(-not $state.Evidence){$state.Evidence=Join-Path $captureRoot 'wmi-only';[IO.Directory]::CreateDirectory($state.Evidence)|Out-Null}
        try{
            Stop-WmiOnlyChild $state
            Assert-WmiOnlyFacts (Get-WmiOnlyFacts)
            if(-not $state.Child){throw 'No se inicio el CLI de aislamiento.'}
            $logs=@(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'VictusFanControl\logs') -Filter ('ec-wmi-'+$state.Child.Id+'-*.log') -File | Where-Object {$_.LastWriteTimeUtc -ge [DateTime]::Parse($state.StartedUtc).ToUniversalTime()})
            if($logs.Count -ne 1){throw 'No hay un unico log de cronologia del proceso aislado.'}
            Copy-Item -LiteralPath $logs[0].FullName -Destination (Join-Path $state.Evidence 'chronology.log')
            Test-WmiOnlyTrace (Join-Path $state.Evidence 'chronology.log') $state.Child.Id
            $state.ValidIsolation=(-not $state.Fault)
            Write-Host 'Analisis offline: separar tiempos WMI nativos, preparacion y cierre; no consulta hardware.'
            try{
                & $state.Cli --analyze-ec-wmi-trace (Join-Path $state.Evidence 'chronology.log') --analysis-output-dir (Join-Path $state.Evidence 'analysis')
                if($LASTEXITCODE -ne 0){$state.AnalysisFault='El analizador offline termino con codigo '+$LASTEXITCODE+'. El log original se conserva.'}
            }catch{$state.AnalysisFault=$_.Exception.Message}
            if($state.AnalysisFault){Write-Host ('INFORME OFFLINE INCOMPLETO: '+$state.AnalysisFault) -ForegroundColor Yellow}
        }catch{if(-not $state.Fault){$state.Fault=$_.Exception.Message}}
        finally{
            try{Resume-WmiOnlyWatchdog $state}catch{
                $state.ValidIsolation=$false
                $state.Fault='No se pudo volver a iniciar el watchdog M4: '+$_.Exception.Message
            }
            Write-WmiOnlyJson (Join-Path $state.Evidence 'isolation-summary.json') ([pscustomobject]@{
                Mode='wmi-only-no-direct-ec';ValidIsolation=$state.ValidIsolation;Fault=$state.Fault;StartedUtc=$state.StartedUtc;FinishedUtc=[DateTimeOffset]::UtcNow.ToString('o');
                Scenario=$(if($state.ScenarioB){'B'}else{'WMI-only'});DetectorVersion=2;InitialSystemRecordId=$state.InitialSystemRecordId;StopReason=$state.StopReason;Acpi13=$state.Acpi13;
                CliPid=$(if($state.Child){$state.Child.Id}else{$null});ExitCode=$state.ExitCode;ExitCodeCaptured=$state.ExitCaptured;
                WatchdogWasRunning=$state.WasRunning;WatchdogTemporarilyStopped=$state.Paused;WatchdogResumed=$state.Resumed;NoFanCommands=$true;AnalysisFault=$state.AnalysisFault;
                Scope='VFC process boundary and sampled process/service isolation; Windows/firmware may still access EC; external tools are not excluded.'
            })
            if($state.Fault){Write-Host ('AISLAMIENTO INCOMPLETO: '+$state.Fault+'. Se conserva el ZIP parcial.') -ForegroundColor Yellow}
        }
    }
    & (Join-Path $PSScriptRoot 'Collect-Victus-WmiTimeout.ps1') -RepoRoot $root -OutputRoot $OutputRoot -CaptureMinutes $CaptureMinutes `
        -MinimalPreparation:$ScenarioB -SkipAcpiTrace:$SkipAcpiTrace -InvestigationMode 'wmi-only-no-direct-ec' -ObservationStarted $start -ObservationGuard $guard -ObservationFinished $finish -ObservationContext $script:WmiOnlyState
    if(-not $script:WmiOnlyState.ValidIsolation){throw ('La comparacion no quedo validada: '+$script:WmiOnlyState.Fault+'. Adjunta igualmente el ZIP para revisar la evidencia.')}
    Write-Host 'AISLAMIENTO VERIFICADO: sin intentos EC directos ni comandos de ventiladores en este CLI.' -ForegroundColor Green
}finally{
    try{Stop-WmiOnlyChild $script:WmiOnlyState}finally{
        try{Resume-WmiOnlyWatchdog $script:WmiOnlyState}finally{if($script:WmiOnlyState.Child){$script:WmiOnlyState.Child.Dispose()};Pop-Location}
    }
}
