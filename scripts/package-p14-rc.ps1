param(
    [Parameter(Mandatory = $true)][string]$PublishRoot,
    [Parameter(Mandatory = $true)][string]$PawnIoArchivePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceHead
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publish = [IO.Path]::GetFullPath($PublishRoot)
$archive = [IO.Path]::GetFullPath($PawnIoArchivePath)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$release = Get-Content -LiteralPath (Join-Path $repoRoot 'release\p14-software-rc.json') -Raw | ConvertFrom-Json
$layout = Get-Content -LiteralPath (Join-Path $repoRoot 'release\p14-publish-layout.json') -Raw | ConvertFrom-Json
$external = Get-Content -LiteralPath (Join-Path $repoRoot 'release\p14-external-inputs.json') -Raw | ConvertFrom-Json

if ([string]$release.version -ne '0.4.0-rc.1') { throw 'Unexpected P14.3 RC version.' }
if ([string]$layout.runtimeIdentifier -ne 'win-x64') { throw 'Unexpected P14.3 RID.' }
& (Join-Path $repoRoot 'scripts\verify-p14-publish-layout.ps1') -OutputRoot $publish

$actualArchiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
$expectedArchiveHash = ([string]$external.pawnIoModules.sha256).ToLowerInvariant()
if ($actualArchiveHash -ne $expectedArchiveHash) { throw "PawnIO archive SHA-256 mismatch. Expected $expectedArchiveHash, got $actualArchiveHash." }

if (Test-Path -LiteralPath $output) {
    $existing = @(Get-ChildItem -LiteralPath $output -Force -ErrorAction Stop)
    if ($existing.Count -ne 0) { throw "P14.3 package output must be absent or empty: $output" }
} else { New-Item -ItemType Directory -Force -Path $output | Out-Null }

$packageName = 'VictusFanControl-0.4.0-rc.1-win-x64'
$stage = Join-Path $output $packageName
$app = Join-Path $stage 'app'
$watchdog = Join-Path $stage 'watchdog'
$releaseDir = Join-Path $stage 'release'
New-Item -ItemType Directory -Force -Path $app, $watchdog, $releaseDir | Out-Null
Copy-Item -Path (Join-Path $publish 'app\*') -Destination $app -Recurse -Force
Copy-Item -Path (Join-Path $publish 'watchdog\*') -Destination $watchdog -Recurse -Force

$temp = Join-Path ([IO.Path]::GetTempPath()) ('VFC-P14.3-' + [Guid]::NewGuid().ToString('N'))
$extract = Join-Path $temp 'pawnio'
New-Item -ItemType Directory -Force -Path $extract | Out-Null
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
    $resolved = @{}
    foreach ($name in @('IntelMSR.bin','LpcACPIEC.bin')) {
        $matches = @(Get-ChildItem -LiteralPath $extract -Recurse -File | Where-Object { $_.Name -ieq $name })
        if ($matches.Count -ne 1) { throw "Expected exactly one $name in pinned PawnIO archive; found $($matches.Count)." }
        $resolved[$name] = $matches[0].FullName
    }
    $appModules = Join-Path $app 'modules'
    $watchdogModules = Join-Path $watchdog 'modules'
    New-Item -ItemType Directory -Force -Path $appModules, $watchdogModules | Out-Null
    Copy-Item -LiteralPath $resolved['IntelMSR.bin'] -Destination (Join-Path $appModules 'IntelMSR.bin') -Force
    Copy-Item -LiteralPath $resolved['LpcACPIEC.bin'] -Destination (Join-Path $appModules 'LpcACPIEC.bin') -Force
    Copy-Item -LiteralPath $resolved['LpcACPIEC.bin'] -Destination (Join-Path $watchdogModules 'LpcACPIEC.bin') -Force
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }

Copy-Item -LiteralPath (Join-Path $repoRoot 'release\p14-software-rc.json') -Destination (Join-Path $releaseDir 'p14-software-rc.json') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'release\p14-publish-layout.json') -Destination (Join-Path $releaseDir 'p14-publish-layout.json') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'release\p14-external-inputs.json') -Destination (Join-Path $releaseDir 'p14-external-inputs.json') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'release\README-RC.txt') -Destination (Join-Path $stage 'README-RC.txt') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $stage 'THIRD_PARTY_NOTICES.md') -Force

$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object { $_.FullName.Substring($stage.Length + 1).Replace('\','/') })
$entries = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($stage.Length + 1).Replace('\','/')
    $entries += [ordered]@{ path = $relative; size = [long]$file.Length; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    schemaVersion = 1
    packageName = $packageName
    version = '0.4.0-rc.1'
    runtimeIdentifier = 'win-x64'
    sourceHead = $SourceHead.ToLowerInvariant()
    deterministicZipTimestampUtc = '2000-01-01T00:00:00Z'
    pawnIoModulesArchive = [ordered]@{ version = [string]$external.pawnIoModules.version; archiveName = [string]$external.pawnIoModules.archiveName; sha256 = $expectedArchiveHash }
    files = $entries
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
$manifestPath = Join-Path $stage 'PACKAGE-MANIFEST.json'
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 8) + "`n"), $utf8)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $output ($packageName + '.zip')
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $zipFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object { $_.FullName.Substring($stage.Length + 1).Replace('\','/') })
        $fixedTime = [DateTimeOffset]::Parse('2000-01-01T00:00:00Z')
        foreach ($file in $zipFiles) {
            $relative = $file.FullName.Substring($stage.Length + 1).Replace('\','/')
            $entry = $zip.CreateEntry(($packageName + '/' + $relative), [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = $fixedTime
            $input = [IO.File]::OpenRead($file.FullName)
            $entryStream = $entry.Open()
            try { $input.CopyTo($entryStream) } finally { $entryStream.Dispose(); $input.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }

$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
$shaPath = $zipPath + '.sha256'
[IO.File]::WriteAllText($shaPath, ($zipHash + '  ' + [IO.Path]::GetFileName($zipPath) + "`n"), $utf8)
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "P14.3 deterministic package built: $zipPath" -ForegroundColor Green
Write-Host "SHA-256: $zipHash"
