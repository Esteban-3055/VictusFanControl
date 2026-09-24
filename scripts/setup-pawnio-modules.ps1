$ErrorActionPreference = 'Stop'

$release = '0.2.11'
$archiveName = "release_0_2_11.zip"
$url = "https://github.com/namazso/PawnIO.Modules/releases/download/$release/$archiveName"
$expectedSha256 = '43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4'

$repoRoot = Split-Path -Parent $PSScriptRoot
$modulesDir = Join-Path $repoRoot 'modules'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("VictusFanControl-PawnIO-" + [Guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tempRoot $archiveName
$extractPath = Join-Path $tempRoot 'extract'

New-Item -ItemType Directory -Force -Path $tempRoot, $extractPath, $modulesDir | Out-Null

try {
    Write-Host "Downloading official signed PawnIO.Modules $release..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zipPath

    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedSha256) {
        throw "PawnIO.Modules archive SHA-256 mismatch. Expected $expectedSha256, got $actualHash."
    }

    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

    foreach ($name in @('IntelMSR.bin', 'LpcACPIEC.bin')) {
        $match = Get-ChildItem -Path $extractPath -Recurse -File |
            Where-Object { $_.Name -ieq $name } |
            Select-Object -First 1

        if (-not $match) {
            throw "$name was not found inside $archiveName."
        }

        Copy-Item -Force $match.FullName (Join-Path $modulesDir $name)
        Write-Host "Installed module: $name" -ForegroundColor Green
    }
}
finally {
    Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'PawnIO modules are ready.' -ForegroundColor Green
Write-Host 'Next: .\scripts\probe-backends.ps1'
