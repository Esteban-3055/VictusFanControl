param([switch]$Live,[string]$OutputDirectory=(Join-Path ([IO.Path]::GetTempPath()) ('Victus-updater-129-'+[Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$baseline='e8a58a5cb01eb673114a8dca0c5c0e925f55fddc'
Push-Location $repo
try {
    git cat-file -e ($baseline+'^{commit}') 2>$null
    if($LASTEXITCODE -ne 0){git fetch --depth=1 origin $baseline;if($LASTEXITCODE -ne 0){throw 'Cannot read pinned 1.2.9 baseline.'}}
    $root=[IO.Path]::GetFullPath($OutputDirectory)
    $sources=Join-Path $root 'baseline'
    New-Item -ItemType Directory -Force -Path $sources | Out-Null
    foreach($name in @('ProductUpdates.cs','ProductUpdates.Cache.cs','ProductRelease.cs')) {
        $content=& git show ($baseline+':src/VictusFanControl/Product/'+$name)
        if($LASTEXITCODE -ne 0){throw 'Baseline source unavailable.'}
        [IO.File]::WriteAllText((Join-Path $sources $name),($content -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
    }
    $options=@('run','--project','tools/UpdaterCompatibility.Tests','-c','Release',('-p:UpdaterBaselineRoot='+$sources),'-p:NuGetAudit=false','--')
    if($Live){$options+=@('--live',(Join-Path $root 'cache'))}
    & dotnet @options
    if($LASTEXITCODE -ne 0){throw 'Original 1.2.9 updater compatibility failed.'}
} finally {Pop-Location}
