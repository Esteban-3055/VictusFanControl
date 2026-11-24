param([Parameter(Mandatory=$true)][string]$RuntimeDirectory)
$ErrorActionPreference='Stop'
$runtime=[IO.Path]::GetFullPath($RuntimeDirectory)
foreach($file in @('VictusFanControl.App.dll','VictusFanControl.App.runtimeconfig.json','performance-guardian/VictusFanControl.PerformanceGuardian.dll')) {
    if(-not (Test-Path -LiteralPath (Join-Path $runtime $file))){throw "Missing isolated runtime dependency: $file"}
}
$repo=Split-Path -Parent $PSScriptRoot
$work=Join-Path ([IO.Path]::GetTempPath()) ('Victus-function-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
dotnet build (Join-Path $repo 'tools/FunctionLifecycle.Tests') -c Release -o $work --nologo -m:1 /p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Lifecycle acceptance build failed.'}
# Overwrite build references with the exact candidate under test, including its
# executable/runtime config. Nothing is written into the source or installed app.
Get-ChildItem -LiteralPath $runtime | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $work -Recurse -Force}
dotnet (Join-Path $work 'FunctionLifecycle.Tests.dll')
if($LASTEXITCODE -ne 0){throw "Lifecycle corrected-outcome checks failed. Evidence: $work"}
Write-Output "PASS lifecycle acceptance evidence: $work"
