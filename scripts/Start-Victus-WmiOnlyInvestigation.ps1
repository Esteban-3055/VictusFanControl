# Windows PowerShell 5.1 / PowerShell 7. Read-only isolation; no fan writes.
[CmdletBinding()]
param(
    [ValidateRange(1,60)][int]$CaptureMinutes=30,
    [string]$OutputRoot='',
    [switch]$SkipAcpiTrace,
    [switch]$ScenarioB,
    [switch]$ScenarioC,
    [ValidateRange(2,60)][int]$EcIntervalSeconds=5,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
if($ScenarioB -and $ScenarioC){throw 'Selecciona B o C, no ambos.'}
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ScenarioAcpiEventFilter.ps1')
$script:WmiOnlyState=@{Child=$null;ExitCode=$null;ExitCaptured=$false;OutTask=$null;ErrTask=$null;OutStream=$null;ErrStream=$null;ScenarioB=[bool]($ScenarioB -or $ScenarioC);ScenarioC=[bool]$ScenarioC;EcIntervalSeconds=$EcIntervalSeconds;Mode=$(if($ScenarioC){'scenario-c-residual-ec'}else{'wmi-only-no-direct-ec'});EvidenceName=$(if($ScenarioC){'scenario-c'}else{'wmi-only'});InitialSystemRecordId=$null;Acpi13=@();StopReason='duration-or-Q';Fault=$null;CaptureRoot=$null;Evidence=$null;WasRunning=$false;Paused=$false;ValidIsolation=$false;NextReport=[DateTimeOffset]::MinValue;Root=$root;Resumed=$false}

$script:WmiOnlyState.AcpiEvents=@()

function Get-WmiOnlyProgress($state) {
    if($state.ScenarioC){
        $path=Join-Path $state.Evidence 'ec-control.csv'
        $samples=@(if(Test-Path -LiteralPath $path){Import-Csv -LiteralPath $path})
        if($samples.Count){
            $last=$samples[-1]
            return ('C: lotes EC='+$samples.Count+'; ultimo UTC='+$last.timestamp_utc+'; estado='+$last.status+'; duracion='+$last.duration_ms+' ms; consignas='+$last.cpu_setpoint_hex+'/'+$last.gpu_setpoint_hex+'; guardas='+$last.max_fan_hex+'/'+$last.fan_switch_hex)
        }
        return 'C: esperando la primera muestra EC; consulta ec-control.csv y cli-stderr.txt.'
    }
    return ($state.Mode+' activo: '+(Get-Content -LiteralPath (Join-Path $state.Evidence 'cli-stdout.txt') -Tail 1 -ErrorAction SilentlyContinue))
}

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
function Test-WmiOnlyTrace([string]$Path,[int]$ExpectedPid,[switch]$AllowResidualEc) {
    $rows=@(Get-Content -LiteralPath $Path | ForEach-Object {$_ | ConvertFrom-Json})
    if($rows.Count -eq 0 -or $rows[0].kind -ne 'session' -or $rows[0].pid -ne $ExpectedPid){throw 'Identidad del log de aislamiento ausente o incorrecta.'}
    $begins=@{};$ends=@{};$ecBegins=@{};$ecEnds=@{};$observed=@{}
    foreach($row in $rows){
        if(($row.Stage -like 'ec.*' -and -not $AllowResidualEc) -or $row.Stage -like 'isolation.*.denied' -or $row.kind -in @('capture-limit-reached','records-dropped')){throw 'El log contiene intentos EC no permitidos, operaciones rechazadas o evidencia truncada.'}
        if($AllowResidualEc -and $row.Stage -like 'ec.*'){
            if($row.Stage -in @('ec.read.failure','ec.mutex.timeout')){throw 'C contiene un fallo de transaccion EC; no es una observacion saludable aunque un lote se completara.'}
            if($row.Stage -notin @('ec.read.begin','ec.read.end','ec.read.failure','ec.command.sent','ec.address.sent','ec.mutex.wait.begin','ec.mutex.acquired','ec.mutex.abandoned-acquired','ec.mutex.released','ec.mutex.timeout')){throw 'Operacion EC desconocida durante C.'}
            if($row.Stage -eq 'ec.command.sent' -and $row.Detail -ne 'RD_EC=0x80'){throw 'Comando EC no permitido.'}
            if($row.Stage -eq 'ec.read.begin'){
                $key=[string]$row.Operation
                if($row.Detail -notmatch '^register=(0x34|0x35|0xEC|0xF4)$' -or -not $key -or $ecBegins.ContainsKey($key)){throw 'Registro EC o identidad fuera del escenario C.'}
                $ecBegins[$key]=$Matches[1];$observed[$Matches[1]]=$true
            }
            if($row.Stage -in @('ec.read.end','ec.read.failure')){
                $key=[string]$row.Operation
                if(-not $ecBegins.ContainsKey($key) -or $row.Detail -notlike ('register='+$ecBegins[$key]+';*')){throw 'Cierre EC sin inicio/registro correspondiente.'}
                $ecEnds[$key]=1+[int]$ecEnds[$key]
            }
        }
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
    if($AllowResidualEc){
        foreach($register in @('0x34','0x35','0xEC','0xF4')){if(-not $observed.ContainsKey($register)){throw ('Falta lectura EC '+$register+'.')}}
        foreach($key in $ecBegins.Keys){if($ecEnds[$key] -ne 1){throw 'Lectura EC sin un unico cierre.'}}
        if(@($rows | Where-Object {$_.Stage -eq 'isolation.ec-sample.end'}).Count -eq 0){throw 'C no contiene una muestra EC completa.'}
        if(@($rows | Where-Object {$_.Stage -eq 'isolation.ec-sample.failure'}).Count -gt 0){throw 'C termino tras una muestra EC fallida.'}
    }
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
        $progressState=@{ScenarioC=$true;Evidence=$nativeRoot}
        if((Get-WmiOnlyProgress $progressState) -notmatch 'esperando'){throw 'Progreso EC inventado antes de la primera muestra.'}
        [IO.File]::WriteAllText((Join-Path $nativeRoot 'ec-control.csv'),"timestamp_utc,elapsed_ms,duration_ms,cpu_setpoint_hex,gpu_setpoint_hex,max_fan_hex,fan_switch_hex,status,error`n2026-10-03T21:34:15Z,5395,1.054,0xFF,0xFF,0x00,0x00,complete,`n")
        $progress=Get-WmiOnlyProgress $progressState
        if($progress -notmatch 'lotes EC=1' -or $progress -notmatch '1.054 ms' -or $progress -notmatch '21:34:15Z'){throw 'No se muestra la ultima muestra EC real.'}
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
        $cRows=@($rows)
        $operation=10
        foreach($register in @('0x34','0x35','0xEC','0xF4')){
            $cRows+=@([pscustomobject]@{Stage='ec.read.begin';Operation=$operation;Detail=('register='+$register)},
                [pscustomobject]@{Stage='ec.command.sent';Operation=$operation;Detail='RD_EC=0x80'},
                [pscustomobject]@{Stage='ec.read.end';Operation=$operation;Detail=('register='+$register+';value=0xFF')})
            $operation++
        }
        $cRows+=@([pscustomobject]@{Stage='isolation.ec-sample.end';Detail='sample=1'})
        [IO.File]::WriteAllLines($temp,@($cRows | ForEach-Object {ConvertTo-Json -Compress -InputObject $_}),(New-Object Text.UTF8Encoding($false)))
        Test-WmiOnlyTrace $temp 42 -AllowResidualEc
        foreach($bad in @([pscustomobject]@{Stage='ec.read.begin';Operation=20;Detail='register=0xB0'},
            [pscustomobject]@{Stage='ec.write.begin';Operation=20},
            [pscustomobject]@{Stage='ec.command.sent';Operation=20;Detail='WR_EC=0x81'},
            [pscustomobject]@{Stage='ec.read.begin';Operation=20;Detail='register=0x34'},
            [pscustomobject]@{Stage='ec.read.end';Operation=20;Detail='register=0x34;value=0xFF'},
            [pscustomobject]@{Stage='ec.read.failure';Operation=10;Detail='register=0x34;TimeoutException: fixture'},
            [pscustomobject]@{Stage='isolation.ec-sample.failure';Detail='fixture'})){
            [IO.File]::WriteAllLines($temp,@(($cRows+@($bad)) | ForEach-Object {ConvertTo-Json -Compress -InputObject $_}),(New-Object Text.UTF8Encoding($false)))
            $rejected=$false;try{Test-WmiOnlyTrace $temp 42 -AllowResidualEc}catch{$rejected=$true}
            if(-not $rejected){throw 'Cronologia C fuera de alcance o incompleta fue aceptada.'}
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
    Write-Host 'Al aparecer ESCENARIO C ACTIVO o WMI-ONLY ACTIVO, usa el equipo normalmente. Los ventiladores quedan bajo firmware.'
    Write-Host 'No abras la GUI ni otros arneses VFC durante esta comparacion. Q termina y genera el ZIP.'

    $start={param($captureRoot,$state)
        $state.CaptureRoot=$captureRoot;$state.Evidence=Join-Path $captureRoot $state.EvidenceName
        [IO.Directory]::CreateDirectory($state.Evidence)|Out-Null
        try{
            Assert-WmiOnlyFacts (Get-WmiOnlyFacts)
            if($state.ScenarioB){$state.InitialSystemRecordId=[long](Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop).RecordId}
            $state.StartedUtc=[DateTimeOffset]::UtcNow.ToString('o')
            $cliMode=$(if($state.ScenarioC){'--residual-ec-investigation'}else{'--wmi-only-investigation'})
            $cliArgs=@($cliMode,'--modules-dir',('"'+$state.Modules+'"'),'--interval-ms','1000',
                '--output',('"'+(Join-Path $state.Evidence 'telemetry.csv')+'"'),
                '--stop-file',('"'+(Join-Path $state.Evidence 'stop.signal')+'"'),
                '--ready-file',('"'+(Join-Path $state.Evidence 'ready.json')+'"'))
            if($state.ScenarioC){$cliArgs+=@('--ec-interval-ms',[string]($state.EcIntervalSeconds*1000))}
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
            if($ready.Pid -ne $state.Child.Id -or -not $ready.HpWritesProhibited -or $ready.Mode -ne $state.Mode){throw 'Frontera de aislamiento no confirmada.'}
            if($state.ScenarioC){
                if($ready.DirectEcProhibited -or -not $ready.ResidualEcOnly -or $ready.EcIntervalMs -ne ($state.EcIntervalSeconds*1000) -or (@($ready.EcRegisters) -join ',') -ne '0x34,0x35,0xEC,0xF4'){throw 'Registros o intervalo EC de C no confirmados.'}
                Write-Host ('ESCENARIO C ACTIVO: PID='+$state.Child.Id+'; solo 34/35/EC/F4 cada '+$state.EcIntervalSeconds+' s; sin Manual ni escrituras HP.') -ForegroundColor Green
            }elseif(-not $ready.DirectEcProhibited){throw 'EC directo no bloqueado en B.'}
            if(-not $state.ScenarioC){Write-Host ('ESCENARIO B / WMI-ONLY ACTIVO: PID='+$state.Child.Id+'. Comienza ahora la comparacion; no usa Manual ni lectura EC directa.') -ForegroundColor Green}
        }catch{$state.Fault=$_.Exception.Message;throw}
    }
    $guard={param($captureRoot,$state)
        try{
            if(-not $state.Child -or $state.Child.HasExited){$state.StopReason='cli-ended';throw 'El CLI termino antes del cierre solicitado; revisar stderr y ec-control.csv.'}
            $facts=Get-WmiOnlyFacts;Assert-WmiOnlyFacts $facts -AllowedCliPid $state.Child.Id
            $power=New-Object VictusWmiOnly.PowerStatus
            $powerAvailable=[VictusWmiOnly.PowerSnapshot]::GetSystemPowerStatus([ref]$power)
            $row=[pscustomobject]@{Utc=[DateTimeOffset]::UtcNow.ToString('o');CliPid=$state.Child.Id;WatchdogStates=@($facts.Services | Select-Object Name,State,ProcessId);RelevantProcesses=$facts.Processes;JournalPresent=($facts.Journals.Count -gt 0);WindowsPowerAvailable=$powerAvailable;WindowsPower=$(if($powerAvailable){$power}else{$null})}
            [IO.File]::AppendAllText((Join-Path $state.Evidence 'isolation-timeline.ndjson'),(ConvertTo-Json -Compress -InputObject $row -Depth 6)+[Environment]::NewLine,(New-Object Text.UTF8Encoding($false)))
            if($state.ScenarioB){
                $latest=Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop
                if([long]$latest.RecordId -lt $state.InitialSystemRecordId){throw 'System se reinicio durante B/C; comparacion invalidada.'}
                $raw=@()
                try{$raw=@(Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery $state.InitialSystemRecordId -IncludeUnexpectedData) -MaxEvents 20 -ErrorAction Stop)}catch{if($_.FullyQualifiedErrorId -notmatch '^NoMatchingEventsFound'){throw}}
                $events=@(Select-ScenarioANewEvents $raw $state.InitialSystemRecordId ([DateTimeOffset]::Parse($state.StartedUtc)) -IncludeUnexpectedData)
                if($events.Count){
                    $state.AcpiEvents=@($events | Select-Object @{Name='TimeCreatedUtc';Expression={([DateTimeOffset]::Parse(([xml]$_.ToXml()).Event.System.TimeCreated.SystemTime)).ToUniversalTime().ToString('o')}},RecordId,Id,ProviderName,Message)
                    $state.Acpi13=@($state.AcpiEvents | Where-Object {$_.Id -eq 13})
                    Write-WmiOnlyJson (Join-Path $state.Evidence 'acpi-events-detected.json') $state.AcpiEvents
                    if($state.Acpi13.Count){Write-WmiOnlyJson (Join-Path $state.Evidence 'acpi13-detected.json') $state.Acpi13}
                    $state.StopReason=if($state.Acpi13.Count){'new-acpi13'}else{'new-acpi15'}
                    throw 'ACPI 13/15 nuevo durante B/C: terminar y preservar cronologia.'
                }
            }
            if([DateTimeOffset]::UtcNow -ge $state.NextReport){
                $state.NextReport=[DateTimeOffset]::UtcNow.AddSeconds($(if($state.ScenarioC){5}else{30}))
                Write-Host (Get-WmiOnlyProgress $state)
            }
        }catch{if($state.StopReason -notin @('new-acpi13','new-acpi15')){$state.Fault=$_.Exception.Message};throw}
    }
    $finish={param($captureRoot,$state)
        if(-not $state.Evidence){$state.Evidence=Join-Path $captureRoot $state.EvidenceName;[IO.Directory]::CreateDirectory($state.Evidence)|Out-Null}
        try{
            Stop-WmiOnlyChild $state
            Assert-WmiOnlyFacts (Get-WmiOnlyFacts)
            if(-not $state.Child){throw 'No se inicio el CLI de aislamiento.'}
            $logs=@(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'VictusFanControl\logs') -Filter ('ec-wmi-'+$state.Child.Id+'-*.log') -File | Where-Object {$_.LastWriteTimeUtc -ge [DateTime]::Parse($state.StartedUtc).ToUniversalTime()})
            if($logs.Count -ne 1){throw 'No hay un unico log de cronologia del proceso aislado.'}
            Copy-Item -LiteralPath $logs[0].FullName -Destination (Join-Path $state.Evidence 'chronology.log')
            Write-Host 'Analisis offline: separar tiempos WMI nativos, preparacion y cierre; no consulta hardware.'
            try{
                & $state.Cli --analyze-ec-wmi-trace (Join-Path $state.Evidence 'chronology.log') --analysis-output-dir (Join-Path $state.Evidence 'analysis')
                if($LASTEXITCODE -ne 0){$state.AnalysisFault='El analizador offline termino con codigo '+$LASTEXITCODE+'. El log original se conserva.'}
            }catch{$state.AnalysisFault=$_.Exception.Message}
            if($state.AnalysisFault){Write-Host ('INFORME OFFLINE INCOMPLETO: '+$state.AnalysisFault) -ForegroundColor Yellow}
            Test-WmiOnlyTrace (Join-Path $state.Evidence 'chronology.log') $state.Child.Id -AllowResidualEc:$state.ScenarioC
            $state.ValidIsolation=(-not $state.Fault)
        }catch{if(-not $state.Fault){$state.Fault=$_.Exception.Message}}
        finally{
            try{Resume-WmiOnlyWatchdog $state}catch{
                $state.ValidIsolation=$false
                $state.Fault='No se pudo volver a iniciar el watchdog M4: '+$_.Exception.Message
            }
            Write-WmiOnlyJson (Join-Path $state.Evidence 'isolation-summary.json') ([pscustomobject]@{
                Mode=$state.Mode;ValidIsolation=$state.ValidIsolation;Fault=$state.Fault;StartedUtc=$state.StartedUtc;FinishedUtc=[DateTimeOffset]::UtcNow.ToString('o');
                Scenario=$(if($state.ScenarioC){'C'}elseif($state.ScenarioB){'B'}else{'WMI-only'});EcIntervalSeconds=$(if($state.ScenarioC){$state.EcIntervalSeconds}else{$null});DetectorVersion=3;InitialSystemRecordId=$state.InitialSystemRecordId;StopReason=$state.StopReason;Acpi13=$state.Acpi13;AcpiEvents=$state.AcpiEvents;
                ObservationHealthy=($state.ValidIsolation -and $state.AcpiEvents.Count -eq 0);EcProtocolRetries=$(if($state.ScenarioC){0}else{$null});
                CliPid=$(if($state.Child){$state.Child.Id}else{$null});ExitCode=$state.ExitCode;ExitCodeCaptured=$state.ExitCaptured;
                WatchdogWasRunning=$state.WasRunning;WatchdogTemporarilyStopped=$state.Paused;WatchdogResumed=$state.Resumed;NoFanCommands=$true;AnalysisFault=$state.AnalysisFault;
                Scope='VFC process boundary and sampled process/service isolation; Windows/firmware may still access EC; external tools are not excluded.'
            })
            if($state.Fault){Write-Host ('AISLAMIENTO INCOMPLETO: '+$state.Fault+'. Se conserva el ZIP parcial.') -ForegroundColor Yellow}
        }
    }
    & (Join-Path $PSScriptRoot 'Collect-Victus-WmiTimeout.ps1') -RepoRoot $root -OutputRoot $OutputRoot -CaptureMinutes $CaptureMinutes `
        -MinimalPreparation:($ScenarioB -or $ScenarioC) -SkipAcpiTrace:$SkipAcpiTrace -InvestigationMode $script:WmiOnlyState.Mode -ObservationStarted $start -ObservationGuard $guard -ObservationFinished $finish -ObservationContext $script:WmiOnlyState
    if(-not $script:WmiOnlyState.ValidIsolation){throw ('La comparacion no quedo validada: '+$script:WmiOnlyState.Fault+'. Adjunta igualmente el ZIP para revisar la evidencia.')}
    Write-Host ('AISLAMIENTO VERIFICADO: modo='+$script:WmiOnlyState.Mode+'; sin comandos de ventiladores.') -ForegroundColor Green
}finally{
    try{Stop-WmiOnlyChild $script:WmiOnlyState}finally{
        try{Resume-WmiOnlyWatchdog $script:WmiOnlyState}finally{if($script:WmiOnlyState.Child){$script:WmiOnlyState.Child.Dispose()};Pop-Location}
    }
}
