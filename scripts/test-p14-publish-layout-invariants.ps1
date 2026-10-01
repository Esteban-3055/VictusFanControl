$ErrorActionPreference = 'Stop'

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message } }
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message } }

$root = Split-Path -Parent $PSScriptRoot
$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$layout = Get-Content -LiteralPath (Join-Path $root 'release\p14-publish-layout.json') -Raw | ConvertFrom-Json
$release = Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$build = Get-Content -LiteralPath (Join-Path $root 'scripts\build-p14-publish-layout.ps1') -Raw
$verify = Get-Content -LiteralPath (Join-Path $root 'scripts\verify-p14-publish-layout.ps1') -Raw

Assert-Contains $props '<Version>0.4.0-rc.1</Version>' 'P14.2 centralized RC version missing.'
Assert-Contains $props '<AssemblyVersion>0.4.0.0</AssemblyVersion>' 'P14.2 centralized assembly version missing.'
Assert-Contains $props '<InformationalVersion>0.4.0-rc.1</InformationalVersion>' 'P14.2 informational version missing.'
Assert-Contains $props '<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>' 'P14.2 exact informational-version guard missing.'

foreach ($project in @('src\VictusFanControl\VictusFanControl.csproj','src\VictusFanControl.App\VictusFanControl.App.csproj','src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj','src\VictusFanControl.ModernStandbyProbe\VictusFanControl.ModernStandbyProbe.csproj')) {
    $text = Get-Content -LiteralPath (Join-Path $root $project) -Raw
    Assert-NotContains $text '<Version>' ("Project-specific Version override remains: {0}" -f $project)
}

if ([string]$layout.version -ne '0.4.0-rc.1') { throw 'P14.2 layout version mismatch.' }
if ([string]$layout.runtimeIdentifier -ne 'win-x64') { throw 'P14.2 RID must remain win-x64.' }
if ([bool]$layout.selfContained) { throw 'P14.2 must remain framework-dependent.' }
if ([bool]$layout.publishSingleFile) { throw 'P14.2 must remain multi-file for auditability.' }
if ([bool]$layout.debugSymbolsIncluded) { throw 'P14.2 publish layout must exclude debug symbols.' }
if ([bool]$layout.readyToRun) { throw 'P14.2 ReadyToRun must remain disabled.' }
if ([bool]$layout.pawnIoModulesIncluded) { throw 'P14.2 must not claim packaged PawnIO modules.' }

Assert-Contains $build "'-r', 'win-x64'" 'P14.2 build script RID missing.'
Assert-Contains $build "'--self-contained', 'false'" 'P14.2 framework-dependent publish missing.'
Assert-Contains $build "'-p:PublishSingleFile=false'" 'P14.2 multi-file guard missing.'
Assert-Contains $build "'-p:PublishReadyToRun=false'" 'P14.2 ReadyToRun guard missing.'
Assert-Contains $build "'-p:DebugType=None'" 'P14.2 debug-symbol guard missing.'
Assert-NotContains $build 'SetFanLevel' 'P14.2 build script must not contain fan-control writes.'
Assert-NotContains $build 'sc.exe' 'P14.2 build script must not install/reconfigure services.'

Assert-Contains $verify 'VictusFanControl.App.exe' 'P14.2 verifier must require GUI executable.'
Assert-Contains $verify 'VictusFanControl.Watchdog.exe' 'P14.2 verifier must require watchdog executable.'
Assert-Contains $verify 'P14.2 publish stage must not silently inject PawnIO module binaries' 'P14.2 module boundary missing.'

if (-not [bool]$release.productization.centralizedRcVersionImplemented) { throw 'Release contract must record centralized RC version.' }
if (-not [bool]$release.productization.deterministicPublishImplemented) { throw 'Release contract must record P14.2 publish implementation.' }
if ([bool]$release.productization.publishLayoutCiValidated) { throw 'Implementation commit must not pre-claim publish-layout CI validation.' }

Write-Host 'HP 8C40 P14.2 publish-layout static invariant: PASS' -ForegroundColor Green
