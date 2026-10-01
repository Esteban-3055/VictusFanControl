$ErrorActionPreference = 'Stop'
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message } }
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message) { if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message } }
$root = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$external = Get-Content -LiteralPath (Join-Path $root 'release\p14-external-inputs.json') -Raw | ConvertFrom-Json
$fetch = Get-Content -LiteralPath (Join-Path $root 'scripts\fetch-p14-external-inputs.ps1') -Raw
$package = Get-Content -LiteralPath (Join-Path $root 'scripts\package-p14-rc.ps1') -Raw
$verify = Get-Content -LiteralPath (Join-Path $root 'scripts\verify-p14-package.ps1') -Raw
if ([string]$release.milestone -notin @('P14.3','P14.4','P14.5')) { throw 'P14.3 package invariant requires P14.3 or later P14 state.' }
if (-not [bool]$release.productization.deterministicPackageImplemented) { throw 'P14.3 deterministic package must be implemented.' }
if (-not [bool]$release.productization.sha256ManifestImplemented) { throw 'P14.3 SHA-256 manifest must be implemented.' }
if (-not [bool]$release.productization.externalInputsPinned) { throw 'P14.3 external inputs must be pinned.' }
if (-not [bool]$release.productization.packageCiValidated) { throw 'P14.3 formal closure requires package CI validation.' }
if (-not [bool]$release.productization.packageClosure.closed) { throw 'P14.3 package closure must be closed.' }
if ([string]$release.productization.packageClosure.sourceHead -ne '9ca006d98b369654ff1fd6ffc3fa7cb1c8745250') { throw 'P14.3 package closure source HEAD mismatch.' }
if ([int]$release.productization.packageClosure.sourceCiRunNumber -ne 1078) { throw 'P14.3 package closure CI number mismatch.' }
if ([long]$release.productization.packageClosure.sourceCiRunId -ne 36903426730) { throw 'P14.3 package closure CI ID mismatch.' }
if ([string]$release.productization.packageClosure.zipSha256 -ne '603ac7b2ca6816fe00002598410983ba031529ec168a70c5ec59041e5a71756c') { throw 'P14.3 package closure ZIP SHA-256 mismatch.' }
if ([string]$external.pawnIoModules.version -ne '0.2.11') { throw 'Unexpected PawnIO.Modules version.' }
if ([string]$external.pawnIoModules.sha256 -ne '43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4') { throw 'Pinned PawnIO archive SHA-256 changed.' }
Assert-Contains $fetch 'Get-FileHash -Algorithm SHA256' 'External-input fetch must verify SHA-256.'
Assert-Contains $package '[IO.Compression.CompressionLevel]::NoCompression' 'P14.3 deterministic ZIP must use no-compression entries.'
Assert-Contains $package '2000-01-01T00:00:00Z' 'P14.3 deterministic ZIP timestamp missing.'
Assert-Contains $package 'PACKAGE-MANIFEST.json' 'P14.3 package manifest missing.'
Assert-Contains $package 'IntelMSR.bin' 'P14.3 package must include IntelMSR module for the GUI.'
Assert-Contains $package 'LpcACPIEC.bin' 'P14.3 package must include ACPI EC module.'
Assert-Contains $verify 'Manifest SHA-256 mismatch' 'P14.3 verifier must rehash manifest files.'
Assert-NotContains $package 'SetFanLevel' 'P14.3 packaging script must not contain fan-control writes.'
Assert-NotContains $package 'sc.exe' 'P14.3 packaging script must not install/reconfigure services.'
Write-Host 'HP 8C40 P14.3 deterministic package static invariant: PASS' -ForegroundColor Green
