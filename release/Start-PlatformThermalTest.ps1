param(
    [ValidateSet('Verify','SelfTest','Run')][string]$Mode='SelfTest',
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
& (Join-Path $root 'Start-ProductGui.ps1') -Mode Verify
$manifest=Get-Content -LiteralPath (Join-Path $root 'PRODUCT-GUI-MANIFEST.json') -Raw | ConvertFrom-Json
if($manifest.platformThermalExperiment -ne 'explicit-physical-AC-ABBA-2580s-TZ01-DTT3-retention'){throw 'This package does not include the physical platform experiment.'}
if($Mode -eq 'Verify'){Write-Host 'Experimental package integrity: PASS. No hardware activation.';return}
$app=Join-Path $root 'app'
$start=New-Object System.Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $app 'VictusFanControl.App.exe'
$start.UseShellExecute=$false
$start.WorkingDirectory=$app
if($Mode -eq 'SelfTest'){
    $start.WorkingDirectory=Join-Path ([IO.Path]::GetTempPath()) ('Victus-Platform-fixtures-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $start.WorkingDirectory | Out-Null
    $start.Arguments='--platform-physical-self-test'
    Write-Host "Zero-hardware fixtures: $($start.WorkingDirectory)"
}else{
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{if(-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run PowerShell as Administrator.'}}finally{$identity.Dispose()}
    if(-not $OutputDirectory){$OutputDirectory=Join-Path ([Environment]::GetFolderPath('Desktop')) ('Victus-Platform-physical-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
    $OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
    if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new evidence directory.'}
    if($OutputDirectory.Contains('"')){throw 'Invalid output path.'}
    $start.Arguments='--modules-dir "'+(Join-Path $app 'modules')+'" --platform-thermal-experiment --experimental-output "'+$OutputDirectory+'"'
    Write-Host 'Physical fan experiment: starts only with the Iniciar prueba button. AC only; 43 minutes; CPU PL1/PL2 and GPU MHz editable in the trial window, frozen on Start; existing fan watchdog and CPU/GPU Guardian. Close other controllers normally. Repeat the same workload when the window says Carga, and close it when it says Enfriamiento.'
}
$process=[Diagnostics.Process]::Start($start)
try{$process.WaitForExit();$result=$process.ExitCode}finally{$process.Dispose()}
if($Mode -eq 'Run' -and (Test-Path -LiteralPath $OutputDirectory)){
    @{sourceHead=$manifest.sourceHead;manifestSha256=(Get-FileHash -LiteralPath (Join-Path $root 'PRODUCT-GUI-MANIFEST.json') -Algorithm SHA256).Hash.ToLowerInvariant();processExitCode=$result;physicalPassClaimed=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package-identity.json') -Encoding utf8
    $entries=@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | ForEach-Object {
        @{path=$_.FullName.Substring($OutputDirectory.TrimEnd('\').Length+1).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    @{schemaVersion=1;files=$entries} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'evidence-sha256.json') -Encoding utf8
    $zip=$OutputDirectory+'.zip'
    if(Test-Path -LiteralPath $zip){throw 'Evidence archive already exists; original evidence retained.'}
    Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $zip
    Write-Host "Evidence ZIP: $zip"
}
if($result -ne 0){throw "Experiment/fixture ended with code $result. Retain its ZIP, raw evidence and pending journals; inspect the recorded cause."}
