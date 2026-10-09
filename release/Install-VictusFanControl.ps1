param([switch]$NoOpen,[int]$InstallerProcessId=0)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Start-ProductGui.ps1') -Mode Verify
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if(-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Abre el instalador como administrador con tu misma cuenta de Windows.'
    }
} finally {$identity.Dispose()}
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PRODUCT-GUI-MANIFEST.json') -Raw | ConvertFrom-Json
if($InstallerProcessId -ne 0) {
    $installer=Get-Process -Id $InstallerProcessId -ErrorAction Stop
    $info=$installer.MainModule.FileVersionInfo
    if($info.FileDescription -ne 'VictusSetup' -or $info.FileVersion -ne ($manifest.version+'.0')) {throw 'Proceso instalador no reconocido.'}
}
$others=@(Get-Process -Name 'VictusFanControl*' -ErrorAction SilentlyContinue | Where-Object {$_.Id -ne $InstallerProcessId})
if($others.Count -ne 0){throw 'Sal de VictusFanControl desde la bandeja antes de instalar. No se detienen procesos ni se eliminan journals.'}
# Refuse unresolved ownership, even after a process disappeared. Never delete recovery records.
$root=Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$performance=Join-Path $root 'Performance\HP-8C40-9D0R1LA-F18'
foreach($name in @('cpu-power-session.json','gpu-clock-session.json')) {
    if(Test-Path -LiteralPath (Join-Path $performance $name)){throw "Resuelve la recuperación pendiente antes de instalar: $name"}
}
foreach($lease in @('WmiFanGui\lease.json','WatchdogM4\state\lease.json','WmiFanExperiment\lease.json')) {
    if(Test-Path -LiteralPath (Join-Path (Join-Path $env:ProgramData 'VictusFanControl') $lease)){throw "Resuelve la sesión de ventiladores pendiente antes de instalar: $lease"}
}
# A build-specific path leaves the previous executable/task available until registration succeeds.
$destination=Join-Path $root ('releases\'+$manifest.version+'-'+$manifest.sourceHead)
if(-not (Test-Path -LiteralPath $destination)) {
    $stage=$destination+'.staging-'+[Guid]::NewGuid().ToString('N')
    try {
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $stage -Recurse
        & (Join-Path $stage 'Start-ProductGui.ps1') -Mode Verify
        Move-Item -LiteralPath $stage -Destination $destination
    } finally {if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}}
}
# Check the destination against the trusted source before executing its launcher.
$actual=@(Get-ChildItem -LiteralPath $destination -Recurse -File)
if($actual.Count -ne $manifest.files.Count+1){throw 'La carpeta de destino contiene archivos adicionales o incompletos.'}
foreach($entry in $manifest.files) {
    $file=Join-Path $destination $entry.path
    if(-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $entry.size -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Destino no íntegro: $($entry.path)"}
}
if((Get-FileHash -LiteralPath (Join-Path $destination 'PRODUCT-GUI-MANIFEST.json') -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PRODUCT-GUI-MANIFEST.json') -Algorithm SHA256).Hash){throw 'Manifiesto de destino distinto al origen.'}
& (Join-Path $destination 'Start-ProductGui.ps1') -Mode Verify
$app=Join-Path $destination 'app'
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $app 'VictusFanControl.App.exe'
$start.UseShellExecute=$false;$start.WorkingDirectory=$app
$start.Arguments='--configure-product-install --modules-dir "'+(Join-Path $app 'modules')+'"'
$process=[Diagnostics.Process]::Start($start)
try{$process.WaitForExit();if($process.ExitCode -ne 0){throw 'No se configuró el inicio. Se conservó la instalación anterior; revisa el mensaje.'}}finally{$process.Dispose()}
$shell=New-Object -ComObject WScript.Shell
$shortcut=$shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'VictusFanControl.lnk'))
$shortcut.TargetPath=Join-Path $app 'VictusFanControl.App.exe'
$shortcut.Arguments='--modules-dir "'+(Join-Path $app 'modules')+'"'
$shortcut.WorkingDirectory=$app
$shortcut.IconLocation=(Join-Path $app 'VictusFanControl.App.exe')+',0'
$shortcut.Save()
Write-Host "Instalado en $destination"
Write-Host 'Perfiles y preferencias anteriores conservados. Primera instalación: inicio con Windows, minimizado y Automático. La apertura mantiene los requisitos de sensores, guardianes y recuperación.'
if(-not $NoOpen){& (Join-Path $destination 'Start-ProductGui.ps1') -Mode Open}
