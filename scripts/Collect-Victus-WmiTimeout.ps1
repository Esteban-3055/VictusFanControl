# Compatible with Windows PowerShell 5.1 and PowerShell 7.
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$OutputRoot = '',
    [ValidateRange(0,60)][int]$CaptureMinutes = 10,
    [ValidateRange(1,10)][int]$IntervalSeconds = 2,
    [ValidateRange(1,48)][int]$EventHours = 12,
    [ValidateRange(100,20000)][int]$MaximumEvents = 10000,
    [switch]$SkipAcpiTrace,
    [switch]$MinimalPreparation,
    [string]$IaslPath = '',
    [scriptblock]$ObservationStarted,
    [scriptblock]$ObservationGuard,
    [scriptblock]$ObservationFinished,
    [object]$ObservationContext,
    [ValidateSet('passive','wmi-only-no-direct-ec','scenario-a-no-vfc')][string]$InvestigationMode = 'passive',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:Warnings = New-Object 'System.Collections.Generic.List[string]'
$script:Copies = New-Object 'System.Collections.Generic.List[object]'
$script:Commands = New-Object 'System.Collections.Generic.List[object]'
$script:Root = $null
$script:ConsoleLog = $null
$script:TraceSessions = New-Object 'System.Collections.Generic.List[object]'
$script:TraceHistory = New-Object 'System.Collections.Generic.List[object]'
$script:Clock = [Diagnostics.Stopwatch]::StartNew()
$script:Regex = 'VictusFanControl|WmiPrvSE|OMEN|HP.*(System|Support|Hotkey|Gaming)|HWiNFO|HWMonitor|FanControl|OmenMon|Afterburner|RTSS|NVDisplay|nvcontainer|PawnIO'

