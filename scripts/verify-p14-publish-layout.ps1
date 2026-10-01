param(
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

function Require-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required P14.2 publish file missing: $Path"
    }
}

$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$app = Join-Path $outputFull 'app'
$watchdog = Join-Path $outputFull 'watchdog'

Require-File (Join-Path $app 'VictusFanControl.App.exe')
Require-File (Join-Path $app 'VictusFanControl.App.dll')
Require-File (Join-Path $app 'VictusFanControl.App.deps.json')
Require-File (Join-Path $app 'VictusFanControl.App.runtimeconfig.json')
Require-File (Join-Path $watchdog 'VictusFanControl.Watchdog.exe')
Require-File (Join-Path $watchdog 'VictusFanControl.Watchdog.dll')
Require-File (Join-Path $watchdog 'VictusFanControl.Watchdog.deps.json')
Require-File (Join-Path $watchdog 'VictusFanControl.Watchdog.runtimeconfig.json')

$pdb = @(Get-ChildItem -LiteralPath $outputFull -Recurse -File -Filter *.pdb)
if ($pdb.Count -ne 0) { throw "P14.2 publish must not contain PDB files: $($pdb.FullName -join ', ')" }

$probe = @(Get-ChildItem -LiteralPath $outputFull -Recurse -File | Where-Object { $_.Name -like 'VictusFanControl.ModernStandbyProbe*' })
if ($probe.Count -ne 0) { throw 'P14.2 release layout must not include the Modern Standby qualification probe.' }

$moduleBins = @(Get-ChildItem -LiteralPath $outputFull -Recurse -File -Filter *.bin)
if ($moduleBins.Count -ne 0) { throw 'P14.2 publish stage must not silently inject PawnIO module binaries; P14.3 owns verified module packaging.' }

foreach ($assemblyPath in @((Join-Path $app 'VictusFanControl.App.dll'),(Join-Path $watchdog 'VictusFanControl.Watchdog.dll'))) {
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
    if ($assembly.Version.ToString() -ne '0.4.0.0') { throw "Unexpected assembly version in $assemblyPath: $($assembly.Version)" }
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath)
    if ($versionInfo.ProductVersion -ne '0.4.0-rc.1') { throw "Unexpected product version in $assemblyPath: '$($versionInfo.ProductVersion)'" }
}

Write-Host 'HP 8C40 P14.2 win-x64 publish layout verification: PASS' -ForegroundColor Green
