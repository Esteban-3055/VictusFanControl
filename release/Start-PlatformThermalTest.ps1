param(
    [ValidateSet('Verify','SelfTest','Run')][string]$Mode='SelfTest',
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
if($Mode -eq 'Run'){throw 'TZ01/DTT3 y la prueba de retención se han retirado. No se inicia ninguna adquisición.'}
$root=$PSScriptRoot
& (Join-Path $root 'Start-ProductGui.ps1') -Mode Verify
$manifest=Get-Content -LiteralPath (Join-Path $root 'PRODUCT-GUI-MANIFEST.json') -Raw | ConvertFrom-Json
if($manifest.platformThermalExperiment -ne 'retired-no-live-entry'){throw 'Unexpected platform sensor contract.'}
if($Mode -eq 'Verify'){Write-Host 'Platform sensors retired: PASS. No hardware activation.';return}
$start=New-Object System.Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $root 'app/VictusFanControl.App.exe'
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle='Hidden'
$start.WorkingDirectory=Join-Path ([IO.Path]::GetTempPath()) ('Victus-Sensor-fixtures-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $start.WorkingDirectory | Out-Null
$start.Arguments='--product-sensor-policy-self-test'
$process=[Diagnostics.Process]::Start($start)
$process.WaitForExit()
if($process.ExitCode -ne 0){throw "Retired sensor policy fixtures failed: $($process.ExitCode)"}
Write-Host 'Retired sensor policy fixtures: PASS. No hardware IO.'
