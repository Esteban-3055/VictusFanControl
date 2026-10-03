# Windows PowerShell 5.1 / PowerShell 7. No hardware probe or fan commands.
[CmdletBinding()]
param(
    [ValidateRange(1,60)][int]$CaptureMinutes=30,
    [string]$OutputRoot='',
    [switch]$SkipAcpiTrace,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$state=@{StartedUtc=$null;InitialSystemRecordId=$null;Evidence=$null;Fault=$null;Samples=0;Events=@();Paused=$false;Resumed=$false;Finished=$false;NextReport=[DateTimeOffset]::MinValue;StopReason='duration-or-Q'}

. (Join-Path $PSScriptRoot 'ScenarioAcpiEventFilter.ps1')

function Assert-ScenarioAFacts($Facts,[switch]$AllowCleanM4) {
    if(@($Facts.Journals).Count){throw 'Existe un lease pendiente. Vuelve a Firmware y espera la restauracion; no borres el journal.'}
    $m4=@($Facts.Services | Where-Object {$_.Name -eq 'VictusFanControlWatchdogM4' -and $_.Status -eq 'Running'})
    foreach($s in $Facts.Services){
        if($s.Status -ne 'Stopped' -and -not($AllowCleanM4 -and $s.Name -eq 'VictusFanControlWatchdogM4' -and $s.Status -eq 'Running')){throw ('Servicio VFC activo: '+$s.Name+' '+$s.Status)}
    }
    foreach($p in $Facts.Processes){
        if($AllowCleanM4 -and $m4.Count -eq 1 -and @($Facts.Processes).Count -eq 1 -and $p.Name -eq 'VictusFanControl.Watchdog' -and $Facts.M4PathVerified -and [string]::Equals($p.Path,$Facts.M4Executable,[StringComparison]::OrdinalIgnoreCase)){continue}
        throw ('Proceso VFC activo: '+$p.Name+' PID='+$p.Id+'. Selecciona Firmware, espera el cierre y sal de la app.')
    }
}
function Get-ScenarioAFacts {
    $base=Join-Path $env:ProgramData 'VictusFanControl'
    $journals=@()
    if(Test-Path -LiteralPath $base){
        foreach($dir in @(Get-ChildItem -LiteralPath $base -Directory)){
            $path=Join-Path $dir.FullName 'state\lease.json'
            if(Test-Path -LiteralPath $path){$journals+=@($path)}
        }
    }
    $services=@(Get-Service -Name 'VictusFanControl*' -ErrorAction SilentlyContinue | Select-Object Name,@{Name='Status';Expression={[string]$_.Status}})
    $verified=$false;$exe=$null
    if(@($services | Where-Object {$_.Name -eq 'VictusFanControlWatchdogM4'}).Count){
        $image=[string](Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\VictusFanControlWatchdogM4' -Name ImagePath).ImagePath
        $match=[regex]::Match($image,'--result-path\s+(?:"([^"]+)"|(\S+))')
        if(-not $match.Success){throw 'No se puede verificar la ruta de estado de M4.'}
        $actual=$match.Groups[1].Value;if(-not $actual){$actual=$match.Groups[2].Value}
        $expected=Join-Path $base 'WatchdogM4\state\m4-8c40.status.json'
        if(-not [string]::Equals([IO.Path]::GetFullPath($actual),[IO.Path]::GetFullPath($expected),[StringComparison]::OrdinalIgnoreCase)){throw 'Ruta M4 personalizada: no se puede descartar un lease fuera de la ruta conocida.'}
        $exeMatch=[regex]::Match($image,'^\s*(?:"([^"]+\.exe)"|(.+?\.exe))(?:\s|$)',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if(-not $exeMatch.Success){throw 'Ejecutable M4 no verificable.'}
        $exe=$exeMatch.Groups[1].Value;if(-not $exe){$exe=$exeMatch.Groups[2].Value}
        $exe=[Environment]::ExpandEnvironmentVariables($exe);$verified=$true
    }
    $processes=@(Get-Process -Name 'VictusFanControl*' -ErrorAction SilentlyContinue | Select-Object @{Name='Name';Expression={$_.ProcessName}},Id,Path)
    return [pscustomobject]@{Journals=$journals;Services=$services;Processes=$processes;M4PathVerified=$verified;M4Executable=$exe}
}
function Save-ScenarioAJson([string]$Path,$Value) {
    [IO.File]::WriteAllText($Path,(ConvertTo-Json -InputObject $Value -Depth 8),(New-Object Text.UTF8Encoding($false)))
}
function Resume-ScenarioAM4($Context) {
    if($Context.Paused -and -not $Context.Resumed){
        Write-Host 'Restaurar el estado inicial del servicio M4 (despues de terminar la observacion).'
        Start-Service -Name 'VictusFanControlWatchdogM4'
        (Get-Service -Name 'VictusFanControlWatchdogM4').WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        $Context.Resumed=$true
    }
}
if($SelfTest){
    # Deliberately feed historical records even when a provider-side filter
    # misbehaves, as it did in the uploaded 17:07 capture on PowerShell 5.1.
    function New-ScenarioAFixtureEvent([long]$RecordId,[string]$Utc,[string]$Provider='ACPI',[int]$Id=13) {
        $e=[pscustomobject]@{Xml="<Event><System><Provider Name='$Provider'/><EventID>$Id</EventID><TimeCreated SystemTime='$Utc'/><EventRecordID>$RecordId</EventRecordID></System></Event>"}
        $e | Add-Member -MemberType ScriptMethod -Name ToXml -Value {return $this.Xml}
        return $e
    }
    $cutoff=[DateTimeOffset]::Parse('2026-10-03T20:07:26.9400119Z')
    $old=@(New-ScenarioAFixtureEvent 4550 '2026-10-03T11:32:55.6175801Z';New-ScenarioAFixtureEvent 4544 '2026-10-03T10:33:16.2377509Z';New-ScenarioAFixtureEvent 4540 '2026-10-03T09:53:30.2726863Z';New-ScenarioAFixtureEvent 4536 '2026-10-03T09:33:46.7724884Z')
    if(@(Select-ScenarioANewEvents $old 4826 $cutoff).Count){throw 'Regresion: ACPI historico contado como nuevo.'}
    $cases=@($old)+(New-ScenarioAFixtureEvent 4827 '2026-10-03T20:07:27Z')+(New-ScenarioAFixtureEvent 4828 '2026-10-03T20:07:25Z')+(New-ScenarioAFixtureEvent 4826 '2026-10-03T20:07:28Z')+(New-ScenarioAFixtureEvent 4829 '2026-10-03T20:07:29Z' 'Other')+(New-ScenarioAFixtureEvent 4830 '2026-10-03T20:07:30Z' 'ACPI' 12)
    $accepted=@(Select-ScenarioANewEvents $cases 4826 $cutoff)
    if($accepted.Count -ne 1 -or $accepted[0].ToXml() -notmatch '4827'){throw 'Clasificacion de evento nuevo incorrecta.'}
    if(@(Select-ScenarioANewEvents @() 4826 $cutoff).Count){throw 'Consulta vacia produjo evento.'}
    $rejected=$false;try{Select-ScenarioANewEvents @(New-ScenarioAFixtureEvent 4831 'invalid') 4826 $cutoff}catch{$rejected=$true}
    if(-not $rejected){throw 'Timestamp XML invalido no rechazo la evidencia.'}
    if((New-ScenarioAEventQuery 4826) -notmatch 'EventRecordID > 4826'){throw 'Falta cursor en consulta XPath.'}
    # Read-only Windows smoke: validate the actual Event Log XPath engine.
    if([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT){
        try{[void](Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery 9223372036854775806) -MaxEvents 1 -ErrorAction Stop)}catch{
            if($_.FullyQualifiedErrorId -notmatch '^NoMatchingEventsFound'){throw}
        }
    }
    $empty=[pscustomobject]@{Journals=@();Services=@();Processes=@();M4PathVerified=$false;M4Executable=$null}
    Assert-ScenarioAFacts $empty
    foreach($kind in @('lease','service','process','unverified-m4')){
        $f=[pscustomobject]@{Journals=@();Services=@();Processes=@();M4PathVerified=$false;M4Executable='C:\m4.exe'}
        switch($kind){
            'lease' {$f.Journals=@('empty-or-malformed-lease.json')}
            'service' {$f.Services=@([pscustomobject]@{Name='VictusFanControlWatchdogM2';Status='Running'})}
            'process' {$f.Processes=@([pscustomobject]@{Name='VictusFanControl.App';Id=1;Path='C:\app.exe'})}
            'unverified-m4' {$f.Services=@([pscustomobject]@{Name='VictusFanControlWatchdogM4';Status='Running'});$f.Processes=@([pscustomobject]@{Name='VictusFanControl.Watchdog';Id=2;Path='C:\m4.exe'})}
        }
        $rejected=$false;try{Assert-ScenarioAFacts $f -AllowCleanM4}catch{$rejected=$true}
        if(-not $rejected){throw ('Fixture no rechazo '+$kind)}
    }
    $clean=[pscustomobject]@{Journals=@();Services=@([pscustomobject]@{Name='VictusFanControlWatchdogM4';Status='Running'});Processes=@([pscustomobject]@{Name='VictusFanControl.Watchdog';Id=2;Path='C:\m4.exe'});M4PathVerified=$true;M4Executable='C:\m4.exe'}
    Assert-ScenarioAFacts $clean -AllowCleanM4
    $rejected=$false;try{Assert-ScenarioAFacts $clean}catch{$rejected=$true}
    if(-not $rejected){throw 'M4 activo fue admitido durante la observacion.'}
    $clean.Processes[0].Path='C:\other.exe'
    $rejected=$false;try{Assert-ScenarioAFacts $clean -AllowCleanM4}catch{$rejected=$true}
    if(-not $rejected){throw 'Se admitio un ejecutable M4 diferente.'}
    Write-Host 'PASS: escenario A; aislamiento, eventos historicos/nuevos y XPath Windows; sin cambios de servicios ni acceso hardware.'
    return
}
if([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT){throw 'Esta prueba requiere Windows.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Abre PowerShell como administrador.'}
Write-Host 'ESCENARIO A: selecciona Firmware y espera restauracion antes de cerrar VFC. No se restauran ventiladores desde este script.'
Assert-ScenarioAFacts (Get-ScenarioAFacts) -AllowCleanM4
try{
    $service=Get-Service -Name 'VictusFanControlWatchdogM4' -ErrorAction SilentlyContinue
    if($service -and $service.Status -eq 'Running'){
        Write-Host 'Pausar temporalmente M4 sin lease; no se cambia su tipo de inicio.'
        $state.Paused=$true;Stop-Service -Name $service.Name
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
    }
    Assert-ScenarioAFacts (Get-ScenarioAFacts)
    $start={param($captureRoot,$context)
        $context.Evidence=Join-Path $captureRoot 'scenario-a';[IO.Directory]::CreateDirectory($context.Evidence)|Out-Null
        Assert-ScenarioAFacts (Get-ScenarioAFacts)
        # Capture the cursor before the start instant; both conditions must
        # hold, so no event written before this observation is called new.
        $latest=Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop
        $context.InitialSystemRecordId=[long]$latest.RecordId
        $context.StartedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        Save-ScenarioAJson (Join-Path $context.Evidence 'start.json') (Get-ScenarioAFacts)
        Write-Host 'ESCENARIO A ACTIVO: VFC cerrado, watchdog detenido. Usa el equipo normalmente; Q termina y genera ZIP.' -ForegroundColor Green
    }
    $guard={param($captureRoot,$context)
        try{
            $facts=Get-ScenarioAFacts;Assert-ScenarioAFacts $facts
            $context.Samples++
            $row=[pscustomobject]@{Utc=[DateTimeOffset]::UtcNow.ToString('o');Sample=$context.Samples;Facts=$facts}
            [IO.File]::AppendAllText((Join-Path $context.Evidence 'isolation-timeline.ndjson'),(ConvertTo-Json $row -Compress -Depth 8)+[Environment]::NewLine,(New-Object Text.UTF8Encoding($false)))
            $latest=Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop
            if([long]$latest.RecordId -lt $context.InitialSystemRecordId){throw 'El registro System se reinicio durante la prueba; comparacion invalidada.'}
            $raw=@(Get-WinEvent -LogName System -FilterXPath (New-ScenarioAEventQuery $context.InitialSystemRecordId) -MaxEvents 20 -ErrorAction Stop)
            $events=@(Select-ScenarioANewEvents $raw $context.InitialSystemRecordId ([DateTimeOffset]::Parse($context.StartedUtc)))
        }catch{
            if($_.FullyQualifiedErrorId -notmatch '^NoMatchingEventsFound'){$context.Fault=$_.Exception.Message;$context.StopReason='isolation-or-event-query-failure';throw}
            $events=@()
        }
        if($events.Count){
            $context.Events=@($events | Select-Object @{Name='TimeCreatedUtc';Expression={([DateTimeOffset]::Parse(([xml]$_.ToXml()).Event.System.TimeCreated.SystemTime)).ToUniversalTime().ToString('o')}},RecordId,ProviderName,Id,Message)
            Save-ScenarioAJson (Join-Path $context.Evidence 'acpi13-detected.json') $context.Events
            [IO.File]::WriteAllText((Join-Path $context.Evidence 'acpi13-detected.xml'),($events | ForEach-Object {$_.ToXml()}) -join [Environment]::NewLine,(New-Object Text.UTF8Encoding($false)))
            $context.StopReason='new-acpi13';Write-Host 'ACPI 13 NUEVO: terminar observacion y conservar evidencia.' -ForegroundColor Yellow
            throw 'Escenario A finalizado por ACPI 13 nuevo; consultar scenario-a/summary.json.'
        }
        if([DateTimeOffset]::UtcNow -ge $context.NextReport){Write-Host ('Escenario A verificado por muestreo; muestras='+$context.Samples+'; ACPI 13 nuevos=0');$context.NextReport=[DateTimeOffset]::UtcNow.AddSeconds(30)}
    }
    $finish={param($captureRoot,$context)
        try{Assert-ScenarioAFacts (Get-ScenarioAFacts)}catch{$context.Fault=$_.Exception.Message}
        $ended=[DateTimeOffset]::UtcNow.ToString('o')
        try{Resume-ScenarioAM4 $context}catch{$context.Fault='No se pudo reanudar M4: '+$_.Exception.Message}
        if(-not $context.Evidence){$context.Evidence=Join-Path $captureRoot 'scenario-a';[IO.Directory]::CreateDirectory($context.Evidence)|Out-Null}
        Save-ScenarioAJson (Join-Path $context.Evidence 'summary.json') ([pscustomobject]@{
            Scenario='A';DetectorVersion=2;InitialSystemRecordId=$context.InitialSystemRecordId;StartedUtc=$context.StartedUtc;EndedUtc=$ended;SampledIsolationValid=([bool]$context.StartedUtc -and $context.Samples -gt 0 -and -not $context.Fault);Fault=$context.Fault;Samples=$context.Samples;StopReason=$context.StopReason;Acpi13Count=$context.Events.Count;Acpi13=$context.Events;M4TemporarilyPaused=$context.Paused;M4Resumed=$context.Resumed;DirectEcReads=$false;HpWmiCalls=$false;FanWrites=$false;Scope='Sampled VFC isolation. Windows/firmware and other applications remain possible EC users. No raw fan ownership proof is performed.'
        })
        $context.Finished=$true
    }
    & (Join-Path $PSScriptRoot 'Collect-Victus-WmiTimeout.ps1') -RepoRoot $repo -OutputRoot $OutputRoot -CaptureMinutes $CaptureMinutes -InvestigationMode 'scenario-a-no-vfc' -MinimalPreparation -SkipAcpiTrace:$SkipAcpiTrace -ObservationStarted $start -ObservationGuard $guard -ObservationFinished $finish -ObservationContext $state
    if($state.Fault){throw ('Escenario A incompleto: '+$state.Fault+'. Conserva el ZIP.')}
    if(-not $state.Finished){throw 'No se confirmo cierre del escenario A.'}
}finally{Resume-ScenarioAM4 $state}
