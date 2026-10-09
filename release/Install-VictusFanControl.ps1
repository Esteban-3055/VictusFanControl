$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Start-ProductGui.ps1') -Mode Verify
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if(-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Abre PowerShell como administrador con tu misma cuenta de Windows.'
    }
} finally {$identity.Dispose()}
if(Get-Process -Name 'VictusFanControl*' -ErrorAction SilentlyContinue){throw 'Sal de VictusFanControl desde la bandeja antes de instalar. No se detienen procesos ni se eliminan journals.'}
# A build-specific stable path makes updates reversible and leaves the active task intact until registration succeeds.
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PRODUCT-GUI-MANIFEST.json') -Raw | ConvertFrom-Json
$destination=Join-Path $env:LOCALAPPDATA ('VictusFanControl\releases\1.0.0-'+$manifest.sourceHead)
if(-not (Test-Path -LiteralPath $destination)) {
    New-Item -ItemType Directory -Path $destination | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $destination -Recurse
}
& (Join-Path $destination 'Start-ProductGui.ps1') -Mode Verify
$app=Join-Path $destination 'app'
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $app 'VictusFanControl.App.exe'
$start.UseShellExecute=$false;$start.WorkingDirectory=$app
$start.Arguments='--configure-product-startup --modules-dir "'+(Join-Path $app 'modules')+'"'
$process=[Diagnostics.Process]::Start($start)
try{$process.WaitForExit();if($process.ExitCode -ne 0){throw 'No se configuró el inicio. Conserva los perfiles y revisa el mensaje.'}}finally{$process.Dispose()}
Write-Host "Instalado en $destination"
Write-Host 'Inicio con Windows, minimizado y Automático al iniciar configurados. Tres lecturas frescas, CPU/GPU confirmados y ningún journal pendiente son necesarios. Suspender no rearma el control.'
& (Join-Path $destination 'Start-ProductGui.ps1') -Mode Open
