param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$Sha256Path,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSourceHead
)

$ErrorActionPreference = 'Stop'
$zipFull = [IO.Path]::GetFullPath($ZipPath)
$shaFull = [IO.Path]::GetFullPath($Sha256Path)
$actualZipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipFull).Hash.ToLowerInvariant()
$shaText = ([IO.File]::ReadAllText($shaFull)).Trim()
$expectedZipHash = ($shaText -split '\s+')[0].ToLowerInvariant()
if ($actualZipHash -ne $expectedZipHash) { throw "P14.3 ZIP SHA-256 mismatch. Expected $expectedZipHash, got $actualZipHash." }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('VFC-P14.3-VERIFY-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
try {
    Expand-Archive -LiteralPath $zipFull -DestinationPath $temp -Force
    $roots = @(Get-ChildItem -LiteralPath $temp -Directory)
    if ($roots.Count -ne 1 -or $roots[0].Name -ne 'VictusFanControl-0.4.0-rc.1-win-x64') { throw 'P14.3 ZIP must contain exactly one expected package root.' }
    $root = $roots[0].FullName
    $manifestPath = Join-Path $root 'PACKAGE-MANIFEST.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'P14.3 package manifest missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$manifest.version -ne '0.4.0-rc.1') { throw 'P14.3 manifest version mismatch.' }
    if ([string]$manifest.runtimeIdentifier -ne 'win-x64') { throw 'P14.3 manifest RID mismatch.' }
    if ([string]$manifest.sourceHead -ne $ExpectedSourceHead.ToLowerInvariant()) { throw 'P14.3 manifest source HEAD mismatch.' }
    $listed = @{}
    foreach ($entry in @($manifest.files)) {
        $rel = [string]$entry.path
        if ($listed.ContainsKey($rel)) { throw "Duplicate manifest path: $rel" }
        $listed[$rel] = $true
        $file = Join-Path $root ($rel.Replace('/','\'))
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Manifest file missing: $rel" }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
        if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Manifest SHA-256 mismatch: $rel" }
        if ([long](Get-Item -LiteralPath $file).Length -ne [long]$entry.size) { throw "Manifest size mismatch: $rel" }
    }
    $actualPayload = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Name -ne 'PACKAGE-MANIFEST.json' })
    if ($actualPayload.Count -ne $listed.Count) { throw "P14.3 payload count mismatch. Manifest=$($listed.Count), actual=$($actualPayload.Count)." }
    foreach ($file in $actualPayload) {
        $rel = $file.FullName.Substring($root.Length + 1).Replace('\','/')
        if (-not $listed.ContainsKey($rel)) { throw "Unlisted P14.3 payload file: $rel" }
    }
    foreach ($required in @('app/VictusFanControl.App.exe','app/modules/IntelMSR.bin','app/modules/LpcACPIEC.bin','watchdog/VictusFanControl.Watchdog.exe','watchdog/modules/LpcACPIEC.bin','release/p14-software-rc.json','release/p14-publish-layout.json','release/p14-external-inputs.json','README-RC.txt','LICENSE','THIRD_PARTY_NOTICES.md')) {
        if (-not $listed.ContainsKey($required)) { throw "Required package file missing from manifest: $required" }
    }
    $pdb = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.pdb)
    if ($pdb.Count -ne 0) { throw 'P14.3 package must not contain PDB files.' }
    $probe = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Name -like 'VictusFanControl.ModernStandbyProbe*' })
    if ($probe.Count -ne 0) { throw 'P14.3 package must not include ModernStandbyProbe.' }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "HP 8C40 P14.3 package verification: PASS ($actualZipHash)" -ForegroundColor Green
