# Windows PowerShell 5.1 / PowerShell 7. Launches the normal app with optional
# passive chronology, then the existing collector. Never applies a fan target.
[CmdletBinding()]
param([ValidateRange(1,60)][int]$CaptureMinutes=15)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Abre PowerShell como administrador y vuelve a ejecutar este archivo.'}
if(Get-Process -Name 'VictusFanControl.App' -ErrorAction SilentlyContinue){throw 'Primero vuelve a Firmware y cierra la app. El lanzador no la cierra ni interrumpe Manual.'}
Push-Location $root
try{
 Write-Host '1/3 Compilar la solucion actual en Release. No se reinstala ni reinicia el watchdog.'
 & dotnet build .\VictusFanControl.sln -c Release
 if($LASTEXITCODE -ne 0){throw 'La compilacion fallo; no se abre la app.'}
 $app=Join-Path $root 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
 $modules=Join-Path $root 'modules'
 if(-not(Test-Path -LiteralPath $app)){throw ('Ejecutable no encontrado: '+$app)}
 Write-Host '2/3 Abrir app normal con cronologia EC/WMI opcional. Los ventiladores se controlan desde su interfaz habitual.'
 Write-Host ('Logs detallados: '+(Join-Path $env:LOCALAPPDATA 'VictusFanControl\logs\ec-wmi-*.log'))
 Write-Host 'El watchdog instalado conserva su version; sus logs normales y PID se incluyen en la captura.'
 $previous=$env:VFC_EC_WMI_DIAGNOSTICS
 try{
  $env:VFC_EC_WMI_DIAGNOSTICS='1'
  $child=Start-Process -FilePath $app -ArgumentList @('--modules-dir',('"'+$modules+'"')) -WorkingDirectory $root -PassThru
 }finally{$env:VFC_EC_WMI_DIAGNOSTICS=$previous}
 Write-Host ('App iniciada, PID='+$child.Id+'. El diagnostico se activa solo en este proceso hijo.')
 Write-Host '3/3 Recolectar logs, eventos Windows, tablas estaticas y trazas ETW.'
 Write-Host 'Espera a que aparezca Observando antes de usar Manual. Si falla, espera la restauracion y conserva un minuto posterior.'
 Write-Host 'Pulsa Q para finalizar el recolector. La app sigue abierta; puedes cerrarla desde su interfaz.'
 & (Join-Path $PSScriptRoot 'Collect-Victus-WmiTimeout.ps1') -RepoRoot $root -CaptureMinutes $CaptureMinutes
}finally{Pop-Location}