function Save-Text([string]$Path,[string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path,$Text,$script:Utf8)
}
function Tick { return $script:Clock.ElapsedMilliseconds }
function Save-Json([string]$Path,$Value) { Save-Text $Path (ConvertTo-Json -InputObject $Value -Depth 12) }
function Say([string]$Message,[string]$Level='INFO') {
    $line = '{0} [{1}] {2}' -f [DateTimeOffset]::Now.ToString('o'),$Level,$Message
    Write-Host $line
    if ($script:ConsoleLog) { [IO.File]::AppendAllText($script:ConsoleLog,$line+[Environment]::NewLine,$script:Utf8) }
}
function Warn([string]$Message) { $script:Warnings.Add($Message); Say $Message 'WARN' }
function Stage([string]$Name,[scriptblock]$Action) {
    Say $Name
    try { & $Action } catch { Warn ($Name+': '+$_.Exception.Message) }
}
function Copy-Shared([string]$Source,[string]$Destination,[long]$MaximumBytes=8388608) {
    # Read a bounded snapshot while app/watchdog can continue writing/rotating.
    $stream = $null
    try {
        $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $stream = [IO.File]::Open($Source,[IO.FileMode]::Open,[IO.FileAccess]::Read,$share)
        $length = $stream.Length
        $offset = [Math]::Max([long]0,$length-$MaximumBytes)
        [void]$stream.Seek($offset,[IO.SeekOrigin]::Begin)
        $size = [int]($length-$offset)
        $buffer = New-Object byte[] $size
        $read = 0
        while ($read -lt $size) {
            $count = $stream.Read($buffer,$read,$size-$read)
            if ($count -eq 0) { break }
            $read += $count
        }
        if ($read -ne $size) { $buffer = [byte[]]$buffer[0..([Math]::Max(0,$read-1))]; if ($read -eq 0) { $buffer = New-Object byte[] 0 } }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
        [IO.File]::WriteAllBytes($Destination,$buffer)
        $script:Copies.Add([pscustomobject]@{Source=$Source;Destination=$Destination;ObservedUtc=[DateTimeOffset]::UtcNow.ToString('o');CollectorElapsedMs=(Tick);SourceLength=$length;StartOffset=$offset;CopiedBytes=$read;Truncated=($offset -gt 0);LiveFileSnapshot=$true})
        return $true
    } catch { Warn ('No se pudo copiar '+$Source+': '+$_.Exception.Message); return $false }
    finally { if ($stream) { $stream.Dispose() } }
}
function Invoke-Bounded([string]$File,[string]$Arguments,[string]$Destination,[int]$TimeoutSeconds=25,[string]$WorkingDirectory='') {
    Say ('Ejecutar lectura: '+$File+' '+$Arguments.Substring(0,[Math]::Min(160,$Arguments.Length)))
    $process = New-Object Diagnostics.Process
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $status = 'error'; $exitCode = $null
    try {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName=$File; $info.Arguments=$Arguments; $info.UseShellExecute=$false
        $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true; $info.CreateNoWindow=$true
        if ($WorkingDirectory) { $info.WorkingDirectory=$WorkingDirectory }
        $process.StartInfo=$info
        [void]$process.Start()
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds*1000)) {
            $status='timeout'; try { $process.Kill(); [void]$process.WaitForExit(1000) } catch { }
            Warn ('Timeout de la lectura auxiliar: '+$File+'; solo se detuvo el proceso creado por el recolector.')
        } else { $exitCode=$process.ExitCode; if ($exitCode -eq 0) {$status='ok'} else {$status='exit-nonzero'} }
        $out='[salida no completada]';$err='[errores no completados]'
        if ($stdout.Wait(2000)) {$out=$stdout.Result}; if ($stderr.Wait(2000)) {$err=$stderr.Result}
        Save-Text $Destination ($out+[Environment]::NewLine+'--- STDERR ---'+[Environment]::NewLine+$err)
        if ($status -eq 'exit-nonzero') { Warn ('Lectura auxiliar devolvio codigo '+$exitCode+': '+$Destination) }
    } catch { Save-Text $Destination $_.Exception.ToString(); Warn ('Lectura auxiliar no disponible: '+$File+'; '+$_.Exception.Message) }
    finally {
        $script:Commands.Add([pscustomobject]@{File=$File;Arguments=$Arguments;Destination=$Destination;Status=$status;ExitCode=$exitCode;DurationMs=$watch.ElapsedMilliseconds;EndedUtc=[DateTimeOffset]::UtcNow.ToString('o');CollectorElapsedMs=(Tick)})
        $process.Dispose()
    }
}
function Invoke-ReadScript([string]$Code,[string]$Destination,[int]$TimeoutSeconds=40) {
    Save-Text ($Destination+'.query.ps1') $Code
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Code))
    $shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Say ('Consulta Windows aislada -> '+$Destination)
    Invoke-Bounded $shell ('-NoProfile -NonInteractive -EncodedCommand '+$encoded) $Destination $TimeoutSeconds
}
function Process-Snapshot([string]$Label) {
    $utc=[DateTimeOffset]::UtcNow.ToString('o');$tick=Tick
    $processes=Get-Process
    if($Label -notin @('before','after')){$processes=@($processes|Where-Object {$_.ProcessName -match $script:Regex})}
    $rows=@($processes | ForEach-Object {
        $p=$_; $relevant=$p.ProcessName -match $script:Regex
        $path=$null;$started=$null;$cpu=$null;$threads=$null;$handles=$null
        try {$cpu=$p.CPU} catch { }
        if ($relevant) { try {$threads=$p.Threads.Count;$handles=$p.HandleCount;$path=$p.Path;$started=$p.StartTime.ToUniversalTime().ToString('o')} catch { } }
        [pscustomobject]@{CapturedUtc=$utc;CollectorElapsedMs=$tick;Id=$p.Id;Name=$p.ProcessName;Relevant=$relevant;CPUSeconds=$cpu;Threads=$threads;Handles=$handles;WorkingSetBytes=$p.WorkingSet64;Path=$path;StartUtc=$started}
    })
    Save-Json (Join-Path $script:Root ('processes\'+$Label+'.json')) $rows
    $live=@($rows | Where-Object {$_.Relevant})
    foreach($row in $live) {
        [IO.File]::AppendAllText((Join-Path $script:Root 'process-timeline.ndjson'),(ConvertTo-Json -InputObject $row -Compress)+[Environment]::NewLine,$script:Utf8)
    }
    return $rows
}
function Capture-Logs([string]$Label) {
    $sources=@(@{Root=(Join-Path $env:LOCALAPPDATA 'VictusFanControl\logs');Tag='app'},@{Root=(Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\logs');Tag='watchdog'})
    foreach($source in $sources) {
        if (-not(Test-Path -LiteralPath $source.Root)) { Warn ('Carpeta ausente: '+$source.Root);continue }
        $files=@(Get-ChildItem -LiteralPath $source.Root -File | Where-Object {$_.Name -match '\.log(\.1)?$'} | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 16)
        foreach($file in $files) {[void](Copy-Shared $file.FullName (Join-Path $script:Root ('logs\'+$Label+'\'+$source.Tag+'\'+$file.Name)))}
        Say ('Logs '+$source.Tag+': '+$files.Count+' archivos; maximo 8 MiB por archivo, truncamiento registrado.')
    }
}
function Export-Events([string]$Label) {
    foreach($channel in @('System','Application','Microsoft-Windows-WMI-Activity/Operational','Microsoft-Windows-Kernel-Power/Thermal-Operational','Microsoft-Windows-Kernel-PnP/Configuration')) {
        $safe=$channel.Replace('/','_')
        $destination=Join-Path $script:Root ('windows-events\'+$Label+'\'+$safe+'.txt')
        $code=@"
`$ErrorActionPreference='Stop'
try {
    `$log=Get-WinEvent -ListLog '$channel' -ErrorAction Stop
    'CHANNEL '+(`$log|Select-Object LogName,IsEnabled,RecordCount,LastWriteTime,MaximumSizeInBytes|ConvertTo-Json -Compress)
    if(-not `$log.IsEnabled){'CHANNEL_DISABLED_NO_CHANGE';exit 0}
    `$rows=@(Get-WinEvent -FilterHashtable @{LogName='$channel';StartTime=(Get-Date).AddHours(-$EventHours)} -MaxEvents $MaximumEvents -ErrorAction Stop)
    'Returned='+`$rows.Count+';Limit=$MaximumEvents;NewestFirst=True'
    foreach(`$event in `$rows){
        `$message=`$null;try{`$message=`$event.Message}catch{`$message='MESSAGE_UNAVAILABLE: '+`$_.Exception.Message}
        'MESSAGE_JSON '+([pscustomobject]@{TimeCreated=`$event.TimeCreated.ToString('o');RecordId=`$event.RecordId;Provider=`$event.ProviderName;Id=`$event.Id;Message=`$message}|ConvertTo-Json -Compress)
        `$event.ToXml()
    }
} catch {if(`$_.FullyQualifiedErrorId -match 'NoMatchingEventsFound'){'NO_MATCHING_EVENTS';exit 0};'CHANNEL_UNAVAILABLE '+`$_.Exception.Message;exit 0}
"@
        Invoke-ReadScript $code $destination 45
        if((Get-Content -LiteralPath $destination -Raw) -match '(?m)^CHANNEL_UNAVAILABLE|CHANNEL_DISABLED_NO_CHANGE'){Say ('Canal ausente/desactivado; se conserva su estado: '+$channel);continue}
        # EVTX includes provider context for independent Event Viewer inspection.
        $evtx=Join-Path $script:Root ('windows-events\'+$Label+'\'+$safe+'.evtx')
        $milliseconds=[long]$EventHours*3600000
        Invoke-Bounded 'wevtutil.exe' ('epl "'+$channel+'" "'+$evtx+'" /q:"*[System[TimeCreated[timediff(@SystemTime) <= '+$milliseconds+']]]" /ow:true') ($evtx+'.export.txt') 30
    }
}
function Capture-AcpiTables {
    $helper=Join-Path $PSScriptRoot 'Export-Victus-AcpiTables.ps1'
    if(-not(Test-Path -LiteralPath $helper)){Warn 'Helper ACPI ausente: extrae todos los archivos del ZIP en la misma carpeta.';return}
    $destination=Join-Path $script:Root 'acpi\tables'
    Invoke-ReadScript ("& '"+$helper.Replace("'","''")+"' -Destination '"+$destination.Replace("'","''")+"'") (Join-Path $script:Root 'acpi\capture.txt') 45
    if($IaslPath){
        if(-not(Test-Path -LiteralPath $IaslPath -PathType Leaf)){Warn 'IaslPath no existe; tablas binarias conservadas.';return}
        $tool=Get-Item -LiteralPath $IaslPath
        Save-Json (Join-Path $script:Root 'acpi\iasl-identity.json') ([pscustomobject]@{Path=$tool.FullName;SHA256=(Get-FileHash -LiteralPath $tool.FullName -Algorithm SHA256).Hash})
        foreach($table in @(Get-ChildItem -LiteralPath $destination -File -Recurse -ErrorAction SilentlyContinue|Where-Object {$_.Name -match '^(DSDT|SSDT)(-\d+)?\.dat$'})){
            # -d disassembles a file only. No compile/load/evaluate commands.
            Invoke-Bounded $tool.FullName ('-d "'+$table.FullName+'"') (Join-Path $script:Root ('acpi\iasl-'+$table.BaseName+'.txt')) 30 $destination
        }
    }
}
function Start-AcpiTrace {
    if($SkipAcpiTrace){Say 'ETW omitido por -SkipAcpiTrace.';return}
    if(-not $admin){Warn 'ETW omitido: requiere administrador. Eventos/tablas se intentan igualmente.';return}
    Invoke-Bounded 'logman.exe' 'query providers' (Join-Path $script:Root 'etw\providers.txt') 20
    $inventory=Get-Content -LiteralPath (Join-Path $script:Root 'etw\providers.txt') -Raw
    foreach($provider in @('Microsoft-Windows-Kernel-Acpi','Microsoft-Windows-WMI-Activity')){
        if($inventory -notmatch ([regex]::Escape($provider)+'\s')){Say ('Proveedor ETW no disponible: '+$provider);continue}
        $name='VFCdiag_'+[Guid]::NewGuid().ToString('N')
        $path=Join-Path $script:Root ('etw\'+$provider+'.etl')
        $session=[pscustomobject]@{Name=$name;Provider=$provider;Path=$path;StartedUtc=[DateTimeOffset]::UtcNow.ToString('o');CollectorElapsedMs=(Tick);StartStatus='attempted';StopStatus='pending';StoppedUtc=$null}
        # Own the unique attempted session even if logman times out after creation.
        $script:TraceSessions.Add($session);$script:TraceHistory.Add($session)
        Save-Json (Join-Path $script:Root 'etw\sessions.json') @($script:TraceHistory.ToArray())
        Save-Text (Join-Path $script:Root 'etw\STOP-IF-CONSOLE-WAS-CLOSED.cmd') (($script:TraceSessions|ForEach-Object {'logman stop '+$_.Name+' -ets'}) -join [Environment]::NewLine)
        Say ('Iniciar sesion ETW temporal propia: '+$name+'; proveedor='+$provider+'; circular 128 MiB. No evalua metodos ACPI.')
        Invoke-Bounded 'logman.exe' ('create trace '+$name+' -p "'+$provider+'" 0xffffffffffffffff 5 -o "'+$path+'" -f bincirc -max 128 -bs 64 -nb 4 16 -ets') ($path+'.start.txt') 20
        $session.StartStatus=$script:Commands[$script:Commands.Count-1].Status
    }
}
function Stop-AcpiTrace {
    foreach($session in @($script:TraceSessions.ToArray())){
        Say ('Detener unicamente la sesion ETW propia: '+$session.Name)
        Invoke-Bounded 'logman.exe' ('stop '+$session.Name+' -ets') ($session.Path+'.stop.txt') 20
        $session.StopStatus=$script:Commands[$script:Commands.Count-1].Status;$session.StoppedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        if($session.StartStatus -eq 'ok' -and $session.StopStatus -ne 'ok'){Warn ('No se confirmo cierre ETW: '+$session.Name+'; usar comando STOP en etw.')}
    }
    Save-Json (Join-Path $script:Root 'etw\sessions.json') @($script:TraceHistory.ToArray())
    $script:TraceSessions.Clear()
}
function Package {
    Save-Json (Join-Path $script:Root 'copied-files.json') @($script:Copies.ToArray())
    Save-Json (Join-Path $script:Root 'commands.json') @($script:Commands.ToArray())
    Save-Json (Join-Path $script:Root 'warnings.json') @($script:Warnings.ToArray())
    $manifest=@(Get-ChildItem -LiteralPath $script:Root -Recurse -File | Where-Object {$_.Name -ne 'manifest-sha256.json'} | ForEach-Object {
        [pscustomobject]@{Path=$_.FullName.Substring($script:Root.Length+1);Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    Save-Json (Join-Path $script:Root 'manifest-sha256.json') $manifest
    $zip=$script:Root+'.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($script:Root,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Save-Text ($zip+'.sha256') ($hash+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine)
    Write-Host ''
    Write-Host ('ZIP LISTO: '+$zip) -ForegroundColor Green
    Write-Host ('SHA-256: '+$hash)
    Write-Host ('Advertencias registradas: '+$script:Warnings.Count)
    Write-Host 'Adjunta el ZIP y el archivo .sha256. La carpeta original tambien queda conservada.'
    return $zip
}

if ($env:OS -ne 'Windows_NT') { throw 'Este recolector requiere Windows.' }
if ($SelfTest) {
    $script:Root=Join-Path ([IO.Path]::GetTempPath()) ('vfc-collector-test-'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($script:Root)|Out-Null
    $script:ConsoleLog=Join-Path $script:Root 'collector.log'
    $source=Join-Path $script:Root 'fixture.bin';$writer=$null;$zip=$null
    try {
        $writer=[IO.File]::Open($source,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
        $bytes=[Text.Encoding]::UTF8.GetBytes('0123456789');$writer.Write($bytes,0,$bytes.Length);$writer.Flush()
        $copy=Join-Path $script:Root 'copy.bin'
        if(-not(Copy-Shared $source $copy 4)){throw 'Shared writer copy failed.'}
        if([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($copy)) -ne '6789'){throw 'Bounded tail mismatch.'}
        if($script:Copies[0].StartOffset -ne 6 -or -not $script:Copies[0].Truncated){throw 'Truncation metadata missing.'}
        $writer.Dispose();$writer=$null
        Save-Text (Join-Path $script:Root 'empty.bin') ''
        if(-not(Copy-Shared (Join-Path $script:Root 'empty.bin') (Join-Path $script:Root 'empty-copy.bin'))){throw 'Empty file failed.'}
        if(Copy-Shared (Join-Path $script:Root 'missing.bin') (Join-Path $script:Root 'missing-copy.bin')){throw 'Missing file falsely copied.'}
        Invoke-ReadScript "'collector child output';[Console]::Error.WriteLine('collector child stderr')" (Join-Path $script:Root 'child.txt') 10
        if((Get-Content -LiteralPath (Join-Path $script:Root 'child.txt') -Raw) -notmatch 'collector child stderr'){throw 'Child stderr not retained.'}
        Invoke-ReadScript 'Start-Sleep -Seconds 15' (Join-Path $script:Root 'timeout.txt') 1
        if($script:Commands[1].Status -ne 'timeout'){throw 'Child timeout not bounded.'}
        & (Join-Path $PSScriptRoot 'Export-Victus-AcpiTables.ps1') -Destination (Join-Path $script:Root 'acpi-fixture') -SelfTest
        Say 'PASS: live-file copy, bounded tail, empty/missing file, stdout/stderr and child timeout.'
        $zip=Package
        $archive=[IO.Compression.ZipFile]::OpenRead($zip)
        try {
            if(-not($archive.Entries | Where-Object {$_.FullName -eq 'manifest-sha256.json'})){throw 'ZIP manifest missing.'}
            foreach($entry in $archive.Entries){$stream=$entry.Open();try{$stream.CopyTo([IO.Stream]::Null)}finally{$stream.Dispose()}}
        } finally {$archive.Dispose()}
        $expected=(Get-Content -LiteralPath ($zip+'.sha256') -Raw).Split(' ')[0]
        if($expected -ne (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'ZIP checksum mismatch.'}
        Write-Host 'PASS: collector fixture ZIP and SHA-256; no hardware queries.'
    } finally {
        if($writer){$writer.Dispose()}
        Remove-Item -LiteralPath $script:Root -Recurse -Force
        if($zip){Remove-Item -LiteralPath $zip,($zip+'.sha256') -Force -ErrorAction SilentlyContinue}
    }
    return
}

if(-not $OutputRoot){$OutputRoot=[Environment]::GetFolderPath('Desktop');if(-not $OutputRoot){$OutputRoot=(Get-Location).Path}}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$script:Root=Join-Path $OutputRoot ('VFC-WMI-Diagnostico_'+(Get-Date -Format 'yyyy-MM-dd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N').Substring(0,6))
[IO.Directory]::CreateDirectory($script:Root)|Out-Null
$script:ConsoleLog=Join-Path $script:Root 'collector.log'
$started=[DateTimeOffset]::Now
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say ('INICIO: recoleccion diagnostica Manual/WMI; modo='+$InvestigationMode+'. El recolector no escribe ventiladores; el lanzador opcional controla su propio CLI y pausa el watchdog limpio.')
Say ('Destino: '+$script:Root)
Say ('Administrador='+$admin+'; captura='+$CaptureMinutes+' min; Q termina la observacion y genera ZIP.')
if(-not $admin){Warn 'Sin administrador: algunos eventos, procesos o logs pueden quedar inaccesibles.'}
Save-Text (Join-Path $script:Root 'LEEME.txt') @'
RECOLECTOR PASIVO VFC / WMI
No consulta root\wmi de HP, RPM, EC, PawnIO ni la pipe del watchdog.
No arranca/cierra la app ni cambia su modo. No activa canales de eventos existentes. Crea sesiones ETW temporales propias
y las cierra al finalizar; cada proveedor usa un archivo circular de 128 MiB.
-SkipAcpiTrace permite comparar sin ETW. No cerrar la ventana a la fuerza; Q
finaliza y empaqueta. Si se cerro, etw/STOP-IF-CONSOLE-WAS-CLOSED.cmd contiene
solo los nombres unicos de las sesiones de esta captura.
No reinicia procesos, servicios ni proveedores. Solo puede terminar sus propios
subprocesos auxiliares cuando una lectura supera su timeout.
Recopila archivos existentes y eventos locales de Windows. Hasta 16 logs por
origen y etapa; hasta 8 MiB finales por log. Eventos: hasta MaximumEvents (10000 por defecto) por canal/consulta en texto.
EVTX conserva eventos del periodo seleccionado sin ese limite de filas; los
logs EVTX/ETL pueden contener rutas y metadatos de consultas de otros procesos.
Tablas ACPI: solo API Windows; primera instancia por firma, limite 4 MiB/tabla
y 32 MiB total. SSDT duplicadas se cuentan, no se presentan como dump completo.
MSDM excluida para evitar recopilar la clave OEM de Windows. No lee memoria
fisica ni instala drivers. iASL opcional: solo -d sobre DSDT/SSDT exportadas.
Las omisiones/truncamientos aparecen en warnings, commands y copied-files.
Snapshots de archivos activos pueden reflejar una escritura parcial; los logs
deben correlacionarse con los snapshots siguientes. Journal no equivale a prueba EC.
Procesos relevantes: nombres, PID, CPU, hilos, ruta y hora de inicio. No recopila
lineas de comandos generales, contrasenas, volcados de memoria ni claves BIOS.
Los eventos pueden contener rutas/nombres del equipo y otros metadatos Windows.
Los valores EC no registrados por VFC no pueden reconstruirse con este archivo.
Faltan aun etiquetas de motivo por consulta y los valores concretos que activan
ConfirmUnexpectedControlGuardAsync. Instrumentacion VFC antigua tampoco aporta
queueMs/nativeMs; este recolector no inventa esos datos ni prueba una causa fisica.
Para nueva sesion: ejecutar antes de abrir/usar la app normal; mantenerlo mientras
se observa Manual. Si falla, no repetir Apply; esperar restauracion y conservar
1-2 minutos posteriores. Pulsar Q o dejar terminar. No reutilizar arnes P16.
Solo recopilar una sesion anterior: -CaptureMinutes 0. No hace falta reproducir.
Este paquete documenta observaciones, no aprueba control fisico ni estabilidad.
'@
if($InvestigationMode -eq 'wmi-only-no-direct-ec'){
    Save-Text (Join-Path $script:Root 'LEEME.txt') @'
PRUEBA VFC WMI-ONLY: ver wmi-only/isolation-summary.json y chronology.log.
El lanzador ejecuta telemetria RPM HP WMI 20008h/2Dh, CPU Intel MSR y GPU NVML.
El CLI rechaza EC directo y toda otra llamada HP WMI antes de I/O.
La GUI permanece cerrada. Se pausa M4 solo con lease ausente y se restaura su
estado previo al finalizar. El estado AC/bateria de Windows se muestrea cada
dos segundos mediante GetSystemPowerStatus; no identifica el instante electrico.
ETW y tablas ACPI estaticas siguen las mismas reglas del recolector pasivo.
Las prohibiciones cubren este CLI; Windows, firmware y herramientas externas
pueden acceder a EC. No prueba estabilidad de Manual ni autorizacion WMI 26h.
Q cierra el CLI, vacia su log y empaqueta. No cerrar la consola a la fuerza.
'@
}

try {
    Stage '1/8 Contexto de sistema y reloj (registro local, sin consultas HP WMI).' {
        $bios=Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS' -ErrorAction SilentlyContinue
        $os=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
        Save-Json (Join-Path $script:Root 'system-context.json') ([pscustomobject]@{StartedLocal=$started.ToString('o');StartedUtc=$started.UtcDateTime.ToString('o');CollectorElapsedMs=(Tick);TimeZone=[TimeZoneInfo]::Local.Id;PowerShell=$PSVersionTable.PSVersion.ToString();IsAdmin=$admin;Machine=$env:COMPUTERNAME;OSVersion=[Environment]::OSVersion.VersionString;WindowsBuild=$os.CurrentBuild;UBR=$os.UBR;DisplayVersion=$os.DisplayVersion;Manufacturer=$bios.SystemManufacturer;Product=$bios.SystemProductName;Board=$bios.BaseBoardProduct;BIOS=$bios.BIOSVersion;CaptureMinutes=$CaptureMinutes;IntervalSeconds=$IntervalSeconds;EventHours=$EventHours})
    }
    Stage '2/8 Logs iniciales y procesos relevantes.' {Capture-Logs 'before';$script:InitialProcesses=@(Process-Snapshot 'before')}
    Stage '3/8 Identidad Git y hashes de ejecutables (sin actualizar ni compilar).' {
        if(-not $RepoRoot){
            $candidates=@((Get-Location).Path,(Split-Path -Parent $PSScriptRoot),(Join-Path $env:USERPROFILE 'VictusFanControl'))
            foreach($candidate in $candidates){if(Test-Path -LiteralPath (Join-Path $candidate '.git')){$script:RepoRoot=$candidate;break}}
        }
        Save-Text (Join-Path $script:Root 'repo-path.txt') $RepoRoot
        if($RepoRoot -and (Test-Path -LiteralPath $RepoRoot)){
            $script:RepoRoot=[IO.Path]::GetFullPath($RepoRoot)
            foreach($item in @(@{Name='head';Args='rev-parse HEAD'},@{Name='branch';Args='branch --show-current'},@{Name='status';Args='status --short'},@{Name='last-commits';Args='log -8 --format=fuller'},@{Name='runtime-diff';Args='diff -- src .github/workflows/build.yml'})){
                Invoke-Bounded 'git.exe' $item.Args (Join-Path $script:Root ('git\'+$item.Name+'.txt')) 20 $RepoRoot
            }
        }else{Warn 'Repositorio no localizado. Puedes especificarlo con -RepoRoot.'}
        $paths=@($script:InitialProcesses | Where-Object {$_.Name -match 'VictusFanControl' -and $_.Path} | ForEach-Object {$_.Path})
        if($RepoRoot){$paths+=@(Join-Path $RepoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe');$paths+=@(Join-Path $RepoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.dll');$paths+=@(Join-Path $RepoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.dll');$paths+=@(Join-Path $RepoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.pdb')}
        $binaries=@(foreach($path in ($paths|Select-Object -Unique)){
            if(Test-Path -LiteralPath $path){$file=Get-Item -LiteralPath $path;[pscustomobject]@{Path=$path;Bytes=$file.Length;LastWriteUtc=$file.LastWriteTimeUtc.ToString('o');Version=$file.VersionInfo.FileVersion;SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}}
        })
        Save-Json (Join-Path $script:Root 'binary-identities.json') $binaries
        if($RepoRoot){foreach($file in @('modules\LpcACPIEC.bin','modules\IntelMSR.bin')){$source=Join-Path $RepoRoot $file;if(Test-Path -LiteralPath $source){$item=Get-Item -LiteralPath $source;Save-Json (Join-Path $script:Root ('module-'+$item.Name+'.json')) ([pscustomobject]@{Path=$source;Bytes=$item.Length;SHA256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash})}}}
    }
    Stage '4/8 Servicios, drivers y configuracion de energia (solo lectura).' {
        Invoke-ReadScript "Get-Service | Where-Object { `$_.Name -match 'Victus|Pawn|Winmgmt|HP|Omen|NV' -or `$_.DisplayName -match 'Victus|Pawn|WMI|OMEN' } | Select-Object Name,DisplayName,Status,StartType | ConvertTo-Json -Depth 3" (Join-Path $script:Root 'services.txt')
        Invoke-Bounded 'driverquery.exe' '/fo csv' (Join-Path $script:Root 'drivers.csv')
        Invoke-Bounded 'powercfg.exe' '/getactivescheme' (Join-Path $script:Root 'power-active.txt')
        Invoke-Bounded 'powercfg.exe' '/a' (Join-Path $script:Root 'power-capabilities.txt')
        if(-not $MinimalPreparation){
        Invoke-ReadScript "Get-PnpDevice -PresentOnly | Where-Object { `$_.InstanceId -like 'ACPI*' -or `$_.FriendlyName -match 'Pawn|Embedded|integrado|integrada|OMEN|HP.*(System|Firmware)' } | ForEach-Object { `$d=`$_; [pscustomobject]@{Device=`$d;Properties=@(Get-PnpDeviceProperty -InstanceId `$d.InstanceId -ErrorAction SilentlyContinue | Where-Object { `$_.KeyName -match 'DriverVersion|DriverDate|DriverProvider|Service|HardwareIds|LocationPaths|ProblemCode|Parent|DriverInfPath' })} } | ConvertTo-Json -Depth 8" (Join-Path $script:Root 'acpi\pnp-devices.txt') 40
        }else{Say 'Escenario minimo: omitir enumeracion PnP y propiedades de dispositivos.'}
        foreach($file in @((Join-Path $env:SystemRoot 'System32\drivers\acpi.sys'),(Join-Path $env:SystemRoot 'System32\wbem\wmiprov.dll'))){if(Test-Path -LiteralPath $file){ $item=Get-Item -LiteralPath $file;Save-Json (Join-Path $script:Root ('driver-'+$item.Name+'.json')) ([pscustomobject]@{Path=$file;Version=$item.VersionInfo.FileVersion;SHA256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash})}}
    }
    Stage '5/8 ACPI estatico, eventos con mensajes/EVTX y trazas ETW propias.' {if(-not $MinimalPreparation){Capture-AcpiTables};Export-Events 'before';Start-AcpiTrace}
    Stage '6/8 Observacion pasiva: procesos, cambios del journal y nuevos avisos de VFC.' {
        if($ObservationStarted){[void](& $ObservationStarted $script:Root $ObservationContext)}
        $journal=Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'
        $lastJournal='';$snapshots=0;$iterations=0;$lastReport=-30
        $cursor=@{};$timer=[Diagnostics.Stopwatch]::StartNew();$duration=$CaptureMinutes*60
        $appDir=Join-Path $env:LOCALAPPDATA 'VictusFanControl\logs'
        foreach($file in @(Get-ChildItem -LiteralPath $appDir -File -Filter 'events-*.log' -ErrorAction SilentlyContinue)){$cursor[$file.FullName]=$file.Length}
        do {
            if($ObservationGuard){[void](& $ObservationGuard $script:Root $ObservationContext)}
            $iterations++
            try {[void](Process-Snapshot ('sample-'+$iterations.ToString('D4')))}catch{Warn ('Snapshot procesos: '+$_.Exception.Message)}
            if(Test-Path -LiteralPath $journal){
                $temp=Join-Path $script:Root 'journal-current.json'
                if(Copy-Shared $journal $temp 1048576){
                    $hash=(Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash
                    if($hash -ne $lastJournal){
                        $lastJournal=$hash
                        if($snapshots -lt 300){$snapshots++;$dest=Join-Path $script:Root ('journal\'+$snapshots.ToString('D4')+'.json');[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))|Out-Null;Copy-Item -LiteralPath $temp -Destination $dest}
                        [IO.File]::AppendAllText((Join-Path $script:Root 'journal-timeline.ndjson'),(ConvertTo-Json -Compress -InputObject ([pscustomobject]@{Utc=[DateTimeOffset]::UtcNow.ToString('o');Tick=(Tick);Exists=$true;SHA256=$hash;SavedSnapshots=$snapshots}))+[Environment]::NewLine,$script:Utf8)
                    }
                }
            } elseif($lastJournal -ne 'absent'){
                $lastJournal='absent';Say 'Journal ausente (observacion de archivo; no demuestra por si sola FF/FF).'
                [IO.File]::AppendAllText((Join-Path $script:Root 'journal-timeline.ndjson'),(ConvertTo-Json -Compress -InputObject ([pscustomobject]@{Utc=[DateTimeOffset]::UtcNow.ToString('o');Tick=(Tick);Exists=$false}))+[Environment]::NewLine,$script:Utf8)
            }
            foreach($file in @(Get-ChildItem -LiteralPath $appDir -File -Filter 'events-*.log' -ErrorAction SilentlyContinue)){
                $previous=0L;if($cursor.ContainsKey($file.FullName)){$previous=[long]$cursor[$file.FullName]}
                if($file.Length -ne $previous){
                    $tail=Join-Path $script:Root 'app-live-tail.txt'
                    if(Copy-Shared $file.FullName $tail 262144){
                        $bytes=[IO.File]::ReadAllBytes($tail);$start=[Math]::Max([long]0,$file.Length-$bytes.Length)
                        $skip=0;if($file.Length -ge $previous){$skip=[int][Math]::Min([long]$bytes.Length,[Math]::Max([long]0,$previous-$start))}
                        $text=$script:Utf8.GetString($bytes,$skip,$bytes.Length-$skip)
                        if($text){[IO.File]::AppendAllText((Join-Path $script:Root 'app-live-observed.log'),$text,$script:Utf8)}
                        foreach($line in @($text -split '[\r\n]+'|Where-Object {$_ -match 'HP WMI ACQUIRE|COMMAND PROOF|missing=cpu_fan,gpu_fan|Custom -> Restoring|Restoring -> Firmware|timed out|TimeoutException|query timed out'})){Say ('VFC: '+$line) 'EVENT'}
                        $cursor[$file.FullName]=$file.Length
                    }
                }
            }
            if($timer.Elapsed.TotalSeconds-$lastReport -ge 30){Say ('Observando '+[int]$timer.Elapsed.TotalSeconds+'/'+$duration+' s; muestras procesos='+$iterations+'; journal snapshots='+$snapshots);$lastReport=$timer.Elapsed.TotalSeconds}
            if($timer.Elapsed.TotalSeconds -ge $duration){break}
            $quit=$false
            try{if(-not [Console]::IsInputRedirected -and [Console]::KeyAvailable){$quit=[Console]::ReadKey($true).Key -eq [ConsoleKey]::Q}}catch{}
            if($quit){Say 'Q recibida: finalizando captura.';break}
            Start-Sleep -Seconds $IntervalSeconds
        } while($true)
    }
} catch { Warn ('Error general; se conservara evidencia parcial: '+$_.Exception.ToString()) }
finally {
    # Stop/flush the optional isolated child before ETW/log snapshots and ZIP.
    if($ObservationFinished){Stage 'Finalizar el proceso de investigacion aislado.' {[void](& $ObservationFinished $script:Root $ObservationContext)}}
    Stage 'ETW finalizar y vaciar solo las sesiones propias.' {Stop-AcpiTrace}
    Stage '7/8 Logs y procesos finales; eventos Windows actualizados.' {
        Capture-Logs 'after';[void](Process-Snapshot 'after');Export-Events 'after'
        Save-Json (Join-Path $script:Root 'capture-summary.json') ([pscustomobject]@{StartedLocal=$started.ToString('o');EndedLocal=[DateTimeOffset]::Now.ToString('o');Warnings=$script:Warnings.Count;HardwareReads=($InvestigationMode -eq 'wmi-only-no-direct-ec');InvestigationMode=$InvestigationMode;StaticFirmwareApi=(-not $MinimalPreparation);MinimalPreparation=[bool]$MinimalPreparation;EtwRequested=(-not $SkipAcpiTrace);MaximumEvents=$MaximumEvents;CollectorVersion='3-isolation';FanWrites=$false;EventChannelsEnabled=$false;SourceLogsPreserved=$true;RepoRoot=$RepoRoot})
    }
    Say '8/8 Generar manifiesto SHA-256 y ZIP. No se borran los logs originales.'
    try {[void](Package)}catch{Write-Host ('No se pudo crear ZIP: '+$_.Exception.Message) -ForegroundColor Red;Write-Host ('Adjunta manualmente la carpeta: '+$script:Root)}
}
