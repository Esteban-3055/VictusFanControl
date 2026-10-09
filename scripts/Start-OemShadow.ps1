param(
    [ValidateSet('Verify','SelfTest','Capture')][string]$Mode='SelfTest',
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$ModulesDirectory,
    [ValidateRange(0,2147483647)][int]$DurationSeconds=3600,
    [string]$OutputDirectory,
    [string]$ParametersFile
)
$ErrorActionPreference='Stop'
$runtime=(Resolve-Path -LiteralPath $RuntimeDirectory).Path
$manifestPath=Join-Path $runtime 'sha256.json'
if(-not (Test-Path -LiteralPath $manifestPath)){throw 'Missing runtime integrity manifest.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$rootPrefix=$runtime.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
foreach($entry in $manifest.files){
    $path=[IO.Path]::GetFullPath((Join-Path $runtime $entry.path))
    if(-not $path.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path outside runtime.'}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Hash mismatch: $($entry.path)"}
}
if(-not $manifest.files -or -not ($manifest.files.path -contains 'VictusFanControl.OemShadowCapture.dll')){throw 'Invalid observer manifest.'}
if($Mode -eq 'Verify'){Write-Output "OEM shadow integrity: PASS ($($manifest.sourceRevision))";exit 0}
$exe=Join-Path $runtime 'VictusFanControl.OemShadowCapture.exe'
$command=$exe;$prefix=@()
if(-not (Test-Path -LiteralPath $exe)){
    $command=(Get-Command dotnet -ErrorAction Stop).Source
    $prefix=@((Join-Path $runtime 'VictusFanControl.OemShadowCapture.dll'))
}
if($Mode -eq 'SelfTest'){
    & $command @prefix '--self-test'
    if($LASTEXITCODE -ne 0){throw 'OEM shadow self-test failed.'}
    exit 0
}
if(-not $ModulesDirectory){throw 'Capture requires -ModulesDirectory from the verified VFC package.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path ([Environment]::GetFolderPath('Desktop')) ('Victus-OEM-shadow-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$launchArgs=@('--live-read-only','--modules-dir',(Resolve-Path -LiteralPath $ModulesDirectory).Path,'--output',[IO.Path]::GetFullPath($OutputDirectory),'--duration-seconds',$DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
if($ParametersFile){$launchArgs+=@('--parameters',(Resolve-Path -LiteralPath $ParametersFile).Path)}
& $command @prefix @launchArgs
if($LASTEXITCODE -ne 0){throw "Capture failed. Retain the evidence directory: $OutputDirectory"}
Write-Output "Evidence directory: $OutputDirectory"
