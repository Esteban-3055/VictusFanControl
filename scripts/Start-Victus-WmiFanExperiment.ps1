# Supervised HP 8C40/F.18 experiment. Defaults to shadow; -Control explicitly sends fan levels.
[CmdletBinding()]
param(
    [ValidateRange(1,10)][int]$CaptureMinutes=5,
    [string]$OutputRoot='',
    [string]$FanConfigurationPath='',
    [switch]$Control,
    [switch]$SkipAcpiTrace,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot

function Assert-WmiFanExperimentAnalysis($Envelope,[int]$ExpectedPid) {
    # Offline analyzer returns {Source,...,Analysis:{...}}, never a flat analysis.
    if(-not $Envelope -or -not $Envelope.PSObject.Properties['Analysis'] -or -not $Envelope.Analysis){throw 'Informe offline sin objeto Analysis.'}
    $a=$Envelope.Analysis
    if(-not $a.PSObject.Properties['ChronologyConsistent'] -or -not $a.ChronologyConsistent){throw 'Cronologia offline inconsistente; revisar Analysis.Warnings.'}
    if(-not $a.PSObject.Properties['Pid'] -or $a.Pid -ne $ExpectedPid){throw 'PID de cronologia distinto del proceso esperado.'}
    if(-not $a.PSObject.Properties['EcReads']){throw 'Informe offline sin inventario de lecturas EC.'}
    if(@($a.EcReads).Count -gt 0){throw 'Cronologia contiene lecturas EC directas.'}
}
function Add-WmiFanExperimentFault($State,[string]$Message) {
    $State.Valid=$false
    if(-not $State.Fault){$State.Fault=$Message}elseif($State.Fault -notlike ('*'+$Message+'*')){$State.Fault+=' | '+$Message}
}

function Assert-WmiFanExperimentIsolation($Facts,[int[]]$AllowedPids=@(),[switch]$AllowM4) {
    foreach($path in @($Facts.Journals)){throw ('Lease pendiente; conservarlo y volver a Firmware antes de probar: '+$path)}
    foreach($service in @($Facts.Services)){
        if($service.State -eq 'Stopped'){continue}
        if($AllowM4 -and $service.Name -eq 'VictusFanControlWatchdogM4' -and $service.State -eq 'Running'){continue}
        throw ('Servicio VFC activo fuera de esta prueba: '+$service.Name)
    }
    $allowed=@($AllowedPids)
    if($AllowM4){$allowed+=@($Facts.Services | Where-Object {$_.Name -eq 'VictusFanControlWatchdogM4'} | ForEach-Object {[int]$_.ProcessId})}
    foreach($p in @($Facts.Processes)){if([int]$p.Id -notin $allowed){throw ('Proceso VFC ajeno activo: '+$p.Name+' PID='+$p.Id)}}
}
function Get-WmiFanExperimentFacts([switch]$AllowExperimentLease) {
    $base=Join-Path $env:ProgramData 'VictusFanControl'
    $experimentLease=Join-Path $base 'WmiFanExperiment\lease.json'
    $journals=@()
    if(Test-Path -LiteralPath $base){
        $journals=@(Get-ChildItem -LiteralPath $base -Filter lease.json -Recurse -File -ErrorAction Stop |
            Where-Object {-not($AllowExperimentLease -and $_.FullName -eq $experimentLease)} | ForEach-Object {$_.FullName})
    }
    $services=@(Get-CimInstance Win32_Service -Filter "Name LIKE 'VictusFanControl%'" | Select-Object Name,State,ProcessId,PathName)
    $m4=@($services | Where-Object {$_.Name -eq 'VictusFanControlWatchdogM4'})
    if($m4.Count){
        $match=[regex]::Match($m4[0].PathName,'--result-path\s+(?:"([^"]+)"|(\S+))')
        $actual=$match.Groups[1].Value;if(-not $actual){$actual=$match.Groups[2].Value}
        $expected=Join-Path $base 'WatchdogM4\state\m4-8c40.status.json'
        if(-not $match.Success -or -not [string]::Equals([IO.Path]::GetFullPath($actual),[IO.Path]::GetFullPath($expected),[StringComparison]::OrdinalIgnoreCase)){
            throw 'Ruta de estado M4 personalizada/no verificable; prueba rechazada.'
        }
    }
    $processes=@(Get-Process -Name 'VictusFanControl*' -ErrorAction SilentlyContinue | Select-Object @{Name='Name';Expression={$_.ProcessName}},Id)
    [pscustomobject]@{Journals=$journals;Services=$services;Processes=$processes}
}
if($SelfTest){
    $envelope=[pscustomobject]@{Source='fixture';Analysis=[pscustomobject]@{Pid=31920;ChronologyConsistent=$true;EcReads=@()}}
    Assert-WmiFanExperimentAnalysis $envelope 31920
    foreach($bad in @(
        [pscustomobject]@{Pid=31920;ChronologyConsistent=$true;EcReads=@()},
        [pscustomobject]@{Analysis=[pscustomobject]@{Pid=31584;ChronologyConsistent=$true;EcReads=@()}},
        [pscustomobject]@{Analysis=[pscustomobject]@{Pid=31920;ChronologyConsistent=$false;EcReads=@()}},
        [pscustomobject]@{Analysis=[pscustomobject]@{Pid=31920;ChronologyConsistent=$true;EcReads=@('read')}},
        [pscustomobject]@{Analysis=[pscustomobject]@{Pid=31920;ChronologyConsistent=$true}}
    )){
        $rejected=$false;try{Assert-WmiFanExperimentAnalysis $bad 31920}catch{$rejected=$true}
        if(-not $rejected){throw 'Envelope incompleto/contaminado aceptado.'}
    }
    $f=@{Valid=$true;Fault='Telemetry admission lost'}
    Add-WmiFanExperimentFault $f 'Offline mismatch'
    if($f.Valid -or $f.Fault -ne 'Telemetry admission lost | Offline mismatch'){throw 'Se perdio el motivo original del fallo.'}
    $clean=[pscustomobject]@{Journals=@();Services=@();Processes=@()}
    Assert-WmiFanExperimentIsolation $clean
    foreach($bad in @(
        [pscustomobject]@{Journals=@('pending');Services=@();Processes=@()},
        [pscustomobject]@{Journals=@();Services=@([pscustomobject]@{Name='Other';State='Running'});Processes=@()},
        [pscustomobject]@{Journals=@();Services=@();Processes=@([pscustomobject]@{Name='GUI';Id=3})}
    )){
        $rejected=$false;try{Assert-WmiFanExperimentIsolation $bad}catch{$rejected=$true}
        if(-not $rejected){throw 'Aislamiento contaminado aceptado.'}
    }
    $allowed=[pscustomobject]@{Journals=@();Services=@();Processes=@([pscustomobject]@{Name='worker';Id=3})}
    Assert-WmiFanExperimentIsolation $allowed -AllowedPids @(3)
    Write-Host 'PASS: experiment isolation, nested analyzer envelope, contaminated evidence and original fault preservation.'
    return
}
if($env:OS -ne 'Windows_NT'){throw 'Esta prueba requiere Windows.'}
if($FanConfigurationPath){
    $FanConfigurationPath=(Resolve-Path -LiteralPath $FanConfigurationPath).Path
    if($FanConfigurationPath.Contains('"')){throw 'Ruta de configuracion no valida.'}
}
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Abre PowerShell como administrador.'}
Assert-WmiFanExperimentIsolation (Get-WmiFanExperimentFacts) -AllowM4
$state=@{Control=[bool]$Control;Root=$root;Minutes=$CaptureMinutes;Directory=$null;Guardian=$null;OutStream=$null;ErrStream=$null;OutTask=$null;ErrTask=$null;M4Paused=$false;M4Resumed=$false;Fault=$null;Valid=$false;WorkerPid=0;StartedUtc=[DateTimeOffset]::UtcNow.ToString('o');Cli=$null}
Push-Location $root
$state.FanConfigurationPath=$FanConfigurationPath
try{
    & dotnet build .\VictusFanControl.sln -c Release
    if($LASTEXITCODE -ne 0){throw 'Build fallo; no se inicia la prueba.'}
    $state.Cli=Join-Path $root 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.exe'
    & $state.Cli --wmi-fan-experiment-self-test
    if($LASTEXITCODE -ne 0){throw 'Self-test fallo; no se inicia la prueba.'}
    Assert-WmiFanExperimentIsolation (Get-WmiFanExperimentFacts) -AllowM4
    $m4=Get-Service VictusFanControlWatchdogM4 -ErrorAction SilentlyContinue
    if($m4 -and $m4.Status -eq 'Running'){
        $state.M4Paused=$true
        Stop-Service VictusFanControlWatchdogM4
        (Get-Service VictusFanControlWatchdogM4).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
    }
    Assert-WmiFanExperimentIsolation (Get-WmiFanExperimentFacts)
    $start={param($captureRoot,$s)
        $s.Directory=Join-Path $captureRoot 'wmi-fan-experiment'
        [IO.Directory]::CreateDirectory($s.Directory)|Out-Null
        $arguments='--wmi-fan-experiment --session-dir "'+$s.Directory+'" --modules-dir "'+(Join-Path $s.Root 'modules')+'" --duration-seconds '+($s.Minutes*60)
        if($s.Control){$arguments+=' --control'}
        if($s.FanConfigurationPath){$arguments+=' --fan-config "'+$s.FanConfigurationPath+'"'}
        $p=New-Object Diagnostics.Process
        $info=New-Object Diagnostics.ProcessStartInfo
        $info.FileName=$s.Cli;$info.Arguments=$arguments;$info.WorkingDirectory=$s.Root
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
        $p.StartInfo=$info
        $s.OutStream=[IO.File]::Open((Join-Path $s.Directory 'guardian-stdout.txt'),[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        $s.ErrStream=[IO.File]::Open((Join-Path $s.Directory 'guardian-stderr.txt'),[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        $previous=$env:VFC_EC_WMI_DIAGNOSTICS
        try{$env:VFC_EC_WMI_DIAGNOSTICS='1';if(-not $p.Start()){throw 'No inicio el supervisor.'}}finally{$env:VFC_EC_WMI_DIAGNOSTICS=$previous}
        $s.Guardian=$p
        $s.OutTask=$p.StandardOutput.BaseStream.CopyToAsync($s.OutStream);$s.ErrTask=$p.StandardError.BaseStream.CopyToAsync($s.ErrStream)
        $timer=[Diagnostics.Stopwatch]::StartNew()
        $ready=Join-Path $s.Directory 'ready.json'
        while(-not(Test-Path -LiteralPath $ready)){
            if($p.HasExited){throw 'Supervisor/worker rechazaron el inicio; revisar guardian-stderr.txt.'}
            if($timer.Elapsed.TotalSeconds -gt 40){throw 'Inicio no confirmado dentro de 40 segundos.'}
            Start-Sleep -Milliseconds 100
        }
        $r=Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
        if(-not $r.DirectEcProhibited -or $r.Control -ne $s.Control -or $r.ProductionAuthorized){throw 'Frontera experimental incorrecta.'}
        $s.WorkerPid=[int]$r.Pid
        Write-Host ('PRUEBA ACTIVA: '+$(if($s.Control){'AUTOMATICO WMI 30-50'}else{'SIMULACION; FIRMWARE CONTROLA'})+'. Sin EC directo; Q para terminar.') -ForegroundColor Green
    }
    $guard={param($captureRoot,$s)
        if(-not $s.Guardian){throw 'Supervisor ausente.'}
        if($s.Guardian.HasExited){
            if($s.Guardian.ExitCode -ne 0){
                $summaryPath=Join-Path $s.Directory 'summary.json'
                $reason='revisar guardian-stderr.txt'
                if(Test-Path -LiteralPath $summaryPath){$reason=(Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json).StopReason}
                throw ('Prueba terminada: '+$reason+'; conservar evidencia.')
            }
            return
        }
        Assert-WmiFanExperimentIsolation (Get-WmiFanExperimentFacts -AllowExperimentLease) -AllowedPids @($s.Guardian.Id,$s.WorkerPid)
        $last=Get-Content -LiteralPath (Join-Path $s.Directory 'worker-stdout.txt') -Tail 1 -ErrorAction SilentlyContinue
        if($last){Write-Host $last}
    }
    $finish={param($captureRoot,$s)
        try{
            if(-not $s.Directory){throw 'No se inicio la prueba.'}
            [IO.File]::WriteAllText((Join-Path $s.Directory 'stop.signal'),'collector-stop')
            if(-not $s.Guardian -or -not $s.Guardian.WaitForExit(30000)){throw 'Supervisor aun activo; no se mata una llamada nativa. Evidencia incompleta.'}
            foreach($name in @('Out','Err')){
                $task=$s[$name+'Task'];$stream=$s[$name+'Stream']
                if($task -and -not $task.Wait(5000)){throw 'Salida del supervisor incompleta.'}
                if($stream){$stream.Flush();$stream.Dispose();$s[$name+'Stream']=$null}
            }
            $summary=Get-Content -LiteralPath (Join-Path $s.Directory 'summary.json') -Raw | ConvertFrom-Json
            if($s.Guardian.ExitCode -ne 0 -or $summary.ExitCode -ne 0 -or $summary.LeaseRetained -or $summary.NativeCompletionUnknown){throw ('Prueba interrumpida: '+$summary.StopReason+'; lease retenido='+$summary.LeaseRetained+'; finalizacion nativa desconocida='+$summary.NativeCompletionUnknown+'. Adjuntar ZIP.')}
            $s.Valid=$true
        }catch{$s.Fault=$_.Exception.Message}
        finally{
            # Preserve both process chronologies; guardian and worker share no EC code path.
            if($s.Directory){
                $ids=@($s.WorkerPid);if($s.Guardian){$ids+=@($s.Guardian.Id)}
                foreach($id in $ids){
                    if($id -le 0){continue}
                    $logs=@(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'VictusFanControl\logs') -Filter ('ec-wmi-'+$id+'-*.log') -File -ErrorAction SilentlyContinue |
                        Where-Object {$_.LastWriteTimeUtc -ge [DateTime]::Parse($s.StartedUtc).ToUniversalTime()})
                    if($logs.Count -ne 1){Add-WmiFanExperimentFault $s 'Falta una cronologia unica por proceso.';continue}
                    Copy-Item -LiteralPath $logs[0].FullName -Destination (Join-Path $s.Directory ('chronology-'+$id+'.log'))
                    & $s.Cli --analyze-ec-wmi-trace $logs[0].FullName --analysis-output-dir (Join-Path $s.Directory ('analysis-'+$id))
                    if($LASTEXITCODE -ne 0){Add-WmiFanExperimentFault $s 'Analisis offline incompleto.'}
                    try{
                        $envelope=Get-Content -LiteralPath (Join-Path $s.Directory ('analysis-'+$id+'\summary.json')) -Raw | ConvertFrom-Json
                        Assert-WmiFanExperimentAnalysis $envelope $id
                    }catch{Add-WmiFanExperimentFault $s $_.Exception.Message}
                    $rows=@(Get-Content -LiteralPath $logs[0].FullName | ForEach-Object {$_ | ConvertFrom-Json})
                    if(@($rows | Where-Object {$_.Stage -like 'ec.*' -or $_.Stage -like 'isolation.*.denied'}).Count){Add-WmiFanExperimentFault $s 'Intento EC o solicitud fuera de la frontera.'}
                }
                [IO.File]::WriteAllText((Join-Path $s.Directory 'launcher-summary.json'),(ConvertTo-Json -Depth 5 -InputObject ([pscustomobject]@{Valid=$s.Valid;Fault=$s.Fault;Control=$s.Control;M4Paused=$s.M4Paused;FirmwareRestorationVerified=$false})))
            }
            if($s.Fault){Write-Host ('PRUEBA INCOMPLETA: '+$s.Fault) -ForegroundColor Yellow}
        }
    }
    & (Join-Path $PSScriptRoot 'Collect-Victus-WmiTimeout.ps1') -RepoRoot $root -OutputRoot $OutputRoot -CaptureMinutes $CaptureMinutes `
        -MinimalPreparation -SkipAcpiTrace:$SkipAcpiTrace -InvestigationMode 'wmi-fan-normal-recovery-no-ec' `
        -ObservationStarted $start -ObservationGuard $guard -ObservationFinished $finish -ObservationContext $state
    if(-not $state.Valid){throw ('Captura incompleta: '+$state.Fault+'. Adjunta igualmente el ZIP.')}
    Write-Host 'CAPTURA COMPLETA. Una sesion sin eventos no demuestra ausencia definitiva del fallo.' -ForegroundColor Green
}finally{
    $lease=Join-Path $env:ProgramData 'VictusFanControl\WmiFanExperiment\lease.json'
    if($state.Directory -and $state.Guardian -and -not $state.Guardian.HasExited){[IO.File]::WriteAllText((Join-Path $state.Directory 'stop.signal'),'launcher-finally')}
    if($state.M4Paused -and -not(Test-Path -LiteralPath $lease) -and (-not $state.Guardian -or $state.Guardian.HasExited)){
        Start-Service VictusFanControlWatchdogM4
        (Get-Service VictusFanControlWatchdogM4).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        $state.M4Resumed=$true
    }elseif($state.M4Paused){Write-Warning 'M4 queda detenido porque la prueba/recuperacion sigue pendiente. Conservar lease y evidencia.'}
    Pop-Location
}
