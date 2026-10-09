$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $PSScriptRoot 'package-m9b-evidence.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ("VFC-M9B-PackageTest-"+[Guid]::NewGuid().ToString('N'))
$evidenceRoot=Join-Path $tempRoot 'logs\m9b-production-watchdog-preflight_test'

try {
    New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

    '{"gate":"M9B","result":"PASS"}' |
        Set-Content -LiteralPath (Join-Path $evidenceRoot 'm9b-preflight-result.json') -Encoding UTF8
    'M8_PREFLIGHT_TELEMETRY_PASS' |
        Set-Content -LiteralPath (Join-Path $evidenceRoot 'telemetry-output.txt') -Encoding UTF8

    # The helper intentionally hashes live repository source files. For the
    # synthetic evidence directory only the result/telemetry are fabricated.
    $package=& $helper -EvidenceRoot $evidenceRoot -RepositoryRoot $repoRoot

    if(-not (Test-Path -LiteralPath $package.ZipPath -PathType Leaf)){throw 'M9B packaging self-test ZIP missing.'}
    if(-not (Test-Path -LiteralPath $package.Sha256SidecarPath -PathType Leaf)){throw 'M9B packaging self-test SHA sidecar missing.'}
    if(-not (Test-Path -LiteralPath $package.ManifestPath -PathType Leaf)){throw 'M9B packaging self-test manifest missing.'}

    $actual=(Get-FileHash -LiteralPath $package.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -cne [string]$package.ZipSha256){throw 'M9B packaging self-test ZIP SHA mismatch.'}

    $manifest=Get-Content -LiteralPath $package.ManifestPath -Raw | ConvertFrom-Json
    if($manifest.gate -cne 'M9B' -or
       [int]$manifest.schemaVersion -ne 1 -or
       [bool]$manifest.destructiveOperations){
        throw 'M9B packaging self-test manifest contract mismatch.'
    }

    $resultEntry=@($manifest.files | Where-Object {$_.role -ceq 'result'})
    $telemetryEntry=@($manifest.files | Where-Object {$_.role -ceq 'telemetry'})
    if($resultEntry.Count -ne 1 -or -not [bool]$resultEntry[0].present -or
       $telemetryEntry.Count -ne 1 -or -not [bool]$telemetryEntry[0].present){
        throw 'M9B packaging self-test required evidence hashes are missing.'
    }

    if(-not (Test-Path -LiteralPath (Join-Path $evidenceRoot 'm9b-preflight-result.json'))){
        throw 'M9B packaging helper deleted source evidence.'
    }

    Write-Host 'HP 8C40 M9B evidence packaging self-test: PASS' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
